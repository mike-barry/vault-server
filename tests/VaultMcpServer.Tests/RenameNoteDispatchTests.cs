using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end coverage for the real `tools/rename_note.ps1` script (as opposed to
/// <see cref="ScriptToolDispatcherTests"/>, which drives a throwaway fixture script) -
/// boots the real host pointed at the repo's actual `tools/` directory so the script's own
/// relative import of the VaultAccess module resolves correctly, and a temp copy of
/// `test-vault/` so the mutation doesn't touch the committed fixture.
/// </summary>
// Deliberately not IClassFixture-shared: both tests below invoke rename_note, which
// mutates the vault. A fixture shared across tests (the pattern ScriptToolDispatcherTests
// uses for its read-only "greet" tool) would let one test's rename bleed into the other's
// assertions, so each test spins up (and disposes) its own host + temp vault copy.
[Collection("Program host tests")]
public sealed class RenameNoteDispatchTests
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
    public async Task ToolsCall_WriteGrant_RenamesNoteAndRewritesBacklinks()
    {
        using var fixture = new Fixture();
        using var client = AuthedClient(fixture, Fixture.WriteToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rename_note","arguments":{"Note":"Gizmo-Reference","NewName":"Widget-Part-Reference"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text);

        Assert.False(File.Exists(Path.Combine(fixture.VaultRoot, "Resources", "Gizmo-Reference.md")));
        Assert.True(File.Exists(Path.Combine(fixture.VaultRoot, "Resources", "Widget-Part-Reference.md")));

        var widget = await File.ReadAllTextAsync(Path.Combine(fixture.VaultRoot, "Projects", "Widget", "Widget.md"));
        Assert.Contains("[[Widget-Part-Reference]]", widget);

        var tasks = await File.ReadAllTextAsync(Path.Combine(fixture.VaultRoot, "Projects", "Widget", "Tasks.md"));
        Assert.Contains("[[Widget-Part-Reference]]", tasks);
        Assert.Contains("[[Widget-Part-Reference#Torque Specs]]", tasks);

        var plan = await File.ReadAllTextAsync(Path.Combine(fixture.VaultRoot, "Projects", "Widget", "Inbox", "Plan-Widget-Assembly.md"));
        Assert.Contains("[[Widget-Part-Reference#Torque Specs|the torque table]]", plan);
        Assert.Contains("[[Resources/Widget-Part-Reference]]", plan);
    }

    [Fact]
    public async Task ToolsCall_ReadOnlyGrant_IsRejectedAsToolError()
    {
        using var fixture = new Fixture();
        using var client = AuthedClient(fixture, Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rename_note","arguments":{"Note":"Gizmo-Reference","NewName":"Should-Not-Rename"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        Assert.True(File.Exists(Path.Combine(fixture.VaultRoot, "Resources", "Gizmo-Reference.md")), "read-only grant must not have been able to mutate the vault");
    }

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public const string WriteToken = "test-rename-write-token";
        public const string ReadOnlyToken = "test-rename-readonly-token";

        private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-rename-dispatch-tests-").FullName;

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
                [WriteToken] = new { id = "rename-write", opClasses = new[] { "Read", "Write" }, vaults = new[] { "test" }, ttl = "*" },
                [ReadOnlyToken] = new { id = "rename-readonly", opClasses = new[] { "Read" }, vaults = new[] { "test" }, ttl = "*" },
            }));

            // Points at the real repo tools/ directory (not a temp copy) so rename_note.ps1's
            // own relative Import-Module of ../powershell/VaultAccess resolves correctly.
            var toolsDir = Path.Combine(TestVaultFixture.RepoRoot, "tools");

            Environment.SetEnvironmentVariable("VaultsConfigPath", vaultsPath);
            Environment.SetEnvironmentVariable("GrantsConfigPath", grantsPath);
            Environment.SetEnvironmentVariable("ToolsDirectory", toolsDir);
            // rename_note.ps1 (like every tool script) resolves its own vault config through
            // the VaultAccess PowerShell module, which reads $env:VAULT_MCP_VAULTS_JSON - a
            // separate env var from the C# host's VaultsConfigPath above, both pointed at the
            // same vaults.json so the in-process script sees the same vault the host does.
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
