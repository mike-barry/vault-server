using System.Collections.Concurrent;
using System.ComponentModel;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>Represents the configuration for <see cref="ElevationRequestTool"/>.</summary>
/// <param name="DefaultTtlMinutes">The rebind lifetime used when a request names no TTL.</param>
/// <param name="MaxRequestsPerMinute">The per-principal request limit within a fixed one-minute
/// window.</param>
public sealed record ElevationOptions(int DefaultTtlMinutes = 15, int MaxRequestsPerMinute = 5);

/// <summary>
/// Represents <see cref="ElevationRequestTool"/>'s arguments. This record is both the
/// deserialization target and the source of the tool's advertised <c>inputSchema</c> (see
/// <see cref="NativeToolArguments"/>).
/// </summary>
/// <remarks>
/// Every member is optional, so nothing is <c>required</c> in the derived schema. The rule that a
/// request names either <c>grant</c> or <c>scope_roots</c> and <c>op_class</c> but not both is a
/// cross-argument constraint that JSON Schema's <c>required</c> cannot state, so the tool enforces
/// it itself.
///
/// <see cref="Validate"/> returns null for the same reason as
/// <see cref="MintTokenArguments.Validate"/>: the form-selection rule blames no single argument,
/// so it cannot be expressed as an <see cref="ArgumentFault"/> without inventing an offender, and
/// every other rule here needs the op-class registry, the vault configuration, or the caller's own
/// grant.
/// </remarks>
/// <param name="Grant">The id of a named grant to elevate to.</param>
/// <param name="ScopeRoots">The vault-prefixed scope roots for a freshly-minted target.</param>
/// <param name="OpClass">The op classes for a freshly-minted target.</param>
/// <param name="Ttl">The elevation's lifetime in minutes, or null to use the configured
/// default.</param>
/// <param name="SessionContext">Free-text context shown to a human approver.</param>
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
    /// <summary>Performs no validation; see this record's remarks.</summary>
    /// <returns>Always null.</returns>
    public ArgumentFault? Validate() => null;
}

/// <summary>
/// Requests a time-limited rebind of the caller's token to a different grant - either a named
/// grant that has no token of its own, or a freshly-minted scoped grant - through
/// <see cref="GrantStore"/>'s binding table.
/// </summary>
/// <remarks>
/// <para>
/// The tool mirrors <see cref="MintTokenTool"/>'s shape but is gated by the per-grant
/// <see cref="Grant.CanRequestElevation"/> opt-in rather than by an op class, so its declaration
/// is <see cref="Floor.SelfGated"/>. The gate is inside the tool, so an unauthorized caller gets
/// an informative refusal instead of finding that the tool has disappeared.
/// </para>
/// <para>
/// The declaration sets <see cref="ToolDeclaration.RequiresClientSession"/>, because the rebind is
/// recorded against the session's principal key.
/// </para>
/// <para>
/// An invocation verifies that the caller may request elevation, applies the per-principal rate
/// limit, resolves the requested grant, asks the configured <see cref="IElevationApprover"/>, and
/// on approval rebinds with a TTL. On a machine whose default binding is already broad this is
/// off the daily path; it matters where the default binding is minimal.
/// </para>
/// <para>
/// Both authority denials - the missing <c>canRequestElevation</c> opt-in and the approver's
/// refusal - share the <c>authority_required</c> code, so a caller cannot tell "not allowed to
/// ask" from "asked and refused". The throttle is <c>rate_limited</c> rather than a capability or
/// argument code, so a retryable condition is not routed into a branch that never retries.
/// Malformed or contradictory target arguments are <c>invalid_argument</c>, a named target that
/// is not a configured grant is <c>not_found</c>, and a scoped target's vault is
/// <c>unknown_vault</c> or <c>vault_unavailable</c> according to which config map holds the name.
/// </para>
/// <para>
/// Because the tool is self-gated, a caller on the minimum-access floor reaches every one of
/// those paths. The <c>data</c> of a refusal therefore carries only what the caller itself
/// supplied, plus the configured rate limit, and never the caller's grant id, op classes, vault
/// set, or scope roots.
/// </para>
/// <para>
/// The invocation blocks while the approver decides. The tool surface is synchronous because a
/// mutex hold cannot cross a suspension point; this tool takes no lock, so the whole cost of that
/// design falls here.
/// </para>
/// </remarks>
/// <param name="grantStore">Resolves the target grant and performs the rebind.</param>
/// <param name="approver">Decides whether the elevation is granted.</param>
/// <param name="opClassRegistry">Validates and canonicalizes a scoped target's op classes.</param>
/// <param name="vaultConfig">Resolves the vaults a scoped target's scope roots name.</param>
/// <param name="options">The default TTL and the per-principal rate limit.</param>
/// <param name="timeProvider">Supplies the clock for the expiry and the rate-limit window.</param>
public sealed class ElevationRequestTool(
    GrantStore grantStore,
    IElevationApprover approver,
    OpClassRegistry opClassRegistry,
    VaultConfig vaultConfig,
    ElevationOptions options,
    TimeProvider timeProvider) : ITool
{
    /// <summary>The tool's name on the wire.</summary>
    public const string ToolName = "request_elevation";

    /// <summary>The declaration the tool registry and the shared execute path work from.</summary>
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

    /// <summary>Represents what an approved elevation reports back.</summary>
    /// <param name="Elevated">Always true; the report is only produced on approval.</param>
    /// <param name="Grant">The id of the grant the token is now bound to.</param>
    /// <param name="FromGrant">The id of the grant the token was bound to before.</param>
    /// <param name="TtlMinutes">The elevation's lifetime in minutes.</param>
    /// <param name="ExpiresAtUtc">When the binding reverts to the base grant.</param>
    public sealed record ElevationReport(bool Elevated, string Grant, string FromGrant, int TtlMinutes, DateTimeOffset ExpiresAtUtc);

    /// <summary>Reviews an elevation request and, on approval, rebinds the caller's token.</summary>
    /// <param name="call">The call, carrying the arguments, the caller's grant, and the session.</param>
    /// <returns>The elevation report for an approved request.</returns>
    /// <exception cref="ToolRefusal">The caller may not request elevation, is rate-limited, named
    /// a malformed or unresolvable target, or the approver denied the request.</exception>
    public ToolResult Invoke(ToolCall call)
    {
        var args = call.ArgumentsAs<ElevationRequestArguments>();
        var grant = call.Grant;
        // The declaration requires a session, and the execute path refuses a call that arrives
        // without one before it reaches here.
        var tokenHashKey = call.Session!.PrincipalKey;

        // Only a grant that opts in may request elevation. Minted grants never do, so an
        // automated subagent cannot escalate itself. The opt-in stands in for an op class on this
        // tool, so its shortfall carries the op-class shortfall's code.
        if (!grant.CanRequestElevation)
            throw new ToolRefusal(
                ToolCodes.AuthorityRequired,
                $"Grant \"{grant.Id}\" is not permitted to request elevation - requires a grant with canRequestElevation enabled.");

        // The rate limit is consumed before the target is resolved, so it also bounds how fast a
        // caller can probe which named grants exist through the not_found refusal below.
        if (!TryConsumeRequestSlot(tokenHashKey))
            throw new ToolRefusal(
                ToolCodes.RateLimited,
                $"Elevation requests are rate-limited to {options.MaxRequestsPerMinute} per minute for this session - try again shortly.",
                new { limitPerMinute = options.MaxRequestsPerMinute });

        var (requestedGrantId, requestedGrant, mintTarget, mintOpClasses, mintVaults, mintScopeRoots) = ResolveTarget(args);
        var ttlMinutes = ReadTtl(args);

        var request = new ElevationRequest(grant.Id, grant, requestedGrantId, requestedGrant, ttlMinutes, args.SessionContext);
        var decision = approver.ReviewAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        // The same code as the opt-in denial above: both mean the caller does not have this
        // authority, and neither is fixable by changing the arguments. There is no data, because
        // the approver's reason is prose in the message and the minted target's id is
        // server-generated.
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
    /// Resolves the elevation target: either a named grant, from the <c>grant</c> argument, or a
    /// freshly-minted scoped grant, from <c>scope_roots</c> and <c>op_class</c>. Exactly one form
    /// must be supplied, which is a cross-argument rule the schema cannot express.
    /// </summary>
    /// <param name="args">The call's arguments.</param>
    /// <returns>The target's id and grant, whether it must be minted, and for a minted target its
    /// op classes, vaults, and per-vault scope roots.</returns>
    /// <exception cref="ToolRefusal">Neither form or both forms were supplied, a named target is
    /// not configured, or a scoped target's arguments are malformed.</exception>
    private (string Id, Grant Grant, bool Mint, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, IReadOnlyDictionary<string, VaultScope> ScopeRoots) ResolveTarget(ElevationRequestArguments args)
    {
        var hasNamed = !string.IsNullOrEmpty(args.Grant);
        var hasScoped = args.ScopeRoots is not null || args.OpClass is not null;

        // No single argument is at fault in either form-selection failure, so neither refusal
        // carries an "argument" datum and the message names the whole rule instead.
        if (hasNamed && hasScoped)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "Provide either \"grant\" (a named grant) or \"scope_roots\"+\"op_class\" (a scoped grant), not both.");
        if (!hasNamed && !hasScoped)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "Provide a \"grant\" (named grant id) or a \"scope_roots\"+\"op_class\" pair to elevate to.");

        if (hasNamed)
        {
            var id = args.Grant!;
            // not_found keeps an unconfigured target distinct from a refused one, and data
            // echoes only the id the caller passed.
            if (!grantStore.TryGetNamedGrant(id, out var named))
                throw new ToolRefusal(ToolCodes.NotFound, $"Unknown named grant \"{id}\" - not a configured elevation target.", new { argument = "grant", grant = id });
            var empty = (IReadOnlySet<string>)new HashSet<string>();
            return (id, named, false, empty, empty, new Dictionary<string, VaultScope>());
        }

        var (opClasses, vaults, scopeRootsByVault) = ParseScopedTarget(args);
        var mintedId = $"elevated-{Guid.NewGuid():N}";
        // A stand-in grant object for the approver to review. The real one is created at rebind
        // time, so nothing is registered unless the request is approved.
        var preview = new Grant(mintedId, opClasses, vaults, timeProvider.GetUtcNow(), scopeRootsByVault);
        return (mintedId, preview, true, opClasses, vaults, scopeRootsByVault);
    }

    /// <summary>
    /// Parses a scoped target's <c>scope_roots</c> and <c>op_class</c> into concrete sets, under
    /// the same shape rules as <see cref="MintTokenTool"/>: scope roots are vault-prefixed and
    /// grouped per vault, a whole-vault entry becomes <see cref="VaultScope.Unscoped"/>, each
    /// subtree is canonicalized through <see cref="ScopePath.Normalize"/>, and op classes must be
    /// explicit rather than <c>"*"</c>. This does not attenuate the target against the caller's
    /// own grant.
    /// </summary>
    /// <param name="args">The call's arguments.</param>
    /// <returns>The target's op classes, vaults, and per-vault scope roots.</returns>
    /// <exception cref="ToolRefusal">An argument is missing, empty, contains <c>"*"</c>, names an
    /// unknown op class, or names an unknown or unavailable vault.</exception>
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
            // The two vault-resolution codes are chosen by which config map holds the name,
            // never by message text.
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

    /// <summary>
    /// Reads the <c>ttl</c> argument. This is a range check, not a type check: binding already
    /// refused anything that is not an integer, but the schema cannot say that the integer must
    /// be positive.
    /// </summary>
    /// <param name="args">The call's arguments.</param>
    /// <returns>The requested lifetime in minutes, or the configured default when <c>ttl</c> was
    /// omitted.</returns>
    /// <exception cref="ToolRefusal">The value is zero or negative.</exception>
    private int ReadTtl(ElevationRequestArguments args)
    {
        if (args.Ttl is not { } minutes)
            return options.DefaultTtlMinutes;
        if (minutes <= 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"ttl\" must be a positive number of minutes, got {minutes}.", new { argument = "ttl" });
        return minutes;
    }

    /// <summary>
    /// Checks the one shape rule binding leaves open: <c>items: {"type": "string"}</c> admits
    /// <c>""</c> and a JSON <c>null</c> element, neither of which is a usable scope root or op
    /// class.
    /// </summary>
    /// <param name="values">The argument's entries, or null when the argument was omitted.</param>
    /// <param name="name">The argument's wire name, used in the refusal.</param>
    /// <returns>The entries, or an empty list when the argument was omitted. The caller reports
    /// the omission itself.</returns>
    /// <exception cref="ToolRefusal">An entry is null or empty.</exception>
    private static List<string> RequireNonEmptyEntries(List<string>? values, string name)
    {
        if (values is null)
            return [];
        if (values.Any(string.IsNullOrEmpty))
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"{name}\" must contain only non-empty strings.", new { argument = name });
        return values;
    }

    /// <summary>
    /// Consumes one slot of the principal's fixed-window rate limit. The injected
    /// <see cref="TimeProvider"/> supplies the clock, so tests can drive the window
    /// deterministically.
    /// </summary>
    /// <param name="tokenHashKey">The principal key the limit is tracked against.</param>
    /// <returns><c>false</c> once the principal has made
    /// <see cref="ElevationOptions.MaxRequestsPerMinute"/> requests inside the current window;
    /// otherwise <c>true</c>.</returns>
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
