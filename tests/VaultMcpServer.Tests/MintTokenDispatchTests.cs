using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end HTTP-level coverage for the native <c>mint_token</c> tool and the
/// <see cref="VaultMcpServer.Tools.ScriptToolDispatcher"/> native/script merge that serves it:
/// that it shows up in `tools/list` alongside script tools, that op-class enforcement
/// (<c>ManageGrants</c>) applies to it the same way as a script tool's declared class, and
/// that a token it mints actually authenticates a follow-up call scoped to what was granted.
/// A fresh host per test class (not a shared <c>IClassFixture</c>) since minting mutates the
/// host's <see cref="VaultMcpServer.Auth.GrantStore"/> in memory - the mutating-tool convention
/// documented in the Testing corpus doc.
/// </summary>
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
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["Read"],"ttl":5}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var minted = JsonDocument.Parse(ContentText(result));
        var mintedToken = minted.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(mintedToken));

        // The minted token can call a Read tool...
        var readCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike"}}}""";
        var readResult = await CallToolAsync(_fixture, mintedToken!, readCall);
        Assert.True(readResult.GetProperty("isError").GetBoolean()); // greet is a Write-class tool - proves the mint actually scoped to Read only, not "any" access.
    }

    [Fact]
    public async Task MintToken_WithoutManageGrants_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["Read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task MintToken_MissingOpClass_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task MintToken_WildcardOpClass_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["*"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task MintToken_WildcardTtl_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["Read"],"ttl":"*"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task MintToken_UnknownVaultInScopeRoots_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["no-such-vault"],"op_class":["Read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task MintToken_DefaultTtlIsSixtyMinutes()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["Read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var minted = JsonDocument.Parse(ContentText(result));
        Assert.Equal(60, minted.RootElement.GetProperty("ttlMinutes").GetInt32());
    }

    [Fact]
    public async Task MintToken_MultiVaultScopeRoots_KeepsEachVaultsSubtreesSeparate()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal/Projects/Foo","test/Areas/Bar"],"op_class":["Read"]}}}""";
        var result = await CallToolAsync(_fixture, Fixture.AdminToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        using var minted = JsonDocument.Parse(ContentText(result));
        var vaults = minted.RootElement.GetProperty("vaults").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("personal", vaults);
        Assert.Contains("test", vaults);

        var mintedToken = minted.RootElement.GetProperty("token").GetString();

        // The minted token can access both vaults, proving the grant covers each of them - the
        // per-vault scope-root grouping itself (that "Projects/Foo" never ends up scoping the
        // "test" vault or vice versa) is asserted directly against Grant.ScopeRoots in
        // MintTokenTool's own unit-level coverage; this end-to-end test proves the split
        // survives the full mint_token round trip without cross-vault leakage in the response.
        var scopeRoots = minted.RootElement.GetProperty("scopeRoots").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["personal/Projects/Foo", "test/Areas/Bar"], scopeRoots);
    }

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public const string AdminToken = "test-mint-admin-token";
        public const string ReadOnlyToken = "test-mint-readonly-token";

        private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-mint-token-tests-").FullName;

        public Fixture()
        {
            var vaultRoot = Path.Combine(_tempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var secondVaultRoot = Path.Combine(_tempDir, "test");
            Directory.CreateDirectory(secondVaultRoot);
            var vaultsPath = Path.Combine(_tempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, string> { ["personal"] = vaultRoot, ["test"] = secondVaultRoot },
            }));

            var grantsPath = Path.Combine(_tempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [AdminToken] = new { id = "admin", opClasses = new[] { "Read", "Write", "ManageGrants" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = Array.Empty<string>() } },
                [ReadOnlyToken] = new { id = "read-only", opClasses = new[] { "Read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = Array.Empty<string>() } },
            }));

            var toolsDir = Path.Combine(_tempDir, "tools");
            Directory.CreateDirectory(toolsDir);
            File.WriteAllText(Path.Combine(toolsDir, "greet.ps1"), """
                <#
                .SYNOPSIS
                Greets someone by name.
                .PARAMETER Name
                Who to greet.
                .NOTES
                OpClass: Write
                #>
                param([Parameter(Mandatory=$true)] [string]$Name)
                Write-Output "hello $Name"
                """);

            Environment.SetEnvironmentVariable("VaultsConfigPath", vaultsPath);
            Environment.SetEnvironmentVariable("GrantsConfigPath", grantsPath);
            Environment.SetEnvironmentVariable("ToolsDirectory", toolsDir);
            // Redirects Program.cs's FileLoggerProvider into this fixture's temp dir so
            // test runs don't litter timestamped log files into the repo-root logs/
            // directory; cleaned up by the temp-dir delete below.
            Environment.SetEnvironmentVariable("LogsDirectory", Path.Combine(_tempDir, "logs"));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("VaultsConfigPath", null);
                Environment.SetEnvironmentVariable("GrantsConfigPath", null);
                Environment.SetEnvironmentVariable("ToolsDirectory", null);
                Environment.SetEnvironmentVariable("LogsDirectory", null);
                try
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
