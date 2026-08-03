using System.Text.Json;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Health;

namespace VaultShared.Tests;

/// <summary>
/// The declaration's place in the load cascade: one instance per file however many vaults name it,
/// a failure recorded Degraded per vault rather than taking the host down, and a state for every
/// vault whether or not it named a file. Writes its own vaults.json over temp directories, because
/// every case here turns on what a vault entry names and whether that file is there.
/// </summary>
public sealed class FrontmatterRegistriesTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-registries-").FullName;

    private static readonly OpClassRegistry OpClasses =
        OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new RecordingProblemSink());

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Writes a vaults.json over two rooted vaults, each optionally naming a declaration
    /// file, and runs the cascade over it.</summary>
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
        // The file is the identity. A vocabulary declared once is one object however many vaults
        // take it, so nothing can hold two readings of one declaration.
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
        // Degraded rather than Fatal: everything but frontmatter writes in this vault is still
        // correct, and taking the host down would refuse operations the failure does not touch.
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
        // Fail-closed with nothing to point at: the vault has no vocabulary, so every frontmatter
        // write in it refuses, and the condition is visible in health state rather than inferred
        // from those refusals.
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
        // tier, and is never discovered by walking vault content. A copy inside the vault must not
        // be what gets read.
        WriteDeclaration("registry.json", SyntheticDeclaration.Valid);
        File.WriteAllText(Path.Combine(Root("first"), "registry.json"), "{ not json");

        Assert.IsType<RegistryState.Loaded>(Load(new RecordingProblemSink(), "registry.json", null).For("first"));
    }
}
