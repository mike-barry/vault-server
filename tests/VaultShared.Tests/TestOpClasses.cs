using VaultShared.Configuration;

namespace VaultShared.Tests;

/// <summary>
/// Provides the repository's own op-classes.json, loaded once, for tests that need a registry to
/// hand to a loader that validates op-class names.
/// </summary>
/// <remarks>
/// Use the committed file rather than a synthetic registry. A synthetic registry would always
/// contain whatever names the test needed, so it could never fail when the repository drops an
/// op class.
/// </remarks>
internal static class TestOpClasses
{
    /// <summary>Gets the registry loaded from the repository's op-classes.json.</summary>
    internal static OpClassRegistry Repo { get; } =
        OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new RecordingProblemSink());
}
