using VaultShared.Configuration;

namespace VaultShared.Tests;

/// <summary>
/// The repository's own op-classes.json, loaded once for the cases that only need a registry to
/// hand to a loader that validates names against one. Deliberately the committed file rather than a
/// synthetic set: every one of those checks is a claim about a name an operator would really write,
/// and a test registry containing whatever the case needed could not fail on a class the repo
/// dropped.
/// </summary>
internal static class TestOpClasses
{
    internal static OpClassRegistry Repo { get; } =
        OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new RecordingProblemSink());
}
