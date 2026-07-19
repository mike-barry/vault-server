using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end: boots the real host against a temp tools/ folder and drives tools/list and
/// tools/call over HTTP, proving script discovery, schema exposure, execution, and
/// per-tool op-class enforcement against the caller's grant all work together - not just
/// each piece in isolation.
/// </summary>
[Collection("Program host tests")]
public sealed class ScriptToolDispatcherTests : IClassFixture<ScriptToolDispatcherTests.Fixture>
{
    private readonly Fixture _fixture;

    public ScriptToolDispatcherTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpClient AuthedClient(Fixture fixture, string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Streamable HTTP requires both media types accepted (406 otherwise); the server
        // replies as an SSE stream when text/event-stream is present, so responses need
        // ExtractJsonRpcPayload below rather than a direct JsonDocument.Parse.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    /// <summary>Extracts the JSON-RPC payload from a response body that may be a single SSE `data:` event rather than a plain JSON object.</summary>
    private static string ExtractJsonRpcPayload(string body)
    {
        if (!body.TrimStart().StartsWith("event:", StringComparison.Ordinal) && !body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return body;

        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}");
        return dataLine["data:".Length..].Trim();
    }

    [Fact]
    public async Task ToolsList_IncludesTheFixtureToolWithItsSchema()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(body));
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var greet = Assert.Single(tools, t => t.GetProperty("name").GetString() == "greet");
        Assert.Equal("Greets someone by name.", greet.GetProperty("description").GetString());
        Assert.True(greet.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task ToolsCall_AdminGrant_ExecutesTheScriptAndReturnsItsOutput()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var content = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString();
        Assert.Equal("hello Mike", content);
    }

    [Fact]
    public async Task ToolsCall_ReadOnlyGrantCallingWriteTool_IsRejectedAsToolError()
    {
        using var client = AuthedClient(_fixture, Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"Name":"Mike"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    /// <summary>
    /// End-to-end proof that the `tools/` file watcher is wired into the real host: a
    /// script dropped onto disk after startup shows up in `tools/list` without a restart,
    /// once the watcher's debounce window has elapsed.
    /// </summary>
    [Fact]
    public async Task ToolsList_AfterAddingScriptFileOnDisk_ReflectsTheNewToolOnceDebounced()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);

        await File.WriteAllTextAsync(Path.Combine(_fixture.ToolsDir, "farewell.ps1"), """
            <#
            .SYNOPSIS
            Says goodbye.
            .NOTES
            OpClass: Read
            #>
            Write-Output "bye"
            """);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        List<string> names = [];
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(body));
            names = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
            if (names.Contains("farewell"))
                break;
            await Task.Delay(200);
        }

        Assert.Contains("farewell", names);
        Assert.Contains("greet", names);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsRejectedAsToolError()
    {
        using var client = AuthedClient(_fixture, Fixture.AdminToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public const string AdminToken = "test-admin-token";
        public const string ReadOnlyToken = "test-readonly-token";

        private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-tools-dispatch-tests-").FullName;

        public string ToolsDir { get; }

        public Fixture()
        {
            var vaultRoot = Path.Combine(_tempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var vaultsPath = Path.Combine(_tempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, string> { ["personal"] = vaultRoot },
            }));

            var grantsPath = Path.Combine(_tempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [AdminToken] = new { id = "admin", opClasses = new[] { "Read", "Write" }, vaults = new[] { "personal" }, ttl = "*" },
                [ReadOnlyToken] = new { id = "read-only", opClasses = new[] { "Read" }, vaults = new[] { "personal" }, ttl = "*" },
            }));

            var toolsDir = Path.Combine(_tempDir, "tools");
            ToolsDir = toolsDir;
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
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("VaultsConfigPath", null);
                Environment.SetEnvironmentVariable("GrantsConfigPath", null);
                Environment.SetEnvironmentVariable("ToolsDirectory", null);
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
