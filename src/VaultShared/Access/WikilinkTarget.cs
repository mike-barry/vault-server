namespace VaultShared.Access;

/// <summary>
/// Decides whether a link's target names a given path. Repair on move and the census on delete both
/// ask this question, and one implementation answers it for both, because either of them getting it
/// wrong fails silently: a repair that fails to match leaves a link pointing at nothing, and a
/// census that fails to match leaves an orphan nobody reports.
/// </summary>
/// <remarks>
/// A target names a candidate when its final component matches the candidate's basename whole and
/// the text before it is a character suffix of the candidate's folder path. So
/// <c>Redesign/Spec-Tool-Capabilities</c> names
/// <c>Projects/Vault-MCP-Server/Inbox/Redesign/Spec-Tool-Capabilities</c>, and so does
/// <c>sign/Spec-Tool-Capabilities</c>: the suffix is measured in characters, so it may cut a folder
/// name in half. Only the leading component can be cut, because only it sits at the start of the
/// suffix. The final component is never partial, so <c>Capabilities</c> alone names nothing. This
/// was measured against Obsidian on 2026-08-04.
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

    /// <summary>Returns whether a link's target names a candidate note.</summary>
    /// <remarks>
    /// The comparison is case-insensitive throughout, matching this layer's path equality and the
    /// scope semantics behind it: two spellings name one file, and a link whose case drifted from
    /// the file's still opens it in the editor.
    ///
    /// The target may omit the extension. The editor writes a note's target without <c>.md</c> and
    /// writes every other file's target with its own extension, so the final component matches a
    /// segment either exactly or as that segment's stem when the segment is a note.
    /// </remarks>
    /// <param name="target">The link's target text.</param>
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
