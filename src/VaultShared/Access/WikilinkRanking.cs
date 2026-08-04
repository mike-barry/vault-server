namespace VaultShared.Access;

/// <summary>
/// Which of several same-named candidates a bare wikilink resolves to. A comparator over a candidate
/// set and the linking note's path: it takes the candidates it is handed, finds nothing itself, and
/// touches no filesystem.
///
/// The rule is Obsidian's observed behaviour, not a design choice, and the vault's existing content
/// already depends on it. It was measured on 2026-07-23, against Obsidian configured with <i>New
/// link format = shortest path when possible</i> and <i>Automatically update internal links = on</i>;
/// re-measure the editor before changing it. The rule is isolated to <see cref="Compare"/>, which is
/// the whole of what a re-measurement would change.
/// </summary>
public static class WikilinkRanking
{
    /// <summary>
    /// The candidates in the order the editor would rank them, nearest first. Its autocomplete
    /// popup ranks a duplicated basename this way too.
    /// </summary>
    /// <param name="linkingNote">The note the link is written in. Distance is measured from it.</param>
    /// <exception cref="ArgumentException">A candidate is in another vault.</exception>
    public static IReadOnlyList<VaultPath> Rank(VaultPath linkingNote, IEnumerable<VaultPath> candidates)
    {
        var set = candidates.ToArray();

        // Checked over the set rather than inside the comparison, so a candidate from another vault
        // - a layer defect rather than a ranking answer, the posture VaultAccess.Admits takes -
        // surfaces as itself instead of as whatever the sort wraps it in.
        foreach (var candidate in set)
            if (candidate.Vault != linkingNote.Vault)
                throw new ArgumentException(
                    $"Candidate path belongs to vault \"{candidate.Vault.Name}\", not \"{linkingNote.Vault.Name}\".", nameof(candidates));

        return [.. set.Order(Comparer<VaultPath>.Create((a, b) => Compare(linkingNote, a, b)))];
    }

    /// <summary>
    /// Which candidate a bare wikilink in <paramref name="linkingNote"/> opens, or null when it is
    /// handed none. Resolution is deterministic rather than ambiguous, which is what lets repair
    /// rewrite a bare link only where it resolves to the relocated item.
    /// </summary>
    public static VaultPath? Resolve(VaultPath linkingNote, IEnumerable<VaultPath> candidates) =>
        Rank(linkingNote, candidates).FirstOrDefault();

    /// <summary>
    /// The observed rule, entire: shortest relative path from the linking file by pure character
    /// count - <c>..</c> components carry no extra weight, so two short hops beat one long one -
    /// with ties broken by ordinal comparison of the candidates' vault-relative paths.
    ///
    /// The tie-break compares vault-relative rather than relative paths on purpose: <c>..</c> sorts
    /// before letters, so comparing the relative forms would hand every dead tie to the candidate
    /// further out, which is not what the editor does.
    /// </summary>
    private static int Compare(VaultPath from, VaultPath a, VaultPath b)
    {
        var byDistance = RelativeTo(from, a).Length.CompareTo(RelativeTo(from, b).Length);
        return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Value, b.Value);
    }

    /// <summary>
    /// The path of <paramref name="candidate"/> written from the folder holding
    /// <paramref name="from"/>, in the editor's own separator convention. Only its length is read;
    /// the string is what the rule is stated over.
    /// </summary>
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
