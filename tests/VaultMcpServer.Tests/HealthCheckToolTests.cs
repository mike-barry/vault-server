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
/// Covers <see cref="HealthCheckTool"/>'s own output shape at the unit level.
/// <c>HealthCheckDispatchTests</c> covers the same feature set over HTTP.
/// </summary>
/// <remarks>
/// The wire names asserted here are the <c>snake_case</c> renderings of the report records' members
/// under the shared serializer options, which apply to results and arguments alike.
/// </remarks>
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

        // Each problem is checked as a whole, so a severity or a detail landing on the wrong
        // problem fails here rather than satisfying a check that only asks whether both values
        // appear somewhere in the report.
        var fatal = Assert.Single(problems, p => p.GetProperty("source").GetString() == "GrantStore");
        Assert.Equal("Fatal", fatal.GetProperty("severity").GetString());
        // Absolute filesystem paths in a problem's Detail string reach this tool's output verbatim.
        // There is no sanitized second rendering.
        Assert.Contains(@"C:\some\absolute\path\grants.json", fatal.GetProperty("detail").GetString()!);

        var degraded = Assert.Single(problems, p => p.GetProperty("source").GetString() == "VaultConfig");
        Assert.Equal("Degraded", degraded.GetProperty("severity").GetString());
        Assert.Contains(@"C:\vaults\broken", degraded.GetProperty("detail").GetString()!);

        Assert.All(problems, p => Assert.True(p.TryGetProperty("recorded_at_utc", out _)));
    }

    [Fact]
    public void Invoke_ReportsConfiguredVaults()
    {
        using var doc = Report(new ServerHealthState());

        var vaults = doc.RootElement.GetProperty("vaults");
        var configured = vaults.GetProperty("configured").EnumerateArray().Select(e => e.GetString()).ToList();
        // The fixture configures exactly these two vaults, so the whole list is asserted in the
        // case-insensitive order the report promises rather than only that both names appear.
        Assert.Equal(["scratch", "test"], configured);
        Assert.Equal("test", vaults.GetProperty("default_vault").GetString());
    }

    /// <summary>
    /// Verifies that a vault whose root directory is missing is skipped rather than failing the
    /// whole config file, and that the report lists it separately from the usable vaults along with
    /// the reason it was skipped.
    /// </summary>
    /// <remarks>
    /// A call naming a skipped vault is refused with <c>vault_unavailable</c>, while a name nobody
    /// configured gets <c>unknown_vault</c>. Listing only the usable vaults would leave an operator
    /// unable to tell those cases apart without making a call.
    ///
    /// The committed fixture has no missing-root vault, so this test builds its own config: <c>test</c>
    /// over a real directory alongside <c>broken</c> over a path that does not exist.
    /// </remarks>
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
        // The reason is the one the load recorded, carried verbatim, absolute path included.
        Assert.Contains(missing, skipped.GetProperty("broken").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the <c>skipped</c> member is always present. An empty map states that nothing
    /// was skipped, which an absent member would not.
    /// </summary>
    [Fact]
    public void Invoke_WithNothingSkipped_StillCarriesTheMember()
    {
        using var doc = Report(new ServerHealthState());

        Assert.Empty(doc.RootElement.GetProperty("vaults").GetProperty("skipped").EnumerateObject());
    }
}
