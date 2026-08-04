using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers over HTTP that <c>read</c>, <c>glob</c> and <c>grep</c> are advertised and dispatched
/// like every other declaration: listed with their derived schemas, routed through the shared
/// execute path, and answering in the content tier over a real vault on disk.
/// </summary>
/// <remarks>
/// The tools' own behaviour is asserted in <c>VaultShared.Tests</c>, against the layer rather than
/// the transport. Only the wiring is observable here: the registry carries them, the execute path's
/// floor, argument, vault and access-object steps run before them, and a refusal arrives as the
/// shared <c>{code, message, data?}</c> envelope.
/// </remarks>
[Collection("Program host tests")]
public sealed class VaultToolDispatchTests(VaultToolDispatchTests.Fixture fixture) : IClassFixture<VaultToolDispatchTests.Fixture>
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

    private static async Task<JsonElement> SendAsync(Fixture fixture, string token, string json)
    {
        using var client = AuthedClient(fixture, token);
        using var response = await client.PostAsync("/mcp", JsonBody(json));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static async Task<(bool IsError, string Text)> CallAsync(Fixture fixture, string token, string tool, string argumentsJson)
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":ARGS}}"""
            .Replace("TOOL", tool, StringComparison.Ordinal)
            .Replace("ARGS", argumentsJson, StringComparison.Ordinal);
        var result = await SendAsync(fixture, token, body);
        return (
            result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!);
    }

    private static string Code(string envelope)
    {
        using var doc = JsonDocument.Parse(envelope);
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    // ---- Listing ----

    [Theory]
    [InlineData("read")]
    [InlineData("glob")]
    [InlineData("grep")]
    public async Task ToolsList_AdvertisesTheVaultTool(string name)
    {
        var result = await SendAsync(fixture, Fixture.ReadToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var tool = Assert.Single(result.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == name);
        Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
        Assert.True(tool.GetProperty("inputSchema").TryGetProperty("properties", out _));
    }

    // ---- Dispatch ----

    [Fact]
    public async Task Read_DispatchesAndAnswersTheNotesText()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "read", """{"path":"Projects/Widget.md"}""");

        Assert.False(isError, text);
        Assert.Equal(Fixture.WidgetNote, text);
    }

    /// <summary>
    /// Verifies that the one structured content-tier result serializes under the shared naming
    /// policy, so a caller reads back <c>content_hash</c> rather than a camelCase spelling.
    /// </summary>
    [Fact]
    public async Task Read_WithContentHash_AnswersTheStructuredResultUnderTheSharedNamingPolicy()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "read", """{"path":"Projects/Widget.md","with_content_hash":true}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(Fixture.WidgetNote, doc.RootElement.GetProperty("content").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("content_hash").GetString()!.Length);
    }

    [Fact]
    public async Task Glob_DispatchesAndAnswersVaultRelativePaths()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "glob", """{"pattern":"Projects/**"}""");

        Assert.False(isError, text);
        Assert.Equal(["Projects/Widget.md"], text.Split('\n'));
    }

    [Fact]
    public async Task Grep_DispatchesAtEachGranularity()
    {
        var (_, lines) = await CallAsync(fixture, Fixture.ReadToken, "grep", """{"pattern":"needle"}""");
        var (_, files) = await CallAsync(fixture, Fixture.ReadToken, "grep", """{"pattern":"needle","granularity":"files"}""");
        var (_, counts) = await CallAsync(fixture, Fixture.ReadToken, "grep", """{"pattern":"needle","granularity":"counts"}""");

        Assert.Equal("Projects/Widget.md:6:A needle in it.", lines);
        Assert.Equal("Projects/Widget.md", files);
        Assert.Equal("Projects/Widget.md:1", counts);
    }

    // ---- The shared execute path ----

    /// <summary>
    /// Verifies that the vault argument is optional and that an omitted one resolves to the
    /// configured default in the execute path, before the tool runs.
    /// </summary>
    [Fact]
    public async Task AnOmittedVault_ResolvesToTheConfiguredDefault()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "glob", """{"pattern":"**","vault":"personal"}""");
        var (_, defaulted) = await CallAsync(fixture, Fixture.ReadToken, "glob", """{"pattern":"**"}""");

        Assert.False(isError, text);
        Assert.Equal(text, defaulted);
    }

    /// <summary>
    /// Verifies that the op-class floor is checked before arguments are bound, so a grant holding
    /// no read class cannot use a malformed call to probe the argument surface of a tool it is not
    /// allowed to run.
    /// </summary>
    [Fact]
    public async Task AGrantWithoutTheReadClass_IsRefusedBeforeItsArgumentsAreEvenLookedAt()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.NoReadClassToken, "read", """{}""");

        Assert.True(isError, text);
        Assert.Equal("authority_required", Code(text));
    }

    [Fact]
    public async Task AnUnrecognizedEnumeratedValue_ArrivesAsACodedArgumentRefusal()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "grep", """{"pattern":"needle","granularity":"paths"}""");

        Assert.True(isError, text);
        Assert.Equal("invalid_argument", Code(text));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("granularity", doc.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    [Fact]
    public async Task AnExtensionlessOperand_ArrivesCarryingTheSpellingThatWouldHaveWorked()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "read", """{"path":"Projects/Widget"}""");

        Assert.True(isError, text);
        Assert.Equal("invalid_path", Code(text));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("Projects/Widget.md", doc.RootElement.GetProperty("data").GetProperty("suggested").GetString());
    }

    [Fact]
    public async Task AMissingNote_ArrivesAsPathNotFound()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "read", """{"path":"Projects/Absent.md"}""");

        Assert.True(isError, text);
        Assert.Equal("path_not_found", Code(text));
    }

    /// <summary>
    /// Hosts the server over a single-note vault of the fixture's own making, with one grant that
    /// holds the read class and one that does not.
    /// </summary>
    public sealed class Fixture : ProgramHostFixtureBase
    {
        /// <summary>A grant holding <c>content:read</c> over the whole vault.</summary>
        public const string ReadToken = "test-vault-tools-read-token";

        /// <summary>A grant over the same vault that holds no content class, so a content-tier call
        /// is refused by the op-class floor.</summary>
        public const string NoReadClassToken = "test-vault-tools-no-read-class-token";

        /// <summary>The text of the only note in the fixture's vault.</summary>
        public const string WidgetNote = "---\ntype: note\n---\n# Widget\n\nA needle in it.\n";

        public Fixture() : base("vault-mcp-vault-tools-tests-")
        {
            // A tree of this fixture's own, so nothing here reads or depends on the committed
            // test-vault/ fixture.
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
                [ReadToken] = new { id = "reader", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = ["*"] } },
                // Scoped to the same vault, so what refuses is the floor rather than membership.
                [NoReadClassToken] = new { id = "no-read-class", opClasses = new[] { "grants:manage" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = ["*"] } },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
        }
    }
}
