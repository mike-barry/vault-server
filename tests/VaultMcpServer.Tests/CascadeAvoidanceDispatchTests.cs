using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VaultMcpServer.Auth;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultShared.Health;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers end to end what Program.cs does when vaults.json or op-classes.json fails to load at
/// all. On a Fatal from either file it skips <see cref="GrantStore.Load"/> and substitutes
/// <see cref="GrantStore.Empty"/> plus one Degraded problem recording the skip.
/// </summary>
/// <remarks>
/// Running the real load in that state would validate every grants.json row against an empty
/// registry or vault config and fail each one, burying the root cause under row-level "unknown
/// opClass" and "unknown vault" problems.
///
/// Both fixtures supply a grants.json that would load cleanly on its own, so the absence of
/// row-level problems shows the skip happened because of the upstream Fatal rather than because
/// grants.json was itself broken.
/// </remarks>
[Collection("Program host tests")]
public sealed class CascadeAvoidanceDispatchTests
{
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task FatalVaultsJson_SkipsRealGrantLoad_LeavesOnlyRootCauseAndOneBreadcrumb()
    {
        using var fixture = new Fixture(vaultsFatal: true);

        using var client = fixture.CreateClient();
        using var noAuthResponse = await client.PostAsync("/mcp", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noAuthResponse.StatusCode);
        var noAuthBody = await noAuthResponse.Content.ReadAsStringAsync();
        Assert.Equal(BearerGrantMiddleware.FatalDegradedBody, noAuthBody);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "some-token");
        using var authedResponse = await client.PostAsync("/mcp", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, authedResponse.StatusCode);

        var healthState = fixture.Services.GetRequiredService<ServerHealthState>();
        AssertOnlyRootCauseAndBreadcrumb(healthState, rootCauseSource: "VaultConfig");
    }

    [Fact]
    public async Task FatalOpClassesJson_SkipsRealGrantLoad_LeavesOnlyRootCauseAndOneBreadcrumb()
    {
        using var fixture = new Fixture(vaultsFatal: false);

        using var client = fixture.CreateClient();
        using var response = await client.PostAsync("/mcp", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(BearerGrantMiddleware.FatalDegradedBody, body);

        var healthState = fixture.Services.GetRequiredService<ServerHealthState>();
        AssertOnlyRootCauseAndBreadcrumb(healthState, rootCauseSource: "OpClassRegistry");
    }

    /// <summary>Asserts that health state holds exactly the root-cause Fatal and the one Degraded
    /// problem recording the skipped grant load.</summary>
    /// <param name="healthState">The booted host's health state.</param>
    /// <param name="rootCauseSource">The source name expected on the Fatal problem.</param>
    private static void AssertOnlyRootCauseAndBreadcrumb(ServerHealthState healthState, string rootCauseSource)
    {
        Assert.True(healthState.IsFatallyDegraded);
        var problems = healthState.Problems.ToList();

        // Exactly two problems: the root-cause Fatal and the single GrantStore problem. Any more
        // would mean the per-row grants.json load ran.
        Assert.True(problems.Count == 2, "Expected exactly 2 problems (root cause + breadcrumb), got: " +
            string.Join(" | ", problems.Select(p => $"[{p.Severity}] {p.Source}: {p.Detail}")));

        var fatal = Assert.Single(problems, p => p.Severity == ProblemSeverity.Fatal);
        Assert.Equal(rootCauseSource, fatal.Source);

        var breadcrumb = Assert.Single(problems, p => p.Severity == ProblemSeverity.Degraded);
        Assert.Equal("GrantStore", breadcrumb.Source);
        Assert.Contains("Skipped loading", breadcrumb.Detail);
        Assert.Contains("a prior fatal error", breadcrumb.Detail);

        // No row-level grant problems ("Grant row skipped - ...") anywhere in the set.
        Assert.DoesNotContain(problems, p => p.Detail.Contains("Grant row skipped"));
    }

    /// <summary>Boots the host with exactly one config file broken badly enough to be Fatal.</summary>
    public sealed class Fixture : ProgramHostFixtureBase
    {
        /// <param name="vaultsFatal">True to break vaults.json; false to break op-classes.json
        /// instead and leave vaults.json valid.</param>
        public Fixture(bool vaultsFatal) : base("vault-mcp-cascade-avoidance-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);

            // An empty tools/ directory. Pointing at the real repo tools/ directory would add
            // unrelated ScriptTools problems - in the op-classes-fatal case every real script
            // fails OpClass validation against the empty registry - and break the exact-count
            // assertions.
            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            // A grants.json that would load cleanly on its own, so an absence of row-level
            // problems means the load was skipped rather than that this file was empty.
            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["some-token"] = new
                {
                    id = "someone",
                    opClasses = new[] { "content:read" },
                    vaults = new[] { "personal" },
                    ttl = "*",
                    scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } },
                },
            }));

            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            string? opClassesPath = null;
            if (vaultsFatal)
            {
                // Missing "default_vault" - VaultConfigLoader records a whole-file Fatal.
                // op-classes.json is left at Program.cs's default, the real and valid repo file,
                // so this is the only Fatal.
                File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
                {
                    vaults = new Dictionary<string, object>{ ["personal"] = new { path = vaultRoot } },
                }));
            }
            else
            {
                // A valid vaults.json - the Fatal here comes from op-classes.json instead.
                File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
                {
                    default_vault = "personal",
                    vaults = new Dictionary<string, object>{ ["personal"] = new { path = vaultRoot } },
                }));

                // Non-array root - OpClassRegistryLoader records a whole-file Fatal.
                opClassesPath = Path.Combine(TempDir, "op-classes.json");
                File.WriteAllText(opClassesPath, """{ "not": "an array" }""");
            }

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir, opClassesConfigPath: opClassesPath);
        }
    }
}
