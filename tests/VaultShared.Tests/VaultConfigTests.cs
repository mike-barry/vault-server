using VaultShared.Health;
using VaultShared.Configuration;

namespace VaultShared.Tests;

/// <summary>
/// What loading vaults.json produces. Which of those vaults is usable, and what a name resolves
/// to, is the access layer's - see <c>VaultResolutionTests</c>.
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
        var config = VaultConfigLoader.Load(Path.Combine(_tempDir, "absent.json"), sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_MissingDefaultVaultThrows()
    {
        var path = WriteConfig($$"""{ "vaults": { "a": {{JsonVault(_tempDir)}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_DefaultNotInVaultsThrows()
    {
        var path = WriteConfig($$"""{ "default_vault": "b", "vaults": { "a": {{JsonVault(_tempDir)}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_EmptyVaultsThrows()
    {
        var path = WriteConfig("""{ "default_vault": "a", "vaults": {} }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, sink);

        AssertFatalAndEmpty(sink, config);
    }

    [Fact]
    public void Load_NonexistentRoot_SkipsThatVaultNotTheFile()
    {
        // Two configured vaults, one with a missing root: the load is NOT fatal - the broken
        // vault is skipped (with a Degraded record) and the healthy vault keeps working.
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(_tempDir)}}, "b": {{JsonVault(Path.Combine(_tempDir, "no-such-dir"))}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, sink);

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
        // Per-entry skip semantics hold even when every entry skips: the server still starts
        // (nothing resolvable, one Degraded per vault) rather than going fatal.
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonVault(Path.Combine(_tempDir, "gone-a"))}}, "b": {{JsonVault(Path.Combine(_tempDir, "gone-b"))}} } }""");
        var sink = new RecordingProblemSink();
        var config = VaultConfigLoader.Load(path, sink);

        Assert.False(sink.IsFatallyDegraded);
        Assert.Equal(2, sink.Problems.Count);
        Assert.All(sink.Problems, p => Assert.Equal(ProblemSeverity.Degraded, p.Severity));
        Assert.Empty(config.Vaults);
        Assert.Equal(2, config.SkippedVaults.Count);
        // The default vault is among the skipped, which is what makes resolving it answer
        // unavailable rather than unknown.
        Assert.Contains(config.DefaultVault, config.SkippedVaults.Keys);
    }

    /// <summary>Shared assertion for every whole-file-failure case above: the load no
    /// longer throws - it records a Fatal problem from source "VaultConfig" and returns an
    /// inert config with no resolvable vaults.</summary>
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

    private static string JsonVault(string path) =>
        $$"""{ "path": {{System.Text.Json.JsonSerializer.Serialize(path)}} }""";
}
