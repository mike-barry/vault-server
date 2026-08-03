using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// The read-class scope floor, proved through the whole stack rather than at the containment check.
/// The vault declares <c>Resources/Vault/Guides</c> <c>always_readable</c> and the grant is scoped to
/// <c>Projects/Widget</c> alone, so both halves of the floor are observable in one fixture: the union
/// reaches reads, and a write is checked against the grant's own roots and nothing wider.
///
/// <b>What is only observable here.</b> <c>PathPolicy.EffectiveReadScope</c> and
/// <c>VaultAccess</c> have unit coverage of the union itself; what they cannot show is that every
/// path-touching operation actually arrives at the check that applies it. A failure in this file is
/// the floor's rather than a tool's: it means an operation reached the scope check on a path that
/// should have been unioned in, or a write path consulted the floor when it must not.
///
/// <b>The result sets are asserted exactly.</b> An over-wide union is only visible as a path that
/// should not be in an enumeration, so a containment assertion would pass on the very defect the
/// step exists to find.
///
/// It runs against a temp copy of the committed <c>test-vault/</c> under both halves of the
/// isolation guard, because one case writes: the floor must not narrow the grant's own write reach,
/// and that is only provable by a write that lands.
/// </summary>
[Collection("Program host tests")]
public sealed class ScopeFloorDispatchTests(ScopeFloorDispatchTests.Fixture fixture) : IClassFixture<ScopeFloorDispatchTests.Fixture>
{
    private const string VaultGuide = "Resources/Vault/Guides/Vault-Guide.md";
    private const string Conventions = "Resources/Vault/Guides/Document-Conventions.md";

    /// <summary>A note under the always-readable root that is not there. The oracle rule's other
    /// half: a refusal must not disclose whether its target exists.</summary>
    private const string AbsentGuide = "Resources/Vault/Guides/Absent-Guide.md";

    /// <summary>Outside the grant's roots and outside the floor, in the two shapes the vault
    /// offers - a note in another folder, and one at the vault root.</summary>
    private const string LooseNote = "Inbox/Note-Loose-Thought.md";
    private const string RootNote = "Test-Vault.md";
    private const string AbsentOutside = "Inbox/Absent-Note.md";

    /// <summary>What the narrow grant reaches: its own root's three notes and the floor's two, and
    /// nothing else. Sorted, because a result set is a set and the engine's order is its own.</summary>
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

    /// <summary>Every mutating call goes through here, so a fixture whose two config paths ever stop
    /// resolving inside its sandbox fails before the call runs rather than after it has written to
    /// the committed fixture.</summary>
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

    /// <summary>Enumeration runs over the same effective scope a containment check uses, so the
    /// floor needs no case of its own here - and the result is the union exactly, not a superset.</summary>
    [Fact]
    public async Task Glob_ReturnsTheUnionOfTheGrantsRootAndTheFloorAndNothingElse()
    {
        var (isError, text) = await CallAsync(fixture, "glob", """{"pattern":"**"}""");

        Assert.False(isError, text);
        Assert.Equal([.. Reachable.Order(StringComparer.Ordinal)], Lines(text));
    }

    /// <summary>Every note in the fixture carries a wikilink, so the pattern discriminates on scope
    /// alone: whatever comes back is exactly what the effective read scope admitted.</summary>
    [Fact]
    public async Task Grep_ReturnsTheUnionOfTheGrantsRootAndTheFloorAndNothingElse()
    {
        var (isError, text) = await CallAsync(fixture, "grep", """{"pattern":"\\[\\[","granularity":"files"}""");

        Assert.False(isError, text);
        Assert.Equal([.. Reachable.Order(StringComparer.Ordinal)], Lines(text));
    }

    /// <summary>Filtering is silent: the paths withheld are simply absent, and nothing in the answer
    /// says a filter ran.</summary>
    [Fact]
    public async Task Grep_AgainstAnOutOfScopeNotesOwnContent_AnswersEmptyRatherThanRefusing()
    {
        var (isError, text) = await CallAsync(fixture, "grep", """{"pattern":"untriaged root-inbox","granularity":"files"}""");

        Assert.False(isError, text);
        Assert.Equal("", text);
    }

    // ---- The union does not reach writes ----

    /// <summary>A write is scope-checked against the grant's own roots and nothing wider. The two
    /// cases differ only in whether the target exists, and the refusal must not.</summary>
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

        // Identical apart from the caller's own path, which is the only coordinate a refusal carries.
        Assert.Equal(
            present.Replace(VaultGuide, "<path>", StringComparison.Ordinal),
            absent.Replace(AbsentGuide, "<path>", StringComparison.Ordinal));

        Assert.Equal(Fixture.CommittedText(VaultGuide), fixture.OnDisk(VaultGuide));
    }

    /// <summary>A body-only edit reads no key, so it is the shape that would expose a write path
    /// consulting the read scope by taking a different route to the same file.</summary>
    [Fact]
    public async Task ABodyOnlyEditOfAGuide_IsRefusedOutOfScopeToo()
    {
        var (isError, text) = await EditAsync(
            fixture, $$$"""{"path":"{{{Conventions}}}","body":{"find":"Headings are sentence case","replace":"Headings are title case"}}""");

        Assert.True(isError, text);
        Assert.Equal("out_of_scope", Code(text));
        Assert.Equal(Fixture.CommittedText(Conventions), fixture.OnDisk(Conventions));
    }

    /// <summary>The floor unions into the read scope; it does not narrow the grant's own write
    /// reach, so a note inside the grant's root still writes.</summary>
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

    /// <summary>A named path outside both the grant's roots and the floor is gated loudly, and the
    /// refusal is the same whether or not the note is there.</summary>
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

    /// <summary>The floor is one folder prefix, matched by whole segment: the folder above it is not
    /// in it, and neither is a sibling whose name starts the same way.</summary>
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
        /// <summary>Scoped to <c>Projects/Widget</c> alone, and holding both content classes - so
        /// what refuses a write to a guide is scope rather than the tool's floor.</summary>
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

                        // The same declaration the committed vaults.json carries. Restated here
                        // rather than pointed at, so the whole configuration a call reads sits
                        // inside the sandbox.
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

        /// <summary>What the sandbox copy holds now, for a case asserting that a refused write
        /// changed nothing.</summary>
        public string OnDisk(string relativePath) =>
            File.ReadAllText(Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>What the committed fixture holds, as the comparison a refusal is measured
        /// against.</summary>
        public static string CommittedText(string relativePath) =>
            File.ReadAllText(Path.Combine(TestVaultFixture.RepoRoot, "test-vault", relativePath.Replace('/', Path.DirectorySeparatorChar)));

        /// <summary>The prevention half of the isolation guard pair, exposed so a case can call it
        /// immediately before a mutating call.</summary>
        public void AssertSandboxed() => AssertMutationSandboxed();
    }
}
