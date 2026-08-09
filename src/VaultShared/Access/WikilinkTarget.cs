namespace VaultShared.Access;

/// <summary>
/// Reads a link's target: normalizes an anchored spelling against the folder it is anchored to, and
/// decides whether the resulting text names a given path. Either question answered wrong fails
/// silently - a repair that fails to match leaves a link pointing at nothing, and a census that
/// fails to match leaves an orphan nobody reports - so one implementation answers both for every
/// consumer.
/// </summary>
/// <remarks>
/// A target is read as an address first. One beginning with <c>/</c> is measured from the vault
/// root and one beginning with <c>./</c> or <c>../</c> from the linking note's own folder, while
/// anything else is already measured from the vault root and is read exactly as written. See
/// <see cref="Address"/>.
///
/// The address then names a candidate when its final component matches the candidate's basename
/// whole and the text before it is a character suffix of the candidate's folder path. So
/// <c>Redesign/Spec-Tool-Capabilities</c> names
/// <c>Projects/Vault-MCP-Server/Inbox/Redesign/Spec-Tool-Capabilities</c>, and so does
/// <c>sign/Spec-Tool-Capabilities</c>: the suffix is measured in characters, so it may cut a folder
/// name in half. Only the leading component can be cut, because only it sits at the start of the
/// suffix. The final component is never partial, so <c>Capabilities</c> alone names nothing. This is
/// Obsidian's observed behaviour rather than a rule of this build's own.
///
/// Matching is wider than resolution and does not decide anything on its own. Several notes can
/// match one target; <see cref="WikilinkRanking"/> orders them, and a note whose whole path the
/// target spells takes the link ahead of all of them.
/// </remarks>
public static class WikilinkTarget
{
    /// <summary>
    /// The separator the editor writes, and the only one recognized here. A target containing a
    /// backslash is a bare name that happens to contain one, and names nothing.
    /// </summary>
    private const char Separator = '/';

    /// <summary>Returns the target's final component, the part that must match a note's basename.</summary>
    /// <param name="target">The link's target text.</param>
    /// <returns>The text after the last separator, or the whole target when it has none.</returns>
    public static string LastComponent(string target) => target[(target.LastIndexOf(Separator) + 1)..];

    /// <summary>
    /// Returns a target measured from the vault root, or null when the target names no file.
    /// </summary>
    /// <remarks>
    /// A target whose first component is <c>/</c>'s empty leading text is anchored at the vault
    /// root, and one whose first component is <c>.</c> or <c>..</c> is anchored at the linking
    /// note's own folder, with each <c>..</c> climbing one folder. The anchored text is normalized
    /// against its anchor and the result is resolved like any other target, loose matching included:
    /// written in <c>S/T/Spellings.md</c>, <c>./Delta</c> becomes <c>S/T/Delta</c>, <c>../Delta</c>
    /// becomes <c>S/Delta</c>, and <c>/Delta</c> and <c>../../Delta</c> both become <c>Delta</c>.
    /// Obsidian's own <i>Relative path to file</i> link-format setting writes the <c>../</c> form,
    /// so these are ordinary targets rather than exotic ones.
    ///
    /// Every other target is already measured from the vault root and is returned exactly as
    /// written, with no normalization at all. That is what keeps <c>S/../Delta</c> and
    /// <c>..Delta</c> naming nothing: only a leading component decides an anchor, and interior
    /// dot-segments are ordinary characters that no file's path contains.
    ///
    /// Climbing past the vault root is ignored rather than refused, so <c>../../../../Delta</c>
    /// written two folders deep still names the root's <c>Delta</c>. An empty component anywhere but
    /// the leading anchor names nothing, which is what makes <c>//Delta</c>, <c>./Delta/</c> and
    /// <c>./</c> unresolved. A target that normalizes to a folder, such as <c>.</c> or <c>..</c>,
    /// needs no rule of its own: it comes back as that folder's path, which no note bears.
    ///
    /// Every case above is Obsidian's observed behaviour, measured in a note at <c>S/T/</c> with
    /// files at <c>Delta.md</c>, <c>S/T/Delta.md</c> and <c>Zed/S/Delta.md</c> and nothing at
    /// <c>S/Delta.md</c>. Changing any of it changes which note existing links open.
    /// </remarks>
    /// <param name="target">The link's target text, as the note carries it.</param>
    /// <param name="linkingNote">The note the link is written in. An anchored target is measured
    /// from its folder.</param>
    /// <returns>The target measured from the vault root, or null when it names no file.</returns>
    public static string? Address(string target, VaultPath linkingNote)
    {
        var components = target.Split(Separator);
        var anchor = components[0];

        // Not anchored, so the text is already vault-relative and is handed on untouched.
        if (anchor.Length > 0 && anchor is not ("." or ".."))
            return target;

        var segments = new List<string>();
        if (anchor.Length > 0)
            segments.AddRange(linkingNote.Segments.Take(linkingNote.Segments.Count - 1));

        // A root anchor is the empty text before the leading separator and is consumed by seeding
        // the segments; a folder anchor is a component the walk reads like any other.
        for (var i = anchor.Length > 0 ? 0 : 1; i < components.Length; i++)
        {
            var component = components[i];
            if (component.Length == 0)
                return null;

            if (component == ".")
                continue;

            if (component == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(component);
        }

        return segments.Count == 0 ? null : string.Join(Separator, segments);
    }

    /// <summary>Returns whether a link's target names a candidate note.</summary>
    /// <remarks>
    /// The target is the text <see cref="Address"/> produced, measured from the vault root. An
    /// anchored spelling is normalized before it arrives, so nothing here reads a leading <c>/</c>,
    /// <c>./</c> or <c>../</c> as anything but ordinary characters that no path contains.
    ///
    /// The comparison is case-insensitive throughout, matching this layer's path equality and the
    /// scope semantics behind it: two spellings name one file, and a link whose case drifted from
    /// the file's still opens it in the editor.
    ///
    /// The target may omit the extension. The editor writes a note's target without <c>.md</c> and
    /// writes every other file's target with its own extension, so the final component matches a
    /// segment either exactly or as that segment's stem when the segment is a note.
    /// </remarks>
    /// <param name="target">The link's target, measured from the vault root by <see cref="Address"/>.</param>
    /// <param name="candidate">The note to test the target against.</param>
    /// <returns>True when the target names the candidate.</returns>
    public static bool Matches(string target, VaultPath candidate)
    {
        var segments = candidate.Segments;
        var cut = target.LastIndexOf(Separator);

        if (!NameMatches(target[(cut + 1)..], segments[^1]))
            return false;

        // A bare target has no ancestor text, so the basename alone settles it.
        if (cut < 0)
            return true;

        var folders = segments.Count > 1
            ? string.Join(Separator, segments.Take(segments.Count - 1)) + Separator
            : string.Empty;

        return folders.EndsWith(target[..(cut + 1)], StringComparison.OrdinalIgnoreCase);
    }

    private static bool NameMatches(string component, string segment) =>
        component.Length > 0
        && (string.Equals(component, segment, StringComparison.OrdinalIgnoreCase)
            || (segment.EndsWith(VaultName.NoteExtension, StringComparison.OrdinalIgnoreCase)
                && string.Equals(component, segment[..^VaultName.NoteExtension.Length], StringComparison.OrdinalIgnoreCase)));
}
