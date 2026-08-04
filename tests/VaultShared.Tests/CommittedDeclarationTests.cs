using VaultShared.Frontmatter;
using VaultShared.Health;

namespace VaultShared.Tests;

/// <summary>
/// Tests the frontmatter declaration that the committed <c>vaults.json</c> names for the fixture
/// vault, loaded through the real loader against the repository's real op-class configuration.
/// </summary>
/// <remarks>
/// These are the only tests asserted against files the repository ships rather than against a
/// synthetic declaration, because a declaration that fails to load leaves a host degraded with
/// every frontmatter write refusing. They check that the committed files load and stay wired up,
/// not what the vocabulary contains; the behaviour of the write path is covered by
/// <see cref="Frontmatter.FrontmatterWritePathTests"/> against a declaration those tests own.
/// </remarks>
public sealed class CommittedDeclarationTests
{
    private static string Path(string name) => System.IO.Path.Combine(TestVaultFixture.RepoRoot, name);

    [Fact]
    public void TheCommittedDeclarationLoadsClean()
    {
        var state = FrontmatterRegistryLoader.Load(Path("frontmatter-registry.json"), TestOpClasses.Repo);

        var loaded = Assert.IsType<RegistryState.Loaded>(state);
        Assert.NotEmpty(loaded.Registry.Keys);
        Assert.NotEmpty(loaded.Registry.EntrySchemas);
    }

    /// <summary>
    /// Checks that the committed vault names a frontmatter declaration, so a host that starts on
    /// this configuration loads one instead of recording the degraded problem a vault naming none
    /// produces. The loader resolves the file relative to the directory holding <c>vaults.json</c>.
    /// </summary>
    [Fact]
    public void TheFixtureVaultNamesItAndTheCascadeLoadsItWithNoProblemRecorded()
    {
        var sink = new RecordingProblemSink();
        var config = Configuration.VaultConfigLoader.Load(Path("vaults.json"), TestOpClasses.Repo, sink);

        var registries = FrontmatterRegistries.Load(config, TestVaultFixture.RepoRoot, TestOpClasses.Repo, sink);

        Assert.IsType<RegistryState.Loaded>(registries.For("test"));
        Assert.DoesNotContain(sink.Problems, p => p.Severity == ProblemSeverity.Degraded);
        Assert.DoesNotContain(sink.Problems, p => p.Severity == ProblemSeverity.Fatal);
    }

    /// <summary>
    /// Checks that the fixture vault's path policy still declares the guides folder as always
    /// readable. Dropping the entry would narrow the floor silently, and every grant would stop
    /// reaching the material it is instructed to obey.
    /// </summary>
    [Fact]
    public void TheFixtureVaultDeclaresTheGuidesFolderAsTheReadFloor()
    {
        var config = Configuration.VaultConfigLoader.Load(Path("vaults.json"), TestOpClasses.Repo, new RecordingProblemSink());

        Assert.Equal<string[]>(["Resources/Vault/Guides"], [.. config.Vaults["test"].PathPolicy.AlwaysReadableRoots]);
    }

    /// <summary>
    /// Checks that every variant in the declaration reaches a client as its own branch, so the
    /// vocabulary a caller composes an entry against is the one the write path enforces.
    /// </summary>
    [Fact]
    public void ItsVariantsProjectIntoTheAdvertisedVocabulary()
    {
        var vocabulary = FrontmatterVocabulary.Of(
            FrontmatterRegistryLoader.Load(Path("frontmatter-registry.json"), TestOpClasses.Repo));

        Assert.Contains(vocabulary.Keys, k => k.Key == "nav.contents");
        Assert.Contains(vocabulary.Branches, b => b.Variant == "use_skill" && b.Keys.Contains("agent.instructions"));
        // Two keys naming one schema hold one object, so their shared variant is one branch.
        Assert.Single(vocabulary.Branches, b => b.Variant == "link");
    }
}
