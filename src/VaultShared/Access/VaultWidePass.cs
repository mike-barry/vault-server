namespace VaultShared.Access;

/// <summary>
/// The one pass scope does not bound. Moving a note changes what every link to it must say, and
/// creating a note whose basename already exists changes what bare links to that basename resolve
/// to; either set of links can be anywhere, so the pass must reach notes the grant can neither read
/// nor write.
///
/// The separate object is the whole of the mechanism. Its operations are ordinary - enumerate every
/// note, read one, write one - so a consumer composes what it needs rather than asking for a new
/// primitive each time. What the seam buys is that bypassing scope is a deliberate call
/// (<see cref="VaultAccess.BypassingScope"/>) rather than a method within reach on an object every
/// caller already holds, and that every site doing it is one grep away.
///
/// Its operations take merely-contained paths: a <see cref="VaultPath"/> is proof of containment,
/// which is all this pass has and all it needs. It is the one producer of an admitted path besides
/// <see cref="VaultAccess"/>, and it is deliberately not reachable without naming what it is.
///
/// What may be <em>reported</em> out of such a pass - how much of an out-of-scope note reaches a
/// result, a preview, or a log - is a constraint on the report rather than on these primitives, and
/// belongs to the tool vocabulary with the rest of the result tiers.
/// </summary>
public sealed class VaultWidePass
{
    private readonly SearchEngine _engine;

    internal VaultWidePass(Vault vault, SearchEngine engine)
    {
        Vault = vault;
        _engine = engine;
    }

    public Vault Vault { get; }

    /// <summary>
    /// Every note in the vault, whatever the grant's scope admits. Dot entries are the one thing
    /// left out, because no path under a dot-directory is reachable by the tool set at all - a
    /// narrowing this pass shares with every other surface rather than one scope imposes.
    ///
    /// The markdown restriction cannot ride the engine's glob - positive globs union rather than
    /// intersect, which <see cref="VaultAccess"/> states in full at its own filter - while the dot
    /// rule prunes the walk through a negative one and is kept in the predicate beside it.
    /// </summary>
    public SearchOutcome<VaultPath> EnumerateNotes() =>
        VaultSearch.Enumerate(Vault, _engine, pattern: null, pruneDotEntries: true, path =>
            !path.HasDotSegment && path.Value.EndsWith(VaultName.NoteExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads one note, classifying a failure exactly as the scoped read does. A pass that runs over
    /// every note in the vault meets content a write would refuse, and must survive it.
    /// </summary>
    public ReadOutcome Read(VaultPath note) => VaultFiles.Read(new ReadPath(Contained(note)));

    /// <summary>
    /// Replaces one note's content. There is no creating form here: bringing a note into existence
    /// is the caller's own operation and is scope-checked normally, and this pass exists to rewrite
    /// what already links.
    /// </summary>
    public WriteOutcome Replace(VaultPath note, string content) =>
        VaultFiles.Replace(new WritePath(Contained(note)), content);

    /// <summary>
    /// A path from another vault is a layer defect rather than a scope answer, the same posture
    /// <see cref="VaultAccess.Admits"/> takes.
    /// </summary>
    private VaultPath Contained(VaultPath note) =>
        note.Vault == Vault
            ? note
            : throw new ArgumentException(
                $"Path belongs to vault \"{note.Vault.Name}\", not \"{Vault.Name}\".", nameof(note));
}
