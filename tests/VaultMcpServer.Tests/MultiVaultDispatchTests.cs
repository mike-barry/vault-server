using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end coverage for the real `vaults.json`'s two-entry shape ("personal" default
/// plus a second "test" vault pointed at the committed `test-vault/` fixture) - proves the
/// three behaviors the multi-vault checklist step cares about all work together through the
/// real host over HTTP: default-vault fallback, explicit `vault` targeting, and grant
/// `vaults`-scope rejection for a vault that IS configured but outside the caller's grant.
///
/// Unlike <see cref="BearerGrantMiddlewareTests"/>'s out-of-scope test (which names a vault
/// absent from vaults.json entirely), the rejection case here names a vault that exists in
/// the fixture's config but isn't in the presented grant's `vaults` list - the scenario the
/// real two-entry vaults.json actually creates.
/// </summary>
// Deliberately not IClassFixture-shared: these tests drive rename_note, which mutates the
// vault. Each test spins up (and disposes) its own host + temp vault copies, same rationale
// as RenameNoteDispatchTests.
[Collection("Program host tests")]
public sealed class MultiVaultDispatchTests
{
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

    [Fact]
    public async Task ToolsCall_OmittingVault_FallsBackToDefaultVault()
    {
        using var fixture = new Fixture();
        fixture.AssertMutationSandboxedForTest();
        using var client = AuthedClient(fixture, Fixture.BroadToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rename_note","arguments":{"Note":"Personal-Note","NewName":"Personal-Note-Renamed"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text);

        Assert.False(File.Exists(Path.Combine(fixture.PersonalRoot, "Personal-Note.md")));
        Assert.True(File.Exists(Path.Combine(fixture.PersonalRoot, "Personal-Note-Renamed.md")));

        // The "test" vault copy must be untouched by a call that named no vault.
        Assert.True(File.Exists(Path.Combine(fixture.TestRoot, "Resources", "Gizmo-Reference.md")));
    }

    [Fact]
    public async Task ToolsCall_ExplicitTestVault_ResolvesAgainstTestVaultNotPersonal()
    {
        using var fixture = new Fixture();
        fixture.AssertMutationSandboxedForTest();
        using var client = AuthedClient(fixture, Fixture.BroadToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rename_note","arguments":{"vault":"test","Note":"Gizmo-Reference","NewName":"Widget-Part-Reference"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text);

        Assert.False(File.Exists(Path.Combine(fixture.TestRoot, "Resources", "Gizmo-Reference.md")));
        Assert.True(File.Exists(Path.Combine(fixture.TestRoot, "Resources", "Widget-Part-Reference.md")));

        // The "personal" vault copy must be untouched by a call explicitly naming "test".
        Assert.True(File.Exists(Path.Combine(fixture.PersonalRoot, "Personal-Note.md")));
    }

    [Fact]
    public async Task ToolsCall_GrantNotScopedToTestVault_Returns403AndDoesNotMutateTestVault()
    {
        using var fixture = new Fixture();
        fixture.AssertMutationSandboxedForTest();
        using var client = AuthedClient(fixture, Fixture.PersonalOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rename_note","arguments":{"vault":"test","Note":"Gizmo-Reference","NewName":"Should-Not-Rename"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(fixture.TestRoot, "Resources", "Gizmo-Reference.md")), "grant scoped only to personal must not have been able to touch the test vault");
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string BroadToken = "test-multivault-broad-token";
        public const string PersonalOnlyToken = "test-multivault-personal-only-token";

        public string PersonalRoot { get; }
        public string TestRoot { get; }

        public Fixture() : base("vault-mcp-multivault-tests-")
        {
            PersonalRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(PersonalRoot);
            File.WriteAllText(Path.Combine(PersonalRoot, "Personal-Note.md"), "# Personal Note\n");

            TestRoot = Path.Combine(TempDir, "test");
            CopyDirectory(Path.Combine(TestVaultFixture.RepoRoot, "test-vault"), TestRoot);

            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, string> { ["personal"] = PersonalRoot, ["test"] = TestRoot },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [BroadToken] = new { id = "multivault-broad", opClasses = new[] { "Read", "Write" }, vaults = new[] { "personal", "test" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = Array.Empty<string>(), ["test"] = Array.Empty<string>() } },
                [PersonalOnlyToken] = new { id = "multivault-personal-only", opClasses = new[] { "Read", "Write" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = Array.Empty<string>() } },
            }));

            // Points at the real repo tools/ directory (not a temp copy) so rename_note.ps1's
            // own relative Import-Module of ../powershell/VaultAccess resolves correctly.
            var toolsDir = Path.Combine(TestVaultFixture.RepoRoot, "tools");

            // rename_note.ps1 resolves its own vault config through the VaultAccess
            // PowerShell module, which reads $env:VAULT_MCP_VAULTS_JSON - a separate env var
            // from the C# host's VaultsConfigPath above, both pointed at the same vaults.json
            // so the in-process script sees the same vaults the host does.
            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir, vaultMcpVaultsJsonPath: vaultsPath);
        }

        /// <summary>
        /// Public wrapper over the protected <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/>
        /// guard - see RenameNoteDispatchTests.Fixture's identical wrapper for the rationale.
        /// </summary>
        public void AssertMutationSandboxedForTest() => AssertMutationSandboxed();
    }
}
