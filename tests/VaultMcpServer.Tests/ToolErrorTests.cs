using System.Text.Json;
using VaultMcpServer.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Pins the C# error helper's wire form against its PowerShell counterpart,
/// <c>New-VaultToolError</c> (<c>powershell/VaultAccess/Public/ToolResult.ps1</c>), which builds an
/// <c>[ordered]</c> <c>code</c>/<c>message</c>/<c>data</c> map and emits it with
/// <c>ConvertTo-Json -Compress</c>. The invariant: a caller cannot tell which layer refused, so the
/// two serializations must not drift - no whitespace, same field names, same order, and <c>data</c>
/// absent rather than null when there is none.
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
