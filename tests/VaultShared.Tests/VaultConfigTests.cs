using VaultShared.Health;
using VaultShared.Configuration;

namespace VaultShared.Tests;

/// <summary>
/// Tests what loading vaults.json produces. <c>VaultResolutionTests</c> covers which of those
/// vaults is usable and what a vault name resolves to.
/// </summary>
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
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(Path.Combine(_tempDir, "absent.json"), TestOpClasses.Repo, sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_MissingDefaultVaultThrows()
    {
        var path = WriteConfig($$"""{ "vaults": { "a": {{JsonVault(_tempDir)}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_DefaultNotInVaultsThrows()
    {
        var path = WriteConfig($$"""{ "default_vault": "b", "vaults": { "a": {{JsonVault(_tempDir)}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_EmptyVaultsThrows()
    {
        var path = WriteConfig("""{ "default_vault": "a", "vaults": {} }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_NonexistentRoot_SkipsThatVaultNotTheFile()
    {
        // Two configured vaults, one with a missing root. The load is not fatal: the broken vault
        // is skipped with a degraded problem recorded, and the healthy vault keeps working.
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(_tempDir)}}, "b": {{JsonVault(Path.Combine(_tempDir, "no-such-dir"))}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        Assert.False(sink.IsFatallyDegraded);
        var problem = Assert.Single(sink.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal("VaultConfig", problem.Source);
        Assert.Contains("\"b\"", problem.Detail);
        Assert.Contains("does not exist", problem.Detail);

        Assert.Equal("a", Assert.Single(config.Vaults).Key);
        var skippedReason = Assert.Single(config.SkippedVaults).Value;
        Assert.Contains("does not exist", skippedReason);
        Assert.True(config.IsConfigured("b"));
        Assert.Contains("b", config.ConfiguredVaultNames);
    }

    [Fact]
    public void Load_EveryRootMissing_IsDegradedNotFatal()
    {
        // Skipping is per entry even when every entry skips, so the server still starts, with one
        // degraded problem per vault and nothing resolvable, rather than failing fatally.
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(Path.Combine(_tempDir, "gone-a"))}}, "b": {{JsonVault(Path.Combine(_tempDir, "gone-b"))}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        Assert.False(sink.IsFatallyDegraded);
        Assert.Equal(2, sink.Problems.Count);
        Assert.All(sink.Problems, p => Assert.Equal(ProblemSeverity.Degraded, p.Severity));
        Assert.Empty(config.Vaults);
        Assert.Equal(2, config.SkippedVaults.Count);
        // The default vault is among the skipped, which is what makes resolving it answer
        // unavailable rather than unknown.
        Assert.Contains(config.DefaultVault, config.SkippedVaults.Keys);
    }

    [Fact]
    public void Load_CarriesTheDeclaredFrontmatterRegistryName()
    {
        // The name is carried exactly as the entry spells it. The registry cascade resolves it,
        // which keeps the file's location a property of the configuration rather than the vault.
        var path = WriteConfig($$"""
            { "default_vault": "a", "vaults": { "a": { "path": {{JsonPath(_tempDir)}}, "frontmatter_registry": "frontmatter-registry.json" } } }
            """);
        var sink = new RecordingProblemSink();

        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        Assert.Equal("frontmatter-registry.json", config.Vaults["a"].FrontmatterRegistry);
        Assert.Empty(sink.Problems);
    }

    [Fact]
    public void Load_AVaultNamingNoRegistryStaysValid()
    {
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(_tempDir)}} } }""");
        var sink = new RecordingProblemSink();

        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        Assert.Null(config.Vaults["a"].FrontmatterRegistry);
        Assert.Empty(sink.Problems);
    }

    [Fact]
    public void Load_AnEmptyFrontmatterRegistryIsFatal()
    {
        var path = WriteConfig($$"""
            { "default_vault": "a", "vaults": { "a": { "path": {{JsonPath(_tempDir)}}, "frontmatter_registry": "" } } }
            """);
        var sink = new RecordingProblemSink();

        var config = VaultConfigLoader.Load(path, TestOpClasses.Repo, sink);

        AssertFatalAndEmpty(sink, config);
    }

    /// <summary>
    /// Asserts the outcome shared by every whole-file failure case above: the load records one
    /// fatal problem from source "VaultConfig" and returns an empty config, rather than throwing.
    /// </summary>
    /// <param name="sink">The sink the failed load recorded into.</param>
    /// <param name="config">The config the failed load returned.</param>
    private static void AssertFatalAndEmpty(RecordingProblemSink sink, VaultConfig config)
    {
        Assert.True(sink.IsFatallyDegraded);
        var problem = Assert.Single(sink.Problems);
        Assert.Equal(ProblemSeverity.Fatal, problem.Severity);
        Assert.Equal("VaultConfig", problem.Source);
        Assert.NotEmpty(problem.Detail);

        Assert.Empty(config.Vaults);
        Assert.Empty(config.SkippedVaults);
    }

    private static string JsonVault(string path) => $$"""{ "path": {{JsonPath(path)}} }""";

    private static string JsonPath(string path) => System.Text.Json.JsonSerializer.Serialize(path);
}
