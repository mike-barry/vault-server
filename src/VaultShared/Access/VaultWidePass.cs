namespace VaultShared.Access;

/// <summary>
/// Enumerates, reads and writes notes across the whole vault, ignoring the caller's scope. Wikilink
/// maintenance is the only work that needs this: moving a note changes what every link to it must
/// say, and creating a note whose basename already exists changes what bare links to that basename
/// resolve to. Either set of links can be anywhere, so the pass must reach notes the grant can
/// neither read nor write.
/// </summary>
/// <remarks>
/// Being a separate object is the whole of the mechanism. Reach it only through
/// <see cref="VaultAccess.BypassingScope"/>, so that bypassing scope reads as a deliberate act at
/// the call site and every site doing it can be found with one search. Do not expose these
/// operations on <see cref="VaultAccess"/> itself.
///
/// Its operations take a <see cref="VaultPath"/>, which is proof of containment and nothing more.
/// That is all this pass has and all it needs.
///
/// This pass reaches notes the caller's own grant cannot. What may then be reported out of one of
/// them is the calling tool's to decide, and nothing here decides it.
/// </remarks>
public sealed class VaultWidePass
{
    private readonly SearchEngine _engine;

    internal VaultWidePass(Vault vault, SearchEngine engine)
    {
        Vault = vault;
        _engine = engine;
    }

    /// <summary>Gets the vault this pass runs over.</summary>
    public Vault Vault { get; }

    /// <summary>
    /// Returns every note in the vault, whatever the grant's scope admits. Dot entries are left
    /// out, because no path under a dot-directory is reachable by any surface in the build.
    /// </summary>
    /// <remarks>
    /// The markdown restriction is in the predicate rather than in a glob passed to the engine,
    /// because positive globs union rather than intersect. <see cref="VaultAccess"/> states that
    /// hazard in full at its own filter. The dot rule prunes the engine's walk through a negative
    /// glob and is also kept in the predicate beside it.
    /// </remarks>
    /// <returns>The notes, or a search failure.</returns>
    public SearchOutcome<VaultPath> EnumerateNotes() =>
        VaultSearch.Enumerate(Vault, _engine, pattern: null, pruneDotEntries: true, path =>
            !path.HasDotSegment && path.Value.EndsWith(VaultName.NoteExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads one note, classifying a failure exactly as the scoped read does. A pass over every
    /// note in the vault meets content a write would refuse and has to survive it.
    /// </summary>
    /// <param name="note">The note to read. Must be in this pass's vault.</param>
    /// <returns>The note's content, or a classified failure.</returns>
    /// <exception cref="ArgumentException"><paramref name="note"/> belongs to another vault.</exception>
    public ReadOutcome Read(VaultPath note) => VaultFiles.Read(new ReadPath(Contained(note)));

    /// <summary>
    /// Replaces one note's content. There is no creating form here, because this pass exists to
    /// rewrite notes that already link, and bringing a note into existence is the caller's own
    /// operation and is scope-checked normally.
    /// </summary>
    /// <param name="note">The note to rewrite. Must be in this pass's vault.</param>
    /// <param name="content">The content to store.</param>
    /// <returns>The result of the write.</returns>
    /// <exception cref="ArgumentException"><paramref name="note"/> belongs to another vault.</exception>
    public WriteOutcome Replace(VaultPath note, string content) =>
        VaultFiles.Replace(new WritePath(Contained(note)), content);

    /// <summary>
    /// Returns the note when it is in this vault. A path from another vault is a defect in the
    /// calling code rather than a scope answer, so it throws, as <see cref="VaultAccess.Admits"/>
    /// also does.
    /// </summary>
    /// <param name="note">The note to check.</param>
    /// <returns>The same note.</returns>
    /// <exception cref="ArgumentException"><paramref name="note"/> belongs to another vault.</exception>
    private VaultPath Contained(VaultPath note) =>
        note.Vault == Vault
            ? note
            : throw new ArgumentException(
                $"Path belongs to vault \"{note.Vault.Name}\", not \"{Vault.Name}\".", nameof(note));
}
