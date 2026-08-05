using System.Text;
using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// Maintains the vault's wikilinks for the three mutating tools: relocation repairs links to what
/// moved, creation pins links a new note would re-aim, and removal reports the links that would be
/// left dangling.
/// </summary>
/// <remarks>
/// All three share one grammar, one matching rule and one ranking comparator
/// (<see cref="Wikilink"/>, <see cref="WikilinkTarget"/>, <see cref="WikilinkRanking"/>). Keep them
/// shared: a link one consumer recognizes and another does not is a rewrite that misses a link or a
/// census that under-reports, and both fail silently. All three reach the matching rule through
/// <see cref="WikilinkUniverse"/>, which is what decides the note a target opens; only the census
/// also reads <see cref="WikilinkTarget"/> directly, for the targets that name a file the universe
/// does not index.
///
/// Every pass runs over every note in the vault, through <see cref="VaultWidePass"/>, never only
/// the notes inside the caller's scope. Links to a moved note can be anywhere, so a scoped pass
/// leaves the rest of the vault pointing at nothing or stops a census short. Note enumeration is
/// also the domain that decides whether an emitted link is short or qualified. The separate census
/// a removal runs over the file surface belongs to the removing tool and is not this.
///
/// Nothing here asserts authority. The consumer asserts the wikilink dimension; the notes rewritten
/// here contribute none of their own, because a rewrite is maintenance rather than authoring and
/// the substitution reads no key.
///
/// Nothing here parses a note. A rewrite is written over the target's extent in the text as read,
/// so every byte outside a replaced target survives and a note whose frontmatter will not parse is
/// repaired like any other. Only <see cref="Apply"/> writes, and only through the pass.
/// </remarks>
public static class WikilinkMaintenance
{
    /// <summary>
    /// Plans the link repair one note's relocation causes, against the vault as it is now.
    /// </summary>
    /// <remarks>
    /// Every link naming the note is re-emitted in its canonical form: bare when the basename is
    /// unique after the move, the full vault path when it is not. A deliberately path-qualified link
    /// to a unique name therefore collapses to bare. That is the editor's own emit rule, and the
    /// author's chosen form is not preserved. Every target, bare or path-qualified, is rewritten
    /// only where it resolves to the relocated note, because a target can name a note the editor
    /// does not open with it.
    ///
    /// Where the move would change what some other bare link resolves to, that link is rewritten to
    /// name the target it has now and planning continues. Ambiguity never fails the pass, because a
    /// failure part-way through a hundred-note pass is worse than a pinned link.
    /// </remarks>
    /// <param name="pass">The vault-wide pass to enumerate and read through.</param>
    /// <param name="from">Where the note is now.</param>
    /// <param name="to">Where the note will be.</param>
    /// <returns>The plan, or the failure that stopped the pass.</returns>
    public static LinkPassOutcome<WikilinkPlan> PlanNoteRelocation(VaultWidePass pass, VaultPath from, VaultPath to) =>
        Relocate(pass, _ => new RelocationMap([new NoteRelocation(from, to)], null));

    /// <summary>
    /// Plans the link repair a folder's relocation causes, against the vault as it is now.
    /// </summary>
    /// <remarks>
    /// Contained names do not change, so the map is a single prefix substitution over every note
    /// under the folder. The folder note is the one exception: it is renamed to match its new
    /// folder, and a folder without one does not get one created.
    ///
    /// That rename is derived here because a link's emitted form depends on where each note ends up,
    /// and a repair computed against the folder note's old name would leave every link to it wrong.
    /// <see cref="WikilinkPlan.Relocated"/> and <see cref="WikilinkPlan.FolderNote"/> carry the map
    /// out so the relocating tool moves exactly what this planned against.
    /// </remarks>
    /// <param name="pass">The vault-wide pass to enumerate and read through.</param>
    /// <param name="from">Where the folder is now.</param>
    /// <param name="to">Where the folder will be.</param>
    /// <returns>The plan, or the failure that stopped the pass.</returns>
    public static LinkPassOutcome<WikilinkPlan> PlanFolderRelocation(VaultWidePass pass, VaultPath from, VaultPath to) =>
        Relocate(pass, notes => ContainedBy(notes, from, to));

    /// <summary>
    /// Plans the pins a note's creation requires, against the vault as it is now and before the new
    /// note is written.
    /// </summary>
    /// <remarks>
    /// The consumer must pin before it writes the note. Interrupted after pinning, the vault holds
    /// links naming a target that is still correct; interrupted after writing, it holds bare links
    /// whose meaning has silently changed.
    ///
    /// Only a basename already in use triggers the pass. Where the name is unique the result is
    /// <see cref="LinkPassOutcome{TResult}.NotNeeded"/>: no pass ran, and no wikilink authority is
    /// required.
    ///
    /// Every link whose resolution the new note would change is rewritten to the qualified form
    /// naming the target it resolves to now. A path-qualified link is not exempt: a target need not
    /// spell a note's whole path to name it, so a note created at exactly the path a target spells
    /// takes that link from the note it used to open. Every other link is left alone, and no link is
    /// ever aimed at the new note.
    /// </remarks>
    /// <param name="pass">The vault-wide pass to enumerate and read through.</param>
    /// <param name="creating">The note about to be created.</param>
    /// <returns>The plan, <see cref="LinkPassOutcome{TResult}.NotNeeded"/> when the basename is
    /// unique, or the failure that stopped the pass.</returns>
    public static LinkPassOutcome<WikilinkPlan> PlanCreation(VaultWidePass pass, VaultPath creating)
    {
        var listed = pass.EnumerateNotes();
        if (listed is not SearchOutcome<VaultPath>.Results results)
            return new LinkPassOutcome<WikilinkPlan>.EnumerationFailed(listed);

        var notes = results.Items;
        var before = new WikilinkUniverse(notes);

        // Asked before any note is read. A basename nothing else carries cannot change what any
        // link resolves to, so there is nothing for a pass to find.
        if (before.Bearers(WikilinkName.Bare(creating)) == 0)
            return new LinkPassOutcome<WikilinkPlan>.NotNeeded();

        var (texts, unreadable) = ReadAll(pass, notes);
        if (unreadable is not null)
            return new LinkPassOutcome<WikilinkPlan>.ReadFailed(unreadable.Path, unreadable.Reason);

        var after = new WikilinkUniverse([.. notes, creating]);

        var planned = new List<NoteRewrite>();
        for (var i = 0; i < notes.Count; i++)
        {
            if (texts[i] is not { } text)
                continue;

            var linking = notes[i];
            if (Rewritten(linking, text, target =>
                {
                    if (before.Resolve(linking, target) is not { } held)
                        return null;

                    // Every rewrite a creation plans is a pin. Nothing moved, so the replacement
                    // always names the note the link already resolved to, never the new note.
                    return after.Resolve(linking, target) == held
                        ? null
                        : new Replacement(WikilinkName.Qualified(before[held]), Pinned: true);
                }) is { } rewrite)
                planned.Add(rewrite);
        }

        return new LinkPassOutcome<WikilinkPlan>.Completed(new WikilinkPlan(planned, [], null));
    }

    /// <summary>
    /// Finds every surviving link into the set a removal destroys. Nothing is rewritten and nothing
    /// is pinned; the caller decides what to do with each occurrence.
    /// </summary>
    /// <remarks>
    /// Removing one of two same-named notes does re-aim bare links onto the survivor, but they are
    /// still deliberately not pinned, because what the caller wants for them is not knowable here.
    ///
    /// A link that opens a note is reported when that note is in the set, and not otherwise: a note
    /// the target also names but does not open is not that link's target. A link that opens no note
    /// is matched against the set directly, which is how a link to a file that is not a note is
    /// reported. Links inside the removed set are not reported. Each surviving occurrence is
    /// reported individually, since a caller fixing them visits each one.
    /// </remarks>
    /// <param name="pass">The vault-wide pass to enumerate and read through.</param>
    /// <param name="removing">
    /// What the removal destroys, as its own plan lists it. A path that is not a note is never in
    /// the resolution index, so a target naming one is matched against this set directly, bare and
    /// path-qualified alike. Ranking itself sees notes only, which is the domain the editor resolves
    /// a bare link against.
    /// </param>
    /// <returns>Every surviving occurrence, or the failure that stopped the pass.</returns>
    public static LinkPassOutcome<IReadOnlyList<LinkOccurrence>> Census(
        VaultWidePass pass, IReadOnlyCollection<VaultPath> removing)
    {
        var listed = pass.EnumerateNotes();
        if (listed is not SearchOutcome<VaultPath>.Results results)
            return new LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.EnumerationFailed(listed);

        var notes = results.Items;
        var removed = new HashSet<VaultPath>(removing);
        var surviving = new List<int>();
        for (var i = 0; i < notes.Count; i++)
            if (!removed.Contains(notes[i]))
                surviving.Add(i);

        var (texts, unreadable) = ReadAll(pass, notes, surviving);
        if (unreadable is not null)
            return new LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.ReadFailed(unreadable.Path, unreadable.Reason);

        var universe = new WikilinkUniverse(notes);
        var found = new List<LinkOccurrence>();

        foreach (var i in surviving)
        {
            if (texts[i] is not { } text)
                continue;

            var linking = notes[i];
            var lines = new LineCounter(text);
            foreach (var occurrence in Wikilink.Recognize(text))
            {
                var target = occurrence.TargetIn(text);

                // A target that resolves is answered by the note it opens, whether or not that note
                // is being removed. A target no note answers may still name a file that is not a
                // note, which the universe does not index, so it is matched against the set here.
                // That match reads the address rather than the target as written, or a relative link
                // to an attachment - the form the editor writes for one - would be reported by
                // nobody. Matching is wider than the exact-path tier and subsumes it, so the two
                // tiers resolution runs need no separate expression here.
                var hit = universe.Resolve(linking, target) is { } held
                    ? removed.Contains(universe[held])
                    : WikilinkTarget.Address(target, linking) is { } address
                        && removing.Any(path => WikilinkTarget.Matches(address, path));

                if (hit)
                    found.Add(new LinkOccurrence(linking.Value, lines.At(occurrence.TargetStart), target));
            }
        }

        return new LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.Completed(found);
    }

    /// <summary>
    /// Writes a plan's notes back through the pass, using merely-contained paths, with no scope
    /// check and no authority contribution.
    /// </summary>
    /// <remarks>
    /// The plan names every note by the path it has now, so a relocation must call this before it
    /// moves anything and a creation before it writes the new note. Applying after the mutation
    /// would address notes that are no longer where the plan found them.
    ///
    /// A failure stops the pass and names what landed and what did not. Nothing is rolled back: the
    /// mutex lock means no other tool interleaved, not that the writes already made are undone.
    /// </remarks>
    /// <param name="pass">The vault-wide pass to write through.</param>
    /// <param name="plan">The plan to write.</param>
    /// <returns>What was written, or the first write failure with the split of landed and unlanded
    /// changes.</returns>
    public static LinkApplyOutcome Apply(VaultWidePass pass, WikilinkPlan plan)
    {
        var written = new List<LinkRewrite>();
        for (var i = 0; i < plan.Notes.Count; i++)
        {
            var note = plan.Notes[i];
            var outcome = pass.Replace(note.Note, note.Content);
            if (outcome is not WriteOutcome.Written)
                return new LinkApplyOutcome.Failed(
                    note.Note.Value, outcome, written, [.. plan.Notes.Skip(i).SelectMany(n => n.Rewrites)]);

            written.AddRange(note.Rewrites);
        }

        return new LinkApplyOutcome.Applied(plan.Rewrites);
    }

    /// <summary>
    /// Runs the relocation pass both entry points share. A note relocation and a folder relocation
    /// differ only in which notes move, so the caller supplies that map and everything after it is
    /// the same.
    /// </summary>
    /// <remarks>
    /// Two universes are built, one for the vault before the move and one for after it, and every
    /// link is decided by comparing the two. Resolution depends only on where files are, not on what
    /// other links say, so the rewrites do not interact and every answer is computed against the
    /// same pair.
    /// </remarks>
    /// <param name="pass">The vault-wide pass to enumerate and read through.</param>
    /// <param name="expand">Produces the relocation map from the enumerated notes.</param>
    /// <returns>The plan, or the failure that stopped the pass.</returns>
    private static LinkPassOutcome<WikilinkPlan> Relocate(
        VaultWidePass pass, Func<IReadOnlyList<VaultPath>, RelocationMap> expand)
    {
        var listed = pass.EnumerateNotes();
        if (listed is not SearchOutcome<VaultPath>.Results results)
            return new LinkPassOutcome<WikilinkPlan>.EnumerationFailed(listed);

        var notes = results.Items;
        var map = expand(notes);

        var relocated = map.Pairs.ToDictionary(pair => pair.From, pair => pair.To);
        var moved = new HashSet<int>();
        var destinations = notes.ToArray();
        for (var i = 0; i < notes.Count; i++)
            if (relocated.TryGetValue(notes[i], out var destination))
            {
                destinations[i] = destination;
                moved.Add(i);
            }

        var (texts, unreadable) = ReadAll(pass, notes);
        if (unreadable is not null)
            return new LinkPassOutcome<WikilinkPlan>.ReadFailed(unreadable.Path, unreadable.Reason);

        var before = new WikilinkUniverse(notes);
        var after = new WikilinkUniverse(destinations);

        var planned = new List<NoteRewrite>();
        for (var i = 0; i < notes.Count; i++)
        {
            if (texts[i] is not { } text)
                continue;

            var wasAt = notes[i];
            var willBe = destinations[i];

            if (Rewritten(wasAt, text, target =>
                {
                    if (before.Resolve(wasAt, target) is not { } held)
                        return null;

                    // The link named the relocated note, so re-emit it at the destination. This is
                    // a repair.
                    if (moved.Contains(held))
                        return new Replacement(Emit(after, destinations[held]), Pinned: false);

                    // A bystander link. Its target did not move, but the move may have put a nearer
                    // same-named candidate in its way, or moved the linking note itself. Resolution
                    // after the move is measured from willBe, not wasAt, for exactly that reason.
                    // Where the answer would change, pin the link to what it opens today.
                    return after.Resolve(willBe, target) == held
                        ? null
                        : new Replacement(WikilinkName.Qualified(destinations[held]), Pinned: true);
                }) is { } rewrite)
                planned.Add(rewrite);
        }

        return new LinkPassOutcome<WikilinkPlan>.Completed(new WikilinkPlan(planned, map.Pairs, map.FolderNote));
    }

    /// <summary>
    /// Builds a folder relocation's map as a prefix substitution over the enumerated notes under the
    /// folder, with the folder note's rename overriding its own prefix mapping.
    /// </summary>
    /// <param name="notes">Every note the enumeration found.</param>
    /// <param name="from">Where the folder is now.</param>
    /// <param name="to">Where the folder will be.</param>
    /// <returns>The contained notes' before and after paths, and where the folder note lands.</returns>
    private static RelocationMap ContainedBy(IReadOnlyList<VaultPath> notes, VaultPath from, VaultPath to)
    {
        var prefix = from.Value + '/';
        var folderNoteWas = prefix + from.Segments[^1] + VaultName.NoteExtension;
        var folderNoteWillBe = to.Value + '/' + to.Segments[^1] + VaultName.NoteExtension;

        var pairs = new List<NoteRelocation>();
        VaultPath? folderNote = null;

        foreach (var note in notes)
        {
            if (!note.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var isFolderNote = string.Equals(note.Value, folderNoteWas, StringComparison.OrdinalIgnoreCase);
            var moved = At(from.Vault, isFolderNote ? folderNoteWillBe : to.Value + note.Value[from.Value.Length..]);

            if (isFolderNote)
                folderNote = moved;

            pairs.Add(new NoteRelocation(note, moved));
        }

        return new RelocationMap(pairs, folderNote);
    }

    /// <summary>
    /// Creates a path from segments this layer already produced. The value is well-formed by
    /// construction, so a rejection is a defect here rather than malformed caller input.
    /// </summary>
    /// <param name="vault">The vault the path belongs to.</param>
    /// <param name="value">The composed vault-relative path.</param>
    /// <returns>The path.</returns>
    /// <exception cref="InvalidOperationException">The composed value is not a valid vault path.</exception>
    private static VaultPath At(Vault vault, string value) =>
        VaultPath.TryCreate(vault, value, out var path, out var reason)
            ? path
            : throw new InvalidOperationException($"Composed relocation target \"{value}\" is not a vault path: {reason}");

    /// <summary>
    /// Returns the canonical form of a link to <paramref name="target"/>: the bare basename when the
    /// name is unique in the vault, the full vault path when it is not.
    /// </summary>
    /// <remarks>
    /// This matches what the editor itself writes, so the emitted link survives the editor's next
    /// touch. Emitting the shortest sufficient suffix instead would be minimal rather than
    /// canonical, and the editor would rewrite it.
    /// </remarks>
    /// <param name="universe">The universe to count bearers in, normally the one after the change.</param>
    /// <param name="target">The note being linked to.</param>
    /// <returns>The target text to write into the link.</returns>
    private static string Emit(WikilinkUniverse universe, VaultPath target) =>
        universe.Bearers(WikilinkName.Bare(target)) == 1
            ? WikilinkName.Bare(target)
            : WikilinkName.Qualified(target);

    /// <summary>
    /// Returns one note's text with every replaced target written over its extent, or null when
    /// nothing in it changed.
    /// </summary>
    /// <remarks>
    /// Do not normalize or re-encode the text before splicing. Extents index the string exactly as
    /// <see cref="VaultWidePass.Read"/> decoded it, and rewriting line terminators first would shift
    /// every offset past a two-character terminator so the splice would write over the wrong
    /// characters.
    /// </remarks>
    /// <param name="note">The note the text was read from.</param>
    /// <param name="text">The note's text, exactly as read.</param>
    /// <param name="replacement">Decides each target's replacement, or null to leave it alone.</param>
    /// <returns>The composed rewrite, or null when no target changed.</returns>
    private static NoteRewrite? Rewritten(VaultPath note, string text, Func<string, Replacement?> replacement)
    {
        StringBuilder? composed = null;
        List<PlannedRewrite>? planned = null;
        var copied = 0;
        var lines = new LineCounter(text);

        foreach (var occurrence in Wikilink.Recognize(text))
        {
            var target = occurrence.TargetIn(text);
            if (replacement(target) is not { } chosen || string.Equals(target, chosen.Emitted, StringComparison.Ordinal))
                continue;

            composed ??= new StringBuilder(text.Length);
            planned ??= [];

            composed.Append(text, copied, occurrence.TargetStart - copied).Append(chosen.Emitted);
            copied = occurrence.TargetStart + occurrence.TargetLength;
            planned.Add(new PlannedRewrite(
                new LinkRewrite(note.Value, lines.At(occurrence.TargetStart), target, chosen.Emitted), chosen.Pinned));
        }

        if (composed is null)
            return null;

        composed.Append(text, copied, text.Length - copied);
        return new NoteRewrite(note, composed.ToString(), planned!);
    }

    /// <summary>
    /// Represents what one link's target becomes and which of the pass's two jobs produced it. The
    /// distinction is recorded here, at the point of decision, because a repair and a pin cannot be
    /// told apart from the emitted text afterwards and the relocating tool reports them separately.
    /// </summary>
    /// <param name="Emitted">The target text to write in place of the old one.</param>
    /// <param name="Pinned">True for a pin, false for a repair.</param>
    private readonly record struct Replacement(string Emitted, bool Pinned);

    /// <summary>
    /// Reads every requested note's text into a positional array, stopping at the first read
    /// failure.
    /// </summary>
    /// <remarks>
    /// A note that the enumeration listed but that has since disappeared is skipped and its position
    /// stays null, because there is nothing left in it to read. A note that is still present and
    /// will not open fails the whole pass instead, because skipping it would turn a failed operation
    /// into a silent success.
    /// </remarks>
    /// <param name="pass">The vault-wide pass to read through.</param>
    /// <param name="notes">Every enumerated note. Positions index into this list.</param>
    /// <param name="positions">Which notes to read, or null to read them all.</param>
    /// <returns>The texts by position, and the failure that stopped the read, or null when none
    /// did.</returns>
    private static (string?[] Texts, ReadOutcome.Failed? Unreadable) ReadAll(
        VaultWidePass pass, IReadOnlyList<VaultPath> notes, IReadOnlyList<int>? positions = null)
    {
        var texts = new string?[notes.Count];
        foreach (var i in positions ?? Enumerable.Range(0, notes.Count))
            switch (pass.Read(notes[i]))
            {
                case ReadOutcome.Content content:
                    texts[i] = content.Text;
                    break;
                case ReadOutcome.Failed failed:
                    return (texts, failed);
            }

        return (texts, null);
    }

    /// <summary>Represents which notes a relocation moves, and where its folder note lands.</summary>
    /// <param name="Pairs">Each moved note's path before and after.</param>
    /// <param name="FolderNote">Where the folder note lands, or null when there is none.</param>
    private sealed record RelocationMap(IReadOnlyList<NoteRelocation> Pairs, VaultPath? FolderNote);

    /// <summary>
    /// Converts character offsets into one-based line numbers, scanning the text forward only once.
    /// Offsets must be requested in increasing order, which they are because occurrences are found
    /// in order.
    /// </summary>
    /// <remarks>
    /// Counts <c>\n</c> alone, which is correct for both line terminators a note may be stored with:
    /// the carriage return of a two-character terminator sits before the newline it belongs to and
    /// starts no line of its own.
    /// </remarks>
    /// <param name="text">The text the offsets index into.</param>
    private sealed class LineCounter(string text)
    {
        private int _scanned;
        private int _line = 1;

        /// <summary>Returns the one-based line the given offset falls on.</summary>
        /// <param name="offset">A character offset, not less than any previously requested.</param>
        /// <returns>The line number.</returns>
        public int At(int offset)
        {
            while (_scanned < offset)
                if (text[_scanned++] == '\n')
                    _line++;

            return _line;
        }
    }
}
