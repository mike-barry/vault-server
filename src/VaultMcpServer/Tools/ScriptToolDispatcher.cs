using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
/// </summary>
public sealed class ScriptToolDispatcher(ScriptToolRegistry registry, ScriptToolRunner runner, IHttpContextAccessor httpContextAccessor)
{
    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        var tools = registry.Tools
            .Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.BuildInputSchema() })
            .ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var name = context.Params?.Name ?? "";
        var tool = registry.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return ErrorResult($"Unknown tool: \"{name}\".");

        var grant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
        if (grant is null || !grant.Allows(tool.OpClass))
            return ErrorResult($"Grant does not permit {tool.OpClass} operations for tool \"{name}\".");

        var arguments = ConvertArguments(context.Params?.Arguments);
        var execution = await runner.InvokeAsync(tool.ScriptPath, arguments, cancellationToken);
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
}
