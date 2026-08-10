using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the always-readable scope floor through the whole stack rather than at the containment
/// check alone. The vault declares <c>Resources/Vault/Guides</c> as <c>always_readable</c> and the
/// grant is scoped to <c>Projects/Widget</c>, so one fixture shows both halves: reads see the union
/// of the two, and a write is checked against the grant's own roots and nothing wider.
/// </summary>
/// <remarks>
/// <c>PathPolicy.EffectiveReadScope</c> and <c>VaultAccess</c> have unit coverage of the union
/// itself. What these tests add is that every path-touching operation reaches the check that
/// applies it. A failure here means an operation reached the scope check on a path that should have
/// been in the union, or that a write path consulted the read floor when it must not.
///
/// Result sets are asserted exactly rather than by containment. An over-wide union shows up only as
/// an extra path in an enumeration, which a containment assertion would not catch.
///
/// The fixture runs against a temp copy of the committed <c>test-vault/</c> under both isolation
/// guards, because one test writes.
/// </remarks>
[Collection("Program host tests")]
public sealed class ScopeFloorDispatchTests(ScopeFloorDispatchTests.Fixture fixture) : IClassFixture<ScopeFloorDispatchTests.Fixture>
{
    private const string VaultGuide = "Resources/Vault/Guides/Vault-Guide.md";
    private const string Conventions = "Resources/Vault/Guides/Document-Conventions.md";

    /// <summary>A path under the always-readable root with no note at it. A refusal must not
    /// disclose whether its target exists.</summary>
    private const string AbsentGuide = "Resources/Vault/Guides/Absent-Guide.md";

    /// <summary>Notes outside both the grant's roots and the floor, in the two shapes the vault
    /// offers: one in another folder, and one at the vault root.</summary>
    private const string LooseNote = "Inbox/Note-Loose-Thought.md";
    private const string RootNote = "Test-Vault.md";
    private const string AbsentOutside = "Inbox/Absent-Note.md";

    /// <summary>Everything the narrow grant can read: the three notes under its own root plus the
    /// floor's two. Compared sorted, because the search engine's own ordering is not part of the
    /// contract.</summary>
    private static readonly string[] Reachable =
    [
        "Projects/Widget/Inbox/Plan-Widget-Assembly.md",
        "Projects/Widget/Tasks.md",
        "Projects/Widget/Widget.md",
        Conventions,
        VaultGuide,
    ];

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

        using var client = AuthedClient(fixture, Fixture.NarrowToken);
        using var response = await client.PostAsync("/mcp", new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        var result = doc.RootElement.GetProperty("result");
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
    /// <param name="argumentsJson">The tool's arguments, as a JSON object.</param>
    /// <returns>Whether the call was an error, and the text of its single content block.</returns>
    private static Task<(bool IsError, string Text)> EditAsync(Fixture fixture, string argumentsJson)
    {
        fixture.AssertSandboxed();
        return CallAsync(fixture, "edit", argumentsJson);
    }

    private static string Code(string envelope)
    {
        using var doc = JsonDocument.Parse(envelope);
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : [.. text.Split('\n').Order(StringComparer.Ordinal)];

    // ---- The union admits reads ----

    [Theory]
    [InlineData(VaultGuide)]
    [InlineData(Conventions)]
    public async Task AGuideUnderTheFloor_ReadsUnderAGrantScopedElsewhere(string path)
    {
        var (isError, text) = await CallAsync(fixture, "read", $$"""{"path":"{{path}}"}""");

        Assert.False(isError, text);
        Assert.Equal(fixture.OnDisk(path), text);
    }

    /// <summary>
    /// Verifies that glob returns exactly the union of the grant's root and the floor, and nothing
    /// more. Enumeration runs over the same effective scope a containment check uses.
    /// </summary>
    [Fact]
    public async Task Glob_ReturnsTheUnionOfTheGrantsRootAndTheFloorAndNothingElse()
    {
        var (isError, text) = await CallAsync(fixture, "glob", """{"pattern":"**"}""");

        Assert.False(isError, text);
        Assert.Equal([.. Reachable.Order(StringComparer.Ordinal)], Lines(text));
    }

    /// <summary>
    /// Verifies that grep returns exactly the union of the grant's root and the floor. Every note in
    /// the vault contains a wikilink, so this pattern matches on scope alone and whatever comes back
    /// is what the effective read scope admitted.
    /// </summary>
    [Fact]
    public async Task Grep_ReturnsTheUnionOfTheGrantsRootAndTheFloorAndNothingElse()
    {
        var (isError, text) = await CallAsync(fixture, "grep", """{"pattern":"\\[\\[","granularity":"files"}""");

        Assert.False(isError, text);
        Assert.Equal([.. Reachable.Order(StringComparer.Ordinal)], Lines(text));
    }

    /// <summary>
    /// Verifies that a search matching only out-of-scope notes returns an empty result rather than a
    /// refusal. Withheld paths are simply absent, and nothing in the answer reveals that filtering
    /// happened.
    /// </summary>
    [Fact]
    public async Task Grep_AgainstAnOutOfScopeNotesOwnContent_AnswersEmptyRatherThanRefusing()
    {
        var (isError, text) = await CallAsync(fixture, "grep", """{"pattern":"untriaged root-inbox","granularity":"files"}""");

        Assert.False(isError, text);
        Assert.Equal("", text);
    }

    // ---- The union does not reach writes ----

    /// <summary>
    /// Verifies that a write is scope-checked against the grant's own roots and nothing wider. The
    /// two calls differ only in whether the target note exists, and the refusals must be identical
    /// apart from the path the caller supplied.
    /// </summary>
    [Fact]
    public async Task EditingAGuideUnderTheFloor_IsRefusedOutOfScopeWhetherOrNotItExists()
    {
        var (presentError, present) = await EditAsync(
            fixture, $$"""{"path":"{{VaultGuide}}","operations":[{"op":"set","key":"doc.type","value":"Guide"}]}""");
        var (absentError, absent) = await EditAsync(
            fixture, $$"""{"path":"{{AbsentGuide}}","operations":[{"op":"set","key":"doc.type","value":"Guide"}]}""");

        Assert.True(presentError, present);
        Assert.True(absentError, absent);
        Assert.Equal("out_of_scope", Code(present));

        // The path the caller supplied is the only thing a refusal reports back.
        Assert.Equal(
            present.Replace(VaultGuide, "<path>", StringComparison.Ordinal),
            absent.Replace(AbsentGuide, "<path>", StringComparison.Ordinal));

        Assert.Equal(Fixture.CommittedText(VaultGuide), fixture.OnDisk(VaultGuide));
    }

    /// <summary>
    /// Verifies that a body-only edit under the floor is refused too. It reads no frontmatter key,
    /// so it reaches the file by a different route and would expose a write path that consulted the
    /// read scope.
    /// </summary>
    [Fact]
    public async Task ABodyOnlyEditOfAGuide_IsRefusedOutOfScopeToo()
    {
        var (isError, text) = await EditAsync(
            fixture, $$$"""{"path":"{{{Conventions}}}","body":{"find":"Headings are sentence case","replace":"Headings are title case"}}""");

        Assert.True(isError, text);
        Assert.Equal("out_of_scope", Code(text));
        Assert.Equal(Fixture.CommittedText(Conventions), fixture.OnDisk(Conventions));
    }

    /// <summary>
    /// Verifies that a note inside the grant's own root still writes. The floor only widens the read
    /// scope; it does not narrow the grant's write reach.
    /// </summary>
    [Fact]
    public async Task EditingANoteInsideTheGrantsOwnRoot_Succeeds()
    {
        var (isError, text) = await EditAsync(
            fixture, """{"path":"Projects/Widget/Inbox/Plan-Widget-Assembly.md","operations":[{"op":"set","key":"doc.title","value":"Widget Assembly"}]}""");

        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(["doc.title"], doc.RootElement.GetProperty("changed_keys").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("doc.title: Widget Assembly", fixture.OnDisk("Projects/Widget/Inbox/Plan-Widget-Assembly.md"), StringComparison.Ordinal);
    }

    // ---- Outside both roots is out of reach entirely ----

    /// <summary>
    /// Verifies that reading a path outside both the grant's roots and the floor is refused, with
    /// the same refusal whether or not a note is there.
    /// </summary>
    /// <param name="path">The out-of-scope path to read.</param>
    [Theory]
    [InlineData(LooseNote)]
    [InlineData(RootNote)]
    [InlineData(AbsentOutside)]
    public async Task ReadingANoteOutsideBothRoots_IsRefusedOutOfScope(string path)
    {
        var (isError, text) = await CallAsync(fixture, "read", $$"""{"path":"{{path}}"}""");

        Assert.True(isError, text);
        Assert.Equal("out_of_scope", Code(text));
    }

    /// <summary>
    /// Verifies that the floor is one folder prefix matched whole segment by whole segment. The
    /// folder above it is not covered, and neither is a sibling whose name starts the same way.
    /// </summary>
    /// <param name="path">A path beside the floor rather than under it.</param>
    [Theory]
    [InlineData("Resources/Gizmo-Reference.md")]
    [InlineData("Resources/Vault/Guides-Archive/Vault-Guide.md")]
    public async Task ANoteBesideTheFloorRatherThanUnderIt_IsRefusedOutOfScope(string path)
    {
        var (isError, text) = await CallAsync(fixture, "read", $$"""{"path":"{{path}}"}""");

        Assert.True(isError, text);
        Assert.Equal("out_of_scope", Code(text));
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        /// <summary>A grant scoped to <c>Projects/Widget</c> alone that holds both content classes,
        /// so what refuses a write to a guide is scope rather than the tool's op-class floor.</summary>
        public const string NarrowToken = "test-scope-floor-narrow-token";

        private readonly string _vaultRoot;

        public Fixture() : base("vault-mcp-scope-floor-tests-")
        {
            _vaultRoot = Path.Combine(TempDir, "test-vault");
            CopyDirectory(Path.Combine(TestVaultFixture.RepoRoot, "test-vault"), _vaultRoot);

            File.Copy(
                Path.Combine(TestVaultFixture.RepoRoot, "frontmatter-registry.json"),
                Path.Combine(TempDir, "frontmatter-registry.json"));

            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "test",
                vaults = new Dictionary<string, object>
                {
                    ["test"] = new
                    {
                        path = _vaultRoot.Replace('\\', '/'),
                        frontmatter_registry = "frontmatter-registry.json",

                        // The same declaration the committed vaults.json carries, restated here so
                        // the whole configuration a call reads sits inside the sandbox.
                        path_policy = new[]
                        {
                            new { path = "Resources/Vault/Guides", always_readable = true },
                        },
                    },
                },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [NarrowToken] = new
                {
                    id = "widget-only",
                    opClasses = new[] { "content:read", "content:write" },
                    vaults = new[] { "test" },
                    ttl = "*",
                    scopeRoots = new Dictionary<string, string[]> { ["test"] = ["Projects/Widget"] },
                },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir, vaultMcpVaultsJsonPath: vaultsPath);
        }

        /// <summary>Reads a note's current text from the sandbox copy of the vault.</summary>
        /// <param name="relativePath">Vault-relative path of the note, with forward slashes.</param>
        /// <returns>The file's contents.</returns>
        public string OnDisk(string relativePath) =>
            File.ReadAllText(Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>Reads a note's text from the committed vault, for comparison against the sandbox
        /// copy after a refused write.</summary>
        /// <param name="relativePath">Vault-relative path of the note, with forward slashes.</param>
        /// <returns>The file's contents.</returns>
        public static string CommittedText(string relativePath) =>
            File.ReadAllText(Path.Combine(TestVaultFixture.RepoRoot, "test-vault", relativePath.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>Exposes <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/> so a test
        /// can call it immediately before a mutating call.</summary>
        public void AssertSandboxed() => AssertMutationSandboxed();
    }
}
