using System.Text.Json;
using VaultMcpServer.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Pins MCP's wrapping of the refusal envelope: the JSON is the entire text block of an
/// <c>isError</c> result, so nothing downstream has to unwrap a prose prefix to reach the code. The
/// invariant it protects is that a caller cannot tell which layer refused, so a refusal the
/// dispatcher makes and one a tool produces for itself must arrive identically.
///
/// The envelope's own form - compactness, field order, <c>data</c>'s absence rather than nullness -
/// is <c>ToolRefusalEnvelope</c>'s, and pinned in <c>VaultShared.Tests</c>, because both front-ends
/// emit it. The cases here that assert it do so through this wrapper, which is the one thing that
/// would notice a helper wired to something other than the shared writer.
/// </summary>
public sealed class ToolErrorTests
{
    [Fact]
    public void Json_WithoutData_IsCompactAndOmitsTheDataFieldEntirely()
    {
        var json = ToolError.Json("insufficient_capability", "Grant does not permit content:write operations.");

        Assert.Equal(
            """{"code":"insufficient_capability","message":"Grant does not permit content:write operations."}""",
            json);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void Json_WithData_EmitsCodeThenMessageThenData()
    {
        var json = ToolError.Json("out_of_scope", "Not scoped.", new { tool = "read", vault = "test" });

        Assert.Equal("""{"code":"out_of_scope","message":"Not scoped.","data":{"tool":"read","vault":"test"}}""", json);
        // Field order is part of the parity, not incidental - assert it independently of the
        // literal above so a serializer change that reorders is caught by name.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["code", "message", "data"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Result_IsAnErrorWhoseWholeTextContentIsTheErrorJson()
    {
        // The JSON is the entire text block - the same thing a tool script's thrown error text is -
        // so nothing downstream has to unwrap a prose prefix to reach the code.
        var result = ToolError.Result("not_found", "Unknown tool: \"nope\".", new { tool = "nope" });

        Assert.True(result.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("nope", doc.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }
}
