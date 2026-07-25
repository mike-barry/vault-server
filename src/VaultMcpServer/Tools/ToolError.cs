using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace VaultMcpServer.Tools;

/// <summary>
/// Builds the shared <c>{code, message, data?}</c> tool-error JSON - the C# counterpart to the
/// PowerShell module's <c>New-VaultToolError</c> (<c>powershell/VaultAccess/Public/ToolResult.ps1</c>).
///
/// The invariant it protects: a caller branches on <c>code</c> regardless of which layer refused.
/// A tool script's own refusal reaches the client as the JSON string it threw, surfaced verbatim by
/// <see cref="ScriptToolDispatcher"/>; a refusal the dispatcher makes before any script runs must be
/// indistinguishable from it, or a consumer is forced back to matching message prose for exactly the
/// denials it is most likely to hit.
///
/// Serialization mirrors <c>New-VaultToolError</c>'s <c>ConvertTo-Json -Compress</c> over an
/// <c>[ordered]</c> map: no whitespace, <c>code</c> then <c>message</c> then <c>data</c>, and
/// <c>data</c> omitted from the object entirely (not emitted as null) when there is none, so a caller
/// can test for the field's presence rather than its nullness. <see cref="Utf8JsonWriter"/> is used
/// directly rather than serializing an anonymous envelope type, because field order has to be pinned
/// by the writer rather than left to reflection ordering.
/// </summary>
public static class ToolError
{
    /// <summary>Builds the error JSON as a compact string.</summary>
    public static string Json(string code, string message, object? data = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            if (data is not null)
            {
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, data, data.GetType());
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Wraps <see cref="Json"/> in the <c>isError: true</c> tool result the dispatcher returns.
    /// The JSON is the whole text content block, matching how a script tool's thrown error text
    /// reaches the client - one shape on the wire, whichever layer produced it.
    /// </summary>
    public static CallToolResult Result(string code, string message, object? data = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = Json(code, message, data) }],
    };
}
