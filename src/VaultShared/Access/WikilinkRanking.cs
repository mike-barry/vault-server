namespace VaultShared.Access;

/// <summary>
/// Decides which of several same-named candidates a bare wikilink resolves to. It ranks the
/// candidates it is handed, finds none itself, and touches no filesystem.
/// </summary>
/// <remarks>
/// The rule is Obsidian's observed behaviour rather than a design choice, and the vault's existing
/// content already depends on it. It holds for Obsidian configured with <i>New link format =
/// shortest path when possible</i> and <i>Automatically update internal links = on</i>. Changing it
/// changes which note existing links open, so re-measure the editor first. The rule is confined to
/// <see cref="Compare"/>, which is all a re-measurement would need to change.
///
/// Measure by following a link. The editor's autocomplete popup orders a duplicated basename by a
/// comparator of its own, so its first entry is not necessarily the note the link opens and a
/// measurement taken from it describes the popup rather than resolution.
///
/// <c>Ref-Obsidian-Link-Behavior.md</c> carries the whole rule and the conditions it holds under.
/// </remarks>
public static class WikilinkRanking
{
    /// <summary>
    /// Returns the candidates in the order the editor resolves them, nearest first. This is the
    /// order a click on the link follows.
    /// </summary>
    /// <param name="linkingNote">The note the link is written in. Distance is measured from it.</param>
    /// <param name="candidates">The same-named notes to rank.</param>
    /// <returns>The candidates, nearest first.</returns>
    /// <exception cref="ArgumentException">A candidate is in another vault.</exception>
    public static IReadOnlyList<VaultPath> Rank(VaultPath linkingNote, IEnumerable<VaultPath> candidates)
    {
        var set = candidates.ToArray();

        // Checked here rather than inside the comparison, so the exception surfaces as itself
        // instead of wrapped in whatever the sort throws.
        foreach (var candidate in set)
            if (candidate.Vault != linkingNote.Vault)
                throw new ArgumentException(
                    $"Candidate path belongs to vault \"{candidate.Vault.Name}\", not \"{linkingNote.Vault.Name}\".", nameof(candidates));

        return [.. set.Order(Comparer<VaultPath>.Create((a, b) => Compare(linkingNote, a, b)))];
    }

    /// <summary>
    /// Returns the candidate a bare wikilink in the linking note opens, or null when no candidates
    /// are given. Resolution is always deterministic, which is what lets repair rewrite a bare link
    /// only where it resolves to the relocated item.
    /// </summary>
    /// <param name="linkingNote">The note the link is written in.</param>
    /// <param name="candidates">The same-named notes to choose from.</param>
    /// <returns>The candidate the link opens, or null.</returns>
    /// <exception cref="ArgumentException">A candidate is in another vault.</exception>
    public static VaultPath? Resolve(VaultPath linkingNote, IEnumerable<VaultPath> candidates) =>
        Rank(linkingNote, candidates).FirstOrDefault();

    /// <summary>
    /// Compares two candidates by the measured rule: a candidate inside the linking note's folder
    /// subtree beats one outside it, then the shorter relative spelling wins, then ordinal
    /// comparison of the candidates' vault-relative paths breaks the tie.
    /// </summary>
    /// <remarks>
    /// Distance is asymmetric. Climbing out of the linking note's subtree costs more than descending
    /// into it at any depth, so a candidate three folders below the linking note beats one a single
    /// folder above it. Comparing the two spellings without splitting the groups first would reverse
    /// that pair, because the climb is only three characters and the descent is a whole folder name.
    ///
    /// Within a group the spelling decides, so how much of the prefix a candidate shares affects
    /// only which group it lands in and how many climbs its spelling carries. A candidate two
    /// folders down under short names beats one a single folder down under a long one, and a
    /// candidate two climbs away beats one a single climb away whose descent is longer.
    ///
    /// The comparison is a total order, so the sort is stable whatever order the candidates were
    /// discovered in. The ordinal comparison of two distinct vault paths is never zero.
    /// </remarks>
    /// <param name="from">The note the link is written in.</param>
    /// <param name="a">The first candidate.</param>
    /// <param name="b">The second candidate.</param>
    /// <returns>Negative when <paramref name="a"/> ranks first, positive when <paramref name="b"/> does.</returns>
    private static int Compare(VaultPath from, VaultPath a, VaultPath b)
    {
        var (insideA, spellingA) = Spelling(from, a);
        var (insideB, spellingB) = Spelling(from, b);

        if (insideA != insideB)
            return insideA ? -1 : 1;

        var bySpelling = spellingA.CompareTo(spellingB);
        return bySpelling != 0 ? bySpelling : string.CompareOrdinal(a.Value, b.Value);
    }

    /// <summary>
    /// Measures the relative target a link written in the linking note needs in order to reach the
    /// candidate, reporting whether that target climbs out of the linking note's folder subtree and
    /// how many characters it takes to write.
    /// </summary>
    /// <remarks>
    /// The count is of the target text itself: three characters for each <c>../</c> that climbs one
    /// folder, then the candidate's segments below the shared folders joined with <c>/</c>, filename
    /// and extension included. The text is not built, only measured, because nothing here writes a
    /// link.
    /// </remarks>
    /// <param name="from">The note the link is written in.</param>
    /// <param name="candidate">The note being measured.</param>
    /// <returns>Whether the candidate needs no climb, and the length in characters of the relative
    /// target that reaches it.</returns>
    private static (bool Inside, int Length) Spelling(VaultPath from, VaultPath candidate)
    {
        var shared = SharedDepth(from, candidate);
        var climbs = from.Segments.Count - 1 - shared;

        // One separator between each pair of the segments being descended through, and none after
        // the last of them.
        var descent = candidate.Segments.Count - shared - 1;
        for (var i = shared; i < candidate.Segments.Count; i++)
            descent += candidate.Segments[i].Length;

        return (climbs == 0, (climbs * 3) + descent);
    }

    /// <summary>
    /// Returns how many leading folders the candidate shares with the folder holding the linking
    /// note. Sharing all of them is what puts the candidate inside the linking note's subtree, and
    /// the shortfall is how many folders a target must climb to reach it.
    /// </summary>
    /// <remarks>
    /// Folder names are compared case-insensitively, because the vault sits on a case-insensitive
    /// filesystem and two folders differing only in case are one folder there. The tie-break in
    /// <see cref="Compare"/> is case-sensitive, which is a separate question about ordering two
    /// distinct paths rather than about whether two names denote the same folder.
    /// </remarks>
    /// <param name="from">The note the link is written in.</param>
    /// <param name="candidate">The note being measured.</param>
    /// <returns>The number of shared leading folders.</returns>
    private static int SharedDepth(VaultPath from, VaultPath candidate)
    {
        var folder = from.Segments.Count - 1;
        var shared = 0;
        while (shared < folder
               && shared < candidate.Segments.Count - 1
               && string.Equals(from.Segments[shared], candidate.Segments[shared], StringComparison.OrdinalIgnoreCase))
            shared++;

        return shared;
    }
}
