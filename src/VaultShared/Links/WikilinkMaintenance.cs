using System.Text;
using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// The shared wikilink area the three mutating tools consume: relocation maintains the graph,
/// creation protects it, and removal reports on it - a relocation leaves a new target to point at,
/// a creation leaves a second candidate for a name, and a removal leaves nothing.
///
/// It is one area rather than three because all three read the same grammar, the same matching rule
/// and the same ranking comparator (<see cref="Wikilink"/>, <see cref="WikilinkTarget"/>,
/// <see cref="WikilinkRanking"/>). A link one consumer recognizes and another does not is a rewrite
/// that misses a link or a census that under-reports, and both fail silently.
///
/// <b>Every pass runs over every note in the vault</b>, through <see cref="VaultWidePass"/>. Repairing
/// or reporting only the notes inside the caller's scope leaves the rest of the vault pointing at
/// nothing, or stops a census exactly where the caller most needs the truth. The domain is note
/// enumeration for all three, which is what decides whether an emitted link is short or qualified;
/// the destruction census that asks the file surface is the removing tool's own and is not this.
///
/// <b>Nothing here asserts authority.</b> The wikilink dimension is the consumer's to assert, and
/// the notes this rewrites contribute none of their own: those rewrites are maintenance rather than
/// authoring, and the substitution reads no key.
///
/// <b>Nothing here parses a note.</b> A rewrite is written over the target's extent in the text as
/// read, so every byte outside a replaced target survives and a note whose frontmatter will not
/// parse is repaired like any other. Text goes in and text comes out; only
/// <see cref="Apply"/> writes, and only through the pass.
/// </summary>
public static class WikilinkMaintenance
{
    /// <summary>
    /// The repair one note's relocation causes, planned against the vault as it is now.
    ///
    /// Every link naming the note is re-emitted canonically rather than minimally - bare where the
    /// basename is unique after the move, the full vault path where it is not - so a deliberately
    /// path-qualified link to a unique name collapses to bare, which is the editor's own emit rule
    /// and not a form this preserves. A path-qualified target matches on a suffix of whole path
    /// components; a bare one is rewritten only where it resolves to the relocated note.
    ///
    /// Where the move would change what another bare link resolves to, that link is pinned to the
    /// target it has now and the plan proceeds. Protection extends to bystanders, and nothing ever
    /// fails on ambiguity: a gate that could trip will trip in the middle of a hundred-note pass.
    /// </summary>
    public static LinkPassOutcome<WikilinkPlan> PlanNoteRelocation(VaultWidePass pass, VaultPath from, VaultPath to) =>
        Relocate(pass, _ => new RelocationMap([new NoteRelocation(from, to)], null));

    /// <summary>
    /// The repair a folder's relocation causes. Contained names do not change, so the map is a
    /// single prefix substitution over every note under the folder, and the folder note is the only
    /// name event: it is renamed to match its new folder, and a folder that has none gets none
    /// created.
    ///
    /// The rename is derived here because the emitted form of every link depends on where each note
    /// ends up, and a repair computed against the folder note's old name would leave every link to
    /// it wrong. <see cref="WikilinkPlan.Relocated"/> and <see cref="WikilinkPlan.FolderNote"/> carry
    /// the map out, so the relocating tool moves what this planned against.
    /// </summary>
    public static LinkPassOutcome<WikilinkPlan> PlanFolderRelocation(VaultWidePass pass, VaultPath from, VaultPath to) =>
        Relocate(pass, notes => ContainedBy(notes, from, to));

    /// <summary>
    /// The pins a note's creation requires, planned against the vault as it is now and before the
    /// note is written. Interrupted after pinning, the vault holds links naming a target that is
    /// still correct; interrupted after writing, it holds bare links that have silently changed
    /// meaning - which is why the order is the consumer's to keep and this only reads.
    ///
    /// A basename already in use triggers the pass and nothing else does. Where the name is unique
    /// the answer is <see cref="LinkPassOutcome{TResult}.NotNeeded"/>: no pass ran, and no wikilink
    /// authority is required.
    ///
    /// Every bare link whose resolution the new note would change is rewritten to the qualified form
    /// naming the target it resolves to now. Every other link is left alone, and no link is ever
    /// aimed at the new note.
    /// </summary>
    public static LinkPassOutcome<WikilinkPlan> PlanCreation(VaultWidePass pass, VaultPath creating)
    {
        var listed = pass.EnumerateNotes();
        if (listed is not SearchOutcome<VaultPath>.Results results)
            return new LinkPassOutcome<WikilinkPlan>.EnumerationFailed(listed);

        var notes = results.Items;
        var before = new WikilinkUniverse(notes);

        // The basename census, asked before anything is read. A name nothing else carries cannot
        // change what any link resolves to, so there is nothing for a pass to find.
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
                    // Only bare links can change meaning: a new file leaves every path-qualified
                    // target naming exactly what it named before.
                    if (WikilinkTarget.IsPathQualified(target) || before.Resolve(linking, target) is not { } held)
                        return null;

                    return after.Resolve(linking, target) == held ? null : WikilinkName.Qualified(before[held]);
                }) is { } rewrite)
                planned.Add(rewrite);
        }

        return new LinkPassOutcome<WikilinkPlan>.Completed(new WikilinkPlan(planned, [], null));
    }

    /// <summary>
    /// Every surviving link into the set a removal destroys. Nothing is rewritten and nothing is
    /// pinned: deleting one of two same-named notes leaves bare links resolving to the survivor, and
    /// the caller's intent for them is not knowable here.
    ///
    /// A path-qualified link is reported where its target names something in the set on the suffix
    /// rule. A bare link is reported only where it resolves to something in the set, since a
    /// same-named bystander elsewhere is that link's real target. Links inside the removed set are
    /// not reported, and each surviving occurrence is reported individually, because a caller fixing
    /// them visits each one.
    /// </summary>
    /// <param name="removing">
    /// What the removal destroys, as its own plan lists it. Paths that are not notes are matched by
    /// a path-qualified target like any other; bare resolution ranks notes, which is the domain the
    /// editor resolves a bare link against.
    /// </param>
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
                var hit = WikilinkTarget.IsPathQualified(target)
                    ? removing.Any(path => WikilinkTarget.Matches(target, path))
                    : universe.Resolve(linking, target) is { } held && removed.Contains(universe[held]);

                if (hit)
                    found.Add(new LinkOccurrence(linking.Value, lines.At(occurrence.TargetStart), target));
            }
        }

        return new LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.Completed(found);
    }

    /// <summary>
    /// Writes a plan's notes back, through the pass: merely-contained paths, no scope check, and no
    /// authority contribution.
    ///
    /// <b>The plan names every note by the path it has now</b>, so a relocation applies this before
    /// it moves anything and a creation applies it before it writes the note. Applying after the
    /// mutation would address notes that are no longer where the plan found them.
    ///
    /// A failure stops the pass and names what landed and what did not. The mutex lock means no
    /// other tool interleaved; it does not mean rollback.
    /// </summary>
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
    /// The relocation pass both entry points share. The two differ only in which notes move, so the
    /// map is expanded from the enumeration and everything after it is one piece of reasoning.
    ///
    /// Two universes are built, before the move and after it, and every link is decided by comparing
    /// one against the other. Resolution depends on where files are rather than on what other links
    /// say, so the rewrites do not interact: every answer is computed against the same pair.
    /// </summary>
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
                    if (WikilinkTarget.IsPathQualified(target))
                        return map.Pairs.FirstOrDefault(pair => WikilinkTarget.Matches(target, pair.From)) is { } named
                            ? Emit(after, named.To)
                            : null;

                    if (before.Resolve(wasAt, target) is not { } held)
                        return null;

                    // The link named the relocated note: re-emit it at the destination.
                    if (moved.Contains(held))
                        return Emit(after, destinations[held]);

                    // A bystander. Its target did not move, but the move may have put a nearer
                    // same-named candidate in its way - or moved the linking note itself. Where the
                    // answer would change, the link is pinned to what it opens today.
                    return after.Resolve(willBe, target) == held ? null : WikilinkName.Qualified(destinations[held]);
                }) is { } rewrite)
                planned.Add(rewrite);
        }

        return new LinkPassOutcome<WikilinkPlan>.Completed(new WikilinkPlan(planned, map.Pairs, map.FolderNote));
    }

    /// <summary>
    /// The prefix substitution a folder's relocation is, over the notes the enumeration found under
    /// it, with the folder note's rename overriding its own prefix mapping.
    /// </summary>
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
    /// A path composed from segments this layer already produced. It is well-formed by construction,
    /// so text that is not is a defect here rather than a caller's malformed input.
    /// </summary>
    private static VaultPath At(Vault vault, string value) =>
        VaultPath.TryCreate(vault, value, out var path, out var reason)
            ? path
            : throw new InvalidOperationException($"Composed relocation target \"{value}\" is not a vault path: {reason}");

    /// <summary>
    /// Canonical emission, which is what the editor writes and therefore what will survive its next
    /// touch: the short form where the name is unique in the vault, the full vault path where it is
    /// not. A shortest sufficient suffix would be minimal instead, and the editor would rewrite it.
    /// </summary>
    private static string Emit(WikilinkUniverse universe, VaultPath target) =>
        universe.Bearers(WikilinkName.Bare(target)) == 1
            ? WikilinkName.Bare(target)
            : WikilinkName.Qualified(target);

    /// <summary>
    /// One note's text with every replaced target written over its extent, or null where nothing in
    /// it changed. Extents index the text exactly as <see cref="VaultWidePass.Read"/> decoded it, and
    /// the splice happens in that same string: normalizing anything first would shift every offset
    /// past a two-character terminator and write over the wrong bytes.
    /// </summary>
    private static NoteRewrite? Rewritten(VaultPath note, string text, Func<string, string?> replacement)
    {
        StringBuilder? composed = null;
        List<LinkRewrite>? rewrites = null;
        var copied = 0;
        var lines = new LineCounter(text);

        foreach (var occurrence in Wikilink.Recognize(text))
        {
            var target = occurrence.TargetIn(text);
            if (replacement(target) is not { } emitted || string.Equals(target, emitted, StringComparison.Ordinal))
                continue;

            composed ??= new StringBuilder(text.Length);
            rewrites ??= [];

            composed.Append(text, copied, occurrence.TargetStart - copied).Append(emitted);
            copied = occurrence.TargetStart + occurrence.TargetLength;
            rewrites.Add(new LinkRewrite(note.Value, lines.At(occurrence.TargetStart), target, emitted));
        }

        if (composed is null)
            return null;

        composed.Append(text, copied, text.Length - copied);
        return new NoteRewrite(note, composed.ToString(), rewrites!);
    }

    /// <summary>
    /// Every note's text, positionally, or the first failure that was not a note with nothing left
    /// to read. A note that enumerated and is now gone is skipped and its position stays null - what
    /// may be skipped is a file with nothing left to read. A note that is there and will not open
    /// fails the whole pass, because skipping it would turn a failed operation into a silent
    /// success.
    /// </summary>
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

    /// <summary>Which notes a relocation moves, and where its folder note lands.</summary>
    private sealed record RelocationMap(IReadOnlyList<NoteRelocation> Pairs, VaultPath? FolderNote);

    /// <summary>
    /// One-based line numbers from character offsets, walked forward once. Offsets arrive in order,
    /// because occurrences do.
    ///
    /// It counts <c>\n</c> alone, which is correct for both terminator conventions a note is stored
    /// in: the carriage return of a two-character terminator sits before the newline it belongs to
    /// and contributes no line of its own.
    /// </summary>
    private sealed class LineCounter(string text)
    {
        private int _scanned;
        private int _line = 1;

        public int At(int offset)
        {
            while (_scanned < offset)
                if (text[_scanned++] == '\n')
                    _line++;

            return _line;
        }
    }
}
