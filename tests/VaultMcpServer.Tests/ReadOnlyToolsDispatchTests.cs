using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end HTTP-level dispatch coverage for the three read-only script tools
/// (`resolve_wikilink`, `read_note`, `search`) - boots the real host pointed at the repo's
/// actual `tools/` directory (so each script's own relative import of the VaultAccess module
/// resolves correctly) and a temp copy of `test-vault/`. Shared as an <see cref="IClassFixture{TFixture}"/>
/// across every test in this class, unlike <see cref="RenameNoteDispatchTests"/>: none of
/// these tools write to the vault, so one test's call can't leak state into another's
/// assertions.
/// </summary>
[Collection("Program host tests")]
public sealed class ReadOnlyToolsDispatchTests : IClassFixture<ReadOnlyToolsDispatchTests.Fixture>
{
    private readonly Fixture _fixture;

    public ReadOnlyToolsDispatchTests(Fixture fixture) => _fixture = fixture;

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
    public async Task ResolveWikilink_KnownNote_ReturnsItsVaultRelativePath()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"resolve_wikilink","arguments":{"Link":"Gizmo-Reference"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Equal("Resources/Gizmo-Reference.md", ContentText(result));
    }

    [Fact]
    public async Task ResolveWikilink_UnknownNote_ReturnsNotFoundResultRatherThanAToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"resolve_wikilink","arguments":{"Link":"Nonexistent-Note"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Contains("Not found", ContentText(result));
    }

    [Fact]
    public async Task ReadNote_FullContent_ReturnsFrontmatterAndBody()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_note","arguments":{"Note":"Widget"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var text = ContentText(result);
        Assert.Contains("nav.contents:", text);
        Assert.Contains("# Widget.md - Sample Project for Fixture Testing", text);
    }

    [Fact]
    public async Task ReadNote_FrontmatterOnly_ReturnsJustTheFrontmatterBlock()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_note","arguments":{"Note":"Widget","FrontmatterOnly":true}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var text = ContentText(result);
        Assert.Contains("nav.contents:", text);
        Assert.DoesNotContain("# Widget.md", text);
    }

    [Fact]
    public async Task ReadNote_UnknownNote_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_note","arguments":{"Note":"Nonexistent-Note"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Search_FixtureUniqueToken_FindsTheNotesContainingIt()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"Query":"quorvium"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Contains("Resources/Gizmo-Reference.md", ContentText(result));
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsANonErrorEmptyResult()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"Query":"zzz-token-not-in-vault-zzz"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Contains("No matches", ContentText(result));
    }

    [Fact]
    public async Task WriteOnlyGrant_CallingAnyReadOnlyTool_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"resolve_wikilink","arguments":{"Link":"Gizmo-Reference"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.WriteOnlyToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Search_ScopedGrant_FindsAMatchInsideItsScopeRoot()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"Query":"quorvium"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.WidgetScopedToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Contains("Projects/Widget/Tasks.md", ContentText(result));
    }

    [Fact]
    public async Task Search_ScopedGrant_OmitsAMatchOutsideItsScopeRoot()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"Query":"quorvium"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.WidgetScopedToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.DoesNotContain("Resources/Gizmo-Reference.md", ContentText(result));
    }

    [Fact]
    public async Task ReadNote_ScopedGrant_OutOfScopeNote_IsRejectedAsToolError()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_note","arguments":{"Note":"Gizmo-Reference"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.WidgetScopedToken, body);

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ResolveWikilink_ScopedGrant_OutOfScopeNote_ReturnsNotFoundRatherThanLeakingThePath()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"resolve_wikilink","arguments":{"Link":"Gizmo-Reference"}}}""";
        var result = await CallToolAsync(_fixture, Fixture.WidgetScopedToken, body);

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.Contains("Not found", ContentText(result));
    }

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public const string ReadOnlyToken = "test-readonly-tools-token";
        public const string WriteOnlyToken = "test-writeonly-tools-token";
        public const string WidgetScopedToken = "test-widget-scoped-tools-token";

        private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-readonly-tools-dispatch-tests-").FullName;

        public string VaultRoot { get; }

        public Fixture()
        {
            VaultRoot = Path.Combine(_tempDir, "test");
            CopyDirectory(Path.Combine(TestVaultFixture.RepoRoot, "test-vault"), VaultRoot);

            var vaultsPath = Path.Combine(_tempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "test",
                vaults = new Dictionary<string, string> { ["test"] = VaultRoot },
            }));

            var grantsPath = Path.Combine(_tempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [ReadOnlyToken] = new { id = "readonly-tools", opClasses = new[] { "Read" }, vaults = new[] { "test" }, ttl = "*" },
                [WriteOnlyToken] = new { id = "writeonly-tools", opClasses = new[] { "Write" }, vaults = new[] { "test" }, ttl = "*" },
                [WidgetScopedToken] = new { id = "widget-scoped-tools", opClasses = new[] { "Read" }, vaults = new[] { "test" }, ttl = "*", scopeRoots = new[] { "Projects/Widget" } },
            }));

            // Points at the real repo tools/ directory (not a temp copy) so each script's own
            // relative Import-Module of ../powershell/VaultAccess resolves correctly.
            var toolsDir = Path.Combine(TestVaultFixture.RepoRoot, "tools");

            Environment.SetEnvironmentVariable("VaultsConfigPath", vaultsPath);
            Environment.SetEnvironmentVariable("GrantsConfigPath", grantsPath);
            Environment.SetEnvironmentVariable("ToolsDirectory", toolsDir);
            // Each tool script resolves its own vault config through the VaultAccess
            // PowerShell module, which reads $env:VAULT_MCP_VAULTS_JSON - a separate env var
            // from the C# host's VaultsConfigPath above, both pointed at the same vaults.json
            // so the in-process script sees the same vault the host does.
            Environment.SetEnvironmentVariable("VAULT_MCP_VAULTS_JSON", vaultsPath);
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("VaultsConfigPath", null);
                Environment.SetEnvironmentVariable("GrantsConfigPath", null);
                Environment.SetEnvironmentVariable("ToolsDirectory", null);
                Environment.SetEnvironmentVariable("VAULT_MCP_VAULTS_JSON", null);
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
