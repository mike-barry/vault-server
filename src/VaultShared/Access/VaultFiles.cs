using System.Security.Cryptography;
using System.Text;

namespace VaultShared.Access;

/// <summary>
/// The file primitives: read one note, write one note either form, create one folder, move one
/// entry, remove one entry. Every one of them answers rather than throwing, because callers must
/// tell the outcomes apart structurally.
///
/// Static, and holding nothing: a primitive's authority is the type of the path it is handed -
/// <see cref="ReadPath"/> to read, <see cref="WritePath"/> to write, create, move, or remove - so
/// there is no per-call state for an object to carry. The scope check happened where those proofs
/// were produced, and no operation here can be reached without one.
///
/// Nothing here acquires the mutex lock. The shared execute path holds it around a whole tool
/// invocation, so a primitive called outside a tool runs with no lock held.
/// </summary>
public static class VaultFiles
{
    /// <summary>
    /// The composing seam, stated once: UTF-8 with no byte-order mark. The editor and the
    /// repository own these files jointly, and a byte-order mark appearing on rewritten notes is a
    /// diff on every file the tooling touches - invisible per file and unmistakable across a sync.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads a note's content, classifying a failure from the failure itself.
    /// <see cref="ReadOutcome.NotFound"/> covers a missing note and a missing folder along the way;
    /// everything else the platform raises is <see cref="ReadOutcome.Failed"/>.
    ///
    /// The bytes are taken once and both answers derive from them: the decoded text, and the digest
    /// the staleness guard is carried by. Hashing from a second read would describe bytes the
    /// returned text may no longer be a decoding of.
    /// </summary>
    public static ReadOutcome Read(ReadPath path)
    {
        var target = path.Path;
        try
        {
            var bytes = File.ReadAllBytes(target.Absolute);
            return new ReadOutcome.Content(Decode(bytes), Digest(bytes));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadOutcome.NotFound(target.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ReadOutcome.Failed(target.Value, Describe(ex, target));
        }
    }

    /// <summary>
    /// Replaces an existing note's content wholesale. Nothing there to replace is
    /// <see cref="WriteOutcome.NotFound"/>, from the open rather than from a probe; a missing folder
    /// is <see cref="WriteOutcome.MissingParent"/>. This form brings no name into existence, so the
    /// name grammar does not apply to it and a note predating the grammar stays writable.
    /// </summary>
    public static WriteOutcome Replace(WritePath path, string content)
    {
        var target = path.Path;
        try
        {
            // Truncate rather than Create: only this mode refuses a target that is not already
            // there, which is what keeps replacing and creating two different operations.
            using var stream = new FileStream(target.Absolute, FileMode.Truncate, FileAccess.Write, FileShare.None);
            Emit(stream, content);
            return new WriteOutcome.Written();
        }
        catch (FileNotFoundException)
        {
            return new WriteOutcome.NotFound(target.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Classify(ex, target);
        }
    }

    /// <summary>
    /// Creates a note that must not already exist. The refusal is the filesystem's answer to an
    /// atomic create - <see cref="WriteOutcome.AlreadyExists"/>, distinguishable from every other
    /// write failure - never a check the layer runs first, which would leave a window in which
    /// something else creates the file and the write then destroys it.
    ///
    /// The note's own name is the one segment this call brings into existence, so it is checked
    /// against the name grammar; nothing else in the path is.
    /// </summary>
    public static WriteOutcome Create(WritePath path, string content)
    {
        var target = path.Path;
        var name = target.Segments[^1];
        if (!VaultName.IsNoteName(name))
            return new WriteOutcome.InvalidName(name);

        try
        {
            using var stream = new FileStream(target.Absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Emit(stream, content);
            return new WriteOutcome.Written();
        }
        catch (IOException ex) when (IsAlreadyExists(ex))
        {
            return new WriteOutcome.AlreadyExists(target.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Classify(ex, target);
        }
    }

    /// <summary>
    /// Creates one folder under a parent that already exists, and refuses a missing parent as a
    /// write does. Its own primitive, so materializing a path is a deliberate call rather than a
    /// side effect of writing, and the folder's name is checked against the name grammar.
    ///
    /// A folder already there is not an error: the platform's directory creation is idempotent and
    /// cannot report the difference without the probe every other primitive here avoids.
    /// </summary>
    public static WriteOutcome CreateFolder(WritePath path)
    {
        var target = path.Path;
        var name = target.Segments[^1];
        if (!VaultName.IsFolderName(name))
            return new WriteOutcome.InvalidName(name);

        // The platform offers no non-recursive directory creation, so the parent is established
        // here: without this the call would materialize every missing folder above the target,
        // which is exactly what nothing may do on the way past.
        var parent = Directory.GetParent(target.Absolute);
        if (parent is null || !parent.Exists)
            return MissingParent(target);

        try
        {
            Directory.CreateDirectory(target.Absolute);
            return new WriteOutcome.Written();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Classify(ex, target);
        }
    }

    /// <summary>
    /// Moves one entry. The only primitive taking two paths, and both must carry the same vault - a
    /// pair that does not is a layer defect and throws rather than being answered.
    ///
    /// Occupancy is decided against the resolved source ordinally, so a case-only rename is not a
    /// collision; the outcome reports that the move was case-only, because git does not see such a
    /// change and staging it is above this layer. The destination's name is the one segment brought
    /// into existence and is checked against the name grammar, as a note or as a folder.
    /// </summary>
    /// <exception cref="ArgumentException">The two paths carry different vaults.</exception>
    public static MoveOutcome Move(WritePath source, WritePath destination)
    {
        var from = source.Path;
        var to = destination.Path;

        if (from.Vault != to.Vault)
            throw new ArgumentException(
                $"Move spans vaults \"{from.Vault.Name}\" and \"{to.Vault.Name}\".", nameof(destination));

        var name = to.Segments[^1];
        if (!VaultName.IsNoteName(name) && !VaultName.IsFolderName(name))
            return new MoveOutcome.InvalidName(name);

        // Equality is case-insensitive and spelling is ordinal, so the two questions together say
        // whether the destination is the source under another spelling.
        var sameEntry = from.Equals(to);
        var caseOnly = sameEntry && !from.HasSameSpellingAs(to);

        try
        {
            // The destination may be replaced only when it is the source itself: on a
            // case-insensitive filesystem the rename otherwise refuses its own target. Every other
            // occupant is refused below.
            File.Move(from.Absolute, to.Absolute, overwrite: sameEntry);
            return new MoveOutcome.Moved(caseOnly);
        }
        catch (FileNotFoundException)
        {
            return new MoveOutcome.SourceMissing(from.Value);
        }
        catch (DirectoryNotFoundException)
        {
            // The source is established by the move's own first step, so a missing folder here is
            // the destination's.
            return to.ParentValue is { } parent
                ? new MoveOutcome.MissingParent(parent)
                : new MoveOutcome.Failed(to.Value, RootMissing);
        }
        catch (IOException ex) when (IsAlreadyExists(ex))
        {
            return new MoveOutcome.DestinationOccupied(to.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MoveOutcome.Failed(from.Value, Describe(ex, from, to));
        }
    }

    /// <summary>
    /// Removes one file. The refusal comes from the attempt: the open fails when nothing is there,
    /// and the entry is unlinked as the handle closes, so no probe stands between the two.
    /// </summary>
    public static RemovalOutcome RemoveFile(WritePath path)
    {
        var target = path.Path;
        try
        {
            // Opening with delete-on-close is the removal: the platform's delete answers nothing
            // when the file was already gone, which is the one distinction this operation owes.
            using var stream = new FileStream(
                target.Absolute, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            return new RemovalOutcome.Removed();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new RemovalOutcome.NotFound(target.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RemovalOutcome.Failed(target.Value, Describe(ex, target));
        }
    }

    /// <summary>
    /// Removes one folder with nothing in it. Never walks a tree, and the emptiness refusal is the
    /// platform's answer to the removal rather than a check the layer runs first.
    /// </summary>
    public static RemovalOutcome RemoveEmptyFolder(WritePath path)
    {
        var target = path.Path;
        try
        {
            Directory.Delete(target.Absolute, recursive: false);
            return new RemovalOutcome.Removed();
        }
        catch (DirectoryNotFoundException)
        {
            return new RemovalOutcome.NotFound(target.Value);
        }
        catch (IOException ex) when (IsDirectoryNotEmpty(ex))
        {
            return new RemovalOutcome.NotEmpty(target.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RemovalOutcome.Failed(target.Value, Describe(ex, target));
        }
    }

    /// <summary>
    /// The reading half of the composing seam: UTF-8, with a byte-order mark recognized and dropped
    /// rather than surfacing as a leading character. A mark naming another encoding is honoured, so
    /// a note the editor wrote as UTF-16 still decodes to its own text instead of to mojibake.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The staleness guard's value: SHA-256 of the bytes as stored, lowercase hex. Over the stored
    /// bytes rather than the decoded or composed text, which is what makes a hash from a read and a
    /// hash from a write the same value and lets a chain run in either direction.
    /// </summary>
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The other half of the composing seam: LF line terminators. Notes are stored with them, and
    /// nothing detects what was there or preserves another convention.
    /// </summary>
    private static void Emit(Stream stream, string content) =>
        stream.Write(Utf8NoBom.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal)));

    /// <summary>
    /// The failures a write shares with folder creation. A missing folder is named in vault-relative
    /// terms; everything else is operational.
    /// </summary>
    private static WriteOutcome Classify(Exception ex, VaultPath target) =>
        ex is DirectoryNotFoundException
            ? MissingParent(target)
            : new WriteOutcome.Failed(target.Value, Describe(ex, target));

    /// <summary>
    /// A path directly under the root has no parent to name - the root is a vault rather than a
    /// path - so a root that is not there is operational, and vault resolution is what reports it.
    /// </summary>
    private static WriteOutcome MissingParent(VaultPath target) =>
        target.ParentValue is { } parent
            ? new WriteOutcome.MissingParent(parent)
            : new WriteOutcome.Failed(target.Value, RootMissing);

    private const string RootMissing = "The vault root directory is not present.";

    /// <summary>
    /// What the platform said, with every location it names rendered vault-relative. A message
    /// carrying an absolute path would take one out of the layer.
    /// </summary>
    private static string Describe(Exception ex, params VaultPath[] paths)
    {
        var text = ex.Message;
        foreach (var path in paths)
            text = text.Replace(path.Absolute, path.Value, StringComparison.OrdinalIgnoreCase);
        return paths.Length == 0
            ? text
            : text.Replace(paths[0].Vault.Root, paths[0].Vault.Name, StringComparison.OrdinalIgnoreCase);
    }

    // The platform reports "already there" and "not empty" as a plain IOException, so the code the
    // OS set is the only thing that tells them from a sharing conflict or a device error. Windows
    // codes carry the 0x8007 facility; the bare values are the POSIX errnos.
    private const int ErrorFileExists = unchecked((int)0x80070050);
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorDirNotEmpty = unchecked((int)0x80070091);
    private const int Eexist = 17;
    private const int EnotemptyLinux = 39;
    private const int EnotemptyBsd = 66;

    private static bool IsAlreadyExists(IOException ex) =>
        ex.HResult is ErrorFileExists or ErrorAlreadyExists or Eexist;

    private static bool IsDirectoryNotEmpty(IOException ex) =>
        ex.HResult is ErrorDirNotEmpty or EnotemptyLinux or EnotemptyBsd or Eexist;
}
