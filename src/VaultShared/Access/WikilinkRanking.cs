namespace VaultShared.Access;

/// <summary>
/// Decides which of several same-named candidates a bare wikilink resolves to. It ranks the
/// candidates it is handed, finds none itself, and touches no filesystem.
/// </summary>
/// <remarks>
/// The rule is not a design choice. It is Obsidian's observed behaviour, and the vault's existing
/// content already depends on it. It was measured on 2026-08-04 against Obsidian configured with
/// <i>New link format = shortest path when possible</i> and <i>Automatically update internal links
/// = on</i>. Re-measure the editor before changing it, because changing it changes which note
/// existing links open. The rule is confined to <see cref="Compare"/>, which is all a
/// re-measurement would need to change.
///
/// This is only the editor's second resolution tier. A target that names a whole vault path is
/// taken by the file at that path and never reaches ranking at all, which is why a note at the
/// vault root holds every bare link to its basename. That tier is applied before these candidates
/// are gathered.
/// </remarks>
public static class WikilinkRanking
{
    /// <summary>
    /// Returns the candidates in the order the editor ranks them, nearest first. The editor's
    /// autocomplete popup orders a duplicated basename the same way.
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
    /// Compares two candidates by the measured rule: the candidate sharing the longer folder prefix
    /// with the linking note wins, then the shallower of the two, then ordinal comparison of the
    /// candidates' vault-relative paths.
    /// </summary>
    /// <remarks>
    /// Distance is asymmetric. Climbing out of the linking note's subtree costs more than descending
    /// into it at any depth, so a candidate three folders below the linking note beats one a single
    /// folder above it. Ranking by the length of the relative path instead would reverse that pair.
    ///
    /// Descending is not free, though, only cheap: among candidates that climb equally far, the one
    /// that descends least wins, so a candidate in the linking note's own folder beats one in a
    /// subfolder of it. Leaving that to the ordinal comparison instead would decide it by the
    /// subfolder's first letter.
    /// </remarks>
    /// <param name="from">The note the link is written in.</param>
    /// <param name="a">The first candidate.</param>
    /// <param name="b">The second candidate.</param>
    /// <returns>Negative when <paramref name="a"/> ranks first, positive when <paramref name="b"/> does.</returns>
    private static int Compare(VaultPath from, VaultPath a, VaultPath b)
    {
        var byDepth = SharedDepth(from, b).CompareTo(SharedDepth(from, a));
        if (byDepth != 0)
            return byDepth;

        var byDescent = FolderDepth(a).CompareTo(FolderDepth(b));
        return byDescent != 0 ? byDescent : string.CompareOrdinal(a.Value, b.Value);
    }

    /// <summary>Returns how many folders the candidate sits inside.</summary>
    /// <param name="candidate">The note being measured.</param>
    /// <returns>The number of folders above the note.</returns>
    private static int FolderDepth(VaultPath candidate) => candidate.Segments.Count - 1;

    /// <summary>
    /// Returns how many leading folders the candidate shares with the folder holding the linking
    /// note. This is the whole of the distance measure: a longer shared prefix is nearer.
    /// </summary>
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
