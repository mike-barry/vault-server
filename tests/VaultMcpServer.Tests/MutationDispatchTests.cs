using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end HTTP coverage that <c>create</c>, <c>move</c> and <c>delete</c> are advertised and
/// dispatched like every other declaration, and that each reaches disk through the shared execute
/// path - which holds the vault's mutex around the whole invocation, because all three declare that
/// they mutate.
///
/// Their own behaviour is asserted in <c>VaultShared.Tests</c>. What is only observable here is the
/// wiring: the registry carries them, the result serializes under the shared naming policy, a
/// refusal arrives as the shared <c>{code, message, data?}</c> envelope, and the preview gate is a
/// real gate across the transport rather than only inside the tool.
///
/// It runs against a temp copy of the committed <c>test-vault/</c>, with the isolation guard pair:
/// <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/> before every mutating call, and
/// <see cref="TestVaultCleanGuard"/> proving on dispose that the committed copy was never touched.
/// </summary>
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

    /// <summary>Every call goes through here, so a fixture whose two config paths ever stop
    /// resolving inside its sandbox fails before the call runs rather than after it has written to
    /// the committed fixture.</summary>
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

    /// <summary>The mutation result is the structured tier, serialized under the shared naming
    /// policy - so what a caller reads back is <c>content_hash</c>, not a camelCase spelling.</summary>
    [Fact]
    public async Task Create_WritesANoteAndAnswersTheStructuredResult()
    {
        var (isError, text) = await CallAsync(
            fixture, "create", """{"path":"Inbox/Note-Fresh.md","kind":"note","content":"# Fresh\n"}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("Inbox/Note-Fresh.md", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(64, doc.RootElement.GetProperty("content_hash").GetString()!.Length);
        // A unique basename ran no pass, so the member is absent rather than empty.
        Assert.False(doc.RootElement.TryGetProperty("pinned_links", out _));
        Assert.Empty(doc.RootElement.GetProperty("asserted_authority").EnumerateArray());

        Assert.Equal("# Fresh\n", await File.ReadAllTextAsync(fixture.In("Inbox/Note-Fresh.md")));
    }

    /// <summary>The perimeter is narrower than the operation's footprint: a caller asks folder
    /// creation for the placeholder and cannot name one.</summary>
    [Fact]
    public async Task Create_SeedsTheGitKeepAFolderAsksFor()
    {
        var (isError, text) = await CallAsync(fixture, "create", """{"path":"Areas","kind":"folder","git_keep":true}""");

        Assert.False(isError, text);
        Assert.True(File.Exists(fixture.In("Areas/.gitkeep")));

        using var doc = JsonDocument.Parse(text);
        // A folder brings no note into existence, so neither member is populated.
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

    /// <summary>The gate is real across the transport: the routine call reports and moves
    /// nothing.</summary>
    [Fact]
    public async Task Move_PreviewsByDefault()
    {
        var (isError, text) = await CallAsync(
            fixture, "move", """{"path":"Resources/Gizmo-Reference.md","destination":"Inbox/Gizmo-Reference.md"}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("applied").GetBoolean());
        // A path-valued member names a post-mutation path on a plan exactly as on a result.
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
    /// The one behaviour in the set that is matched rather than designed: which note a bare wikilink
    /// opens in Obsidian when several share a basename. The comparator is
    /// asserted directly in <c>WikilinkRankingTests</c> and the passes over it in
    /// <c>VaultShared.Tests/Links</c>; what only this level shows is that a real call through the
    /// transport rewrites exactly the links the rule names and leaves the rest of the vault alone.
    ///
    /// <c>Ma/Ref.md</c> and <c>Mb/Ref.md</c> carry the same bare link and resolve to different notes,
    /// because resolution is relative to the linking file rather than vault-global. Moving
    /// <c>Ma/Dupe.md</c> therefore repairs one and must not touch the other - and the repaired link is
    /// emitted qualified, since <c>Mb/Dupe.md</c> leaves the basename duplicated.
    /// </summary>
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
        // Its answer never changed, so nothing about it is rewritten.
        Assert.Equal("See [[Dupe]].\n", await File.ReadAllTextAsync(fixture.In("Mb/Ref.md")));
    }

    /// <summary>
    /// The protective half of the same rule. Creating a note whose basename is already in use changes
    /// what some bare links open, and those are pinned to the note they open now - the ranking is what
    /// decides which ones. <c>Cb/Ref.md</c> would be re-aimed onto the new note and is pinned;
    /// <c>Ca/Ref.md</c> already opens the nearer copy and is left as it is.
    /// </summary>
    [Fact]
    public async Task Create_PinsOnlyTheBystanderLinksTheNewNoteWouldReAim()
    {
        var (isError, text) = await CallAsync(
            fixture, "create", """{"path":"Cb/Gizmo.md","kind":"note","content":"# The second one\n"}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        // The member is present because the basename was taken and the pass ran, which is a different
        // answer from the absent member a unique name leaves.
        var pin = Assert.Single(doc.RootElement.GetProperty("pinned_links").EnumerateArray());
        Assert.Equal("Cb/Ref.md", pin.GetProperty("path").GetString());
        Assert.Equal("Gizmo", pin.GetProperty("from").GetString());
        Assert.Equal("Ca/Gizmo", pin.GetProperty("to").GetString());

        // Pinning freezes and never redirects: no link is aimed at the note just created.
        Assert.Equal("See [[Ca/Gizmo]].\n", await File.ReadAllTextAsync(fixture.In("Cb/Ref.md")));
        Assert.Equal("See [[Gizmo]].\n", await File.ReadAllTextAsync(fixture.In("Ca/Ref.md")));
    }

    // ---- delete ----

    /// <summary>The routine call reports the full set the removal would destroy and every surviving
    /// wikilink into it, and removes nothing.</summary>
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
        // A census that ran is a list, never an absence.
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

    /// <summary>Kind is required, and omitting it refuses naming the argument rather than the first
    /// member silently standing in.</summary>
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
        /// The committed fixture carries no two notes sharing a basename, so the editor's resolution
        /// rule has nothing to bind on there and the cases that need one seed their own. Two
        /// independent sets, keyed on different basenames so neither move nor creation can change
        /// what the other's links resolve to:
        ///
        /// <list type="bullet">
        /// <item><c>Dupe</c> - two copies and a bare link beside each, for the relocation.</item>
        /// <item><c>Gizmo</c> - one copy and a bare link in each of two folders, for the creation
        /// that duplicates it.</item>
        /// </list>
        ///
        /// Written here rather than added to <c>test-vault/</c> because a duplicate basename in the
        /// committed fixture would change what every other suite's bare links resolve to.
        /// </summary>
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
