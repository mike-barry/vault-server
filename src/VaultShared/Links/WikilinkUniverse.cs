using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// Indexes a set of files by the names that can name them, and resolves bare wikilinks against it.
/// Answers which file a target opens and how many files share a name.
/// </summary>
/// <remarks>
/// A relocation or a creation builds two of these, one for the vault before the change and one for
/// after it, and decides each link by comparing the two answers. A file is identified by its
/// position in the list rather than by its path, so it keeps the same identity across the pair even
/// though its path may differ between them. Both universes must therefore be built from lists in
/// the same order.
///
/// A caller whose question is about notes alone gives it notes alone. A relocation also gives it the
/// non-note files a folder move carries, because an embed naming one of them has to be re-emitted at
/// its destination like any other link.
/// </remarks>
internal sealed class WikilinkUniverse
{
    private readonly IReadOnlyList<VaultPath> _paths;

    /// <summary>
    /// Every name that names a file, mapped to the positions bearing it. A file is registered under
    /// its final segment and, for a note, under that segment's stem as well, because a link target
    /// may omit the note extension and both spellings name the same note.
    /// </summary>
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every whole vault path that names a file, mapped to the position bearing it - for a note, in
    /// both the spelled-out form and the extensionless form the editor writes. A target spelling a
    /// file's whole path takes that file outright, ahead of every same-named candidate.
    /// </summary>
    /// <remarks>
    /// A file at the vault root is indexed here under its basename, because there its basename and
    /// its whole path are the same string. That is what makes a root note unreachable by ranking:
    /// it holds every bare link to its name however deeply a same-named note is created, and
    /// creating one at the root takes every such link across the vault at once.
    /// </remarks>
    private readonly Dictionary<string, int> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a universe over the given files, indexed by position.</summary>
    /// <param name="paths">The files to resolve against. Positions index into this list.</param>
    public WikilinkUniverse(IReadOnlyList<VaultPath> paths)
    {
        _paths = paths;
        for (var i = 0; i < paths.Count; i++)
        {
            if (!Nameable(paths[i]))
                continue;

            var name = paths[i].Segments[^1];
            Register(name, i);

            var stem = WikilinkName.StemOf(name);
            if (!string.Equals(stem, name, StringComparison.Ordinal))
                Register(stem, i);

            _byPath[paths[i].Value] = i;
        }

        // A second pass, so that a note's whole path always beats another note's extensionless one.
        // The two forms collide whenever a name ends in the note extension twice: `A/Report.md.md`
        // is written `A/Report.md`, which is `A/Report.md`'s own whole path, and the editor gives
        // that target to the note whose whole path it is. Registering both forms together would
        // hand it to whichever note enumerated later instead.
        for (var i = 0; i < paths.Count; i++)
            if (Nameable(paths[i]))
                _byPath.TryAdd(WikilinkName.StemOf(paths[i].Value), i);
    }

    /// <summary>Gets the file at the given position.</summary>
    /// <param name="position">A position produced by <see cref="Resolve"/> or by construction order.</param>
    public VaultPath this[int position] => _paths[position];

    /// <summary>
    /// Returns how many files a bare target of this text names.
    /// </summary>
    /// <remarks>
    /// A count of one is what lets a link be emitted in its short form.
    /// </remarks>
    /// <param name="name">The bare name to count: a note's name with or without the note extension,
    /// and any other file's name with its own extension.</param>
    /// <returns>The number of files bearing the name, or zero when none do.</returns>
    public int Bearers(string name) => _byName.TryGetValue(name, out var found) ? found.Count : 0;

    /// <summary>
    /// Returns the position of the file a bare <paramref name="target"/> opens when written in
    /// <paramref name="linkingNote"/>, or null when nothing carries the name. The answer is always
    /// deterministic, so a pass can decide a bare link without treating ambiguity as a failure.
    /// </summary>
    /// <remarks>
    /// The linking note is a parameter rather than a position in this universe because a relocation
    /// moves the linking note as well, and the answer must be measured from where that note will be
    /// rather than from where it is. It is also what an anchored target is measured from, which is
    /// why the target is normalized here rather than once by the caller. A link written
    /// <c>./Delta</c> in a note that is moving names one path in the universe before the move and
    /// another in the universe after it, so normalizing once and passing the result to both would
    /// decide the second answer from where the linking note used to be.
    ///
    /// Resolution runs in two tiers, which is Obsidian's observed behaviour, over the address
    /// <see cref="WikilinkTarget.Address"/> produces. A target naming a whole vault path takes the
    /// file at that path outright; only a target naming no path is ranked against the files sharing
    /// its name. Collapsing the two would let a nested note take a link belonging to a root note,
    /// and would miss the pins a root-level creation owes. <c>Ref-Obsidian-Link-Behavior.md</c>
    /// carries both tiers in full.
    ///
    /// A whole path is read from the vault root, never from the linking note's folder, so the first
    /// tier is never satisfied by a note sitting beside the linking note. An anchored target reaches
    /// the vault root by being normalized before the tiers run, not by either tier reading it
    /// relatively. A target that carries ancestor text but names no whole path falls to the second
    /// tier, where every note sharing the basename is re-tested with
    /// <see cref="WikilinkTarget.Matches"/> - an anchored target included, which is how
    /// <c>../Delta</c> written in <c>S/T/</c> opens <c>Zed/S/Delta.md</c> when nothing sits at
    /// <c>S/Delta.md</c>.
    /// </remarks>
    /// <param name="linkingNote">The note the link is written in. Ranking distance and an anchored
    /// target are both measured from it.</param>
    /// <param name="target">The target text as the note carries it.</param>
    /// <returns>The position of the file the target opens, or null when nothing bears the name.</returns>
    public int? Resolve(VaultPath linkingNote, string target)
    {
        if (WikilinkTarget.Address(target, linkingNote) is not { } address)
            return null;

        if (_byPath.TryGetValue(address, out var named))
            return named;

        var last = WikilinkTarget.LastComponent(address);
        if (!_byName.TryGetValue(last, out var bearers))
            return null;

        // Indexing is by basename, so a target carrying ancestor text has to be re-tested against
        // each bearer. A bare target needs no filtering, and skipping it keeps the common case free.
        IReadOnlyList<int> candidates = last.Length == address.Length
            ? bearers
            : [.. bearers.Where(position => WikilinkTarget.Matches(address, _paths[position]))];

        if (candidates.Count == 0)
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

    /// <summary>
    /// Returns whether any target spelling can name the file, and so whether it belongs in the
    /// index at all.
    /// </summary>
    /// <remarks>
    /// Measured by following links in Obsidian: a target carrying no extension opens a markdown note
    /// and never a file of any other type, and a file with another extension is opened only by a
    /// target carrying that extension. A file whose name carries no extension is therefore named by
    /// nothing. Indexing one would let a bare target reach a file the editor never opens with it, and
    /// let that file displace the note of the same name.
    /// <c>Ref-Obsidian-Link-Behavior.md</c> carries the measurement and the conditions it holds under.
    /// </remarks>
    /// <param name="path">The file to test.</param>
    /// <returns>True when the file's name carries an extension.</returns>
    private static bool Nameable(VaultPath path) => path.Segments[^1].LastIndexOf('.') > 0;
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
    /// <param name="path">The file being linked to.</param>
    /// <returns>The file's basename, without the note extension where it has one.</returns>
    public static string Bare(VaultPath path) => StemOf(path.Segments[^1]);

    /// <summary>
    /// Returns the qualified form of a target: the whole vault path. Used when the basename is
    /// duplicated, and always used for a pin, which has to name one file unambiguously.
    /// </summary>
    /// <param name="path">The file being linked to.</param>
    /// <returns>The file's vault-relative path, without the note extension where it has one.</returns>
    public static string Qualified(VaultPath path) => StemOf(path.Value);
}
