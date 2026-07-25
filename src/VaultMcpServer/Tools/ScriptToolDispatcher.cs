using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VaultMcpServer.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tools;

/// <summary>
/// Wires the script tool registry into MCP's `tools/list` and `tools/call` - registered
/// via <c>WithListToolsHandler</c>/<c>WithCallToolHandler</c> rather than compiled
/// `[McpServerTool]` methods, since the tool set is only known by scanning `tools/` at
/// runtime. Enforces op class here: the bearer middleware (see
/// <see cref="BearerGrantMiddleware"/>) only authenticates the caller and scopes the
/// vault; per-tool read/write authorization happens against the grant it attached to
/// <c>HttpContext.Items</c>. Every denial made here - before any tool script runs - is emitted
/// through <see cref="ToolError"/> in the same <c>{code, message, data?}</c> shape a tool script's
/// own error carries, so a caller branches on <c>code</c> whichever layer refused.
///
/// Also tracks the <see cref="McpServer"/> for every session that has made a request, so
/// the `tools/` file watcher (<see cref="ScriptToolsWatcher"/>) can push a
/// `notifications/tools/list_changed` message after a <see cref="ScriptToolRegistry.Refresh"/>.
/// Under the host's stateless HTTP transport a session's <c>McpServer</c> is normally torn
/// down as soon as its one request/response completes, so this is best-effort: a session
/// that has already finished by the time a refresh happens simply won't receive it, and a
/// failed send just drops that (dead) session from the tracked set.
/// </summary>
public sealed class ScriptToolDispatcher(ScriptToolRegistry registry, NativeToolRegistry nativeRegistry, ScriptToolRunner runner, IHttpContextAccessor httpContextAccessor, ServerHealthState healthState, VaultConfig vaultConfig)
{
    private readonly ConcurrentDictionary<McpServer, byte> _sessions = new();

    /// <summary>The script-tool parameter name that targets a vault. Matched case-insensitively -
    /// a schema exposes it verbatim from its PowerShell param() block ("Vault"), the same reason
    /// <see cref="BearerGrantMiddleware"/>'s body-peek looks it up case-insensitively.</summary>
    private const string VaultParameterName = "vault";

    /// <summary>Placeholder <c>inputSchema</c> for a synthesized broken-script listing entry - it takes no real parameters since the script never loaded far enough to have any.</summary>
    private static readonly JsonElement EmptyObjectSchema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>(),
    });

    // Constructed directly and registered before builder.Build() (see the
    // httpContextAccessor comment in Program.cs), so no ILogger is resolvable yet at
    // construction time. Program.cs calls AttachLogger once the service provider exists.
    private ILogger<ScriptToolDispatcher> _logger = NullLogger<ScriptToolDispatcher>.Instance;

    public void AttachLogger(ILogger<ScriptToolDispatcher> logger) => _logger = logger;

    /// <summary>Number of times <see cref="NotifyToolsListChangedAsync"/> has run - lets tests confirm the <c>tools/</c> watcher calls it after a refresh, independent of whether any session was actually reachable to receive it.</summary>
    public int NotifyCount { get; private set; }

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        TrackSession(context.Server);

        // A script that fails to load produces zero ScriptTool instances - there is nothing
        // to attach a "BROKEN:" prefix to. Instead, synthesize a listing entry per current
        // ScriptToolLoader.HealthSource problem, so a broken script still shows up (as
        // visibly broken) rather than silently vanishing from tools/list. Vault-related
        // problems (source "VaultConfig") are deliberately NOT reflected here: a ScriptTool
        // never declares a fixed vault (it takes one as a call-time argument), so there is no
        // clean, non-heuristic way to attribute a vault problem to a specific tool listing.
        var brokenScriptTools = healthState.Problems
            .Where(p => p.Source == ScriptToolLoader.HealthSource && p.SubjectPath is not null)
            .Select(p => new Tool { Name = Path.GetFileNameWithoutExtension(p.SubjectPath!), Description = $"BROKEN: {p.Detail}", InputSchema = EmptyObjectSchema });

        var tools = nativeRegistry.Tools
            .Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema })
            .Concat(registry.Tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.BuildInputSchema() }))
            .Concat(brokenScriptTools)
            .ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        TrackSession(context.Server);
        var name = context.Params?.Name ?? "";

        // Native tools are checked first (a small, fixed set); a name collision with a script
        // tool would be surprising either way, but native wins since it's compiled/reviewed
        // rather than dropped into tools/ at runtime.
        var nativeTool = nativeRegistry.Tools.FirstOrDefault(t => t.Name == name);
        if (nativeTool is not null)
        {
            var nativeGrant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
            if (nativeGrant is null)
                return NoGrantError(name);

            // Two native tools are gated by something other than an op class, so they are
            // special-cased past the usual grant.Allows(tool.OpClass) check by name:
            // health_check is diagnostics (a right of connection for any authenticated grant),
            // and request_elevation is gated by the caller's per-grant canRequestElevation opt-in,
            // enforced inside the tool itself (which returns an informative error when it is not
            // set). Both declare OpClass "" so they could never satisfy an op-class check anyway.
            var isOpClassExempt = nativeTool.Name is HealthCheckTool.ToolName or ElevationRequestTool.ToolName;
            if (!isOpClassExempt && !nativeGrant.Allows(nativeTool.OpClass))
                return OpClassError(name, nativeTool.OpClass);

            var nativeArguments = context.Params?.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value)
                ?? new Dictionary<string, JsonElement>();
            return await nativeTool.InvokeAsync(nativeArguments, nativeGrant, cancellationToken);
        }

        var tool = registry.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return ToolError.Result("not_found", $"Unknown tool: \"{name}\".", new { tool = name });

        var grant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
        if (grant is null)
            return NoGrantError(name);
        if (!grant.Allows(tool.OpClass))
            return OpClassError(name, tool.OpClass);

        // Authoritative vault-membership enforcement, alongside the op-class check above and off
        // the same deserialized arguments the runner will use. The middleware's body-peek (see
        // BearerGrantMiddleware) already rejects an out-of-scope named vault early with a 403, but
        // that peek is best-effort: it can be skipped by an omitted vault arg (which then resolves
        // to the default vault), a non-first batch element, or a chunked/malformed body it could
        // not parse. Re-checking here - the effective vault is the call's vault argument when
        // present, else the configured default vault - is the one that cannot be bypassed. Only
        // tools that take a Vault parameter target a vault; native tools returned above, and a
        // script tool with no Vault parameter is out of scope of this check.
        if (ToolTakesVaultParameter(tool))
        {
            var effectiveVault = ResolveEffectiveVault(context.Params?.Arguments);
            if (!grant.CanAccessVault(effectiveVault))
                return ToolError.Result(
                    "out_of_scope",
                    $"Grant \"{grant.Id}\" is not scoped to vault \"{effectiveVault}\".",
                    // data carries only the call's own coordinates - the tool, and the vault the
                    // call effectively targeted (which the caller may not have named, so it is the
                    // one thing here it cannot already know). The grant's vault set, op classes, and
                    // id stay out: a denial must not become an enumeration surface for authority the
                    // caller does not hold. Same posture as the middleware's detail-free 401.
                    new { tool = name, vault = effectiveVault });
        }

        var arguments = ConvertArguments(context.Params?.Arguments);
        var execution = await runner.InvokeAsync(tool.ScriptPath, arguments, cancellationToken, grant.ScopeRoots, grant.OpClasses);
        // A script's own error text is surfaced verbatim, never re-wrapped: a tool script that threw
        // a New-VaultToolError already carries the {code, message, data?} shape, and re-shaping here
        // would either double-encode it or overwrite its code with a dispatcher-chosen one.
        return execution.HadErrors
            ? ErrorResult(execution.ErrorText)
            : new CallToolResult { Content = [new TextContentBlock { Text = execution.OutputText }] };
    }

    /// <summary>
    /// The dispatcher's op-class (floor) denial. Uses the same <c>insufficient_capability</c> code
    /// as the in-script key assertion (<c>Assert-VaultOpClass</c>), because it is the same condition
    /// at a different layer - a caller retrying after elevation must not have to know whether the
    /// floor or a key refused. <c>data</c> names the required class (a property of the tool, and the
    /// class an elevation request would ask for) but never the classes the grant actually holds.
    /// </summary>
    private static CallToolResult OpClassError(string toolName, string opClass) => ToolError.Result(
        "insufficient_capability",
        $"Grant does not permit {opClass} operations for tool \"{toolName}\".",
        new { tool = toolName, requiredOpClass = opClass });

    /// <summary>
    /// Defensive branch: no grant on <c>HttpContext.Items</c>, meaning the request did not pass
    /// through <see cref="BearerGrantMiddleware"/> (which always attaches one, down to the
    /// minimum-access floor). Coded as a capability denial because the effect is identical - the
    /// call holds no authority - and a caller should not have to distinguish "no grant" from
    /// "grant lacking the class" to route the failure.
    /// </summary>
    private static CallToolResult NoGrantError(string toolName) => ToolError.Result(
        "insufficient_capability",
        $"No grant is attached to this request, so tool \"{toolName}\" cannot be dispatched.",
        new { tool = toolName });

    /// <summary>Whether <paramref name="tool"/> declares a vault-targeting parameter (see
    /// <see cref="VaultParameterName"/>) - the tools this dispatcher's membership check applies to.</summary>
    private static bool ToolTakesVaultParameter(ScriptTool tool) =>
        tool.Parameters.Any(p => string.Equals(p.Name, VaultParameterName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The vault a call effectively targets: the string <c>vault</c> argument when present
    /// (looked up case-insensitively, since the schema casing comes from the param() block), else
    /// the configured default vault. A blank value is treated as absent, matching
    /// <see cref="VaultConfig.Resolve"/>'s own null/blank-to-default handling.
    /// </summary>
    private string ResolveEffectiveVault(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                if (string.Equals(key, VaultParameterName, StringComparison.OrdinalIgnoreCase)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } named)
                {
                    return named;
                }
            }
        }
        return vaultConfig.DefaultVault;
    }

    private static IReadOnlyDictionary<string, object?> ConvertArguments(IDictionary<string, JsonElement>? arguments)
    {
        var result = new Dictionary<string, object?>();
        if (arguments is null)
            return result;
        foreach (var (key, element) in arguments)
            result[key] = ConvertJsonElement(element);
        return result;
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
        _ => null,
    };

    /// <summary>Passes an already-formed error text through as an <c>isError</c> result. Used only for
    /// a script's own thrown text, which must not be re-shaped - every denial the dispatcher itself
    /// makes goes through <see cref="ToolError"/> instead.</summary>
    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private void TrackSession(McpServer? server)
    {
        if (server is not null)
            _sessions[server] = 0;
    }

    /// <summary>
    /// Broadcasts `notifications/tools/list_changed` to every session tracked so far.
    /// Called by <see cref="ScriptToolsWatcher"/> after <see cref="ScriptToolRegistry.Refresh"/>.
    /// A session whose send fails (its request/response has already completed under the
    /// stateless transport) is dropped from the tracked set rather than retried.
    /// </summary>
    public async Task NotifyToolsListChangedAsync(CancellationToken cancellationToken)
    {
        NotifyCount++;
        foreach (var server in _sessions.Keys)
        {
            try
            {
                await server.SendMessageAsync(
                    new JsonRpcNotification { Method = NotificationMethods.ToolListChangedNotification },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dropping a tracked session that could not be notified of a tools list change.");
                _sessions.TryRemove(server, out _);
            }
        }
    }
}
