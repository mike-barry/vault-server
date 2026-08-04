using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers over HTTP that <c>edit</c> is advertised and dispatched like every other declaration and
/// that a mutation reaches disk through the shared execute path, which holds the vault's mutex
/// around the whole invocation because the declaration marks the tool as mutating.
/// </summary>
/// <remarks>
/// The tool's own behaviour is asserted in <c>VaultShared.Tests</c>. Only the wiring is observable
/// here: the registry carries the tool, the declaration named by the committed <c>vaults.json</c>
/// loads, the result serializes under the shared naming policy, and a refusal arrives as the shared
/// <c>{code, message, data?}</c> envelope.
///
/// This fixture mutates, so it uses both isolation guards. It runs against a temp copy of the
/// committed <c>test-vault/</c>, <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/> checks
/// before every mutating call that both config paths resolve inside the sandbox, and
/// <see cref="TestVaultCleanGuard"/> checks on dispose that the committed copy was never touched.
/// </remarks>
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

    /// <summary>
    /// Calls <c>edit</c> after checking the sandbox. Every mutating call goes through here, so a
    /// fixture whose config paths stop resolving inside its sandbox fails before the call runs
    /// rather than after it has written to the committed vault.
    /// </summary>
    /// <param name="fixture">The host fixture to call.</param>
    /// <param name="token">The bearer token the call runs under.</param>
    /// <param name="argumentsJson">The tool's arguments, as a JSON object.</param>
    /// <returns>Whether the call was an error, and the text of its single content block.</returns>
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

    /// <summary>
    /// Verifies that the advertised <c>entry</c> argument carries one schema branch per variant of
    /// the default vault's loaded declaration - the default vault being the one an absent
    /// <c>vault</c> argument targets. This fixture's default vault names the committed
    /// declaration, so the branches are that file's variants.
    /// </summary>
    /// <remarks>
    /// The case where no declaration is configured is covered by
    /// <c>NativeToolSchemaDispatchTests</c>, whose fixture pins that <c>entry</c> then arrives
    /// unconstrained.
    /// </remarks>
    [Fact]
    public async Task ToolsList_EditsEntryCarriesTheDefaultVaultsVariantBranches()
    {
        var result = await SendAsync(fixture, Fixture.WriteToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var tool = Assert.Single(result.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "edit");
        var entry = tool.GetProperty("inputSchema")
            .GetProperty("properties").GetProperty("operations")
            .GetProperty("items").GetProperty("properties").GetProperty("entry");

        var variants = entry.GetProperty("anyOf").EnumerateArray()
            .Select(b => b.GetProperty("title").GetString())
            .ToArray();
        Assert.Contains("link", variants);
        Assert.Contains("use_skill", variants);
        Assert.Contains("hint", variants);
    }

    /// <summary>
    /// Verifies the listing's wire contract against whatever the server advertises rather than a
    /// hand-kept list: every declaration arrives with a name, a description and a schema, and every
    /// name and argument name is <c>snake_case</c>. A casing regression would leave the schema and
    /// the binder agreeing with each other while breaking every existing caller.
    /// </summary>
    [Fact]
    public async Task ToolsList_EveryAdvertisedToolCarriesNameDescriptionAndSchemaInSnakeCase()
    {
        var result = await SendAsync(fixture, Fixture.WriteToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var advertised = result.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(10, advertised.Count);

        foreach (var tool in advertised)
        {
            var name = tool.GetProperty("name").GetString()!;
            Assert.Matches("^[a-z][a-z0-9_]*$", name);
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), name);

            var schema = tool.GetProperty("inputSchema");
            Assert.Equal("object", schema.GetProperty("type").GetString());
            foreach (var argument in schema.GetProperty("properties").EnumerateObject())
                Assert.Matches("^[a-z][a-z0-9_]*$", argument.Name);
        }
    }

    // ---- Dispatch ----

    /// <summary>
    /// Verifies that a mutation returns the structured result serialized under the shared naming
    /// policy, so a caller reads back <c>content_hash</c> rather than a camelCase spelling.
    /// </summary>
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

    /// <summary>
    /// Verifies that the hash a mutation returns equals the hash a read of the same file returns,
    /// which is what lets a caller chain writes without reading in between.
    /// </summary>
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

    /// <summary>
    /// Verifies that a refusal raised below the tool arrives as a code from the shared table in the
    /// shared envelope, rather than as whatever that layer raised.
    /// </summary>
    [Fact]
    public async Task AnUndeclaredKey_ArrivesAsTheCodedRefusal()
    {
        var (isError, text) = await EditAsync(
            fixture, Fixture.WriteToken,
            $$$"""{"path":"{{{Note}}}","operations":[{"op":"set","key":"made.up","value":"x"}]}""");

        Assert.True(isError, text);
        Assert.Equal("unknown_key", Code(text));
    }

    /// <summary>
    /// Verifies that a read-only grant is refused at the tool's write-class floor, before its
    /// arguments are examined.
    /// </summary>
    [Fact]
    public async Task AReadOnlyGrant_IsRefusedAtTheFloor()
    {
        var (isError, text) = await CallAsync(fixture, Fixture.ReadToken, "edit", """{}""");

        Assert.True(isError, text);
        Assert.Equal("authority_required", Code(text));
    }

    /// <summary>
    /// Verifies that the declared floor is not the only authority check. A key under the
    /// declaration's protected namespace also requires the op class that declaration names, and the
    /// class actually used is reported on the result.
    /// </summary>
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

    /// <summary>
    /// Verifies that an argument shape rule is refused in the argument phase above the tool, before
    /// anything is locked or read.
    /// </summary>
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

        /// <summary>The vault this fixture writes to: a per-fixture copy of the committed
        /// test-vault, so a mutating case never reaches the committed one.</summary>
        private readonly string _vaultRoot;

        public string NotePath => Path.Combine(_vaultRoot, "Inbox", "Note-Loose-Thought.md");

        public Fixture() : base("vault-mcp-edit-tests-")
        {
            _vaultRoot = Path.Combine(TempDir, "test-vault");
            CopyDirectory(Path.Combine(TestVaultFixture.RepoRoot, "test-vault"), _vaultRoot);

            // The declaration the committed configuration names, copied beside this fixture's own
            // vaults.json, because the path is resolved relative to that file. Copied rather than
            // referenced so the whole configuration a mutating call reads sits inside the sandbox.
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

            // VAULT_MCP_VAULTS_JSON is set because AssertMutationSandboxed requires it: an unset
            // value is one of the misfires that guard exists to catch.
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

        /// <summary>Exposes <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/> so a test
        /// can call it immediately before a mutating call.</summary>
        public void AssertSandboxed() => AssertMutationSandboxed();
    }
}
