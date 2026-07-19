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
/// on approval rebind with a TTL. Denials - opt-in, rate limit, unknown target, or approver -
/// return <c>isError: true</c> naming what was required. On a dev machine the default binding is
/// already admin, so this is off the daily path; it matters where the default is minimal.
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
        if (httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.PrincipalTokenHashItemKey] is not string tokenHashKey)
            return ErrorResult("Elevation is unavailable: the request principal could not be identified.");

        // Deny by default: only a grant that opts in may request elevation (minted grants never
        // do, so an automated subagent cannot self-escalate).
        if (!grant.CanRequestElevation)
            return ErrorResult($"Grant \"{grant.Id}\" is not permitted to request elevation - requires a grant with canRequestElevation enabled.");

        if (!TryConsumeRequestSlot(tokenHashKey))
            return ErrorResult($"Elevation requests are rate-limited to {options.MaxRequestsPerMinute} per minute for this session - try again shortly.");

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
            return ErrorResult(ex.Message);
        }

        var request = new ElevationRequest(grant.Id, grant, requestedGrantId, requestedGrant, ttlMinutes, sessionContext);
        var decision = await approver.ReviewAsync(request, cancellationToken);
        if (!decision.Approved)
            return ErrorResult($"Elevation to \"{requestedGrantId}\" denied: {decision.Reason}");

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

        if (hasNamed && hasScoped)
            throw new ElevationArgumentException("Provide either \"grant\" (a named grant) or \"scope_roots\"+\"op_class\" (a scoped grant), not both.");
        if (!hasNamed && !hasScoped)
            throw new ElevationArgumentException("Provide a \"grant\" (named grant id) or a \"scope_roots\"+\"op_class\" pair to elevate to.");

        if (hasNamed)
        {
            var id = grantProp.GetString()!;
            if (!grantStore.TryGetNamedGrant(id, out var named))
                throw new ElevationArgumentException($"Unknown named grant \"{id}\" - not a configured elevation target.");
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
            throw new ElevationArgumentException("\"scope_roots\" is required for a scoped elevation target and must contain at least one entry.");
        if (scopeRoots.Any(v => v == "*"))
            throw new ElevationArgumentException("\"scope_roots\" does not accept \"*\" - list explicit folder-prefix roots.");

        var opClassValues = ReadStringArray(arguments, "op_class");
        if (opClassValues.Count == 0)
            throw new ElevationArgumentException("\"op_class\" is required for a scoped elevation target and must contain at least one entry.");
        if (opClassValues.Any(v => v == "*"))
            throw new ElevationArgumentException("\"op_class\" does not accept \"*\" - list explicit op classes.");

        var opClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in opClassValues)
            opClasses.Add(opClassRegistry.Canonicalize(value)
                ?? throw new ElevationArgumentException($"\"op_class\" entry \"{value}\" is not a known op class."));

        var byVault = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var wholeVault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scopeRoots)
        {
            var parts = root.Split('/', 2);
            var vaultName = parts[0];
            if (vaultConfig.SkippedVaults.ContainsKey(vaultName))
                throw new ElevationArgumentException($"\"scope_roots\" entry \"{root}\" names vault \"{vaultName}\", which is configured but currently unavailable.");
            if (!vaultConfig.Vaults.ContainsKey(vaultName))
                throw new ElevationArgumentException($"\"scope_roots\" entry \"{root}\" names an unknown vault \"{vaultName}\".");

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
                    throw new ElevationArgumentException($"\"scope_roots\" entry \"{root}\" has an invalid subtree \"{parts[1]}\": {ex.Message}");
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
            throw new ElevationArgumentException("\"ttl\" does not accept \"*\" - pass an integer number of minutes, or omit it for the default.");
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out var minutes))
            throw new ElevationArgumentException("\"ttl\" must be an integer number of minutes.");
        if (minutes <= 0)
            throw new ElevationArgumentException($"\"ttl\" must be a positive number of minutes, got {minutes}.");
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
            throw new ElevationArgumentException($"\"{name}\" must be an array of strings.");
        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } value)
                throw new ElevationArgumentException($"\"{name}\" must contain only non-empty strings.");
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

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    /// <summary>Internal control-flow exception for a bad argument - caught in <see cref="InvokeAsync"/> and turned into a normal MCP tool error, never propagated.</summary>
    private sealed class ElevationArgumentException(string message) : Exception(message);
}
