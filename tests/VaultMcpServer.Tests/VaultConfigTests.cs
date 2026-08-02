using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class VaultConfigTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    [Fact]
    public void Resolve_FallsBackToDefaultVault()
    {
        Assert.Equal("test", fixture.Config.Resolve(null).Name);
        Assert.Equal("test", fixture.Config.Resolve("").Name);
    }

    [Fact]
    public void Resolve_ExplicitVault()
    {
        Assert.Equal("scratch", fixture.Config.Resolve("scratch").Name);
    }

    [Fact]
    public void Resolve_ReturnsCanonicalNameForCaseInsensitiveInput()
    {
        Assert.Equal("test", fixture.Config.Resolve("TEST").Name);
    }

    [Fact]
    public void Resolve_UnknownVaultThrows()
    {
        Assert.Throws<ArgumentException>(() => fixture.Config.Resolve("nope"));
    }
}

public sealed class VaultConfigLoadTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-config-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        var healthState = new ServerHealthState();
        var config = VaultConfig.Load(Path.Combine(_tempDir, "absent.json"), healthState);

        AssertFatalAndEmpty(healthState, config);
    }

    [Fact]
    public void Load_MissingDefaultVaultThrows()
    {
        var path = WriteConfig($$"""{ "vaults": { "a": {{JsonVault(_tempDir)}} } }""");
        var healthState = new ServerHealthState();
        var config = VaultConfig.Load(path, healthState);

        AssertFatalAndEmpty(healthState, config);
    }

    [Fact]
    public void Load_DefaultNotInVaultsThrows()
    {
        var path = WriteConfig($$"""{ "default_vault": "b", "vaults": { "a": {{JsonVault(_tempDir)}} } }""");
        var healthState = new ServerHealthState();
        var config = VaultConfig.Load(path, healthState);

        AssertFatalAndEmpty(healthState, config);
    }

    [Fact]
    public void Load_EmptyVaultsThrows()
    {
        var path = WriteConfig("""{ "default_vault": "a", "vaults": {} }""");
        var healthState = new ServerHealthState();
        var config = VaultConfig.Load(path, healthState);

        AssertFatalAndEmpty(healthState, config);
    }

    [Fact]
    public void Load_NonexistentRoot_SkipsThatVaultNotTheFile()
    {
        // Two configured vaults, one with a missing root: the load is NOT fatal - the broken
        // vault is skipped (with a Degraded record) and the healthy vault keeps working.
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(_tempDir)}}, "b": {{JsonVault(Path.Combine(_tempDir, "no-such-dir"))}} } }""");
        var healthState = new ServerHealthState();
        var config = VaultConfig.Load(path, healthState);

        Assert.False(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal("VaultConfig", problem.Source);
        Assert.Contains("\"b\"", problem.Detail);
        Assert.Contains("does not exist", problem.Detail);

        Assert.Equal("a", config.Resolve("a").Name);
        Assert.Single(config.Vaults);
        var skippedReason = Assert.Single(config.SkippedVaults).Value;
        Assert.Contains("does not exist", skippedReason);
        Assert.True(config.IsConfigured("b"));
        Assert.Contains("b", config.ConfiguredVaultNames);
    }

    [Fact]
    public void Resolve_SkippedVaultFailsAsUnavailableDistinctFromUnknown()
    {
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(_tempDir)}}, "b": {{JsonVault(Path.Combine(_tempDir, "no-such-dir"))}} } }""");
        var config = VaultConfig.Load(path, new ServerHealthState());

        var unavailable = Assert.Throws<ArgumentException>(() => config.Resolve("b"));
        Assert.Contains("currently unavailable", unavailable.Message);
        Assert.Contains("\"b\"", unavailable.Message);

        var unknown = Assert.Throws<ArgumentException>(() => config.Resolve("nope"));
        Assert.Contains("Unknown vault", unknown.Message);
        Assert.DoesNotContain("currently unavailable", unknown.Message);
    }

    [Fact]
    public void Load_EveryRootMissing_IsDegradedNotFatal()
    {
        // Per-entry skip semantics hold even when every entry skips: the server still starts
        // (nothing resolvable, one Degraded per vault) rather than going fatal.
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(Path.Combine(_tempDir, "gone-a"))}}, "b": {{JsonVault(Path.Combine(_tempDir, "gone-b"))}} } }""");
        var healthState = new ServerHealthState();
        var config = VaultConfig.Load(path, healthState);

        Assert.False(healthState.IsFatallyDegraded);
        Assert.Equal(2, healthState.Problems.Count);
        Assert.All(healthState.Problems, p => Assert.Equal(ProblemSeverity.Degraded, p.Severity));
        Assert.Empty(config.Vaults);
        Assert.Equal(2, config.SkippedVaults.Count);
        // The default vault is skipped, so resolving the default fails as unavailable.
        var ex = Assert.Throws<ArgumentException>(() => config.Resolve(null));
        Assert.Contains("currently unavailable", ex.Message);
    }

    /// <summary>Shared assertion for every whole-file-failure case above: the load no
    /// longer throws - it records a Fatal problem from source "VaultConfig" and returns an
    /// inert config with no resolvable vaults.</summary>
    private static void AssertFatalAndEmpty(ServerHealthState healthState, VaultConfig config)
    {
        Assert.True(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Fatal, problem.Severity);
        Assert.Equal("VaultConfig", problem.Source);
        Assert.NotEmpty(problem.Detail);

        Assert.Empty(config.Vaults);
        Assert.Throws<ArgumentException>(() => config.Resolve(null));
    }

    private static string JsonVault(string path) =>
        $$"""{ "path": {{System.Text.Json.JsonSerializer.Serialize(path)}} }""";
}
