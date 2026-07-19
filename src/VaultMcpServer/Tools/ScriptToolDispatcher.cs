using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VaultMcpServer.Auth;

namespace VaultMcpServer.Tools;

/// <summary>
/// Wires the script tool registry into MCP's `tools/list` and `tools/call` - registered
/// via <c>WithListToolsHandler</c>/<c>WithCallToolHandler</c> rather than compiled
/// `[McpServerTool]` methods, since the tool set is only known by scanning `tools/` at
/// runtime. Enforces op class here: the bearer middleware (see
/// <see cref="BearerGrantMiddleware"/>) only authenticates the caller and scopes the
/// vault; per-tool read/write authorization happens against the grant it attached to
/// <c>HttpContext.Items</c>.
///
/// Also tracks the <see cref="McpServer"/> for every session that has made a request, so
/// the `tools/` file watcher (<see cref="ScriptToolsWatcher"/>) can push a
/// `notifications/tools/list_changed` message after a <see cref="ScriptToolRegistry.Refresh"/>.
/// Under the host's stateless HTTP transport a session's <c>McpServer</c> is normally torn
/// down as soon as its one request/response completes, so this is best-effort: a session
/// that has already finished by the time a refresh happens simply won't receive it, and a
/// failed send just drops that (dead) session from the tracked set.
/// </summary>
public sealed class ScriptToolDispatcher(ScriptToolRegistry registry, NativeToolRegistry nativeRegistry, ScriptToolRunner runner, IHttpContextAccessor httpContextAccessor)
{
    private readonly ConcurrentDictionary<McpServer, byte> _sessions = new();

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
        var tools = nativeRegistry.Tools
            .Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema })
            .Concat(registry.Tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.BuildInputSchema() }))
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
            if (nativeGrant is null || !nativeGrant.Allows(nativeTool.OpClass))
                return ErrorResult($"Grant does not permit {nativeTool.OpClass} operations for tool \"{name}\".");

            var nativeArguments = context.Params?.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value)
                ?? new Dictionary<string, JsonElement>();
            return await nativeTool.InvokeAsync(nativeArguments, nativeGrant, cancellationToken);
        }

        var tool = registry.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return ErrorResult($"Unknown tool: \"{name}\".");

        var grant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
        if (grant is null || !grant.Allows(tool.OpClass))
            return ErrorResult($"Grant does not permit {tool.OpClass} operations for tool \"{name}\".");

        var arguments = ConvertArguments(context.Params?.Arguments);
        var execution = await runner.InvokeAsync(tool.ScriptPath, arguments, cancellationToken, grant.ScopeRoots);
        return execution.HadErrors
            ? ErrorResult(execution.ErrorText)
            : new CallToolResult { Content = [new TextContentBlock { Text = execution.OutputText }] };
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
