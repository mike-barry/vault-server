namespace VaultShared.Access;

/// <summary>
/// Decides which of several same-named candidates a bare wikilink resolves to. It ranks the
/// candidates it is handed, finds none itself, and touches no filesystem.
/// </summary>
/// <remarks>
/// The rule is not a design choice. It is Obsidian's observed behaviour, and the vault's existing
/// content already depends on it. It was measured on 2026-07-23 against Obsidian configured with
/// <i>New link format = shortest path when possible</i> and <i>Automatically update internal links
/// = on</i>. Re-measure the editor before changing it, because changing it changes which note
/// existing links open. The rule is confined to <see cref="Compare"/>, which is all a
/// re-measurement would need to change.
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
    /// Compares two candidates by the measured rule: the shorter relative path from the linking
    /// file wins, measured by character count, with ties broken by ordinal comparison of the
    /// candidates' vault-relative paths. A <c>..</c> component carries no extra weight, so two
    /// short hops beat one long one.
    /// </summary>
    /// <remarks>
    /// The tie-break compares vault-relative paths rather than relative ones deliberately. A
    /// <c>..</c> sorts before letters, so comparing the relative forms would give every tie to the
    /// candidate further out, which is not what the editor does.
    /// </remarks>
    /// <param name="from">The note the link is written in.</param>
    /// <param name="a">The first candidate.</param>
    /// <param name="b">The second candidate.</param>
    /// <returns>Negative when <paramref name="a"/> ranks first, positive when <paramref name="b"/> does.</returns>
    private static int Compare(VaultPath from, VaultPath a, VaultPath b)
    {
        var byDistance = RelativeTo(from, a).Length.CompareTo(RelativeTo(from, b).Length);
        return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Value, b.Value);
    }

    /// <summary>
    /// Returns the candidate's path written from the folder holding the linking note, using the
    /// editor's separator convention. Only its length is read; the string form is how the measured
    /// rule is stated.
    /// </summary>
    /// <param name="from">The note the link is written in.</param>
    /// <param name="candidate">The note the relative path leads to.</param>
    /// <returns>The relative path.</returns>
    private static string RelativeTo(VaultPath from, VaultPath candidate)
    {
        var folder = from.Segments.Count - 1;
        var shared = 0;
        while (shared < folder
               && shared < candidate.Segments.Count - 1
               && string.Equals(from.Segments[shared], candidate.Segments[shared], StringComparison.OrdinalIgnoreCase))
            shared++;

        return string.Join('/', Enumerable.Repeat("..", folder - shared).Concat(candidate.Segments.Skip(shared)));
    }
}
