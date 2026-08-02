using System.Text.Json;
using VaultShared.Configuration;

namespace VaultShared.Tests;

/// <summary>
/// Copies the committed test-vault fixture into a temp directory twice - "test" (the
/// default vault) for read-only assertions, with injected dot-directory and
/// duplicate-name cases the committed fixture deliberately omits, and "scratch" for
/// mutation tests - then loads a VaultConfig over both.
/// </summary>
public sealed class TestVaultFixture : IDisposable
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public string TempRoot { get; }
    public VaultConfig Config { get; }

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
        // Load records failures into the sink and returns an inert config rather than throwing.
        // This fixture's config is always expected to be valid, so surface any recorded problem
        // loudly here rather than letting an empty VaultConfig produce confusing downstream test
        // failures.
        var sink = new RecordingProblemSink();
        Config = VaultConfig.Load(configPath, sink);
        if (sink.IsFatallyDegraded)
            throw new InvalidOperationException(
                "TestVaultFixture's vaults.json failed to load: " + string.Join("; ", sink.Problems.Select(p => p.Detail)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(TempRoot, recursive: true);
        }
        catch (IOException)
        {
        }

        // Detection half of the test-isolation guard pair (see TestVaultCleanGuard's remarks).
        // This fixture has no PowerShell config handoff to misfire, but its "scratch" copy is a
        // real mutation target, so the same cheap check catches any accidental write against
        // `source` instead of `destination` in CopyDirectory or a bad path elsewhere.
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
