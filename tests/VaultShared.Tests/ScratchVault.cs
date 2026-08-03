using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// A real directory tree under an unscoped grant, for the one part of the layer whose subject is
/// the filesystem. Admission is covered elsewhere, so the grant here is wide on purpose: what these
/// cases exercise is what a primitive does once it holds an admitted path.
/// </summary>
internal sealed class ScratchVault : IDisposable
{
    /// <param name="scope">
    /// The grant's scope in this vault. Unscoped unless a case is about what a narrower one
    /// withholds.
    /// </param>
    /// <param name="engine">
    /// Where the search engine is. The default points at whatever <c>PATH</c> resolves, which the
    /// enumeration and search cases run against for real.
    /// </param>
    public ScratchVault(VaultScope? scope = null, SearchEngine? engine = null)
    {
        Root = Directory.CreateTempSubdirectory("vault-files-").FullName;
        Vault = new Vault("scratch", Root, PathPolicy.None);
        Access = new VaultAccess(Vault, new Grant(
            "test-grant",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scratch" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["scratch"] = scope ?? VaultScope.Unscoped }),
            engine);
    }

    public string Root { get; }

    public Vault Vault { get; }

    public VaultAccess Access { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The admitted write proof for a vault-relative path.</summary>
    public WritePath ToWrite(string relativePath) =>
        Assert.IsType<PathResolution<WritePath>.Admitted>(Access.ForWrite(relativePath)).Path;

    /// <summary>The admitted read proof for a vault-relative path.</summary>
    public ReadPath ToRead(string relativePath) =>
        Assert.IsType<PathResolution<ReadPath>.Admitted>(Access.ForRead(relativePath)).Path;

    public string Absolute(string relativePath) =>
        Path.Combine([Root, .. relativePath.Split('/')]);

    public ScratchVault WithFolder(string relativePath)
    {
        Directory.CreateDirectory(Absolute(relativePath));
        return this;
    }

    public ScratchVault WithNote(string relativePath, string content = "# Note\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Absolute(relativePath))!);
        File.WriteAllText(Absolute(relativePath), content);
        return this;
    }

    /// <summary>
    /// Makes one file unreadable for as long as the returned handle is held, so a search has a file
    /// it must fail on. Windows refuses a second reader of an exclusively opened file; elsewhere the
    /// mode bits do it.
    /// </summary>
    public IDisposable Unreadable(string relativePath)
    {
        var absolute = Absolute(relativePath);
        if (!OperatingSystem.IsWindows())
        {
            var original = File.GetUnixFileMode(absolute);
            File.SetUnixFileMode(absolute, UnixFileMode.None);
            // The platform test is repeated inside the restore because the analyzer reads the
            // lambda on its own, not at the site it was built from.
            return new Restore(() =>
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(absolute, original);
            });
        }

        return File.Open(absolute, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public string TextOf(string relativePath) => File.ReadAllText(Absolute(relativePath));

    public byte[] BytesOf(string relativePath) => File.ReadAllBytes(Absolute(relativePath));

    public bool HasFile(string relativePath) => File.Exists(Absolute(relativePath));

    public bool HasFolder(string relativePath) => Directory.Exists(Absolute(relativePath));

    /// <summary>The spelling an entry actually has on disk, which a case-only rename changes.</summary>
    public string SpellingOf(string relativePath) =>
        Path.GetFileName(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Absolute(relativePath))!)
            .Single(e => string.Equals(Path.GetFileName(e), Path.GetFileName(Absolute(relativePath)), StringComparison.OrdinalIgnoreCase)));
}
