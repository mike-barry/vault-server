using System.Collections.Concurrent;
using System.ComponentModel;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>Config for <see cref="ElevationRequestTool"/>: the default rebind TTL and the
/// per-principal request rate limit (a fixed window).</summary>
public sealed record ElevationOptions(int DefaultTtlMinutes = 15, int MaxRequestsPerMinute = 5);

/// <summary>
/// <see cref="ElevationRequestTool"/>'s arguments: both the deserialization target and the source of
/// the tool's advertised <c>inputSchema</c> (see <see cref="NativeToolArguments"/>). Every member is
/// optional, so nothing is <c>required</c> in the derived schema - the two-form rule (<c>grant</c>
/// XOR <c>scope_roots</c>+<c>op_class</c>) is a cross-argument constraint JSON Schema's
/// <c>required</c> cannot state.
///
/// <see cref="Validate"/> is empty, like <see cref="MintTokenArguments"/>'s and for the same reason:
/// the form-selection rule blames no single argument, so it cannot be expressed as an
/// <see cref="ArgumentFault"/> without inventing an offender, and every other rule here needs the
/// registry, the vault configuration, or the caller's own grant.
/// </summary>
public sealed record ElevationRequestArguments(
    [property: Description("Id of a named grant to elevate to (e.g. \"admin\"). Provide this OR scope_roots+op_class, not both.")]
    string? Grant = null,
    [property: Description("Folder-prefix scope roots for a freshly-minted scoped target, e.g. \"primary/Projects/Foo\". Provide with op_class instead of grant.")]
    List<string>? ScopeRoots = null,
    [property: Description("Explicit op classes for a freshly-minted scoped target, e.g. [\"Read\"]. Provide with scope_roots.")]
    List<string>? OpClass = null,
    [property: Description("Minutes the elevation lasts before reverting to the base binding. Optional; defaults to the server's configured elevation TTL.")]
    int? Ttl = null,
    [property: Description("Optional free-text context about why elevation is needed, shown to a human approver.")]
    string? SessionContext = null) : IToolArguments
{
    public ArgumentFault? Validate() => null;
}

/// <summary>
/// Requests a TTL'd elevation rebind of the caller's token to a different grant - a token-less named
/// grant, or a freshly-minted scoped grant - via <see cref="GrantStore"/>'s binding table. Mirrors
/// <see cref="MintTokenTool"/>'s shape but is gated by a per-grant opt-in
/// (<see cref="Grant.CanRequestElevation"/>) rather than an op class, which is why its declaration is
/// <see cref="Floor.SelfGated"/>: the gate is inside this tool, so an unauthorized caller gets an
/// informative refusal rather than the tool vanishing.
///
/// It declares <see cref="ToolDeclaration.RequiresClientSession"/> because it rebinds authority keyed
/// to a live caller's token; the session's principal key is what the rebind is recorded against.
///
/// Flow: verify the caller may request elevation, rate-limit per principal, resolve the requested
/// grant, ask the pluggable <see cref="IElevationApprover"/> (shipped as an auto-deny stub), and
/// on approval rebind with a TTL. On a dev machine the default binding is already admin, so this
/// is off the daily path; it matters where the default is minimal.
///
/// The codes: <c>authority_required</c> for both authority denials - the missing
/// <c>canRequestElevation</c> opt-in and the approver's "no" - deliberately sharing one code so a
/// caller cannot tell "not allowed to ask" from "asked and refused"; <c>rate_limited</c> for the
/// throttle, the code for a transient throttle, since coding it as capability or argument would route
/// a retryable condition into a branch that never retries; <c>invalid_argument</c> for malformed or
/// contradictory target arguments; <c>not_found</c> for a named target that is not a configured
/// grant; <c>unknown_vault</c>/<c>vault_unavailable</c> for a scoped target's vault, classified by
/// which config map holds the name.
///
/// This tool is self-gated, so a caller on the minimum-access floor reaches every one of those paths.
/// <c>data</c> therefore carries only what the caller itself supplied (the argument name, its own
/// <c>grant</c> id echo) plus the configured rate limit, and never the caller's grant id, op classes,
/// vault set, or scope roots.
///
/// <b>The invocation blocks for the approver's answer.</b> The tool surface is synchronous because
/// the mutex hold cannot cross a suspension point; this tool takes no lock, and the whole cost of
/// that choice falls here.
/// </summary>
public sealed class ElevationRequestTool(
    GrantStore grantStore,
    IElevationApprover approver,
    OpClassRegistry opClassRegistry,
    VaultConfig vaultConfig,
    ElevationOptions options,
    TimeProvider timeProvider) : ITool
{
    public const string ToolName = "request_elevation";

    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Requests a time-limited elevation of the calling session to a different grant - a named " +
            "grant, or a freshly-minted scoped grant - subject to approval. Requires a grant that opts " +
            "into elevation. Requests are rate-limited and, in the default configuration, auto-denied.",
        Arguments = typeof(ElevationRequestArguments),
        OpClassFloor = new Floor.SelfGated(),
        RequiresClientSession = true,
    };

    private readonly TimeSpan _rateWindow = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset WindowStart, int Count)> _requestCounts = new();

    /// <summary>What an approved elevation reports back. The minted or named target, and when it
    /// reverts.</summary>
    public sealed record ElevationReport(bool Elevated, string Grant, string FromGrant, int TtlMinutes, DateTimeOffset ExpiresAtUtc);

    public ToolResult Invoke(ToolCall call)
    {
        var args = call.ArgumentsAs<ElevationRequestArguments>();
        var grant = call.Grant;
        // The declaration requires a session, and the execute path refuses the call before it reaches
        // here without one.
        var tokenHashKey = call.Session!.PrincipalKey;

        // Deny by default: only a grant that opts in may request elevation (minted grants never
        // do, so an automated subagent cannot self-escalate). The opt-in stands in for an op class
        // on this tool, so the shortfall carries the op-class shortfall's code.
        if (!grant.CanRequestElevation)
            throw new ToolRefusal(
                ToolCodes.AuthorityRequired,
                $"Grant \"{grant.Id}\" is not permitted to request elevation - requires a grant with canRequestElevation enabled.");

        // Consumed before the target is resolved, so the throttle also bounds how fast a caller can
        // probe which named grants exist through the not_found below.
        if (!TryConsumeRequestSlot(tokenHashKey))
            throw new ToolRefusal(
                ToolCodes.RateLimited,
                $"Elevation requests are rate-limited to {options.MaxRequestsPerMinute} per minute for this session - try again shortly.",
                new { limitPerMinute = options.MaxRequestsPerMinute });

        var (requestedGrantId, requestedGrant, mintTarget, mintOpClasses, mintVaults, mintScopeRoots) = ResolveTarget(args);
        var ttlMinutes = ReadTtl(args);

        var request = new ElevationRequest(grant.Id, grant, requestedGrantId, requestedGrant, ttlMinutes, args.SessionContext);
        var decision = approver.ReviewAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        // Same code as the opt-in denial above: both mean the caller does not have this authority and
        // neither is fixable by changing the arguments. data stays empty - the approver's reason is
        // human-readable prose in the message, and the minted-target id is server-generated.
        if (!decision.Approved)
            throw new ToolRefusal(ToolCodes.AuthorityRequired, $"Elevation to \"{requestedGrantId}\" denied: {decision.Reason}");

        var expiresAt = timeProvider.GetUtcNow().AddMinutes(ttlMinutes);
        if (mintTarget)
            grantStore.RebindToMintedGrant(tokenHashKey, requestedGrantId, mintOpClasses, mintVaults, mintScopeRoots, expiresAt);
        else
            grantStore.RebindToNamedGrant(tokenHashKey, requestedGrantId, expiresAt);

        return new ToolResult.Data(new ElevationReport(true, requestedGrantId, grant.Id, ttlMinutes, expiresAt));
    }

    /// <summary>
    /// Resolves the elevation target from the arguments: either a named grant (the <c>grant</c>
    /// argument) or a freshly-minted scoped grant (<c>scope_roots</c> + <c>op_class</c>). Exactly
    /// one form must be supplied - a cross-argument rule the schema cannot express, so it stays here.
    /// </summary>
    private (string Id, Grant Grant, bool Mint, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, IReadOnlyDictionary<string, VaultScope> ScopeRoots) ResolveTarget(ElevationRequestArguments args)
    {
        var hasNamed = !string.IsNullOrEmpty(args.Grant);
        var hasScoped = args.ScopeRoots is not null || args.OpClass is not null;

        // No single argument is at fault in either form-selection failure, so neither carries an
        // "argument" datum - the message names the whole rule.
        if (hasNamed && hasScoped)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "Provide either \"grant\" (a named grant) or \"scope_roots\"+\"op_class\" (a scoped grant), not both.");
        if (!hasNamed && !hasScoped)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "Provide a \"grant\" (named grant id) or a \"scope_roots\"+\"op_class\" pair to elevate to.");

        if (hasNamed)
        {
            var id = args.Grant!;
            // not_found preserves the distinction this tool's prose already drew between an
            // unconfigured target and a refused one; data echoes only the id the caller passed.
            if (!grantStore.TryGetNamedGrant(id, out var named))
                throw new ToolRefusal(ToolCodes.NotFound, $"Unknown named grant \"{id}\" - not a configured elevation target.", new { argument = "grant", grant = id });
            var empty = (IReadOnlySet<string>)new HashSet<string>();
            return (id, named, false, empty, empty, new Dictionary<string, VaultScope>());
        }

        var (opClasses, vaults, scopeRootsByVault) = ParseScopedTarget(args);
        var mintedId = $"elevated-{Guid.NewGuid():N}";
        // A representative grant object for the approver to review; the real one is created at
        // rebind time (RebindToMintedGrant) so nothing is registered unless approved.
        var preview = new Grant(mintedId, opClasses, vaults, timeProvider.GetUtcNow(), scopeRootsByVault);
        return (mintedId, preview, true, opClasses, vaults, scopeRootsByVault);
    }

    /// <summary>
    /// Parses the freshly-minted scoped target's <c>scope_roots</c>/<c>op_class</c> into concrete
    /// sets, applying the same shape rules as <see cref="MintTokenTool"/>: vault-prefixed scope
    /// roots grouped per vault (a whole-vault entry becomes <see cref="VaultScope.Unscoped"/>), each subtree
    /// canonicalized via <see cref="ScopePath.Normalize"/>, and explicit op classes
    /// only (no <c>"*"</c>). Attenuation against the caller's own grant is a separate, later
    /// checklist item and is not enforced here.
    /// </summary>
    private (IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, IReadOnlyDictionary<string, VaultScope> ScopeRoots) ParseScopedTarget(ElevationRequestArguments args)
    {
        var scopeRoots = RequireNonEmptyEntries(args.ScopeRoots, "scope_roots");
        if (scopeRoots.Count == 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"scope_roots\" is required for a scoped elevation target and must contain at least one entry.", new { argument = "scope_roots" });
        if (scopeRoots.Any(v => v == "*"))
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"scope_roots\" does not accept \"*\" - list explicit folder-prefix roots.", new { argument = "scope_roots" });

        var opClassValues = RequireNonEmptyEntries(args.OpClass, "op_class");
        if (opClassValues.Count == 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"op_class\" is required for a scoped elevation target and must contain at least one entry.", new { argument = "op_class" });
        if (opClassValues.Any(v => v == "*"))
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"op_class\" does not accept \"*\" - list explicit op classes.", new { argument = "op_class" });

        var opClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in opClassValues)
            opClasses.Add(opClassRegistry.Canonicalize(value)
                ?? throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"op_class\" entry \"{value}\" is not a known op class.", new { argument = "op_class", opClass = value }));

        var byVault = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var wholeVault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scopeRoots)
        {
            var parts = root.Split('/', 2);
            var vaultName = parts[0];
            // Classified by which config map holds the name, never by message text.
            if (vaultConfig.SkippedVaults.ContainsKey(vaultName))
                throw new ToolRefusal(
                    ToolCodes.VaultUnavailable,
                    $"\"scope_roots\" entry \"{root}\" names vault \"{vaultName}\", which is configured but currently unavailable.",
                    new { argument = "scope_roots", scopeRoot = root, vault = vaultName });
            if (!vaultConfig.Vaults.ContainsKey(vaultName))
                throw new ToolRefusal(
                    ToolCodes.UnknownVault,
                    $"\"scope_roots\" entry \"{root}\" names an unknown vault \"{vaultName}\".",
                    new { argument = "scope_roots", scopeRoot = root, vault = vaultName });

            if (!byVault.TryGetValue(vaultName, out var subtrees))
                byVault[vaultName] = subtrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                try
                {
                    subtrees.Add(string.Join('/', ScopePath.Normalize(parts[1])));
                }
                catch (ArgumentException ex)
                {
                    throw new ToolRefusal(
                        ToolCodes.InvalidArgument,
                        $"\"scope_roots\" entry \"{root}\" has an invalid subtree \"{parts[1]}\": {ex.Message}",
                        new { argument = "scope_roots", scopeRoot = root });
                }
            }
            else
                wholeVault.Add(vaultName);
        }
        var vaults = new HashSet<string>(byVault.Keys, StringComparer.OrdinalIgnoreCase);
        // A whole-vault entry subsumes any subtree entry for the same vault.
        var scopeRootsByVault = byVault.ToDictionary(
            kv => kv.Key,
            kv => wholeVault.Contains(kv.Key) || kv.Value.Count == 0
                ? VaultScope.Unscoped
                : VaultScope.Of(kv.Value),
            StringComparer.OrdinalIgnoreCase);
        return (opClasses, vaults, scopeRootsByVault);
    }

    /// <summary>Range check, not a type check: binding already refused anything that is not an
    /// integer, but "positive" is a meaning the schema cannot carry. An omitted <c>ttl</c> binds to
    /// null and takes the server's configured default.</summary>
    private int ReadTtl(ElevationRequestArguments args)
    {
        if (args.Ttl is not { } minutes)
            return options.DefaultTtlMinutes;
        if (minutes <= 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"ttl\" must be a positive number of minutes, got {minutes}.", new { argument = "ttl" });
        return minutes;
    }

    /// <summary>The one shape rule binding leaves open: <c>items: {"type": "string"}</c> admits
    /// <c>""</c> and a JSON <c>null</c> element, neither of which is a usable scope root or op
    /// class. A missing argument binds to null and is reported as missing by the caller.</summary>
    private static List<string> RequireNonEmptyEntries(List<string>? values, string name)
    {
        if (values is null)
            return [];
        if (values.Any(string.IsNullOrEmpty))
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"{name}\" must contain only non-empty strings.", new { argument = name });
        return values;
    }

    /// <summary>Fixed-window per-principal rate limit: returns false once a principal has made
    /// <see cref="ElevationOptions.MaxRequestsPerMinute"/> requests inside the current window.
    /// Uses the injected <see cref="TimeProvider"/> so tests drive the window deterministically.</summary>
    private bool TryConsumeRequestSlot(string tokenHashKey)
    {
        var now = timeProvider.GetUtcNow();
        var updated = _requestCounts.AddOrUpdate(
            tokenHashKey,
            _ => (now, 1),
            (_, existing) => now - existing.WindowStart >= _rateWindow ? (now, 1) : (existing.WindowStart, existing.Count + 1));
        return updated.Count <= options.MaxRequestsPerMinute;
    }
}
