using System.Text.Json;
using VaultShared.Configuration;

namespace VaultShared.Tests;

/// <summary>
/// Copies the committed test-vault fixture into a temp directory twice and loads a
/// <see cref="VaultConfig"/> over both copies.
/// </summary>
/// <remarks>
/// The "test" copy is the default vault and is used for read-only assertions. It gains a
/// dot-directory file and a pair of duplicate note names, which the committed fixture omits so
/// that it stays a realistic vault. The "scratch" copy is the target for mutation tests.
/// </remarks>
public sealed class TestVaultFixture : IDisposable
{
    /// <summary>Gets the repository root, found by walking up from the test binaries.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Gets the temp directory holding both vault copies and the generated vaults.json.</summary>
    public string TempRoot { get; }

    /// <summary>Gets the configuration loaded over the "test" and "scratch" vaults.</summary>
    public VaultConfig Config { get; }

    /// <summary>Builds both vault copies and loads the configuration over them.</summary>
    /// <exception cref="InvalidOperationException">The generated vaults.json failed to load.</exception>
    public TestVaultFixture()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "vault-mcp-tests-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(RepoRoot, "test-vault");
        var test = Path.Combine(TempRoot, "test");
        var scratch = Path.Combine(TempRoot, "scratch");
        CopyDirectory(source, test);
        CopyDirectory(source, scratch);

        File.WriteAllText(EnsureParent(Path.Combine(test, ".obsidian", "junk.md")), "# Hidden\n");
        File.WriteAllText(EnsureParent(Path.Combine(test, "A", "Dupe.md")), "# Dupe\n");
        File.WriteAllText(EnsureParent(Path.Combine(test, "B", "Dupe.md")), "# Dupe\n");

        var configPath = Path.Combine(TempRoot, "vaults.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            default_vault = "test",
            vaults = new { test = new { path = test }, scratch = new { path = scratch } },
        }));
        // Load records failures into the sink and returns an empty config rather than throwing.
        // This fixture's config is always expected to be valid, so throw here instead of letting
        // an empty VaultConfig produce confusing failures further downstream.
        var sink = new RecordingProblemSink();
        Config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, sink);
        if (sink.IsFatallyDegraded)
            throw new InvalidOperationException(
                "TestVaultFixture's vaults.json failed to load: " + string.Join("; ", sink.Problems.Select(p => p.Detail)));
    }

    /// <summary>Deletes the temp directory and verifies the committed fixture is still clean.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(TempRoot, recursive: true);
        }
        catch (IOException)
        {
        }

        // The "scratch" copy is a real mutation target, so this check catches a write that landed
        // on `source` instead of `destination` in CopyDirectory, or a bad path elsewhere.
        TestVaultCleanGuard.AssertCommittedFixtureUntouched();
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "test-vault")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repo root containing test-vault/ not found above " + AppContext.BaseDirectory);
    }

    private static string EnsureParent(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
