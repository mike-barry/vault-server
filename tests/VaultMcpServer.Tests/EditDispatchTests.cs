using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end HTTP coverage that <c>edit</c> is advertised and dispatched like every other
/// declaration, and that a mutation reaches disk through the shared execute path - which holds the
/// vault's mutex around the whole invocation, because the declaration says the tool mutates.
///
/// The tool's own behaviour is asserted in <c>VaultShared.Tests</c>. What is only observable here is
/// the wiring: the registry carries it, the declaration the committed <c>vaults.json</c> names
/// actually loads, the result serializes under the shared naming policy, and a refusal arrives as
/// the shared <c>{code, message, data?}</c> envelope.
///
/// <b>This is the suite's first mutating fixture</b>, so it is the first to use the isolation guard
/// pair: it runs against a temp copy of the committed <c>test-vault/</c>,
/// <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/> checks before every mutating call
/// that both config paths resolve inside the sandbox, and <see cref="TestVaultCleanGuard"/> proves
/// on dispose that the committed copy was never touched.
/// </summary>
[Collection("Program host tests")]
public sealed class EditDispatchTests(EditDispatchTests.Fixture fixture) : IClassFixture<EditDispatchTests.Fixture>
{
    private const string Note = "Inbox/Note-Loose-Thought.md";

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
        using var response = await client.PostAsync("/mcp", new StringContent(json, Encoding.UTF8, "application/json"));
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

    /// <summary>Every mutating call goes through here, so a fixture whose two config paths ever
    /// stop resolving inside its sandbox fails before the call runs rather than after it has
    /// written to the committed fixture.</summary>
    private static Task<(bool IsError, string Text)> EditAsync(Fixture fixture, string token, string argumentsJson)
    {
        fixture.AssertSandboxed();
        return CallAsync(fixture, token, "edit", argumentsJson);
    }

    private static string Code(string envelope)
    {
        using var doc = JsonDocument.Parse(envelope);
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    // ---- Listing ----

    [Fact]
    public async Task ToolsList_AdvertisesEdit()
    {
        var result = await SendAsync(fixture, Fixture.WriteToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var tool = Assert.Single(result.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "edit");
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.True(properties.TryGetProperty("operations", out _));
        Assert.True(properties.TryGetProperty("expected_content_hash", out _));
    }

    // ---- Dispatch ----

    /// <summary>The mutation result is the structured tier, serialized under the shared naming
    /// policy - so what a caller reads back is <c>content_hash</c>, not a camelCase spelling.</summary>
    [Fact]
    public async Task Edit_SetsAScalarAndAnswersTheStructuredResult()
    {
        var (isError, text) = await EditAsync(
            fixture, Fixture.WriteToken,
            $$$"""{"path":"{{{Note}}}","operations":[{"op":"set","key":"doc.type","value":"Reference"}]}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("frontmatter_changed").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("body_changed").GetBoolean());
        Assert.Equal(["doc.type"], doc.RootElement.GetProperty("changed_keys").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal([0], doc.RootElement.GetProperty("applied_operations").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(64, doc.RootElement.GetProperty("content_hash").GetString()!.Length);
        Assert.Empty(doc.RootElement.GetProperty("asserted_authority").EnumerateArray());

        Assert.Contains("doc.type: Reference", await File.ReadAllTextAsync(fixture.NotePath), StringComparison.Ordinal);
    }

    /// <summary>The hash a mutation answers is the one a read of the same file returns, which is
    /// what lets a chain of writes run with no interleaved read.</summary>
    [Fact]
    public async Task TheHashAnEditAnswersIsTheOneAReadReturns()
    {
        var (_, edited) = await EditAsync(
            fixture, Fixture.WriteToken,
            $$$"""{"path":"{{{Note}}}","operations":[{"op":"set","key":"doc.title","value":"Chained"}]}""");
        var (_, read) = await CallAsync(fixture, Fixture.WriteToken, "read", $$$"""{"path":"{{{Note}}}","with_content_hash":true}""");

        using var written = JsonDocument.Parse(edited);
        using var current = JsonDocument.Parse(read);
        Assert.Equal(
            written.RootElement.GetProperty("content_hash").GetString(),
            current.RootElement.GetProperty("content_hash").GetString());
    }

    /// <summary>A refusal from beneath the tool arrives named through the one table, in the shared
    /// envelope, rather than as whatever the layer raised.</summary>
    [Fact]
    public async Task AnUndeclaredKey_ArrivesAsTheCodedRefusal()
    {
        var (isError, text) = await EditAsync(
            fixture, Fixture.WriteToken,
            $$$"""{"path":"{{{Note}}}","operations":[{"op":"set","key":"made.up","value":"x"}]}""");

        Assert.True(isError, text);
        Assert.Equal("unknown_key", Code(text));
    }

    /// <summary>The floor is the write class, so a read-only grant is refused before its arguments
    /// are even looked at.</summary>
    [Fact]
    public async Task AReadOnlyGrant_IsRefusedAtTheFloor()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "edit", """{}""");

        Assert.True(isError, text);
        Assert.Equal("authority_required", Code(text));
    }

    /// <summary>The declared floor is not the whole authority: a key under the declaration's
    /// protected namespace requires the class it names, asserted on the outcome inside the tool.</summary>
    [Fact]
    public async Task AKeyUnderTheProtectedNamespace_RequiresTheClassTheDeclarationNames()
    {
        var (isError, denied) = await EditAsync(
            fixture, Fixture.WriteToken,
            $$$"""{"path":"{{{Note}}}","operations":[{"op":"add_entry","key":"agent.instructions","entry":{"use_skill":"vault-hygiene"}}]}""");

        Assert.True(isError, denied);
        Assert.Equal("authority_required", Code(denied));

        var (elevatedError, granted) = await EditAsync(
            fixture, Fixture.AgenticToken,
            $$$"""{"path":"{{{Note}}}","operations":[{"op":"add_entry","key":"agent.instructions","entry":{"use_skill":"vault-hygiene"}}]}""");

        Assert.False(elevatedError, granted);
        using var doc = JsonDocument.Parse(granted);
        Assert.Equal(["agentic:write"], doc.RootElement.GetProperty("asserted_authority").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>The argument phase runs above the tool, so a shape rule is refused before anything
    /// is locked or read.</summary>
    [Fact]
    public async Task AWholesaleBodyReplacementWithNoHash_ArrivesAsAnArgumentRefusal()
    {
        var (isError, text) = await EditAsync(fixture, Fixture.WriteToken, $$$"""{"path":"{{{Note}}}","body":{"content":"# Replaced\n"}}""");

        Assert.True(isError, text);
        Assert.Equal("invalid_argument", Code(text));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("expected_content_hash", doc.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    [Fact]
    public async Task AStaleHash_ArrivesAsContentChanged()
    {
        var (isError, text) = await EditAsync(
            fixture, Fixture.WriteToken,
            $$$"""{"path":"{{{Note}}}","body":{"content":"# Replaced\n"},"expected_content_hash":"{{{new string('0', 64)}}}"}""");

        Assert.True(isError, text);
        Assert.Equal("content_changed", Code(text));
        using var doc = JsonDocument.Parse(text);
        // No payload: returning the current hash would let a caller retry into content it never read.
        Assert.False(doc.RootElement.TryGetProperty("data", out _));
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string WriteToken = "test-edit-write-token";
        public const string AgenticToken = "test-edit-agentic-token";
        public const string ReadToken = "test-edit-read-token";

        /// <summary>The vault this fixture writes to: a copy of the committed fixture, made once
        /// per fixture, so a mutating case never reaches the real one.</summary>
        private readonly string _vaultRoot;

        public string NotePath => Path.Combine(_vaultRoot, "Inbox", "Note-Loose-Thought.md");

        public Fixture() : base("vault-mcp-edit-tests-")
        {
            _vaultRoot = Path.Combine(TempDir, "test-vault");
            CopyDirectory(Path.Combine(TestVaultFixture.RepoRoot, "test-vault"), _vaultRoot);

            // The declaration the committed configuration names, copied beside this fixture's own
            // vaults.json - which is what it is resolved against. Copied rather than pointed at, so
            // the whole configuration a mutating call reads sits inside the sandbox.
            File.Copy(
                Path.Combine(TestVaultFixture.RepoRoot, "frontmatter-registry.json"),
                Path.Combine(TempDir, "frontmatter-registry.json"));

            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "test",
                vaults = new Dictionary<string, object>
                {
                    ["test"] = new { path = _vaultRoot.Replace('\\', '/'), frontmatter_registry = "frontmatter-registry.json" },
                },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [WriteToken] = Row("writer", ["content:read", "content:write"]),
                [AgenticToken] = Row("agentic-writer", ["content:read", "content:write", "agentic:write"]),
                [ReadToken] = Row("reader", ["content:read"]),
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            // The PowerShell-side path is set because the prevention guard asserts on it: a
            // mutating fixture proves both halves of the handoff resolve inside its sandbox, and an
            // unset one is exactly the misfire the guard exists to catch.
            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir, vaultMcpVaultsJsonPath: vaultsPath);
        }

        private static object Row(string id, string[] opClasses) => new
        {
            id,
            opClasses,
            vaults = new[] { "test" },
            ttl = "*",
            scopeRoots = new Dictionary<string, string[]> { ["test"] = ["*"] },
        };

        /// <summary>The prevention half of the isolation guard pair, exposed so a case can call it
        /// immediately before a mutating call.</summary>
        public void AssertSandboxed() => AssertMutationSandboxed();
    }
}
