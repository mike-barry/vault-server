using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// Indexes a set of notes by the names that can name them, and resolves bare wikilinks against it.
/// Answers which note a target opens and how many notes share a basename.
/// </summary>
/// <remarks>
/// A relocation or a creation builds two of these, one for the vault before the change and one for
/// after it, and decides each link by comparing the two answers. A note is identified by its
/// position in the list rather than by its path, so it keeps the same identity across the pair even
/// though its path may differ between them. Both universes must therefore be built from lists in
/// the same order.
/// </remarks>
internal sealed class WikilinkUniverse
{
    private readonly IReadOnlyList<VaultPath> _paths;

    /// <summary>
    /// Every name that names a note, mapped to the positions bearing it. A note is registered under
    /// its final segment and under that segment's stem, because a link target may omit the
    /// extension and both spellings name the same file.
    /// </summary>
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a universe over the given notes, indexed by position.</summary>
    /// <param name="paths">The notes to resolve against. Positions index into this list.</param>
    public WikilinkUniverse(IReadOnlyList<VaultPath> paths)
    {
        _paths = paths;
        for (var i = 0; i < paths.Count; i++)
        {
            var name = paths[i].Segments[^1];
            Register(name, i);

            var stem = WikilinkName.StemOf(name);
            if (!string.Equals(stem, name, StringComparison.Ordinal))
                Register(stem, i);
        }
    }

    /// <summary>Gets the note at the given position.</summary>
    /// <param name="position">A position produced by <see cref="Resolve"/> or by construction order.</param>
    public VaultPath this[int position] => _paths[position];

    /// <summary>
    /// Returns how many notes a bare target of this text names.
    /// </summary>
    /// <remarks>
    /// A count of one is what lets a link be emitted in its short form. Emission is canonical, not
    /// minimal: a unique basename is written bare and a duplicated one is written as the full vault
    /// path.
    /// </remarks>
    /// <param name="name">The bare name to count, with or without the note extension.</param>
    /// <returns>The number of notes bearing the name, or zero when none do.</returns>
    public int Bearers(string name) => _byName.TryGetValue(name, out var found) ? found.Count : 0;

    /// <summary>
    /// Returns the position of the note a bare <paramref name="target"/> opens when written in
    /// <paramref name="linkingNote"/>, or null when nothing carries the name. The answer is always
    /// deterministic, so a pass can decide a bare link without treating ambiguity as a failure.
    /// </summary>
    /// <remarks>
    /// The linking note is a parameter rather than a position in this universe because a relocation
    /// moves the linking note as well, and the answer must be measured from where that note will be
    /// rather than from where it is.
    /// </remarks>
    /// <param name="linkingNote">The note the link is written in. Ranking distance is measured from it.</param>
    /// <param name="target">The bare target text as the note carries it.</param>
    /// <returns>The position of the note the target opens, or null when no note bears the name.</returns>
    public int? Resolve(VaultPath linkingNote, string target)
    {
        if (!_byName.TryGetValue(target, out var candidates))
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        var winner = WikilinkRanking.Resolve(linkingNote, candidates.Select(i => _paths[i]));

        // Matched by reference because ranking hands back one of the instances it was given, and
        // the caller needs a position rather than a path.
        foreach (var position in candidates)
            if (ReferenceEquals(_paths[position], winner))
                return position;

        return null;
    }

    private void Register(string name, int position)
    {
        if (!_byName.TryGetValue(name, out var bearers))
            _byName[name] = bearers = [];
        bearers.Add(position);
    }
}

/// <summary>
/// Writes a path in the two forms a wikilink target takes. The editor writes a note's target
/// without its extension and every other file's with it, so both of a note's forms are its path
/// with the note extension removed.
/// </summary>
internal static class WikilinkName
{
    /// <summary>Returns the name without the note extension, or unchanged when it has none.</summary>
    /// <param name="name">A path segment or a whole vault path.</param>
    /// <returns>The name with a trailing note extension removed.</returns>
    public static string StemOf(string name) =>
        name.EndsWith(VaultName.NoteExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^VaultName.NoteExtension.Length]
            : name;

    /// <summary>Returns the short form of a target: the basename alone, used when the name is unique.</summary>
    /// <param name="path">The note being linked to.</param>
    /// <returns>The note's basename without the note extension.</returns>
    public static string Bare(VaultPath path) => StemOf(path.Segments[^1]);

    /// <summary>
    /// Returns the qualified form of a target: the whole vault path. Used when the basename is
    /// duplicated, and always used for a pin, which has to name one file unambiguously.
    /// </summary>
    /// <param name="path">The note being linked to.</param>
    /// <returns>The note's vault-relative path without the note extension.</returns>
    public static string Qualified(VaultPath path) => StemOf(path.Value);
}
