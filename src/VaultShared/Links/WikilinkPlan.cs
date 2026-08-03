using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// What a relocation or a creation is about to do to the vault's links, composed in full before
/// anything is written. Planning reads; <see cref="WikilinkMaintenance.Apply"/> writes. The split is
/// what lets a consumer order the pass against its own mutation - creation must pin before it
/// writes the note, and a relocation must repair before it moves the file, since the plan names
/// every note by the path it has now.
///
/// A plan holds each rewritten note's whole composed text, so applying it re-reads nothing.
/// </summary>
public sealed class WikilinkPlan
{
    private readonly IReadOnlyList<NoteRewrite> _notes;

    internal WikilinkPlan(IReadOnlyList<NoteRewrite> notes, IReadOnlyList<NoteRelocation> relocated, VaultPath? folderNote)
    {
        _notes = notes;
        Rewrites = [.. notes.SelectMany(n => n.Rewrites)];
        Relocated = relocated;
        FolderNote = folderNote;
    }

    /// <summary>
    /// Every link this plan changes, in note order and then in position order within a note. What a
    /// consumer reports as its repaired or pinned set.
    /// </summary>
    public IReadOnlyList<LinkRewrite> Rewrites { get; }

    /// <summary>
    /// Every note the relocation itself moves, before and after. One pair for a note relocation; one
    /// per contained note for a folder relocation, including the folder note under its new name.
    /// Empty for a creation, which moves nothing.
    ///
    /// It is here because link repair cannot be computed without it - a link's emitted form depends
    /// on where every note ends up - and exposing it means the relocating tool reads the same map
    /// rather than deriving a second one.
    /// </summary>
    public IReadOnlyList<NoteRelocation> Relocated { get; }

    /// <summary>
    /// Where a relocated folder's folder note ends up, or null where the folder has none and where
    /// the relocation is not a folder's. A folder that has none gets none created.
    /// </summary>
    public VaultPath? FolderNote { get; }

    /// <summary>Whether the plan writes nothing. A pass that ran and changed no link is a finding.</summary>
    public bool IsEmpty => _notes.Count == 0;

    internal IReadOnlyList<NoteRewrite> Notes => _notes;
}

/// <summary>One note's composed text and the rewrites that produced it.</summary>
internal sealed record NoteRewrite(VaultPath Note, string Content, IReadOnlyList<LinkRewrite> Rewrites);

/// <summary>
/// What a pass over every note in the vault answers. Failures are answered rather than thrown,
/// because a consumer has to tell them apart to report them: an enumeration that failed and a note
/// that could not be read are different refusals, and neither is a link finding.
///
/// The hierarchy is closed by a private constructor. Not every call can produce every variant -
/// only a creation answers <see cref="NotNeeded"/> - and each member says which.
/// </summary>
/// <typeparam name="TResult">A <see cref="WikilinkPlan"/> for the two planning passes, a list of
/// <see cref="LinkOccurrence"/> for the census.</typeparam>
public abstract record LinkPassOutcome<TResult>
{
    private LinkPassOutcome() { }

    /// <summary>The pass ran over every note and produced its answer, which may be empty.</summary>
    public sealed record Completed(TResult Result) : LinkPassOutcome<TResult>;

    /// <summary>
    /// No pass was needed, so none ran. Creation's answer to a basename nothing else carries: the
    /// census was asked, nothing could change meaning, and no wikilink authority is required.
    /// Distinct from a completed pass that changed nothing, which is what the nullable pinned-links
    /// member on a creation result reports the difference between.
    /// </summary>
    public sealed record NotNeeded : LinkPassOutcome<TResult>;

    /// <summary>
    /// The vault could not be enumerated, carrying the layer's own classification. The pass has no
    /// answer at all: a partial enumeration would silently under-report exactly the links a caller
    /// needs.
    /// </summary>
    public sealed record EnumerationFailed(SearchOutcome<VaultPath> Outcome) : LinkPassOutcome<TResult>;

    /// <summary>
    /// A note that enumerated was there and could not be read. It fails the pass rather than being
    /// skipped: what may be skipped is a file with nothing left to read, and a file present and
    /// genuinely unreadable turns a failed operation into a silent success.
    /// </summary>
    public sealed record ReadFailed(string Path, string Reason) : LinkPassOutcome<TResult>;
}

/// <summary>
/// What applying a plan did. The hierarchy is closed by a private constructor.
/// </summary>
public abstract record LinkApplyOutcome
{
    private LinkApplyOutcome() { }

    /// <summary>Every note in the plan was written.</summary>
    public sealed record Applied(IReadOnlyList<LinkRewrite> Rewrites) : LinkApplyOutcome;

    /// <summary>
    /// A note could not be written, so the pass stopped there. It carries what landed and what did
    /// not, which is what a partial-mutation refusal is composed from - the lock means no other tool
    /// interleaved, never that anything rolls back.
    /// </summary>
    public sealed record Failed(
        string Path,
        WriteOutcome Outcome,
        IReadOnlyList<LinkRewrite> Written,
        IReadOnlyList<LinkRewrite> Unwritten) : LinkApplyOutcome;
}
