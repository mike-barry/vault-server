using System.Text.Json;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Health;

namespace VaultShared.Tests;

/// <summary>
/// Verifies how frontmatter declarations are loaded across vaults: one registry instance per file
/// however many vaults name it, a per-vault failure recorded as Degraded rather than fatal, and a
/// state for every vault whether or not it names a file. Each case writes its own vaults.json into
/// a temporary directory, because the behaviour turns on what a vault entry names and whether that
/// file exists.
/// </summary>
public sealed class FrontmatterRegistriesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-registries-").FullName;

    private static readonly OpClassRegistry OpClasses =
        OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new RecordingProblemSink());

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>
    /// Writes a vaults.json describing two rooted vaults, each optionally naming a declaration
    /// file, and loads the registries from it.
    /// </summary>
    /// <param name="sink">The sink that records configuration and load problems.</param>
    /// <param name="firstNames">The declaration file the first vault names, or null for none.</param>
    /// <param name="secondNames">The declaration file the second vault names, or null for none.</param>
    /// <returns>The loaded registries for both vaults.</returns>
    private FrontmatterRegistries Load(RecordingProblemSink sink, string? firstNames, string? secondNames)
    {
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, $$"""
            {
              "default_vault": "first",
              "vaults": {
                "first":  { "path": {{JsonSerializer.Serialize(Root("first"))}}{{Names(firstNames)}} },
                "second": { "path": {{JsonSerializer.Serialize(Root("second"))}}{{Names(secondNames)}} }
              }
            }
            """);

        var config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, sink);
        return FrontmatterRegistries.Load(config, _tempDir, OpClasses, sink);

        static string Names(string? file) => file is null ? "" : $""", "frontmatter_registry": {JsonSerializer.Serialize(file)}""";
    }

    private string Root(string name)
    {
        var root = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(root);
        return root;
    }

    private void WriteDeclaration(string name, string json) => File.WriteAllText(Path.Combine(_tempDir, name), json);

    private static string[] Problems(RecordingProblemSink sink) => [.. sink.Problems.Select(p => p.Detail)];

    [Fact]
    public void Load_TwoVaultsNamingOneFileShareOneInstance()
    {
        // The file path is the identity, so one declaration is read once and shared by every vault
        // naming it.
        WriteDeclaration("shared.json", SyntheticDeclaration.Valid);
        var sink = new RecordingProblemSink();

        var registries = Load(sink, "shared.json", "shared.json");

        var first = Assert.IsType<RegistryState.Loaded>(registries.For("first"));
        var second = Assert.IsType<RegistryState.Loaded>(registries.For("second"));
        Assert.Same(first.Registry, second.Registry);
        Assert.Empty(Problems(sink));
    }

    [Fact]
    public void Load_TwoVaultsNamingTwoFilesGetTwoInstances()
    {
        WriteDeclaration("one.json", SyntheticDeclaration.Valid);
        WriteDeclaration("two.json", SyntheticDeclaration.Valid);

        var registries = Load(new RecordingProblemSink(), "one.json", "two.json");

        Assert.NotSame(
            Assert.IsType<RegistryState.Loaded>(registries.For("first")).Registry,
            Assert.IsType<RegistryState.Loaded>(registries.For("second")).Registry);
    }

    [Fact]
    public void Load_AMissingDeclarationIsAbsentAndRecordedDegraded()
    {
        var sink = new RecordingProblemSink();

        var registries = Load(sink, "not-there.json", null);

        Assert.IsType<RegistryState.Absent>(registries.For("first"));
        // Degraded rather than fatal, because everything except frontmatter writes in this vault
        // still works and taking the host down would refuse operations the failure does not touch.
        Assert.False(sink.IsFatallyDegraded);
        Assert.All(sink.Problems, p => Assert.Equal(ProblemSeverity.Degraded, p.Severity));
        Assert.Contains(Problems(sink), p => p.Contains("first") && p.Contains("is not there"));
    }

    [Fact]
    public void Load_AnUnloadableDeclarationIsRecordedDegradedWithItsReason()
    {
        WriteDeclaration("broken.json", """{ "keys": { "k": { "shape": "scalar", "value": { "kind": "regex" } } } }""");
        var sink = new RecordingProblemSink();

        var registries = Load(sink, "broken.json", null);

        var unloadable = Assert.IsType<RegistryState.Unloadable>(registries.For("first"));
        Assert.Contains("unknown constraint kind", unloadable.Why);
        Assert.False(sink.IsFatallyDegraded);
        Assert.Contains(Problems(sink), p => p.Contains("first") && p.Contains("unknown constraint kind"));
    }

    [Fact]
    public void Load_AVaultNamingNoDeclarationIsAbsentAndSaysSo()
    {
        // The vault has no declaration, so every frontmatter write in it refuses. The condition is
        // reported to the problem sink so it shows in health rather than only in those refusals.
        var sink = new RecordingProblemSink();

        var registries = Load(sink, null, null);

        Assert.Equal(string.Empty, Assert.IsType<RegistryState.Absent>(registries.For("first")).Path);
        Assert.Contains(Problems(sink), p => p.Contains("names no \"frontmatter_registry\""));
    }

    [Fact]
    public void Load_OneVaultsFailureLeavesTheOtherLoaded()
    {
        WriteDeclaration("good.json", SyntheticDeclaration.Valid);
        var sink = new RecordingProblemSink();

        var registries = Load(sink, "not-there.json", "good.json");

        Assert.IsType<RegistryState.Absent>(registries.For("first"));
        Assert.IsType<RegistryState.Loaded>(registries.For("second"));
    }

    [Fact]
    public void For_AnUnknownVaultAnswersAbsentRatherThanThrowing()
    {
        var registries = Load(new RecordingProblemSink(), null, null);

        Assert.IsType<RegistryState.Absent>(registries.For("no-such-vault"));
        Assert.IsType<RegistryState.Absent>(FrontmatterRegistries.None.For("first"));
    }

    [Fact]
    public void Load_ResolvesTheDeclarationAgainstTheConfigDirectoryRatherThanTheVaultRoot()
    {
        // The declaration lives beside the grant and op-class configuration, at the same trust
        // level, and is never found by walking vault content. A copy inside the vault, which vault
        // writers can create, must not be the file that gets read.
        WriteDeclaration("registry.json", SyntheticDeclaration.Valid);
        File.WriteAllText(Path.Combine(Root("first"), "registry.json"), "{ not json");

        Assert.IsType<RegistryState.Loaded>(Load(new RecordingProblemSink(), "registry.json", null).For("first"));
    }
}
