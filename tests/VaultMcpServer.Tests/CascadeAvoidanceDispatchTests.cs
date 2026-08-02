using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VaultMcpServer.Auth;
using VaultMcpServer.Health;

namespace VaultMcpServer.Tests;

/// <summary>
/// End-to-end coverage for Program.cs's cascade-avoidance path (see the comment above its
/// <c>GrantStore</c> load): when vaults.json OR op-classes.json fails to load entirely
/// (Fatal), Program.cs skips the real <see cref="GrantStore.Load"/> call - which would
/// otherwise validate every grants.json row's opClasses/vaults against an already-empty
/// registry/vault config and fail each one, piling a wall of confusing row-level
/// "unknown opClass"/"unknown vault" Degraded problems on top of the real root cause - and
/// substitutes <see cref="GrantStore.Empty"/> plus a single Degraded breadcrumb instead. This
/// path had zero prior automated coverage. Both fixtures supply a grants.json that would
/// otherwise load cleanly, proving the skip happens because of the upstream Fatal rather than
/// because grants.json itself is broken.
/// </summary>
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

    private static void AssertOnlyRootCauseAndBreadcrumb(ServerHealthState healthState, string rootCauseSource)
    {
        Assert.True(healthState.IsFatallyDegraded);
        var problems = healthState.Problems.ToList();

        // Exactly two problems: the root-cause Fatal and the single GrantStore breadcrumb -
        // proving the real (per-row) grants.json load never ran.
        Assert.True(problems.Count == 2, "Expected exactly 2 problems (root cause + breadcrumb), got: " +
            string.Join(" | ", problems.Select(p => $"[{p.Severity}] {p.Source}: {p.Detail}")));

        var fatal = Assert.Single(problems, p => p.Severity == ProblemSeverity.Fatal);
        Assert.Equal(rootCauseSource, fatal.Source);

        var breadcrumb = Assert.Single(problems, p => p.Severity == ProblemSeverity.Degraded);
        Assert.Equal("GrantStore", breadcrumb.Source);
        Assert.Contains("Skipped loading", breadcrumb.Detail);
        Assert.Contains("a prior fatal error", breadcrumb.Detail);

        // No row-level grant problems ("Grant row skipped - ...") anywhere in the set - the
        // whole point of the cascade-avoidance path.
        Assert.DoesNotContain(problems, p => p.Detail.Contains("Grant row skipped"));
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public Fixture(bool vaultsFatal) : base("vault-mcp-cascade-avoidance-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);

            // An empty tools/ directory - this cascade is entirely about the
            // vaults.json/op-classes.json -> GrantStore skip; pointing at the real repo
            // tools/ directory here would layer unrelated ScriptTools Degraded problems on
            // top (every real script fails OpClass validation against the empty registry in
            // the op-classes-fatal case) and muddy the exact-count assertions below.
            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);

            // A grants.json that would load cleanly on its own - proving any absence of
            // row-level problems below is because the real load was skipped, not because
            // this file happens to be trivially valid or empty.
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
                // Missing "default_vault" - VaultConfig.Load records a whole-file Fatal (see
                // VaultConfig.LoadCore). op-classes.json is left at Program.cs's own default
                // (the real, valid repo file), so this is the only Fatal in the picture.
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

                // Non-array root - OpClassRegistry.Load records a whole-file Fatal (see
                // OpClassRegistry.LoadCore).
                opClassesPath = Path.Combine(TempDir, "op-classes.json");
                File.WriteAllText(opClassesPath, """{ "not": "an array" }""");
            }

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir, opClassesConfigPath: opClassesPath);
        }
    }
}
