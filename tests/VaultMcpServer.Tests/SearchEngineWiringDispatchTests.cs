using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers over HTTP that the server's configured search engine is the engine the native tools run.
/// The host is started with a <c>RipgrepPath</c> naming an executable that is not there, so a
/// configured location the native tools ignored would show up as a search that succeeded.
/// </summary>
/// <remarks>
/// A missing engine is recorded as degraded rather than fatal, so the server keeps serving. It still
/// lists tools, still reads a note, and reports the condition through <c>health_check</c>, while
/// <c>glob</c> and <c>grep</c> refuse with a code no caller retries.
/// </remarks>
[Collection("Program host tests")]
public sealed class SearchEngineWiringDispatchTests(SearchEngineWiringDispatchTests.Fixture fixture)
    : IClassFixture<SearchEngineWiringDispatchTests.Fixture>
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

    private static async Task<(bool IsError, string Text)> CallAsync(Fixture fixture, string tool, string argumentsJson)
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":ARGS}}"""
            .Replace("TOOL", tool, StringComparison.Ordinal)
            .Replace("ARGS", argumentsJson, StringComparison.Ordinal);

        using var client = AuthedClient(fixture, Fixture.AdminToken);
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
        return (
            result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!);
    }

    private static string Code(string envelope)
    {
        using var doc = JsonDocument.Parse(envelope);
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>
    /// Verifies that a search runs the configured engine, asserting the refusal rather than the
    /// configuration. A host that computed an engine location and then handed the tools nothing
    /// would fall back to whatever <c>rg</c> on <c>PATH</c> resolves to, which a machine with
    /// ripgrep installed answers successfully.
    /// </summary>
    [Theory]
    [InlineData("glob", """{"pattern":"**"}""")]
    [InlineData("grep", """{"pattern":"needle"}""")]
    public async Task ASearchRunsTheConfiguredEngineAndRefusesWhenItIsNotThere(string tool, string arguments)
    {
        var (isError, text) = await CallAsync(fixture, tool, arguments);

        Assert.True(isError, text);
        Assert.Equal("dependency_unavailable", Code(text));
    }

    [Fact]
    public async Task ReadStillAnswersWhileTheEngineIsMissing()
    {
        var (isError, text) = await CallAsync(fixture, "read", """{"path":"Projects/Widget.md"}""");

        Assert.False(isError, text);
        Assert.Equal(Fixture.WidgetNote, text);
    }

    [Fact]
    public async Task HealthCheckReportsTheMissingEngineAsADegradedProblem()
    {
        var (isError, text) = await CallAsync(fixture, "health_check", "{}");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("is_fatally_degraded").GetBoolean());
        var problem = Assert.Single(
            doc.RootElement.GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("source").GetString() == "SearchEngine");
        Assert.Equal("Degraded", problem.GetProperty("severity").GetString());
    }

    /// <summary>
    /// Hosts the server over a single-note vault with <c>RipgrepPath</c> naming an executable that
    /// does not exist, so every search the host serves has to refuse.
    /// </summary>
    public sealed class Fixture : ProgramHostFixtureBase
    {
        /// <summary>A grant holding every op class over the whole vault.</summary>
        public const string AdminToken = "test-engine-wiring-admin-token";

        /// <summary>The text of the only note in the fixture's vault.</summary>
        public const string WidgetNote = "---\ntype: note\n---\n# Widget\n\nA needle in it.\n";

        public Fixture() : base("vault-mcp-engine-wiring-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(Path.Combine(vaultRoot, "Projects"));
            File.WriteAllText(Path.Combine(vaultRoot, "Projects", "Widget.md"), WidgetNote);

            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, object> { ["personal"] = new { path = vaultRoot } },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [AdminToken] = new { id = "engine-wiring-admin", opClasses = new[] { "*" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = ["*"] } },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);

            // An absolute location, so what the host resolves it against cannot make it exist. Where
            // a relative one resolves is asserted against the factory itself in VaultShared.Tests.
            SetEnvironmentVariable("RipgrepPath", Path.Combine(TempDir, "no-such-engine.exe"));
        }
    }
}
