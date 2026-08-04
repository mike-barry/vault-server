using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the native <c>mint_token</c> tool over HTTP, along with the native and script tool merge
/// in <see cref="VaultMcpServer.Tools.ScriptToolDispatcher"/> that serves it: that it appears in
/// <c>tools/list</c> beside script tools, that the <c>ManageGrants</c> op class is enforced on it
/// the same way a script tool's declared class is, and that a token it mints authenticates a
/// follow-up call with exactly the access that was granted.
/// </summary>
/// <remarks>
/// This class has its own host fixture, because minting mutates that host's
/// <see cref="VaultMcpServer.Auth.GrantStore"/> in memory and no other test class should inherit
/// the changes.
/// </remarks>
[Collection("Program host tests")]
public sealed class MintTokenDispatchTests : IClassFixture<MintTokenDispatchTests.Fixture>
{
    private readonly Fixture _fixture;

    public MintTokenDispatchTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpClient AuthedClient(Fixture fixture, string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    private static string ExtractJsonRpcPayload(string body)
    {
        if (!body.TrimStart().StartsWith("event:", StringComparison.Ordinal) && !body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return body;

        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}");
        return dataLine["data:".Length..].Trim();
    }

    private static async Task<JsonElement> CallToolAsync(Fixture fixture, string token, string toolCallJson)
    {
        using var client = AuthedClient(fixture, token);
        using var response = await client.PostAsync("/mcp", JsonBody(toolCallJson));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static string ContentText(JsonElement result) =>
        result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;

    /// <summary>
    /// Reads the <c>code</c> from a refusal's <c>{code, message, data?}</c> JSON. Asserting it over
    /// HTTP rather than against the tool object is what shows the dispatcher's native-tool path
    /// passes a native tool's coded error through unchanged instead of re-wrapping it.
    /// </summary>
    /// <param name="result">The <c>result</c> object of a <c>tools/call</c> response.</param>
    /// <returns>The refusal's error code.</returns>
    private static string ErrorCode(JsonElement result)
    {
        using var doc = JsonDocument.Parse(ContentText(result));
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task ToolsList_IncludesMintTokenAlongsideScriptTools()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(body));
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "mint_token");
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "greet");
    }

    [Fact]
    public async Task MintToken_AdminGrant_ReturnsAWorkingTokenScopedToWhatWasRequested()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"],"ttl":5}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var minted = JsonDocument.Parse(ContentText(result));
        var mintedToken = minted.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(mintedToken));

        var readCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike"}}}""";
        var readResult = await CallToolAsync(_fixture, mintedToken!, readCall);
        // greet is a write-class tool, so refusing it shows the mint really scoped to read only.
        Assert.True(readResult.GetProperty("isError").GetBoolean());
        Assert.Equal("authority_required", ErrorCode(readResult));
    }

    [Fact]
    public async Task MintToken_WithoutManageGrants_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        // The dispatcher's grants:manage floor refuses before the tool runs, with the same code the
        // tool's own attenuation check raises, so a caller handles both the same way.
        Assert.Equal("authority_required", ErrorCode(result));
    }

    [Fact]
    public async Task MintToken_MissingOpClass_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("invalid_argument", ErrorCode(result));
    }

    [Fact]
    public async Task MintToken_WildcardOpClass_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["*"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("invalid_argument", ErrorCode(result));
    }

    [Fact]
    public async Task MintToken_WildcardTtl_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"],"ttl":"*"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("invalid_argument", ErrorCode(result));
    }

    [Fact]
    public async Task MintToken_UnknownVaultInScopeRoots_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["no-such-vault"],"op_class":["content:read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("unknown_vault", ErrorCode(result));
    }

    [Fact]
    public async Task MintToken_DefaultTtlIsSixtyMinutes()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var minted = JsonDocument.Parse(ContentText(result));
        Assert.Equal(60, minted.RootElement.GetProperty("ttl_minutes").GetInt32());
    }

    [Fact]
    public async Task MintToken_MultiVaultScopeRoots_KeepsEachVaultsSubtreesSeparate()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal/Projects/Foo","test/Areas/Bar"],"op_class":["content:read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var minted = JsonDocument.Parse(ContentText(result));
        var vaults = minted.RootElement.GetProperty("vaults").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("personal", vaults);
        Assert.Contains("test", vaults);

        var mintedToken = minted.RootElement.GetProperty("token").GetString();

        // MintTokenTool's own unit tests assert against Grant.ScopeRoots that "Projects/Foo" never
        // scopes the "test" vault or vice versa. Here the check is that the split survives the
        // round trip and the response does not mix the two vaults' roots.
        var scopeRoots = minted.RootElement.GetProperty("scope_roots").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["personal/Projects/Foo", "test/Areas/Bar"], scopeRoots);
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-mint-admin-token";
        public const string ReadOnlyToken = "test-mint-readonly-token";

        public Fixture() : base("vault-mcp-mint-token-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var secondVaultRoot = Path.Combine(TempDir, "test");
            Directory.CreateDirectory(secondVaultRoot);
            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, object>{ ["personal"] = new { path = vaultRoot }, ["test"] = new { path = secondVaultRoot } },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                // A full admin grant - unscoped, every vault, no expiry - so attenuation never
                // limits what these tests can mint. The real dev default binding is the same.
                [AdminToken] = new { id = "admin", opClasses = new[] { "*" }, vaults = new[] { "*" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["*"] = new[] { "*" } } },
                [ReadOnlyToken] = new { id = "read-only", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } } },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);
            File.WriteAllText(Path.Combine(toolsDir, "greet.ps1"), """
                <#
                .SYNOPSIS
                Greets someone by name.
                .PARAMETER Name
                Who to greet.
                .NOTES
                OpClass: content:write
                #>
                param([Parameter(Mandatory=$true)] [string]$Name)
                Write-Output "hello $Name"
                """);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
        }
    }
}
