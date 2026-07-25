using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using VaultMcpServer.Auth;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tools;

/// <summary>Config for <see cref="ElevationRequestTool"/>: the default rebind TTL and the
/// per-principal request rate limit (a fixed window).</summary>
public sealed record ElevationOptions(int DefaultTtlMinutes = 15, int MaxRequestsPerMinute = 5);

/// <summary>
/// Native tool (<see cref="INativeTool"/>) that requests a TTL'd elevation rebind of the caller's
/// token to a different grant - a token-less named grant, or a freshly-minted scoped grant - via
/// <see cref="GrantStore"/>'s binding table. Mirrors <see cref="MintTokenTool"/>'s shape but is
/// gated by a per-grant opt-in (<see cref="Grant.CanRequestElevation"/>), not an op class: like
/// <c>health_check</c> it is special-cased past the dispatcher's op-class check by name, then
/// enforces the opt-in itself so unauthorized callers get an informative tool error (the
/// static-tool-listing / informative-denials convention) rather than the tool vanishing.
///
/// Flow: verify the caller may request elevation, rate-limit per principal, resolve the requested
/// grant, ask the pluggable <see cref="IElevationApprover"/> (shipped as an auto-deny stub), and
/// on approval rebind with a TTL. On a dev machine the default binding is already admin, so this
/// is off the daily path; it matters where the default is minimal.
///
/// Every refusal is a coded <see cref="ToolError"/> result in the shared <c>{code, message, data?}</c>
/// shape, so a caller branches on <c>code</c> whichever tier refused. The codes:
/// <c>insufficient_capability</c> for both authority denials - the missing <c>canRequestElevation</c>
/// opt-in and the approver's "no" - deliberately sharing one code so a caller cannot tell "not allowed
/// to ask" from "asked and refused"; <c>rate_limited</c> for the throttle (this tool's one
/// tool-specific code - no core code names a transient throttle, and coding it as capability or
/// argument would route a retryable condition into a non-retryable branch); <c>invalid_argument</c>
/// for malformed or contradictory target arguments; <c>not_found</c> for a named target that is not a
/// configured grant; <c>unknown_vault</c>/<c>vault_unavailable</c> for a scoped target's vault, the
/// same classification <c>Resolve-VaultToolRoot</c> makes PowerShell-side.
///
/// This tool is op-class exempt, so an unauthenticated caller on the minimum-access floor reaches
/// every one of those paths. <c>data</c> therefore carries only what the caller itself supplied (the
/// argument name, its own <c>grant</c> id echo) plus the configured rate limit, and never the caller's
/// grant id, op classes, vault set, or scope roots.
/// </summary>
public sealed class ElevationRequestTool(
    GrantStore grantStore,
    IElevationApprover approver,
    IHttpContextAccessor httpContextAccessor,
    OpClassRegistry opClassRegistry,
    VaultConfig vaultConfig,
    ElevationOptions options,
    TimeProvider timeProvider) : INativeTool
{
    /// <summary>The fixed tool name <see cref="ScriptToolDispatcher"/> matches on to bypass the
    /// op-class check (this tool is gated by the per-grant opt-in instead).</summary>
    public const string ToolName = "request_elevation";

    private readonly TimeSpan _rateWindow = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset WindowStart, int Count)> _requestCounts = new();

    public string Name => ToolName;

    public string Description =>
        "Requests a time-limited elevation of the calling session to a different grant - a named " +
        "grant, or a freshly-minted scoped grant - subject to approval. Requires a grant that opts " +
        "into elevation. Requests are rate-limited and, in the default configuration, auto-denied.";

    // Never consulted: the dispatcher special-cases this tool past the grant.Allows(OpClass)
    // check by name (like health_check), because elevation is gated by the per-grant
    // canRequestElevation opt-in rather than an op class. Left blank so it can never be mistaken
    // for an enforced value - an empty string never matches Grant.Allows's op-class set.
    public string OpClass => "";

    public JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["grant"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Id of a named grant to elevate to (e.g. \"admin\"). Provide this OR scope_roots+op_class, not both.",
            },
            ["scope_roots"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                ["description"] = "Folder-prefix scope roots for a freshly-minted scoped target, e.g. \"primary/Projects/Foo\". Provide with op_class instead of grant.",
            },
            ["op_class"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                ["description"] = "Explicit op classes for a freshly-minted scoped target, e.g. [\"Read\"]. Provide with scope_roots.",
            },
            ["ttl"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Minutes the elevation lasts before reverting to the base binding. Optional; defaults to the server's configured elevation TTL.",
            },
            ["session_context"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional free-text context about why elevation is needed, shown to a human approver.",
            },
        },
    });

    public async Task<CallToolResult> InvokeAsync(IReadOnlyDictionary<string, JsonElement> arguments, Grant grant, CancellationToken cancellationToken)
    {
        // Defensive: BearerGrantMiddleware stashes this on every request it lets through, so reaching
        // here means the middleware did not run. dependency_unavailable rather than a capability or
        // argument code - nothing about the caller's authority or its arguments would change the
        // outcome, and the condition is the server's, not the call's.
        if (httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.PrincipalTokenHashItemKey] is not string tokenHashKey)
            return ToolError.Result("dependency_unavailable", "Elevation is unavailable: the request principal could not be identified.");

        // Deny by default: only a grant that opts in may request elevation (minted grants never
        // do, so an automated subagent cannot self-escalate). The opt-in stands in for an op class
        // on this tool, so the shortfall carries the op-class shortfall's code.
        if (!grant.CanRequestElevation)
            return ToolError.Result(
                "insufficient_capability",
                $"Grant \"{grant.Id}\" is not permitted to request elevation - requires a grant with canRequestElevation enabled.");

        // Consumed before the target is resolved, so the throttle also bounds how fast a caller can
        // probe which named grants exist through the not_found below.
        if (!TryConsumeRequestSlot(tokenHashKey))
            return ToolError.Result(
                "rate_limited",
                $"Elevation requests are rate-limited to {options.MaxRequestsPerMinute} per minute for this session - try again shortly.",
                new { limitPerMinute = options.MaxRequestsPerMinute });

        string requestedGrantId;
        Grant requestedGrant;
        bool mintTarget;
        IReadOnlySet<string> mintOpClasses = new HashSet<string>();
        IReadOnlySet<string> mintVaults = new HashSet<string>();
        IReadOnlyDictionary<string, IReadOnlySet<string>> mintScopeRoots = new Dictionary<string, IReadOnlySet<string>>();
        int ttlMinutes;
        string? sessionContext;
        try
        {
            (requestedGrantId, requestedGrant, mintTarget, mintOpClasses, mintVaults, mintScopeRoots) = ResolveTarget(arguments);
            ttlMinutes = ReadTtl(arguments);
            sessionContext = ReadSessionContext(arguments);
        }
        catch (ElevationArgumentException ex)
        {
            return ToolError.Result(ex.Code, ex.Message, ex.ErrorData);
        }

        var request = new ElevationRequest(grant.Id, grant, requestedGrantId, requestedGrant, ttlMinutes, sessionContext);
        var decision = await approver.ReviewAsync(request, cancellationToken);
        // Same code as the opt-in denial above: both mean the caller does not have this authority and
        // neither is fixable by changing the arguments. data stays empty - the approver's reason is
        // human-readable prose in the message, and the minted-target id is server-generated.
        if (!decision.Approved)
            return ToolError.Result("insufficient_capability", $"Elevation to \"{requestedGrantId}\" denied: {decision.Reason}");

        var expiresAt = timeProvider.GetUtcNow().AddMinutes(ttlMinutes);
        if (mintTarget)
            grantStore.RebindToMintedGrant(tokenHashKey, requestedGrantId, mintOpClasses, mintVaults, mintScopeRoots, expiresAt);
        else
            grantStore.RebindToNamedGrant(tokenHashKey, requestedGrantId, expiresAt);

        var responseJson = JsonSerializer.Serialize(new
        {
            elevated = true,
            grant = requestedGrantId,
            fromGrant = grant.Id,
            ttlMinutes,
            expiresAtUtc = expiresAt,
        });
        return new CallToolResult { Content = [new TextContentBlock { Text = responseJson }] };
    }

    /// <summary>
    /// Resolves the elevation target from the arguments: either a named grant (the <c>grant</c>
    /// argument) or a freshly-minted scoped grant (<c>scope_roots</c> + <c>op_class</c>). Exactly
    /// one form must be supplied.
    /// </summary>
    private (string Id, Grant Grant, bool Mint, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, IReadOnlyDictionary<string, IReadOnlySet<string>> ScopeRoots) ResolveTarget(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var hasNamed = arguments.TryGetValue("grant", out var grantProp) && grantProp.ValueKind == JsonValueKind.String && grantProp.GetString() is { Length: > 0 };
        var hasScoped = arguments.ContainsKey("scope_roots") || arguments.ContainsKey("op_class");

        // No single argument is at fault in either form-selection failure, so neither carries an
        // "argument" datum - the message names the whole rule.
        if (hasNamed && hasScoped)
            throw new ElevationArgumentException("invalid_argument", "Provide either \"grant\" (a named grant) or \"scope_roots\"+\"op_class\" (a scoped grant), not both.");
        if (!hasNamed && !hasScoped)
            throw new ElevationArgumentException("invalid_argument", "Provide a \"grant\" (named grant id) or a \"scope_roots\"+\"op_class\" pair to elevate to.");

        if (hasNamed)
        {
            var id = grantProp.GetString()!;
            // not_found preserves the distinction this tool's prose already drew between an
            // unconfigured target and a refused one; data echoes only the id the caller passed.
            if (!grantStore.TryGetNamedGrant(id, out var named))
                throw new ElevationArgumentException("not_found", $"Unknown named grant \"{id}\" - not a configured elevation target.", new { argument = "grant", grant = id });
            var empty = (IReadOnlySet<string>)new HashSet<string>();
            return (id, named, false, empty, empty, new Dictionary<string, IReadOnlySet<string>>());
        }

        var (opClasses, vaults, scopeRootsByVault) = ParseScopedTarget(arguments);
        var mintedId = $"elevated-{Guid.NewGuid():N}";
        // A representative grant object for the approver to review; the real one is created at
        // rebind time (RebindToMintedGrant) so nothing is registered unless approved.
        var preview = new Grant(mintedId, opClasses, vaults, timeProvider.GetUtcNow(), scopeRootsByVault);
        return (mintedId, preview, true, opClasses, vaults, scopeRootsByVault);
    }

    /// <summary>
    /// Parses the freshly-minted scoped target's <c>scope_roots</c>/<c>op_class</c> into concrete
    /// sets, applying the same shape rules as <see cref="MintTokenTool"/>: vault-prefixed scope
    /// roots grouped per vault (a whole-vault entry becomes an empty set), each subtree
    /// canonicalized via <see cref="VaultAccess.NormalizeScopeSegments"/>, and explicit op classes
    /// only (no <c>"*"</c>). Attenuation against the caller's own grant is a separate, later
    /// checklist item and is not enforced here.
    /// </summary>
    private (IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, IReadOnlyDictionary<string, IReadOnlySet<string>> ScopeRoots) ParseScopedTarget(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var scopeRoots = ReadStringArray(arguments, "scope_roots");
        if (scopeRoots.Count == 0)
            throw new ElevationArgumentException("invalid_argument", "\"scope_roots\" is required for a scoped elevation target and must contain at least one entry.", new { argument = "scope_roots" });
        if (scopeRoots.Any(v => v == "*"))
            throw new ElevationArgumentException("invalid_argument", "\"scope_roots\" does not accept \"*\" - list explicit folder-prefix roots.", new { argument = "scope_roots" });

        var opClassValues = ReadStringArray(arguments, "op_class");
        if (opClassValues.Count == 0)
            throw new ElevationArgumentException("invalid_argument", "\"op_class\" is required for a scoped elevation target and must contain at least one entry.", new { argument = "op_class" });
        if (opClassValues.Any(v => v == "*"))
            throw new ElevationArgumentException("invalid_argument", "\"op_class\" does not accept \"*\" - list explicit op classes.", new { argument = "op_class" });

        var opClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in opClassValues)
            opClasses.Add(opClassRegistry.Canonicalize(value)
                ?? throw new ElevationArgumentException("invalid_argument", $"\"op_class\" entry \"{value}\" is not a known op class.", new { argument = "op_class", opClass = value }));

        var byVault = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var wholeVault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scopeRoots)
        {
            var parts = root.Split('/', 2);
            var vaultName = parts[0];
            // Classified by which config map holds the name, matching Resolve-VaultToolRoot's split.
            if (vaultConfig.SkippedVaults.ContainsKey(vaultName))
                throw new ElevationArgumentException(
                    "vault_unavailable",
                    $"\"scope_roots\" entry \"{root}\" names vault \"{vaultName}\", which is configured but currently unavailable.",
                    new { argument = "scope_roots", scopeRoot = root, vault = vaultName });
            if (!vaultConfig.Vaults.ContainsKey(vaultName))
                throw new ElevationArgumentException(
                    "unknown_vault",
                    $"\"scope_roots\" entry \"{root}\" names an unknown vault \"{vaultName}\".",
                    new { argument = "scope_roots", scopeRoot = root, vault = vaultName });

            if (!byVault.TryGetValue(vaultName, out var subtrees))
                byVault[vaultName] = subtrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                try
                {
                    subtrees.Add(string.Join('/', VaultAccess.NormalizeScopeSegments(parts[1])));
                }
                catch (ArgumentException ex)
                {
                    throw new ElevationArgumentException(
                        "invalid_argument",
                        $"\"scope_roots\" entry \"{root}\" has an invalid subtree \"{parts[1]}\": {ex.Message}",
                        new { argument = "scope_roots", scopeRoot = root });
                }
            }
            else
                wholeVault.Add(vaultName);
        }
        foreach (var vaultName in wholeVault)
            byVault[vaultName].Clear();

        var vaults = new HashSet<string>(byVault.Keys, StringComparer.OrdinalIgnoreCase);
        var scopeRootsByVault = byVault.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
        return (opClasses, vaults, scopeRootsByVault);
    }

    private int ReadTtl(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (!arguments.TryGetValue("ttl", out var prop))
            return options.DefaultTtlMinutes;
        if (prop.ValueKind == JsonValueKind.String)
            throw new ElevationArgumentException("invalid_argument", "\"ttl\" does not accept \"*\" - pass an integer number of minutes, or omit it for the default.", new { argument = "ttl" });
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out var minutes))
            throw new ElevationArgumentException("invalid_argument", "\"ttl\" must be an integer number of minutes.", new { argument = "ttl" });
        if (minutes <= 0)
            throw new ElevationArgumentException("invalid_argument", $"\"ttl\" must be a positive number of minutes, got {minutes}.", new { argument = "ttl" });
        return minutes;
    }

    private static string? ReadSessionContext(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments.TryGetValue("session_context", out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static List<string> ReadStringArray(IReadOnlyDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var prop))
            return [];
        if (prop.ValueKind != JsonValueKind.Array)
            throw new ElevationArgumentException("invalid_argument", $"\"{name}\" must be an array of strings.", new { argument = name });
        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } value)
                throw new ElevationArgumentException("invalid_argument", $"\"{name}\" must contain only non-empty strings.", new { argument = name });
            result.Add(value);
        }
        return result;
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

    /// <summary>
    /// Internal control-flow exception for a refused elevation request - caught in
    /// <see cref="InvokeAsync"/> and turned into a coded <see cref="ToolError"/> result, never
    /// propagated. Carries the <see cref="Code"/> and <see cref="ErrorData"/> alongside the message so
    /// the refusal keeps the shared <c>{code, message, data?}</c> shape rather than reaching the client
    /// as prose. <see cref="ErrorData"/> is named apart from <see cref="Exception.Data"/>, which is an
    /// unrelated <see cref="System.Collections.IDictionary"/> on the base type.
    /// </summary>
    private sealed class ElevationArgumentException(string code, string message, object? data = null) : Exception(message)
    {
        public string Code { get; } = code;

        public object? ErrorData { get; } = data;
    }
}
