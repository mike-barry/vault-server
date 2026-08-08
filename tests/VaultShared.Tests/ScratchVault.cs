using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Creates a real directory tree in a temp folder, wrapped in a vault, a grant, and a
/// <see cref="VaultAccess"/>, for tests whose subject is the filesystem.
/// </summary>
/// <remarks>
/// The grant defaults to unscoped and holding no op classes. Path admission and op-class
/// enforcement are covered by other tests; these cases exercise what a primitive does once it
/// already holds an admitted path. Deleting the temp tree on <see cref="Dispose"/> ignores
/// <see cref="IOException"/>, so a file still held open does not fail the test.
/// </remarks>
internal sealed class ScratchVault : IDisposable
{
    /// <summary>Initializes a scratch vault backed by a new temp directory.</summary>
    /// <param name="scope">
    /// The grant's scope in this vault. Defaults to <see cref="VaultScope.Unscoped"/>.
    /// </param>
    /// <param name="engine">
    /// Where the search engine is. Defaults to the engine as <c>PATH</c> resolves it, which the
    /// enumeration and search tests run against for real.
    /// </param>
    /// <param name="opClasses">
    /// The op classes the grant holds. Defaults to none, so a test that expects a mutation to
    /// succeed must name the classes it needs.
    /// </param>
    /// <param name="policy">
    /// The vault's path policy. Defaults to <see cref="PathPolicy.None"/>.
    /// </param>
    public ScratchVault(
        VaultScope? scope = null,
        SearchEngine? engine = null,
        IReadOnlyCollection<string>? opClasses = null,
        PathPolicy? policy = null)
    {
        Root = Directory.CreateTempSubdirectory("vault-files-").FullName;
        Vault = new Vault("scratch", Root, policy ?? PathPolicy.None);
        Grant = new Grant(
            "test-grant",
            new HashSet<string>(opClasses ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scratch" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["scratch"] = scope ?? VaultScope.Unscoped });
        Access = new VaultAccess(Vault, Grant, engine);
    }

    /// <summary>Gets the absolute path of the temp directory backing this vault.</summary>
    public string Root { get; }

    /// <summary>Gets the vault rooted at <see cref="Root"/>.</summary>
    public Vault Vault { get; }

    /// <summary>Gets the grant the tests operate under.</summary>
    public Grant Grant { get; }

    /// <summary>Gets access to <see cref="Vault"/> under <see cref="Grant"/>.</summary>
    public VaultAccess Access { get; }

    /// <summary>Deletes the temp directory, ignoring failures caused by files still held open.</summary>
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

    /// <summary>Resolves a path for writing and returns the admitted write path.</summary>
    /// <param name="relativePath">The vault-relative path, using forward slashes.</param>
    /// <returns>The admitted write path.</returns>
    /// <exception cref="Xunit.Sdk.IsTypeException">The path was not admitted.</exception>
    public WritePath ToWrite(string relativePath) =>
        Assert.IsType<PathResolution<WritePath>.Admitted>(Access.ForWrite(relativePath)).Path;

    /// <summary>Resolves a path for reading and returns the admitted read path.</summary>
    /// <param name="relativePath">The vault-relative path, using forward slashes.</param>
    /// <returns>The admitted read path.</returns>
    /// <exception cref="Xunit.Sdk.IsTypeException">The path was not admitted.</exception>
    public ReadPath ToRead(string relativePath) =>
        Assert.IsType<PathResolution<ReadPath>.Admitted>(Access.ForRead(relativePath)).Path;

    /// <summary>
    /// Returns a path proved to be contained by this vault, without admitting it against any
    /// scope. The vault-wide pass and the wikilink code take this type rather than caller text.
    /// </summary>
    /// <param name="relativePath">The vault-relative path, using forward slashes.</param>
    /// <returns>The contained path.</returns>
    /// <exception cref="ArgumentException">The path is not contained by this vault.</exception>
    public VaultPath At(string relativePath) =>
        VaultPath.TryCreate(Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    /// <summary>Returns the absolute path of a vault-relative path.</summary>
    /// <param name="relativePath">The vault-relative path, using forward slashes.</param>
    /// <returns>The absolute path on disk.</returns>
    public string Absolute(string relativePath) =>
        Path.Combine([Root, .. relativePath.Split('/')]);

    /// <summary>Creates a folder in the vault.</summary>
    /// <param name="relativePath">The vault-relative folder path, using forward slashes.</param>
    /// <returns>This instance, so calls can be chained.</returns>
    public ScratchVault WithFolder(string relativePath)
    {
        Directory.CreateDirectory(Absolute(relativePath));
        return this;
    }

    /// <summary>Creates a file in the vault, along with any missing parent folders.</summary>
    /// <param name="relativePath">The vault-relative file path, using forward slashes.</param>
    /// <param name="content">The text to write.</param>
    /// <returns>This instance, so calls can be chained.</returns>
    public ScratchVault WithNote(string relativePath, string content = "# Note\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Absolute(relativePath))!);
        File.WriteAllText(Absolute(relativePath), content);
        return this;
    }

    /// <summary>
    /// Makes one file unreadable for as long as the returned handle is held, so a search has a file
    /// it must fail on. On Windows this opens the file exclusively, which blocks a second reader;
    /// elsewhere it clears the Unix mode bits and restores them on dispose.
    /// </summary>
    /// <param name="relativePath">The vault-relative path of an existing file.</param>
    /// <returns>A handle that makes the file readable again when disposed.</returns>
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

    /// <summary>
    /// Makes one file readable but not writable for as long as the returned handle is held, so a
    /// pass that reads every note and then writes some of them has a file it must fail on at the
    /// write. On Windows this holds the file open for reading and shares it with other readers,
    /// which admits a read and blocks a write, because a write opens the file sharing nothing and so
    /// fails while any other handle is open; elsewhere it clears the write mode bits and restores
    /// them on dispose.
    /// </summary>
    /// <param name="relativePath">The vault-relative path of an existing file.</param>
    /// <returns>A handle that makes the file writable again when disposed.</returns>
    public IDisposable Unwritable(string relativePath)
    {
        var absolute = Absolute(relativePath);
        if (!OperatingSystem.IsWindows())
        {
            var original = File.GetUnixFileMode(absolute);
            File.SetUnixFileMode(absolute, UnixFileMode.UserRead);
            // The platform test is repeated inside the restore because the analyzer reads the
            // lambda on its own, not at the site it was built from.
            return new Restore(() =>
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(absolute, original);
            });
        }

        return File.Open(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>Runs an action when disposed.</summary>
    /// <param name="undo">The action to run on dispose.</param>
    private sealed class Restore(Action undo) : IDisposable
    {
        /// <summary>Runs the undo action.</summary>
        public void Dispose() => undo();
    }

    /// <summary>Reads a file in the vault as text.</summary>
    /// <param name="relativePath">The vault-relative file path, using forward slashes.</param>
    /// <returns>The file's contents.</returns>
    public string TextOf(string relativePath) => File.ReadAllText(Absolute(relativePath));

    /// <summary>Reads a file in the vault as bytes.</summary>
    /// <param name="relativePath">The vault-relative file path, using forward slashes.</param>
    /// <returns>The file's contents.</returns>
    public byte[] BytesOf(string relativePath) => File.ReadAllBytes(Absolute(relativePath));

    /// <summary>Determines whether a file exists in the vault.</summary>
    /// <param name="relativePath">The vault-relative file path, using forward slashes.</param>
    /// <returns><see langword="true"/> if the file exists; otherwise <see langword="false"/>.</returns>
    public bool HasFile(string relativePath) => File.Exists(Absolute(relativePath));

    /// <summary>Determines whether a folder exists in the vault.</summary>
    /// <param name="relativePath">The vault-relative folder path, using forward slashes.</param>
    /// <returns><see langword="true"/> if the folder exists; otherwise <see langword="false"/>.</returns>
    public bool HasFolder(string relativePath) => Directory.Exists(Absolute(relativePath));

    /// <summary>
    /// Returns the name an entry actually has on disk, found case-insensitively, so a test can
    /// see the result of a rename that only changed case.
    /// </summary>
    /// <param name="relativePath">The vault-relative path of an existing entry, using forward slashes.</param>
    /// <returns>The entry's name as spelled on disk.</returns>
    public string SpellingOf(string relativePath) =>
        Path.GetFileName(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Absolute(relativePath))!)
            .Single(e => string.Equals(Path.GetFileName(e), Path.GetFileName(Absolute(relativePath)), StringComparison.OrdinalIgnoreCase)));
}
