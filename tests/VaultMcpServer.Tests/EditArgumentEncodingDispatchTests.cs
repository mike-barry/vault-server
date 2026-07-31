using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VaultMcpServer.Tests;

/// <summary>
/// The argument-encoding feature against the real <c>edit</c> tool rather than a fixture script:
/// the host is booted over the repo's actual <c>tools/</c> directory and a throwaway copy of the
/// committed <c>test-vault/</c>, and a client sends <c>frontmatter_ops</c> as a real JSON array -
/// the shape <c>edit</c>'s <c>.SCHEMA</c> block advertises and the shape a computed remedy payload
/// will carry. <c>edit.ps1</c> is unchanged: still a <c>[string] $frontmatter_ops</c>, still its own
/// <c>ConvertFrom-Json -NoEnumerate</c>, so this is proof the encoding is what closes the gap.
///
/// Every mutation lands in this fixture's temp copy. <see cref="ProgramHostFixtureBase"/> supplies
/// both halves of the isolation guard: <c>AssertMutationSandboxed()</c> is called immediately
/// before the mutating call (the host's resolved VaultsConfigPath and the PowerShell module's
/// <c>$env:VAULT_MCP_VAULTS_JSON</c> must both point inside the temp directory), and its
/// <c>Dispose</c> runs <see cref="TestVaultCleanGuard"/> so a committed <c>test-vault/</c> that was
/// touched anyway fails loudly rather than silently.
/// </summary>
[Collection("Program host tests")]
public sealed class EditArgumentEncodingDispatchTests : IClassFixture<EditArgumentEncodingDispatchTests.Fixture>
{
    private const string NotePath = "Inbox/Note-Loose-Thought.md";

    private readonly Fixture _fixture;

    public EditArgumentEncodingDispatchTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static string ExtractJsonRpcPayload(string body)
    {
        if (!body.TrimStart().StartsWith("event:", StringComparison.Ordinal) && !body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return body;
        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}");
        return dataLine["data:".Length..].Trim();
    }

    private async Task<string> CallEditAsync(Dictionary<string, object?> arguments)
    {
        // The isolation guard, immediately before the call that writes - not at fixture
        // construction - so it covers the state the tool actually runs against.
        _fixture.AssertSandboxed();

        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.AdminToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "edit", arguments },
        });
        using var response = await client.PostAsync("/mcp", JsonBody(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync()));
        var result = doc.RootElement.GetProperty("result");
        var content = result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!;
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), content);
        return content;
    }

    /// <summary>
    /// The whole point of the item, end to end: a client sends the ordered op array as JSON, the
    /// host encodes it because <c>frontmatter_ops</c>' side-car declares <c>type: "array"</c> while
    /// the parameter is <c>[string]</c>, and the unchanged script parses it and applies both ops in
    /// order. Before the encoding, this same call splatted an <c>object[]</c> into a
    /// <c>[string]</c> parameter, which stringified to "System.Object[]" and was refused
    /// <c>malformed_op</c>.
    ///
    /// The payload also carries non-ASCII and every character
    /// <c>System.Text.Json</c>'s default encoder escapes, so this doubles as the round-trip
    /// fidelity proof through a real tool onto real disk: what a caller sent is what the note ends
    /// up holding.
    /// </summary>
    [Fact]
    public async Task Edit_StructuredFrontmatterOpsArray_IsAppliedByTheUnchangedScript()
    {
        const string awkward = "quorvium <supply> & résumé — 日本 a+b";
        var notePath = Path.Combine(_fixture.VaultRoot, "Inbox", "Note-Loose-Thought.md");

        var text = await CallEditAsync(new()
        {
            ["path"] = NotePath,
            // A real JSON array of op objects, exactly as edit's .SCHEMA block advertises it - not
            // a pre-encoded string.
            ["frontmatter_ops"] = new object[]
            {
                new { op = "set_field", field = "doc.type", value = "Corpus" },
                // `scope` because the awkward payload has to survive verbatim: it is the one
                // registry key whose declared value pattern is any string, so what is under test
                // stays the argument encoding rather than the frontmatter schema.
                new { op = "set_field", field = "scope", value = awkward },
            },
            // A parameter with no side-car, sent alongside in the same call: it must still bind as
            // the plain string it is, or edit would refuse the write as state_mismatch.
            ["expected_sha"] = Sha256Hex(notePath),
        });

        using var result = JsonDocument.Parse(text);
        Assert.True(result.RootElement.GetProperty("frontmatterChanged").GetBoolean(), text);
        // Both ops, in the order sent - the array survived as an array, not as one op or as junk.
        var applied = result.RootElement.GetProperty("opsApplied").EnumerateArray().ToList();
        Assert.Equal(2, applied.Count);
        Assert.Equal(["doc.type", "scope"], applied.Select(o => o.GetProperty("field").GetString()));

        var written = await File.ReadAllTextAsync(notePath);
        Assert.Contains("doc.type: Corpus", written, StringComparison.Ordinal);
        Assert.Contains($"scope: {awkward}", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The passthrough rule against the real tool: the pre-encoded string form - what every caller
    /// written against today's contract sends, and what <c>tools/Tests</c> drives standalone - still
    /// works unchanged. If the host encoded it a second time, the script would parse a JSON string
    /// where it expects an array and refuse it <c>malformed_op</c>.
    /// </summary>
    [Fact]
    public async Task Edit_PreEncodedFrontmatterOpsString_IsStillAppliedUnchanged()
    {
        var notePath = Path.Combine(_fixture.VaultRoot, "Projects", "Widget", "Widget.md");

        var text = await CallEditAsync(new()
        {
            ["path"] = "Projects/Widget/Widget.md",
            ["frontmatter_ops"] = """[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]""",
        });

        using var result = JsonDocument.Parse(text);
        Assert.True(result.RootElement.GetProperty("frontmatterChanged").GetBoolean(), text);
        Assert.Contains("doc.archived: 2026-07-25", await File.ReadAllTextAsync(notePath), StringComparison.Ordinal);
    }

    private static string Sha256Hex(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-edit-encoding-token";

        /// <summary>The throwaway copy of the committed <c>test-vault/</c> this fixture mutates.</summary>
        public string VaultRoot { get; }

        public Fixture() : base("vault-mcp-edit-encoding-tests-")
        {
            VaultRoot = Path.Combine(TempDir, "vault");
            CopyDirectory(Path.Combine(TestVaultFixture.RepoRoot, "test-vault"), VaultRoot);

            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "test",
                vaults = new Dictionary<string, string> { ["test"] = VaultRoot },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [AdminToken] = new
                {
                    id = "edit-encoding",
                    // content:write is edit's declared floor. agentic:write is deliberately NOT
                    // granted: these ops touch doc.* only, so the in-script agentic assertion must
                    // not fire, and the calls prove that too.
                    opClasses = new[] { "content:read", "content:write" },
                    vaults = new[] { "test" },
                    ttl = "*",
                    scopeRoots = new Dictionary<string, string[]> { ["test"] = [] },
                },
            }));

            // The real tools/ directory and the real op-classes.json - this fixture is about the
            // shipped edit.ps1, so nothing about it is synthesized except the vault and the grant.
            ConfigureEnvironment(
                vaultsPath,
                grantsPath,
                toolsDirectory: Path.Combine(TestVaultFixture.RepoRoot, "tools"),
                opClassesConfigPath: Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"),
                // The PowerShell side resolves vaults.json through its own env var, so both halves
                // of the handoff have to point at this fixture's temp config or the tool would
                // operate on the repo's real vaults.json - the exact misfire AssertMutationSandboxed
                // exists to catch.
                vaultMcpVaultsJsonPath: vaultsPath);
        }

        /// <summary>Exposes the base guard to the test class, which is where the mutating call is
        /// made and so where the check has to happen.</summary>
        public void AssertSandboxed() => AssertMutationSandboxed();
    }
}
