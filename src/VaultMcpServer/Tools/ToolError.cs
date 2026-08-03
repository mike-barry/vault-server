using ModelContextProtocol.Protocol;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>
/// MCP's rendering of the shared <c>{code, message, data?}</c> refusal envelope: the JSON is the
/// whole text content block of an <c>isError: true</c> result.
///
/// The envelope itself - its field order, its compactness, and <c>data</c>'s absence rather than
/// nullness - is <see cref="ToolRefusalEnvelope"/>'s, because both front-ends emit it. What stays
/// here is only the wrapping, which is MCP's alone.
///
/// The invariant that wrapping protects: a caller branches on <c>code</c> regardless of which layer
/// refused. A tool script's own refusal reaches the client as the JSON string it threw, surfaced
/// verbatim by <see cref="ScriptToolDispatcher"/>; a refusal made before any script runs must be
/// indistinguishable from it, or a consumer is forced back to matching message prose for exactly the
/// denials it is most likely to hit.
/// </summary>
public static class ToolError
{
    /// <inheritdoc cref="ToolRefusalEnvelope.Json(string, string, object?)"/>
    public static string Json(string code, string message, object? data = null) =>
        ToolRefusalEnvelope.Json(code, message, data);

    /// <summary>Wraps the envelope in the <c>isError: true</c> tool result the dispatcher returns.</summary>
    public static CallToolResult Result(string code, string message, object? data = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = Json(code, message, data) }],
    };

    /// <inheritdoc cref="Result(string, string, object?)"/>
    public static CallToolResult Result(ToolRefusal refusal) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ToolRefusalEnvelope.Json(refusal) }],
    };
}
