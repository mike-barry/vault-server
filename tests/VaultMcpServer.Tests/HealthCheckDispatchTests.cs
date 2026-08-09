using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the server's diagnostic surfaces over HTTP: the <c>health_check</c> tool, which is listed
/// for everyone and callable by any authenticated grant regardless of op class; the <c>BROKEN:</c>
/// prefix synthesized for a script that failed to load; and the <c>instructions</c> field of the
/// <c>initialize</c> response reflecting the server's degraded state.
/// </summary>
/// <remarks>
/// The fixture boots the real host against a temporary <c>tools/</c> directory holding one good
/// script and two broken ones - a missing <c>.SYNOPSIS</c> and a malformed <c>.SCHEMA</c> side-car
/// - so the server really is in a non-fatal Degraded state. <see cref="HealthCheckToolTests"/>
/// covers the tool's own output shape at the unit level.
/// </remarks>
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
        // ReadOnlyToken's grant only allows "content:read", and health_check's own OpClass is not a
        // real matchable class (see HealthCheckTool.OpClass), so a normal grant.Allows(tool.OpClass)
        // check would reject this call. Succeeding here shows ScriptToolDispatcher's health_check
        // special case bypasses that check rather than the grant simply being permissive.
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
        // The severity is asserted on the same problem as the detail. A script that failed to load
        // must degrade the server rather than fatally degrade it, because a fatal problem makes the
        // middleware reject every request with 503.
        //
        // The file name is matched with its directory separator in front, because the detail
        // carries the script's full path and "schema_broken.ps1" ends with "broken.ps1" too.
        var problem = Assert.Single(problems, p => p.GetProperty("detail").GetString()!.Contains($"{Path.DirectorySeparatorChar}broken.ps1"));
        Assert.Equal("Degraded", problem.GetProperty("severity").GetString());
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
    /// Verifies that a malformed <c>.SCHEMA</c> side-car is reported like any other per-script
    /// load failure: no tools load from that file and a synthesized <c>BROKEN:</c> entry is
    /// advertised. It must not disappear silently, and it must not fall back to the
    /// <c>MapJsonType</c>-derived schema.
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
        // advertised at all rather than advertised with a fallback-derived schema.
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
            // No .SYNOPSIS. This fails ScriptToolLoader validation, loads zero tools from the file,
            // and records a Degraded "ScriptTools" problem naming it. This is what puts the server
            // into the non-fatal Degraded state the tests below need.
            File.WriteAllText(Path.Combine(toolsDir, "broken.ps1"), """
                <#
                .NOTES
                OpClass: content:read
                #>
                param()
                """);
            // A second, differently broken script: the .SCHEMA side-car body is not valid JSON. It
            // fails through the same loader path as broken.ps1, with no separate reporting.
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
