using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers over HTTP that <c>create</c>, <c>move</c> and <c>delete</c> are advertised and dispatched
/// like every other declaration, and that each reaches disk through the shared execute path, which
/// holds the vault's mutex around the whole invocation because all three declare that they mutate.
/// </summary>
/// <remarks>
/// Their own behaviour is asserted in <c>VaultShared.Tests</c>. Only the wiring is observable here:
/// the registry carries them, the result serializes under the shared naming policy, a refusal
/// arrives as the shared <c>{code, message, data?}</c> envelope, and the preview gate holds across
/// the transport rather than only inside the tool.
///
/// The fixture runs against a temp copy of the committed <c>test-vault/</c>, with
/// <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/> before every mutating call and
/// <see cref="TestVaultCleanGuard"/> checking on dispose that the committed copy was never touched.
/// </remarks>
[Collection("Program host tests")]
public sealed class MutationDispatchTests(MutationDispatchTests.Fixture fixture) : IClassFixture<MutationDispatchTests.Fixture>
{
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

    /// <summary>
    /// Calls a tool after checking the sandbox. Every call goes through here, so a fixture whose
    /// config paths stop resolving inside its sandbox fails before the call runs rather than after
    /// it has written to the committed vault.
    /// </summary>
    /// <param name="fixture">The host fixture to call.</param>
    /// <param name="tool">The tool name to invoke.</param>
    /// <param name="argumentsJson">The tool's arguments, as a JSON object.</param>
    /// <returns>Whether the call was an error, and the text of its single content block.</returns>
    private static async Task<(bool IsError, string Text)> CallAsync(Fixture fixture, string tool, string argumentsJson)
    {
        fixture.AssertSandboxed();
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":ARGS}}"""
            .Replace("TOOL", tool, StringComparison.Ordinal)
            .Replace("ARGS", argumentsJson, StringComparison.Ordinal);
        var result = await SendAsync(fixture, Fixture.WriteToken, body);
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

    [Fact]
    public async Task ToolsList_AdvertisesAllThree()
    {
        var result = await SendAsync(fixture, Fixture.WriteToken, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var names = result.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();

        Assert.Contains("create", names);
        Assert.Contains("move", names);
        Assert.Contains("delete", names);
    }

    // ---- create ----

    /// <summary>
    /// Verifies that a mutation returns the structured result serialized under the shared naming
    /// policy, so a caller reads back <c>content_hash</c> rather than a camelCase spelling.
    /// </summary>
    [Fact]
    public async Task Create_WritesANoteAndAnswersTheStructuredResult()
    {
        var (isError, text) = await CallAsync(
            fixture, "create", """{"path":"Inbox/Note-Fresh.md","kind":"note","content":"# Fresh\n"}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("Inbox/Note-Fresh.md", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("content_hash").GetString()!.Length);
        // The basename is unique, so the link-pinning pass never ran and the member is absent
        // rather than present and empty.
        Assert.False(doc.RootElement.TryGetProperty("pinned_links", out _));
        Assert.Empty(doc.RootElement.GetProperty("asserted_authority").EnumerateArray());

        Assert.Equal("# Fresh\n", await File.ReadAllTextAsync(fixture.In("Inbox/Note-Fresh.md")));
    }

    /// <summary>
    /// Verifies that creating a folder with <c>git_keep</c> seeds a <c>.gitkeep</c> placeholder. A
    /// caller can ask for the placeholder but cannot choose its name.
    /// </summary>
    [Fact]
    public async Task Create_SeedsTheGitKeepAFolderAsksFor()
    {
        var (isError, text) = await CallAsync(fixture, "create", """{"path":"Areas","kind":"folder","git_keep":true}""");

        Assert.False(isError, text);
        Assert.True(File.Exists(fixture.In("Areas/.gitkeep")));

        using var doc = JsonDocument.Parse(text);
        // Creating a folder creates no note, so neither note-specific member is present.
        Assert.False(doc.RootElement.TryGetProperty("content_hash", out _));
        Assert.False(doc.RootElement.TryGetProperty("pinned_links", out _));
    }

    [Fact]
    public async Task Create_RefusesAnOccupiedTargetDistinguishably()
    {
        var (isError, text) = await CallAsync(
            fixture, "create", """{"path":"Inbox/Note-Loose-Thought.md","kind":"note","content":"# Replaced\n"}""");

        Assert.True(isError, text);
        Assert.Equal("already_exists", Code(text));
    }

    [Fact]
    public async Task Create_RefusesAnInertParameterInTheArgumentPhase()
    {
        var (isError, text) = await CallAsync(
            fixture, "create", """{"path":"Inbox/Note-Inert.md","kind":"note","content":"","git_keep":true}""");

        Assert.True(isError, text);
        Assert.Equal("invalid_argument", Code(text));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("git_keep", doc.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    // ---- move ----

    /// <summary>
    /// Verifies that a move without <c>apply</c> only reports what it would do and leaves the vault
    /// unchanged, so the preview gate holds across the transport.
    /// </summary>
    [Fact]
    public async Task Move_PreviewsByDefault()
    {
        var (isError, text) = await CallAsync(
            fixture, "move", """{"path":"Resources/Gizmo-Reference.md","destination":"Inbox/Gizmo-Reference.md"}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        // On a preview, a path-valued member reports the path the note would end up at, the same as
        // it does on an applied result.
        Assert.Equal("Inbox/Gizmo-Reference.md", doc.RootElement.GetProperty("path").GetString());

        Assert.True(File.Exists(fixture.In("Resources/Gizmo-Reference.md")));
        Assert.False(File.Exists(fixture.In("Inbox/Gizmo-Reference.md")));
    }

    [Fact]
    public async Task Move_RelocatesANoteAndRepairsTheLinksThatNamedIt()
    {
        var (isError, text) = await CallAsync(
            fixture, "move",
            """{"path":"Projects/Widget/Tasks.md","destination":"Projects/Widget/Task-Board.md","apply":true}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("applied").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("case_only").GetBoolean());
        Assert.Equal(["Projects/Widget/Task-Board.md"], doc.RootElement.GetProperty("moved").EnumerateArray().Select(v => v.GetString()));

        Assert.False(File.Exists(fixture.In("Projects/Widget/Tasks.md")));
        Assert.True(File.Exists(fixture.In("Projects/Widget/Task-Board.md")));
    }

    [Fact]
    public async Task Move_RefusesAnOccupiedDestination()
    {
        var (isError, text) = await CallAsync(
            fixture, "move",
            """{"path":"Inbox/Note-Loose-Thought.md","destination":"Resources/Gizmo-Reference.md","apply":true}""");

        Assert.True(isError, text);
        Assert.Contains(Code(text), new[] { "already_exists", "partial_mutation" });
    }

    // ---- The editor's resolution rule, end to end ----

    /// <summary>
    /// Verifies that a real call through the transport rewrites exactly the bare wikilinks that
    /// resolved to the moved note and leaves the rest of the vault alone.
    /// </summary>
    /// <remarks>
    /// Which note a bare wikilink opens when several notes share a basename is Obsidian's behaviour,
    /// matched rather than designed here. The comparator is asserted in <c>WikilinkRankingTests</c>
    /// and the passes over it in <c>VaultShared.Tests/Links</c>.
    ///
    /// <c>Ma/Ref.md</c> and <c>Mb/Ref.md</c> carry the same bare link but resolve to different
    /// notes, because resolution is relative to the linking file rather than vault-global. Moving
    /// <c>Ma/Dupe.md</c> therefore repairs one and must leave the other alone, and the repaired link
    /// is written qualified because <c>Mb/Dupe.md</c> still duplicates the basename.
    /// </remarks>
    [Fact]
    public async Task Move_RepairsOnlyTheBareLinkThatResolvedToTheMovedNoteAndEmitsItQualified()
    {
        var (isError, text) = await CallAsync(
            fixture, "move", """{"path":"Ma/Dupe.md","destination":"Mc/Dupe.md","apply":true}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        var repaired = doc.RootElement.GetProperty("repaired_links").EnumerateArray().ToList();
        var rewrite = Assert.Single(repaired);
        Assert.Equal("Ma/Ref.md", rewrite.GetProperty("path").GetString());
        Assert.Equal("Dupe", rewrite.GetProperty("from").GetString());
        Assert.Equal("Mc/Dupe", rewrite.GetProperty("to").GetString());

        Assert.Equal("See [[Mc/Dupe]].\n", await File.ReadAllTextAsync(fixture.In("Ma/Ref.md")));
        // This link still resolves to the same note as before, so it is left untouched.
        Assert.Equal("See [[Dupe]].\n", await File.ReadAllTextAsync(fixture.In("Mb/Ref.md")));
    }

    /// <summary>
    /// Verifies that creating a note whose basename is already in use pins only the bare links the
    /// new note would re-aim. <c>Cb/Ref.md</c> would start opening the new note, so it is pinned to
    /// the note it opens now; <c>Ca/Ref.md</c> already opens the nearer copy and is left alone.
    /// </summary>
    [Fact]
    public async Task Create_PinsOnlyTheBystanderLinksTheNewNoteWouldReAim()
    {
        var (isError, text) = await CallAsync(
            fixture, "create", """{"path":"Cb/Gizmo.md","kind":"note","content":"# The second one\n"}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        // The basename was already taken, so the pinning pass ran and the member is present. A
        // unique name leaves it absent instead.
        var pin = Assert.Single(doc.RootElement.GetProperty("pinned_links").EnumerateArray());
        Assert.Equal("Cb/Ref.md", pin.GetProperty("path").GetString());
        Assert.Equal("Gizmo", pin.GetProperty("from").GetString());
        Assert.Equal("Ca/Gizmo", pin.GetProperty("to").GetString());

        // Pinning only freezes an existing target; it never aims a link at the new note.
        Assert.Equal("See [[Ca/Gizmo]].\n", await File.ReadAllTextAsync(fixture.In("Cb/Ref.md")));
        Assert.Equal("See [[Gizmo]].\n", await File.ReadAllTextAsync(fixture.In("Ca/Ref.md")));
    }

    // ---- delete ----

    /// <summary>
    /// Verifies that a delete without <c>apply</c> reports everything the removal would destroy
    /// along with the wikilinks pointing into it, and removes nothing.
    /// </summary>
    [Fact]
    public async Task Delete_PreviewsByDefaultAndReportsTheBlastRadius()
    {
        var (isError, text) = await CallAsync(fixture, "delete", """{"path":"Projects/Widget","kind":"folder","recursive":true}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        Assert.Contains(
            "Projects/Widget/Inbox/Plan-Widget-Assembly.md",
            doc.RootElement.GetProperty("removed").EnumerateArray().Select(v => v.GetString()));
        // The backlink scan ran, so the member is present even when it lists nothing.
        Assert.True(doc.RootElement.TryGetProperty("backlinks", out _));

        Assert.True(File.Exists(fixture.In("Projects/Widget/Widget.md")));
    }

    [Fact]
    public async Task Delete_APlainFolderCallMeetingContentsRefusesCarryingThePlan()
    {
        var (isError, text) = await CallAsync(fixture, "delete", """{"path":"Projects/Widget","kind":"folder","apply":true}""");

        Assert.True(isError, text);
        Assert.Equal("not_empty", Code(text));
        using var doc = JsonDocument.Parse(text);
        Assert.NotEmpty(doc.RootElement.GetProperty("data").GetProperty("plan").EnumerateArray());
    }

    [Fact]
    public async Task Delete_RemovesANoteWhenApplied()
    {
        await CallAsync(fixture, "create", """{"path":"Inbox/Note-Doomed.md","kind":"note","content":"# Doomed\n"}""");
        Assert.True(File.Exists(fixture.In("Inbox/Note-Doomed.md")));

        var (isError, text) = await CallAsync(fixture, "delete", """{"path":"Inbox/Note-Doomed.md","kind":"note","apply":true}""");

        Assert.False(isError, text);
        Assert.False(File.Exists(fixture.In("Inbox/Note-Doomed.md")));
    }

    /// <summary>
    /// Verifies that <c>kind</c> is required and that omitting it is refused with the argument
    /// named, rather than being inferred.
    /// </summary>
    [Fact]
    public async Task Delete_WithoutAKindRefusesNamingIt()
    {
        var (isError, text) = await CallAsync(fixture, "delete", """{"path":"Inbox/Note-Loose-Thought.md"}""");

        Assert.True(isError, text);
        Assert.Equal("invalid_argument", Code(text));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("kind", doc.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string WriteToken = "test-mutation-write-token";

        private readonly string _vaultRoot;

        public string In(string relativePath) => Path.Combine([_vaultRoot, .. relativePath.Split('/')]);

        public Fixture() : base("vault-mcp-mutation-tests-")
        {
            _vaultRoot = Path.Combine(TempDir, "test-vault");
            CopyDirectory(Path.Combine(TestVaultFixture.RepoRoot, "test-vault"), _vaultRoot);
            SeedDuplicateBasenames();

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
                [WriteToken] = new
                {
                    id = "writer",
                    opClasses = new[] { "content:read", "content:write" },
                    vaults = new[] { "test" },
                    ttl = "*",
                    scopeRoots = new Dictionary<string, string[]> { ["test"] = ["*"] },
                },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir, vaultMcpVaultsJsonPath: vaultsPath);
        }

        /// <summary>
        /// Writes the notes the link-resolution tests need: no two notes in the committed vault
        /// share a basename, so those tests seed their own.
        /// </summary>
        /// <remarks>
        /// Two independent sets, on different basenames so neither the move nor the create can
        /// change what the other's links resolve to:
        ///
        /// <list type="bullet">
        /// <item><c>Dupe</c> - two copies with a bare link beside each, for the move.</item>
        /// <item><c>Gizmo</c> - one copy plus a bare link in each of two folders, for the create
        /// that duplicates the basename.</item>
        /// </list>
        ///
        /// These are written here rather than added to <c>test-vault/</c> because a duplicate
        /// basename in the committed vault would change what every other suite's bare links
        /// resolve to.
        /// </remarks>
        private void SeedDuplicateBasenames()
        {
            Seed("Ma/Dupe.md", "# One\n");
            Seed("Mb/Dupe.md", "# Two\n");
            Seed("Mc/Other.md", "# The destination folder has to exist\n");
            Seed("Ma/Ref.md", "See [[Dupe]].\n");
            Seed("Mb/Ref.md", "See [[Dupe]].\n");

            Seed("Ca/Gizmo.md", "# The first one\n");
            Seed("Ca/Ref.md", "See [[Gizmo]].\n");
            Seed("Cb/Ref.md", "See [[Gizmo]].\n");
        }

        private void Seed(string relativePath, string content)
        {
            var full = In(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void AssertSandboxed() => AssertMutationSandboxed();
    }
}
