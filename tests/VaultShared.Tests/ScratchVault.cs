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
    public ScratchVault()
    {
        Root = Directory.CreateTempSubdirectory("vault-files-").FullName;
        Vault = new Vault("scratch", Root, PathPolicy.None);
        Access = new VaultAccess(Vault, new Grant(
            "test-grant",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scratch" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["scratch"] = VaultScope.Unscoped }));
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

    public string TextOf(string relativePath) => File.ReadAllText(Absolute(relativePath));

    public byte[] BytesOf(string relativePath) => File.ReadAllBytes(Absolute(relativePath));

    public bool HasFile(string relativePath) => File.Exists(Absolute(relativePath));

    public bool HasFolder(string relativePath) => Directory.Exists(Absolute(relativePath));

    /// <summary>The spelling an entry actually has on disk, which a case-only rename changes.</summary>
    public string SpellingOf(string relativePath) =>
        Path.GetFileName(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Absolute(relativePath))!)
            .Single(e => string.Equals(Path.GetFileName(e), Path.GetFileName(Absolute(relativePath)), StringComparison.OrdinalIgnoreCase)));
}
