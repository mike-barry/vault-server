using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end HTTP-level coverage for the server's diagnostics surfaces: the
/// <c>health_check</c> tool (listed for everyone, callable by any authenticated grant
/// regardless of op class), the <c>BROKEN:</c> prefix synthesized for a script that failed to
/// load, and the <c>initialize</c> response's <c>instructions</c> field reflecting the
/// server's current degraded state. Boots the real host against a temp <c>tools/</c>
/// directory containing one good script and two broken ones (a missing <c>.SYNOPSIS</c> and a
/// malformed <c>.SCHEMA</c> side-car), so the fixture itself puts the server into a real
/// (non-fatal) Degraded state - as distinct from
/// <see cref="HealthCheckToolTests"/>' unit-level coverage of the tool's own output shape.
/// </summary>
[Collection("Program host tests")]
public sealed class HealthCheckDispatchTests : IClassFixture<HealthCheckDispatchTests.Fixture>
{
    private readonly Fixture _fixture;

    public HealthCheckDispatchTests(Fixture fixture) => _fixture = fixture;

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

    private static async Task<JsonElement> SendAsync(Fixture fixture, string token, string json)
    {
        using var client = AuthedClient(fixture, token);
        using var response = await client.PostAsync("/mcp", JsonBody(json));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        return doc.RootElement.GetProperty("result").Clone();
    }

    [Fact]
    public async Task ToolsList_IncludesHealthCheck()
    {
        var result = await SendAsync(_fixture, Fixture.ReadOnlyToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = result.GetProperty("tools").EnumerateArray().ToList();
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "health_check");
    }

    [Fact]
    public async Task HealthCheck_CallableByReadOnlyGrant_BypassingItsOwnOpClass()
    {
        // ReadOnlyToken's grant only allows "content:read" - health_check's own OpClass is
        // deliberately never a real, matchable class (see HealthCheckTool.OpClass), so a
        // normal grant.Allows(tool.OpClass) check would reject this call. Succeeding here
        // proves ScriptToolDispatcher's health_check special case actually bypasses that
        // check rather than merely relying on a permissive grant.
        var result = await SendAsync(
            _fixture, Fixture.ReadOnlyToken,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""");

        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var content = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var doc = JsonDocument.Parse(content);
        Assert.False(doc.RootElement.GetProperty("is_fatally_degraded").GetBoolean());
    }

    [Fact]
    public async Task HealthCheck_ReportsTheBrokenScriptAsADegradedProblem()
    {
        var result = await SendAsync(
            _fixture, Fixture.ReadOnlyToken,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""");

        var content = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        using var doc = JsonDocument.Parse(content);
        var problems = doc.RootElement.GetProperty("problems").EnumerateArray().ToList();
        Assert.Contains(problems, p => p.GetProperty("detail").GetString()!.Contains("broken.ps1"));
    }

    [Fact]
    public async Task ToolsList_BrokenScript_ShowsUpAsBrokenSynthesizedEntry()
    {
        var result = await SendAsync(_fixture, Fixture.ReadOnlyToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = result.GetProperty("tools").EnumerateArray().ToList();

        var broken = Assert.Single(tools, t => t.GetProperty("name").GetString() == "broken");
        Assert.StartsWith("BROKEN:", broken.GetProperty("description").GetString());
        Assert.Contains("broken.ps1", broken.GetProperty("description").GetString());

        // The good script alongside it is unaffected - the rest of the folder still loads.
        var good = Assert.Single(tools, t => t.GetProperty("name").GetString() == "greet");
        Assert.DoesNotContain("BROKEN:", good.GetProperty("description").GetString());
    }

    /// <summary>
    /// A malformed <c>.SCHEMA</c> side-car takes the same route over the wire as any other
    /// per-script refusal: zero tools from that file, and a synthesized <c>BROKEN:</c> entry
    /// rather than a silent disappearance or - the failure this rule exists to prevent - a
    /// quiet fallback to the <c>MapJsonType</c>-derived schema.
    /// </summary>
    [Fact]
    public async Task ToolsList_ScriptWithMalformedSchemaSideCar_ShowsUpAsBrokenSynthesizedEntry()
    {
        var result = await SendAsync(_fixture, Fixture.ReadOnlyToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = result.GetProperty("tools").EnumerateArray().ToList();

        var broken = Assert.Single(tools, t => t.GetProperty("name").GetString() == "schema_broken");
        var description = broken.GetProperty("description").GetString()!;
        Assert.StartsWith("BROKEN:", description);
        Assert.Contains(".SCHEMA", description);
        // The listed entry carries the placeholder empty-object schema, so the parameter is not
        // advertised at all - as opposed to being advertised with a fallback-derived node.
        Assert.Empty(broken.GetProperty("inputSchema").GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task Initialize_WhileDegraded_InstructionsSummarizeTheProblem()
    {
        var body = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
            """;
        var result = await SendAsync(_fixture, Fixture.ReadOnlyToken, body);

        Assert.True(result.TryGetProperty("instructions", out var instructions));
        var text = instructions.GetString();
        Assert.False(string.IsNullOrEmpty(text));
        Assert.Contains("degraded", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("health_check", text, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string ReadOnlyToken = "test-health-readonly-token";

        public Fixture() : base("vault-mcp-health-check-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, object>{ ["personal"] = new { path = vaultRoot } },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
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
                OpClass: content:read
                #>
                param([Parameter(Mandatory=$true)] [string]$Name)
                Write-Output "hello $Name"
                """);
            // No .SYNOPSIS - fails ScriptToolLoader validation, loads zero tools from this
            // file, and records a Degraded "ScriptTools" problem naming it - the fixture's
            // deliberate source of the non-fatal Degraded state this whole test class needs.
            File.WriteAllText(Path.Combine(toolsDir, "broken.ps1"), """
                <#
                .NOTES
                OpClass: content:read
                #>
                param()
                """);
            // A second, differently-broken script: the .SCHEMA side-car body is not valid JSON.
            // It refuses through the same loader path as broken.ps1, which is the point - the
            // side-car's refusal is not a special case with its own reporting.
            File.WriteAllText(Path.Combine(toolsDir, "schema_broken.ps1"), """
                <#
                .SYNOPSIS
                Applies frontmatter ops to a note.
                .PARAMETER frontmatter_ops
                Ops to apply.
                .SCHEMA frontmatter_ops
                { "type": "array", "items": { "oneOf": [
                .NOTES
                OpClass: content:read
                #>
                param([string]$frontmatter_ops)
                """);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
        }
    }
}
