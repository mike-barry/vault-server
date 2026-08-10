using ModelContextProtocol.Protocol;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>
/// Renders the shared <c>{code, message, data?}</c> refusal envelope as MCP results. The JSON is
/// the whole text content block of an <c>isError: true</c> result.
/// </summary>
/// <remarks>
/// The envelope's own shape - field order, compactness, and <c>data</c> being absent rather than
/// null - belongs to <see cref="ToolRefusalEnvelope"/>, because both front-ends emit it. Only the
/// MCP wrapping is here.
///
/// A tool script's own refusal reaches the client as the JSON string it threw, surfaced verbatim
/// by <see cref="ScriptToolDispatcher"/>, so a refusal made before any script runs is wrapped the
/// same way and a caller can branch on <c>code</c> whichever layer refused.
/// </remarks>
public static class ToolError
{
    /// <inheritdoc cref="ToolRefusalEnvelope.Json(string, string, object?)"/>
    public static string Json(string code, string message, object? data = null) =>
        ToolRefusalEnvelope.Json(code, message, data);

    /// <summary>Builds the <c>isError: true</c> tool result carrying a refusal envelope.</summary>
    /// <param name="code">The refusal code the caller branches on.</param>
    /// <param name="message">The human-readable refusal message.</param>
    /// <param name="data">Optional structured detail about the refusal, omitted when null.</param>
    /// <returns>An error result whose single text block is the refusal envelope's JSON.</returns>
    public static CallToolResult Result(string code, string message, object? data = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = Json(code, message, data) }],
    };

    /// <summary>Builds the <c>isError: true</c> tool result for an already-formed refusal.</summary>
    /// <param name="refusal">The refusal to render.</param>
    /// <returns>An error result whose single text block is the refusal envelope's JSON.</returns>
    public static CallToolResult Result(ToolRefusal refusal) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ToolRefusalEnvelope.Json(refusal) }],
    };
}
