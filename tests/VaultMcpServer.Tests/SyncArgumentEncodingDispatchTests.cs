using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VaultMcpServer.Tests;

/// <summary>
/// The argument-encoding feature against the real <c>sync</c> tool, the mechanism's second
/// consumer and the one whose payload is a nested OBJECT rather than an array. A client sends
/// <c>commit</c> as real structure - the shape <c>sync</c>'s <c>.SCHEMA</c> block advertises and
/// the shape its own <c>commit-changes</c> / <c>include-staged</c> remedies emit - and the host
/// serializes it on the way into <c>[string] $commit</c>. <c>sync.ps1</c> is unchanged: still a
/// <c>[string]</c>, still its own <c>ConvertFrom-Json -NoEnumerate</c>. Before the encoding, this
/// same call splatted a dictionary into that parameter, which stringified to
/// "System.Collections.Hashtable" and was refused <c>malformed_op</c> - the third of the plan's
/// three undispatchable-payload breaks, and the only one the <c>snake_case</c> rename could not
/// close.
///
/// ISOLATION. <c>sync</c>'s subject is a git repository rather than the committed
/// <c>test-vault/</c>, so this fixture builds its own: a bare "remote" and a working clone, both
/// created under <see cref="ProgramHostFixtureBase.TempDir"/> by <c>git init</c>, with a
/// repo-local identity so nothing reads the machine's global config for authorship. No real
/// repository and no real remote is touched at any point, matching the absolute rule
/// <c>tools/Tests/sync.Tests.ps1</c> runs under. <see cref="ProgramHostFixtureBase"/> supplies the
/// same guard pair the edit-encoding suite uses: <c>AssertMutationSandboxed()</c> immediately
/// before the mutating call, and <see cref="TestVaultCleanGuard"/> from <c>Dispose</c>.
/// </summary>
[Collection("Program host tests")]
public sealed class SyncArgumentEncodingDispatchTests : IClassFixture<SyncArgumentEncodingDispatchTests.Fixture>
{
    private readonly Fixture _fixture;

    public SyncArgumentEncodingDispatchTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static string ExtractJsonRpcPayload(string body)
    {
        if (!body.TrimStart().StartsWith("event:", StringComparison.Ordinal) && !body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return body;
        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}");
        return dataLine["data:".Length..].Trim();
    }

    private async Task<string> CallSyncAsync(Dictionary<string, object?> arguments)
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
            @params = new { name = "sync", arguments },
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
    /// The whole point of the item, end to end: a client sends the commit directive as a real
    /// nested object, the host encodes it because <c>commit</c>'s side-car declares
    /// <c>type: "object"</c> while the parameter is <c>[string]</c>, and the unchanged script
    /// parses it and commits exactly the named paths under the caller's own message.
    ///
    /// The message carries non-ASCII and the characters <c>System.Text.Json</c>'s default encoder
    /// escapes, so this doubles as a round-trip fidelity proof: what a caller sent is what git
    /// history ends up holding.
    /// </summary>
    [Fact]
    public async Task Sync_StructuredCommitObject_IsAppliedByTheUnchangedScript()
    {
        const string message = "Record quorvium <supply> & résumé — 日本 a+b";
        _fixture.WriteWorkingTreeFile("Inbox/Structured.md", "structured\n");

        var text = await CallSyncAsync(new()
        {
            ["apply"] = true,
            // A real nested object, exactly as sync's .SCHEMA block advertises it and as its own
            // commit remedies emit it - not a pre-encoded string.
            ["commit"] = new { message, files = new[] { "Inbox/Structured.md" } },
        });

        using var result = JsonDocument.Parse(text);
        var actions = result.RootElement.GetProperty("actions").EnumerateArray()
            .Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("commit", actions);
        Assert.Contains("git:commit", result.RootElement.GetProperty("capabilitiesUsed").EnumerateArray().Select(c => c.GetString()));

        Assert.Contains(message, _fixture.Git("log -1 --pretty=%B"), StringComparison.Ordinal);
        Assert.Equal("Inbox/Structured.md", _fixture.Git("show --name-only --pretty=format: HEAD").Trim());
    }

    /// <summary>
    /// The passthrough rule against the real tool: the pre-encoded string form - what every caller
    /// written against today's contract sends, and what <c>tools/Tests</c> drives standalone -
    /// still works unchanged. If the host encoded it a second time, the script's
    /// <c>ConvertFrom-Json</c> would yield a JSON string where it expects an object and refuse it
    /// <c>malformed_op</c> for not being one.
    /// </summary>
    [Fact]
    public async Task Sync_PreEncodedCommitString_IsStillAppliedUnchanged()
    {
        _fixture.WriteWorkingTreeFile("Inbox/PreEncoded.md", "pre-encoded\n");

        var text = await CallSyncAsync(new()
        {
            ["apply"] = true,
            ["commit"] = """{"message":"Record the pre-encoded call","files":["Inbox/PreEncoded.md"]}""",
        });

        using var result = JsonDocument.Parse(text);
        Assert.Contains("commit", result.RootElement.GetProperty("actions").EnumerateArray()
            .Select(a => a.GetProperty("action").GetString()));
        Assert.Contains("Record the pre-encoded call", _fixture.Git("log -1 --pretty=%B"), StringComparison.Ordinal);
        Assert.Equal("Inbox/PreEncoded.md", _fixture.Git("show --name-only --pretty=format: HEAD").Trim());
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-sync-encoding-token";

        /// <summary>The throwaway git working tree this fixture commits into.</summary>
        public string VaultRoot { get; }

        private readonly string _remote;

        public Fixture() : base("vault-mcp-sync-encoding-tests-")
        {
            VaultRoot = Path.Combine(TempDir, "vault");
            _remote = Path.Combine(TempDir, "remote.git");

            // Both repositories are created here, under TempDir. Identity and signing are set
            // repo-locally so a commit never depends on (or is refused by) the machine's global
            // git config.
            Directory.CreateDirectory(_remote);
            RunGit(TempDir, $"init -q --bare -b master \"{_remote}\"");
            Directory.CreateDirectory(VaultRoot);
            RunGit(TempDir, $"init -q -b master \"{VaultRoot}\"");
            RunGit(VaultRoot, "config user.email test@example.com");
            RunGit(VaultRoot, "config user.name Test");
            RunGit(VaultRoot, "config commit.gpgsign false");
            RunGit(VaultRoot, $"remote add origin \"{_remote}\"");
            WriteWorkingTreeFile("Inbox/Seed.md", "seed\n");
            RunGit(VaultRoot, "add -A");
            RunGit(VaultRoot, "commit -q -m \"initial commit\"");
            RunGit(VaultRoot, "push -q -u origin master");

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
                    id = "sync-encoding",
                    // git:sync is sync's dispatcher-gated floor; git:commit is the key the commit
                    // directive asserts in-script. Without the latter an explicit directive is
                    // insufficient_capability, so both are needed to reach the encoding at all.
                    opClasses = new[] { "git:sync", "git:commit" },
                    vaults = new[] { "test" },
                    ttl = "*",
                    scopeRoots = new Dictionary<string, string[]> { ["test"] = [] },
                },
            }));

            // The real tools/ directory and the real op-classes.json - this fixture is about the
            // shipped sync.ps1, so nothing about it is synthesized except the repositories and the
            // grant.
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

        /// <summary>Writes a file into the throwaway working tree, creating parents.</summary>
        public void WriteWorkingTreeFile(string relativePath, string content)
        {
            var full = Path.Combine(VaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }

        /// <summary>Reads git state back out of the throwaway working tree, for assertions.</summary>
        public string Git(string arguments) => RunGit(VaultRoot, arguments);

        private static string RunGit(string workingDirectory, string arguments)
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Could not start git {arguments}");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {arguments} failed ({process.ExitCode}): {stdout}{stderr}");
            return stdout;
        }

        /// <summary>Exposes the base guard to the test class, which is where the mutating call is
        /// made and so where the check has to happen.</summary>
        public void AssertSandboxed() => AssertMutationSandboxed();
    }
}
