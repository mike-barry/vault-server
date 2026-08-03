using System.Text.Json;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultShared.Tools;
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
public sealed class HealthCheckToolTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private readonly VaultConfig _vaultConfig = fixture.Config;
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-health-tool-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static Grant MakeGrant(string id = "caller") =>
        new(id, new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, null,
            new Dictionary<string, VaultScope> { ["test"] = VaultScope.Unscoped });

    private JsonDocument Report(ServerHealthState healthState) => Report(healthState, _vaultConfig);

    private static JsonDocument Report(ServerHealthState healthState, VaultConfig vaultConfig)
    {
        var outcome = Run(new HealthCheckTool(healthState, vaultConfig), HealthCheckTool.Declaration, Args(new { }), MakeGrant());
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

    /// <summary>
    /// A vault whose root is not there is skipped rather than failing the file, and the report is
    /// where an operator finds out. It is the other half of the configured set, not a second copy of
    /// it: a skipped vault answers <c>vault_unavailable</c> to a call that names it, which is a
    /// different answer from the <c>unknown_vault</c> a name nobody configured gets - so a report
    /// that listed only the usable vaults would leave the operator unable to tell the two apart from
    /// anything but a refusal.
    ///
    /// The committed fixture has no such vault, so this case builds one: <c>test</c> over a real
    /// directory beside <c>broken</c> over a path that does not exist.
    /// </summary>
    [Fact]
    public void Invoke_ReportsSkippedVaultsWithTheirReasonBesideTheConfiguredOnes()
    {
        var root = Path.Combine(_tempDir, "test");
        Directory.CreateDirectory(root);
        var missing = Path.Combine(_tempDir, "not-there");
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            default_vault = "test",
            vaults = new Dictionary<string, object>
            {
                ["test"] = new { path = root.Replace('\\', '/') },
                ["broken"] = new { path = missing.Replace('\\', '/') },
            },
        }));

        var sink = new ServerHealthState();
        var opClasses = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());
        var config = VaultConfigLoader.Load(configPath, opClasses, sink);
        Assert.False(sink.IsFatallyDegraded);

        using var doc = Report(sink, config);

        var vaults = doc.RootElement.GetProperty("vaults");
        Assert.Equal(["test"], vaults.GetProperty("configured").EnumerateArray().Select(e => e.GetString()));

        var skipped = vaults.GetProperty("skipped");
        Assert.Equal(["broken"], skipped.EnumerateObject().Select(p => p.Name));
        // The reason is the one the load recorded, carried verbatim - the same settled decision that
        // lets an absolute path through a problem's detail.
        Assert.Contains(missing, skipped.GetProperty("broken").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The member is a finding either way: an empty map says the load skipped nothing,
    /// which is not what an absent member would say.</summary>
    [Fact]
    public void Invoke_WithNothingSkipped_StillCarriesTheMember()
    {
        using var doc = Report(new ServerHealthState());

        Assert.Empty(doc.RootElement.GetProperty("vaults").GetProperty("skipped").EnumerateObject());
    }
}
