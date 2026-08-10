using System.Text.Json;
using VaultMcpServer.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers how MCP wraps the refusal envelope. The JSON is the entire text block of an
/// <c>isError</c> result, so nothing downstream has to strip a prose prefix to reach the code. A
/// refusal the dispatcher makes and one a tool produces for itself must arrive identically, so a
/// caller cannot tell which layer refused.
/// </summary>
/// <remarks>
/// The envelope's own form - compactness, field order, and <c>data</c> being absent rather than
/// null - belongs to <c>ToolRefusalEnvelope</c> and is pinned in <c>VaultShared.Tests</c>, since
/// both front-ends emit it. The tests here assert it through this wrapper, which is what would
/// notice a helper wired to something other than the shared writer.
/// </remarks>
public sealed class ToolErrorTests
{
    [Fact]
    public void Json_WithoutData_IsCompactAndOmitsTheDataFieldEntirely()
    {
        var json = ToolError.Json("authority_required", "Grant does not permit content:write operations.");

        Assert.Equal(
            """{"code":"authority_required","message":"Grant does not permit content:write operations."}""",
            json);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void Result_IsAnErrorWhoseWholeTextContentIsTheErrorJson()
    {
        var result = ToolError.Result("not_found", "Unknown tool: \"nope\".", new { tool = "nope" });

        Assert.True(result.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("nope", doc.RootElement.GetProperty("data").GetProperty("tool").GetString());
    }
}
