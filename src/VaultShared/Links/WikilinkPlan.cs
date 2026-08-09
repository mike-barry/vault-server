using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// Represents every link change a relocation or a creation is about to make, composed in full
/// before anything is written. Planning only reads; <see cref="WikilinkMaintenance.Apply"/> writes.
/// </summary>
/// <remarks>
/// The plan names every note by the path it has now, so the consumer must apply it before making
/// its own mutation: a creation pins before it writes the new note, and a relocation repairs before
/// it moves the file. A plan holds each rewritten note's whole composed text, so applying it
/// re-reads nothing.
/// </remarks>
public sealed class WikilinkPlan
{
    private readonly IReadOnlyList<NoteRewrite> _notes;

    internal WikilinkPlan(IReadOnlyList<NoteRewrite> notes, IReadOnlyList<NoteRelocation> relocated, VaultPath? folderNote)
    {
        _notes = notes;
        Rewrites = [.. notes.SelectMany(n => n.Rewrites)];
        Repairs = [.. notes.SelectMany(n => n.Planned).Where(p => !p.Pinned).Select(p => p.Rewrite)];
        Pins = [.. notes.SelectMany(n => n.Planned).Where(p => p.Pinned).Select(p => p.Rewrite)];
        Relocated = relocated;
        FolderNote = folderNote;
    }

    /// <summary>
    /// Gets every link this plan changes, in note order and then in position order within a note.
    /// </summary>
    public IReadOnlyList<LinkRewrite> Rewrites { get; }

    /// <summary>
    /// Gets the repairs alone: links that named something the relocation moved and now name where
    /// it went. Empty for a creation, which relocates nothing.
    /// </summary>
    /// <remarks>
    /// Repairs and pins are separate members because the relocating tool reports them separately,
    /// and the two cannot be told apart afterwards from the rewrite text alone.
    /// </remarks>
    public IReadOnlyList<LinkRewrite> Repairs { get; }

    /// <summary>
    /// Gets the pins alone: bystander links rewritten to name the target they resolve to today, so
    /// that the change does not silently re-aim them. All of a creation's plan, and part of a
    /// relocation's.
    /// </summary>
    public IReadOnlyList<LinkRewrite> Pins { get; }

    /// <summary>
    /// Gets every note the relocation itself moves, before and after. One pair for a note
    /// relocation; one per contained note for a folder relocation, including the folder note under
    /// its new name. Empty for a creation, which moves nothing.
    /// </summary>
    /// <remarks>
    /// Planning has to derive this map anyway, because a link's emitted form depends on where every
    /// note ends up. Exposing it lets the relocating tool move exactly what the plan was computed
    /// against instead of deriving a second map that could differ.
    ///
    /// The non-note files a folder carries are not here, though the pass does repair the links
    /// naming them. A single directory rename relocates them under names that do not change, so a
    /// caller has nothing to translate for them and the tool has nothing to move for them by name.
    /// </remarks>
    public IReadOnlyList<NoteRelocation> Relocated { get; }

    /// <summary>
    /// Gets where a relocated folder's folder note ends up, or null when the folder has none and
    /// when the relocation is not a folder's. A folder without a folder note does not get one
    /// created.
    /// </summary>
    public VaultPath? FolderNote { get; }

    /// <summary>Gets whether the plan writes nothing.</summary>
    public bool IsEmpty => _notes.Count == 0;

    internal IReadOnlyList<NoteRewrite> Notes => _notes;
}

/// <summary>Represents one planned rewrite and which of the pass's two jobs produced it.</summary>
/// <param name="Rewrite">The link change itself.</param>
/// <param name="Pinned">True when the link was rewritten to name what it opens today, false when it
/// was re-aimed at where its target went.</param>
internal sealed record PlannedRewrite(LinkRewrite Rewrite, bool Pinned);

/// <summary>Represents one note's composed text and the rewrites that produced it.</summary>
/// <param name="Note">The note to be written.</param>
/// <param name="Content">The note's whole text with every rewrite already applied.</param>
/// <param name="Planned">The rewrites that produced the text, in position order.</param>
internal sealed record NoteRewrite(VaultPath Note, string Content, IReadOnlyList<PlannedRewrite> Planned)
{
    /// <summary>Gets the rewrites alone, in position order, for a consumer that does not need to
    /// know which kind each was.</summary>
    public IEnumerable<LinkRewrite> Rewrites => Planned.Select(p => p.Rewrite);
}

/// <summary>
/// Represents the result of a pass over every note in the vault. Failures are returned rather than
/// thrown, because a consumer has to tell them apart to report them: a failed enumeration and an
/// unreadable note are different refusals, and neither is a link finding.
/// </summary>
/// <remarks>
/// The hierarchy is closed by a private constructor. Not every call can produce every variant; only
/// a creation returns <see cref="NotNeeded"/>.
/// </remarks>
/// <typeparam name="TResult">A <see cref="WikilinkPlan"/> for the two planning passes, or a list of
/// <see cref="LinkOccurrence"/> for the census.</typeparam>
public abstract record LinkPassOutcome<TResult>
{
    private LinkPassOutcome() { }

    /// <summary>Indicates the pass ran over every note and produced its answer, which may be empty.</summary>
    /// <param name="Result">What the pass found.</param>
    public sealed record Completed(TResult Result) : LinkPassOutcome<TResult>;

    /// <summary>
    /// Indicates no pass was needed, so none ran. A creation returns this when nothing else in the
    /// vault carries the new note's basename, so no link's meaning can change. It is distinct from a
    /// completed pass that changed nothing, and the creating tool requires no wikilink authority for
    /// it.
    /// </summary>
    public sealed record NotNeeded : LinkPassOutcome<TResult>;

    /// <summary>
    /// Indicates the vault could not be enumerated, carrying the search layer's own classification.
    /// The pass produces no answer at all, because a partial enumeration would silently under-report
    /// exactly the links the caller needs.
    /// </summary>
    /// <param name="Outcome">The enumeration failure as the search layer classified it.</param>
    public sealed record EnumerationFailed(SearchOutcome<VaultPath> Outcome) : LinkPassOutcome<TResult>;

    /// <summary>
    /// Indicates a note that the enumeration listed was present and could not be read. This fails
    /// the whole pass rather than skipping the note, because skipping a note that is present and
    /// unreadable would turn a failed operation into a silent success. A note that has since
    /// disappeared is skipped instead.
    /// </summary>
    /// <param name="Path">The vault-relative path of the note that could not be read.</param>
    /// <param name="Reason">Why the read failed, as the read layer described it.</param>
    public sealed record ReadFailed(string Path, string Reason) : LinkPassOutcome<TResult>;
}

/// <summary>
/// Represents the result of applying a plan. The hierarchy is closed by a private constructor.
/// </summary>
public abstract record LinkApplyOutcome
{
    private LinkApplyOutcome() { }

    /// <summary>Indicates every note in the plan was written.</summary>
    /// <param name="Rewrites">Every link change that landed.</param>
    public sealed record Applied(IReadOnlyList<LinkRewrite> Rewrites) : LinkApplyOutcome;

    /// <summary>
    /// Indicates a note could not be written, so the pass stopped there. Nothing is rolled back:
    /// the mutex lock means no other tool interleaved, not that the writes already made are undone.
    /// The caller composes its partial-mutation refusal from the two lists.
    /// </summary>
    /// <param name="Path">The vault-relative path of the note that could not be written.</param>
    /// <param name="Outcome">Why the write failed, as the write layer classified it.</param>
    /// <param name="Written">The link changes that landed before the failure.</param>
    /// <param name="Unwritten">The link changes that did not land, including those in the failed note.</param>
    public sealed record Failed(
        string Path,
        WriteOutcome Outcome,
        IReadOnlyList<LinkRewrite> Written,
        IReadOnlyList<LinkRewrite> Unwritten) : LinkApplyOutcome;
}
