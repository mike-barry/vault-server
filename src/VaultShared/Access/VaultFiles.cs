using System.Security.Cryptography;
using System.Text;

namespace VaultShared.Access;

/// <summary>
/// Provides the file primitives: read one note, write one note in either form, create one folder,
/// move one entry, remove one entry. Each returns a classified outcome rather than throwing,
/// because callers must tell the outcomes apart structurally.
/// </summary>
/// <remarks>
/// Moving and removing each have a file form and a folder form, because a path does not say which
/// it names and neither form may guess. Inspecting the target to choose would be the kind of
/// preliminary check every primitive here avoids.
///
/// A primitive decides its own refusals from the attempt rather than from a check run first, so no
/// window opens between the two in which another writer changes the answer. The two move probes
/// (<see cref="PreflightMoveFile"/>, <see cref="PreflightMoveFolder"/>) are the exception, and they
/// replace nothing: they report what a move would refuse, for a caller that has work to do before
/// the move and must not do it for a move that cannot happen. The move still decides.
///
/// The class is static and holds nothing. A primitive's authority is the type of the path it is
/// handed - <see cref="ReadPath"/> to read, <see cref="WritePath"/> to write, create, move, or
/// remove - so there is no per-call state to carry. The scope check happened where those proofs
/// were produced, and no operation here can be reached without one.
///
/// Nothing here acquires the write mutex. The shared execute path holds it around a whole tool
/// invocation, so a primitive called outside a tool runs with no lock held.
/// </remarks>
public static class VaultFiles
{
    /// <summary>
    /// The encoding every write uses: UTF-8 with no byte-order mark. The editor and the repository
    /// own these files jointly, so a byte-order mark appearing on rewritten notes would show up as
    /// a diff on every file the tooling touches.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads a note's content, classifying a failure from the exception itself.
    /// <see cref="ReadOutcome.NotFound"/> covers a missing note and a missing folder along the way;
    /// every other failure the platform raises is <see cref="ReadOutcome.Failed"/>.
    /// </summary>
    /// <remarks>
    /// The bytes are read once and both results derive from them: the decoded text, and the digest
    /// the staleness guard uses. Hashing a second read could describe different bytes from the
    /// ones the returned text was decoded from.
    /// </remarks>
    /// <param name="path">The note to read.</param>
    /// <returns>The content and its digest, or a classified failure.</returns>
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
    /// Replaces an existing note's content wholesale. A note that is not there is
    /// <see cref="WriteOutcome.NotFound"/>, reported by the open rather than by a check made first;
    /// a missing folder is <see cref="WriteOutcome.MissingParent"/>. This form creates no name, so
    /// the name grammar does not apply and a note predating the grammar stays writable.
    /// </summary>
    /// <param name="path">The note to overwrite.</param>
    /// <param name="content">The content to store.</param>
    /// <returns>The digest of what landed, or a classified failure.</returns>
    public static WriteOutcome Replace(WritePath path, string content)
    {
        var target = path.Path;
        try
        {
            // Truncate rather than Create: only this mode refuses a target that is not already
            // there, which is what keeps replacing and creating different operations.
            using var stream = new FileStream(target.Absolute, FileMode.Truncate, FileAccess.Write, FileShare.None);
            return new WriteOutcome.Written(Emit(stream, content));
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
    /// Creates a note whose name is not already taken, by a file or by a folder.
    /// </summary>
    /// <remarks>
    /// The refusals come from <see cref="PreflightCreate"/>, so a caller that asked it first is told
    /// the same thing this call would tell it. The write behind that check is atomic, so a file
    /// appearing in the window between the two is refused as
    /// <see cref="WriteOutcome.AlreadyExists"/> rather than destroyed.
    ///
    /// The note's own name is the one segment this call creates, so it is the only one checked
    /// against the name grammar.
    /// </remarks>
    /// <param name="path">The note to create.</param>
    /// <param name="content">The content to store.</param>
    /// <returns>The digest of what landed, or a classified failure.</returns>
    public static WriteOutcome Create(WritePath path, string content)
    {
        var target = path.Path;
        if (PreflightCreate(path) is { } refusal)
            return refusal;

        try
        {
            using var stream = new FileStream(target.Absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return new WriteOutcome.Written(Emit(stream, content));
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
    /// Creates one folder under a parent that already exists, refusing a missing parent as a write
    /// does. It is its own primitive, so materializing a path is a deliberate call rather than a
    /// side effect of writing. The folder's name is checked against the name grammar.
    /// </summary>
    /// <remarks>
    /// The platform's directory creation is idempotent and reports no difference between creating a
    /// folder and finding one, so a probe is the only thing that can answer whether the target was
    /// occupied, which a caller is told. Note creation probes as well, but has an atomic write
    /// behind its probe; this one has nothing behind it.
    ///
    /// The probe is racy in the same way any check-then-act is, and the race is accepted because
    /// losing it means reporting an occupied target another writer created a moment earlier, or
    /// reporting a creation another writer had just made. No content is destroyed either way.
    /// </remarks>
    /// <param name="path">The folder to create.</param>
    /// <returns>
    /// <see cref="WriteOutcome.Written"/> with a null hash, since a folder has no content,
    /// <see cref="WriteOutcome.AlreadyExists"/> where a folder is already there, or a classified
    /// failure.
    /// </returns>
    public static WriteOutcome CreateFolder(WritePath path)
    {
        var target = path.Path;
        var name = target.Segments[^1];
        if (!VaultName.IsFolderName(name))
            return new WriteOutcome.InvalidName(name);

        // The platform offers no non-recursive directory creation, so the parent is checked here.
        // Without this check the call would create every missing folder above the target, which no
        // operation may do on the way past.
        var parent = Directory.GetParent(target.Absolute);
        if (parent is null || !parent.Exists)
            return MissingParent(target);

        // A file occupying the name is the same answer as a folder occupying it, and only a probe
        // of each kind tells them apart before the attempt.
        if (Directory.Exists(target.Absolute) || File.Exists(target.Absolute))
            return new WriteOutcome.AlreadyExists(target.Value);

        try
        {
            Directory.CreateDirectory(target.Absolute);
            // A folder has no content, so there is no hash to report.
            return new WriteOutcome.Written(null);
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
    /// The name of the placeholder that makes an empty folder exist in the repository. No caller
    /// can name this file: a dot-prefixed segment is refused as a path, and the name grammar
    /// refuses the spelling. Both refusals are deliberate.
    /// </summary>
    public const string GitKeepName = ".gitkeep";

    /// <summary>
    /// Creates <see cref="GitKeepName"/> inside a folder, so a folder expected to exist while empty
    /// survives a repository round trip.
    /// </summary>
    /// <remarks>
    /// This is the only code that can create that file. Nothing else in the build can create a
    /// dot-prefixed path at all: <see cref="VaultAccess.ForWrite"/> refuses one and
    /// <see cref="Create"/> refuses the name. Both refusals are correct for text a caller supplied,
    /// and the placeholder is not caller text, so it has a member of its own instead of either
    /// refusal being weakened. Keep the child path composed here rather than passed in - that is
    /// what makes it true by construction that a caller can ask for a placeholder but cannot name
    /// one.
    ///
    /// The folder's own write proof carries admission for everything under it, because scope roots
    /// are whole-segment prefixes.
    /// </remarks>
    /// <param name="folder">The folder to create the placeholder in.</param>
    /// <returns>
    /// The digest of the empty file, <see cref="WriteOutcome.AlreadyExists"/> when a placeholder is
    /// already there, or a classified failure.
    /// </returns>
    public static WriteOutcome CreateGitKeep(WritePath folder)
    {
        var target = folder.Path;
        if (!VaultPath.TryCreate(target.Vault, $"{target.Value}/{GitKeepName}", out var child, out var reason))
            throw new InvalidOperationException($"Composed placeholder path under \"{target.Value}\" is not a vault path: {reason}");

        try
        {
            using var stream = new FileStream(child.Absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return new WriteOutcome.Written(Emit(stream, string.Empty));
        }
        catch (IOException ex) when (IsAlreadyExists(ex))
        {
            return new WriteOutcome.AlreadyExists(child.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Classify(ex, child);
        }
    }

    /// <summary>
    /// Moves one file. Both paths must be in the same vault; a pair that is not is a defect in the
    /// calling code and throws rather than being answered.
    /// </summary>
    /// <remarks>
    /// Occupancy is compared against the resolved source ordinally, so a case-only rename is not a
    /// collision. The outcome reports that the move was case-only, because git does not see such a
    /// change and staging it happens above this layer.
    ///
    /// The destination's name is the one segment this call creates, and is checked against the name
    /// grammar as either a note or a folder. A source that names a folder answers
    /// <see cref="MoveOutcome.SourceMissing"/>; <see cref="MoveFolder"/> is the folder form.
    /// </remarks>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it should end up.</param>
    /// <returns>The result of the move.</returns>
    /// <exception cref="ArgumentException">The two paths are in different vaults.</exception>
    public static MoveOutcome MoveFile(WritePath source, WritePath destination)
    {
        var from = source.Path;
        var to = destination.Path;

        if (from.Vault != to.Vault)
            throw new ArgumentException(
                $"Move spans vaults \"{from.Vault.Name}\" and \"{to.Vault.Name}\".", nameof(destination));

        var name = to.Segments[^1];
        if (!VaultName.IsNoteName(name) && !VaultName.IsFolderName(name))
            return new MoveOutcome.InvalidName(name);

        // Equality is case-insensitive and spelling is ordinal, so the two together say whether the
        // destination is the source under another spelling.
        var sameEntry = from.Equals(to);
        var caseOnly = sameEntry && !from.HasSameSpellingAs(to);

        try
        {
            // Overwrite only when the destination is the source itself, which a case-only rename
            // names and the rename would otherwise refuse. Every other occupant is refused by the
            // catch below. Windows: sameEntry is case-insensitive path equality, so a case-only
            // rename reaches this call with overwrite set, and on a filesystem that told the two
            // spellings apart the same call would hand overwrite a different file and destroy it
            // silently.
            File.Move(from.Absolute, to.Absolute, overwrite: sameEntry);
            return new MoveOutcome.Moved(caseOnly);
        }
        catch (FileNotFoundException)
        {
            return new MoveOutcome.SourceMissing(from.Value);
        }
        catch (DirectoryNotFoundException)
        {
            // A missing source is reported as FileNotFoundException above, so a missing directory
            // here is the destination's parent.
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
    /// Moves one folder and everything inside it, as a single directory rename.
    /// </summary>
    /// <remarks>
    /// This is one filesystem operation and never a walk. The contents keep their names and their
    /// positions relative to the folder, so nothing under it has to be named, created or removed.
    /// That is what keeps a relocation from having a half-moved state of its own, and what keeps
    /// this primitive from creating the folders a per-file relocation would need on the far side.
    /// <see cref="RemoveEmptyFolder"/> obeys the same rule against walking a tree.
    ///
    /// The destination's own name is the one segment this call creates, and is checked against the
    /// folder grammar. A case-only rename is permitted and reported, exactly as for a file.
    /// </remarks>
    /// <param name="source">The folder to move.</param>
    /// <param name="destination">Where it should end up.</param>
    /// <returns>The result of the move.</returns>
    /// <exception cref="ArgumentException">The two paths are in different vaults.</exception>
    public static MoveOutcome MoveFolder(WritePath source, WritePath destination)
    {
        var from = source.Path;
        var to = destination.Path;

        if (from.Vault != to.Vault)
            throw new ArgumentException(
                $"Move spans vaults \"{from.Vault.Name}\" and \"{to.Vault.Name}\".", nameof(destination));

        var name = to.Segments[^1];
        if (!VaultName.IsFolderName(name))
            return new MoveOutcome.InvalidName(name);

        var sameEntry = from.Equals(to);
        var caseOnly = sameEntry && !from.HasSameSpellingAs(to);

        // The destination's parent is checked here rather than left to the failure, because a
        // directory rename reports a missing source and a missing destination parent as the same
        // platform exception, and those are different answers. Only the destination side is
        // checked; the source's existence still comes from the attempt.
        var parent = Directory.GetParent(to.Absolute);
        if (parent is null || !parent.Exists)
            return to.ParentValue is { } missing
                ? new MoveOutcome.MissingParent(missing)
                : new MoveOutcome.Failed(to.Value, RootMissing);

        try
        {
            Directory.Move(from.Absolute, to.Absolute);
            return new MoveOutcome.Moved(caseOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return new MoveOutcome.SourceMissing(from.Value);
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
    /// Reports the refusal a note creation would raise, or null when it would raise none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three reasons are read: a note name outside the name grammar, a missing parent folder, and a
    /// name already taken by a file or a folder. <see cref="Create"/> answers by calling this, so
    /// the two never disagree, and a caller that must do other work before the write - a pass that
    /// rewrites other notes first - learns of the refusal before that work rather than after it.
    /// </para>
    /// <para>
    /// The window between this and the write is not a way to lose content. The write is atomic and
    /// refuses a file that appeared in between as
    /// <see cref="WriteOutcome.AlreadyExists"/> rather than overwriting it.
    /// </para>
    /// <para>
    /// Windows: a folder at the target is found here by probing for it, because the write refuses
    /// one with an access failure that is indistinguishable from a permission problem, and a port
    /// whose platform reports the two apart could take the answer from the write instead.
    /// </para>
    /// </remarks>
    /// <param name="path">The note that would be created.</param>
    /// <returns>The outcome the creation would answer, or null when none of the three applies.</returns>
    public static WriteOutcome? PreflightCreate(WritePath path)
    {
        var target = path.Path;
        var name = target.Segments[^1];
        if (!VaultName.IsNoteName(name))
            return new WriteOutcome.InvalidName(name);

        var parent = Directory.GetParent(target.Absolute);
        if (parent is null || !parent.Exists)
            return MissingParent(target);

        return File.Exists(target.Absolute) || Directory.Exists(target.Absolute)
            ? new WriteOutcome.AlreadyExists(target.Value)
            : null;
    }

    /// <summary>
    /// Reports whether a folder is on disk at this path.
    /// </summary>
    /// <remarks>
    /// A folder operand has no content to read, so a probe is the only thing that establishes it
    /// exists. A file at the same path answers false: an extensionless file is not a folder, and no
    /// folder operation can act on one.
    /// </remarks>
    /// <param name="path">The folder to look for.</param>
    /// <returns><see langword="true"/> when a directory is there; otherwise <see langword="false"/>.</returns>
    public static bool FolderExists(WritePath path) => Directory.Exists(path.Path.Absolute);

    /// <summary>
    /// Reports the refusal a file move would raise for a reason readable from the filesystem before
    /// the move runs, or null when it would raise none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three reasons are a destination name outside the name grammar, a missing destination
    /// parent, and an occupied destination. <see cref="MoveFile"/> decides all three as part of the
    /// move itself and its answer is the authoritative one, because anything read here can stop
    /// being true before the move runs. This exists for a caller that must do other work before the
    /// move - a preview that writes nothing, or a mutation that rewrites other files first - so that
    /// the refusal arrives before that work instead of after it. Both are needed: without this, such
    /// a caller reports success for a move that cannot happen or leaves earlier writes behind a
    /// refusal; without the check in the move, the answer would come from state read a moment
    /// earlier.
    /// </para>
    /// <para>
    /// The destination is not an occupant when it names the source under another spelling, which is
    /// what lets a case-only rename proceed on a case-insensitive filesystem. Both a file and a
    /// directory at the destination count as an occupant.
    /// </para>
    /// <para>
    /// Only the destination is read. Whether the file to move is there is left to the caller,
    /// because a file that is present and cannot be read is a different answer from one that is not
    /// there and only reading it tells those apart. <see cref="PreflightMoveFolder"/> does report a
    /// missing source, since a folder move has no such read.
    /// </para>
    /// </remarks>
    /// <param name="source">The file that would move.</param>
    /// <param name="destination">Where it would end up.</param>
    /// <returns>The outcome the move would answer, or null when none of the three applies.</returns>
    public static MoveOutcome? PreflightMoveFile(WritePath source, WritePath destination)
    {
        var name = destination.Path.Segments[^1];
        return Preflight(
            source.Path, destination.Path, VaultName.IsNoteName(name) || VaultName.IsFolderName(name));
    }

    /// <summary>
    /// Reports the refusal a folder move would raise for a reason readable from the filesystem
    /// before the move runs, or null when it would raise none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The destination reasons and the relationship to the move itself are the same as for
    /// <see cref="PreflightMoveFile"/>, except that a folder destination is held to the folder
    /// grammar alone.
    /// </para>
    /// <para>
    /// A folder that is not there is reported as well, and ahead of every reason about the
    /// destination: both can be true of one call, and a caller whose operand is missing has to fix
    /// that before anything about where it was going matters. A folder move reads no content, so
    /// there is nothing else to establish that the operand exists.
    /// </para>
    /// </remarks>
    /// <param name="source">The folder that would move.</param>
    /// <param name="destination">Where it would end up.</param>
    /// <returns>The outcome the move would answer, or null when none of them applies.</returns>
    public static MoveOutcome? PreflightMoveFolder(WritePath source, WritePath destination) =>
        !Directory.Exists(source.Path.Absolute)
            ? new MoveOutcome.SourceMissing(source.Path.Value)
            : Preflight(source.Path, destination.Path, VaultName.IsFolderName(destination.Path.Segments[^1]));

    /// <summary>
    /// Reports the refusal a move would raise for a reason readable before the move runs.
    /// </summary>
    /// <param name="from">Where the entry is now.</param>
    /// <param name="to">Where it would end up.</param>
    /// <param name="nameAccepted">Whether the destination's own name satisfies the grammar the
    /// caller's form checks it against.</param>
    /// <returns>The outcome the move would answer, or null when none applies.</returns>
    private static MoveOutcome? Preflight(VaultPath from, VaultPath to, bool nameAccepted)
    {
        if (!nameAccepted)
            return new MoveOutcome.InvalidName(to.Segments[^1]);

        var parent = Directory.GetParent(to.Absolute);
        if (parent is null || !parent.Exists)
            return to.ParentValue is { } missing
                ? new MoveOutcome.MissingParent(missing)
                : new MoveOutcome.Failed(to.Value, RootMissing);

        // Equality is case-insensitive, so the source under another spelling is not an occupant.
        if (!from.Equals(to) && (File.Exists(to.Absolute) || Directory.Exists(to.Absolute)))
            return new MoveOutcome.DestinationOccupied(to.Value);

        return null;
    }

    /// <summary>
    /// Removes one file. The refusal comes from the attempt: the open fails when nothing is there,
    /// and the entry is unlinked as the handle closes, so no check stands between the two.
    /// </summary>
    /// <param name="path">The file to remove.</param>
    /// <returns>The result of the removal.</returns>
    public static RemovalOutcome RemoveFile(WritePath path)
    {
        var target = path.Path;
        try
        {
            // Opening with delete-on-close is the removal. The platform's own delete reports
            // nothing when the file was already gone, and that is the one distinction this
            // operation owes its caller.
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
    /// Removes one folder that has nothing in it. It never walks a tree, and
    /// <see cref="RemovalOutcome.NotEmpty"/> comes from the platform's answer to the removal rather
    /// than from a check made first.
    /// </summary>
    /// <param name="path">The folder to remove.</param>
    /// <returns>The result of the removal.</returns>
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
    /// Decodes stored bytes as UTF-8, recognizing a byte-order mark and dropping it rather than
    /// letting it surface as a leading character. A mark naming another encoding is honoured, so a
    /// note the editor wrote as UTF-16 decodes to its own text instead of to mojibake.
    /// </summary>
    /// <param name="bytes">The file's bytes as stored.</param>
    /// <returns>The decoded text.</returns>
    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Computes the staleness guard's value: SHA-256 of the bytes as stored, lowercase hex. It is
    /// taken over the stored bytes rather than the decoded or composed text, so a hash from a read
    /// and a hash from a write of the same file are the same value.
    /// </summary>
    /// <param name="bytes">The file's bytes as stored.</param>
    /// <returns>The digest, lowercase hex.</returns>
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Encodes content as the bytes a write stores: LF line terminators, UTF-8, no byte-order mark.
    /// Notes are always stored that way; nothing detects what was there before or preserves another
    /// convention.
    /// </summary>
    /// <param name="content">The text to store.</param>
    /// <returns>The bytes to write.</returns>
    private static byte[] Bytes(string content) =>
        Utf8NoBom.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal));

    /// <summary>
    /// Writes the content and returns the digest of the bytes that landed, so a write's hash comes
    /// from the write rather than from a read afterwards.
    /// </summary>
    /// <param name="stream">The open stream to write to.</param>
    /// <param name="content">The text to store.</param>
    /// <returns>The digest of the bytes written, lowercase hex.</returns>
    private static string Emit(Stream stream, string content)
    {
        var bytes = Bytes(content);
        stream.Write(bytes);
        return Digest(bytes);
    }

    /// <summary>
    /// Returns the digest a write of this content would store, without storing it. A caller holding
    /// both a read's hash and a composed result can compare the two: they are equal exactly when
    /// the write would change nothing.
    /// </summary>
    /// <param name="content">The text a write would store.</param>
    /// <returns>The digest, lowercase hex.</returns>
    public static string DigestOf(string content) => Digest(Bytes(content));

    /// <summary>
    /// Classifies a write failure. A missing folder becomes
    /// <see cref="WriteOutcome.MissingParent"/>; everything else is
    /// <see cref="WriteOutcome.Failed"/>.
    /// </summary>
    /// <param name="ex">The exception the platform raised.</param>
    /// <param name="target">The path the write was aimed at.</param>
    /// <returns>The classified outcome.</returns>
    private static WriteOutcome Classify(Exception ex, VaultPath target) =>
        ex is DirectoryNotFoundException
            ? MissingParent(target)
            : new WriteOutcome.Failed(target.Value, Describe(ex, target));

    /// <summary>
    /// Names the missing containing folder. A path directly under the vault root has no parent to
    /// name, so a root that is not there is reported as an operational failure instead; vault
    /// resolution is what reports a missing root properly.
    /// </summary>
    /// <param name="target">The path the write was aimed at.</param>
    /// <returns>The missing-parent outcome, or a failure when the root itself is gone.</returns>
    private static WriteOutcome MissingParent(VaultPath target) =>
        target.ParentValue is { } parent
            ? new WriteOutcome.MissingParent(parent)
            : new WriteOutcome.Failed(target.Value, RootMissing);

    private const string RootMissing = "The vault root directory is not present.";

    /// <summary>
    /// Returns what the platform said, with every location it names rendered vault-relative. An
    /// absolute path must never leave this layer, and an exception message is one of the ways it
    /// could.
    /// </summary>
    /// <param name="ex">The exception the platform raised.</param>
    /// <param name="paths">The paths whose absolute forms may appear in the message.</param>
    /// <returns>The message, with locations rendered vault-relative.</returns>
    private static string Describe(Exception ex, params VaultPath[] paths)
    {
        var text = ex.Message;
        foreach (var path in paths)
            text = text.Replace(path.Absolute, path.Value, StringComparison.OrdinalIgnoreCase);
        return paths.Length == 0
            ? text
            : text.Replace(paths[0].Vault.Root, paths[0].Vault.Name, StringComparison.OrdinalIgnoreCase);
    }

    // The platform reports "already exists" and "not empty" as a plain IOException, so the error
    // code it set is the only thing distinguishing them from a sharing conflict or a device error.
    // Each is the Windows system error carried in the 0x8007 facility.
    private const int ErrorFileExists = unchecked((int)0x80070050);
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);
    private const int ErrorDirNotEmpty = unchecked((int)0x80070091);

    private static bool IsAlreadyExists(IOException ex) =>
        ex.HResult is ErrorFileExists or ErrorAlreadyExists;

    private static bool IsDirectoryNotEmpty(IOException ex) =>
        ex.HResult is ErrorDirNotEmpty;
}
