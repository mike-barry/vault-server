using VaultShared.Access;

namespace VaultShared.Links;

/// <summary>
/// The set of notes a bare wikilink is resolved against, indexed by the names that can name them.
/// Every question the three passes ask about a bare link is asked here: which note a target opens,
/// and whether a basename has one bearer or several.
///
/// It exists in pairs. A relocation and a creation each have a universe before the change and a
/// universe after it, and the whole of "never re-aim" is comparing one against the other for the
/// same link. Positions are the currency rather than paths, so a note keeps its identity across the
/// pair even though its path may differ between them.
/// </summary>
internal sealed class WikilinkUniverse
{
    private readonly IReadOnlyList<VaultPath> _paths;

    /// <summary>
    /// Every name that names a note, mapped to the positions bearing it. A note is registered under
    /// its final segment and under that segment's stem, because the target's extension is the
    /// target's to omit and both spellings name the same file.
    /// </summary>
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);

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

    public VaultPath this[int position] => _paths[position];

    /// <summary>
    /// How many notes a bare target of this text names. One is what makes an emitted link short:
    /// emission is canonical rather than minimal, so a unique basename is written bare and a
    /// duplicated one is written as the full vault path.
    /// </summary>
    public int Bearers(string name) => _byName.TryGetValue(name, out var found) ? found.Count : 0;

    /// <summary>
    /// Which note a bare <paramref name="target"/> written in <paramref name="linkingNote"/> opens,
    /// as a position, or null where nothing carries the name. Resolution is deterministic rather
    /// than ambiguous, which is what lets a pass decide a bare link's fate without a gate.
    ///
    /// The linking note is passed rather than taken from a position, because a relocation moves it
    /// too and the answer is measured from where it will be.
    /// </summary>
    public int? Resolve(VaultPath linkingNote, string target)
    {
        if (!_byName.TryGetValue(target, out var candidates))
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        var winner = WikilinkRanking.Resolve(linkingNote, candidates.Select(i => _paths[i]));

        // Carried back by reference: ranking hands back the instances it was given, and positions
        // are the currency because two universes are compared on them.
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
/// How a path is written inside a wikilink. The editor writes a note's target without its extension
/// and every other file's with it, so a note's two forms - the bare basename and the full vault
/// path - are both the path with <c>.md</c> taken off.
/// </summary>
internal static class WikilinkName
{
    /// <summary>The name without a note's extension, or unchanged for anything else.</summary>
    public static string StemOf(string name) =>
        name.EndsWith(VaultName.NoteExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^VaultName.NoteExtension.Length]
            : name;

    /// <summary>The short form: the basename alone. What a unique name is emitted as.</summary>
    public static string Bare(VaultPath path) => StemOf(path.Segments[^1]);

    /// <summary>
    /// The qualified form: the whole vault path. What a duplicated name is emitted as, and what a
    /// pin is always written as - a pin's job is to name one file unambiguously.
    /// </summary>
    public static string Qualified(VaultPath path) => StemOf(path.Value);
}
