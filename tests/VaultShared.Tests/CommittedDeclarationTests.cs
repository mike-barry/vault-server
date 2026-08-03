using VaultShared.Frontmatter;
using VaultShared.Health;

namespace VaultShared.Tests;

/// <summary>
/// The declaration the committed <c>vaults.json</c> names for the fixture vault, loaded through the
/// real loader against the repository's real op-class configuration.
///
/// It is the one thing in this suite asserted against a file the repository ships rather than
/// against a synthetic vocabulary, and the reason is that the file is configuration: every
/// load-time check runs against it, and a declaration that will not load leaves a host Degraded with
/// every frontmatter write refusing. Nothing here asserts what the vocabulary <em>says</em> - the
/// mechanism's cases are <see cref="Frontmatter.FrontmatterWritePathTests"/>'s, against a
/// declaration they own.
/// </summary>
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
    /// The vault names it, so a started host loads it rather than recording the Degraded that a
    /// vault naming none produces. This is the whole of what the configuration has to say: where
    /// the file is resolved from is the loader's, and it is the directory holding
    /// <c>vaults.json</c>.
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

    /// <summary>Every variant the declaration carries reaches a client as its own branch, so the
    /// vocabulary a caller composes an entry against is the one the write path enforces.</summary>
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
