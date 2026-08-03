using System.Text.Json;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tests;
using static VaultMcpServer.Tests.DeclaredToolHarness;

namespace VaultMcpServer.Tests;

/// <summary>
/// Unit-level coverage for <see cref="HealthCheckTool"/>'s own output shape - as distinct
/// from <c>HealthCheckDispatchTests</c>' HTTP-level, end-to-end coverage (self-gated floor,
/// BROKEN listing, initialize instructions) of the same feature set.
///
/// The wire names asserted here are the <c>snake_case</c> renderings of the report records' members
/// under the shared serializer options, which govern results and arguments alike.
/// </summary>
public sealed class HealthCheckToolTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    private readonly VaultConfig _vaultConfig = fixture.Config;

    private static Grant MakeGrant(string id = "caller") =>
        new(id, new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, null,
            new Dictionary<string, VaultScope> { ["test"] = VaultScope.Unscoped });

    private JsonDocument Report(ServerHealthState healthState)
    {
        var outcome = Run(new HealthCheckTool(healthState, _vaultConfig), HealthCheckTool.Declaration, Args(new { }), MakeGrant());
        Assert.False(outcome.IsError);
        return JsonDocument.Parse(outcome.Payload);
    }

    [Fact]
    public void Invoke_HealthyState_ReportsNotFatallyDegradedAndNoProblems()
    {
        using var doc = Report(new ServerHealthState());

        Assert.False(doc.RootElement.GetProperty("is_fatally_degraded").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("problems").EnumerateArray());
    }

    [Fact]
    public void Invoke_WithRecordedProblems_ReportsThemInDetailIncludingPathsVerbatim()
    {
        var healthState = new ServerHealthState();
        healthState.RecordFatal("GrantStore", @"grants.json not found: C:\some\absolute\path\grants.json");
        healthState.RecordDegraded("VaultConfig", @"vault ""broken"" skipped - root directory does not exist: C:\vaults\broken");

        using var doc = Report(healthState);

        Assert.True(doc.RootElement.GetProperty("is_fatally_degraded").GetBoolean());
        var problems = doc.RootElement.GetProperty("problems").EnumerateArray().ToList();
        Assert.Equal(2, problems.Count);

        // Settled decision: absolute filesystem paths in problem Detail strings flow
        // verbatim into this tool's output - no sanitized second rendering path.
        Assert.Contains(problems, p => p.GetProperty("detail").GetString()!.Contains(@"C:\some\absolute\path\grants.json"));
        Assert.Contains(problems, p => p.GetProperty("detail").GetString()!.Contains(@"C:\vaults\broken"));
        Assert.Contains(problems, p => p.GetProperty("severity").GetString() == "Fatal");
        Assert.Contains(problems, p => p.GetProperty("severity").GetString() == "Degraded");
        Assert.All(problems, p => Assert.True(p.TryGetProperty("recorded_at_utc", out _)));
    }

    [Fact]
    public void Invoke_ReportsConfiguredVaults()
    {
        using var doc = Report(new ServerHealthState());

        var vaults = doc.RootElement.GetProperty("vaults");
        var configured = vaults.GetProperty("configured").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("test", configured);
        Assert.Contains("scratch", configured);
        Assert.Equal(_vaultConfig.DefaultVault, vaults.GetProperty("default_vault").GetString());
    }
}
