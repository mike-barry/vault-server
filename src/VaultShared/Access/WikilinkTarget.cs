namespace VaultShared.Access;

/// <summary>
/// Decides whether a link's target names a given path. Repair on move and the census on delete both
/// ask this question, and one implementation answers it for both, because either of them getting it
/// wrong fails silently: a repair that fails to match leaves a link pointing at nothing, and a
/// census that fails to match leaves an orphan nobody reports.
/// </summary>
/// <remarks>
/// A target comes in two forms. A path-qualified target - one containing a separator - matches a
/// suffix of whole path components, because the editor writes partial paths. So
/// <c>Redesign/Spec-Tool-Capabilities</c> names
/// <c>Projects/Vault-MCP-Server/Inbox/Redesign/Spec-Tool-Capabilities</c>, while
/// <c>sign/Spec-Tool-Capabilities</c> names nothing. A bare target matches on basename alone, and
/// <see cref="WikilinkRanking"/> decides which of several same-named candidates it resolves to.
/// </remarks>
public static class WikilinkTarget
{
    /// <summary>
    /// The separator the editor writes, and the only one recognized here. A target containing a
    /// backslash is a bare name that happens to contain one, and names nothing.
    /// </summary>
    private const char Separator = '/';

    /// <summary>Returns whether a target contains a separator, and so matches as a path.</summary>
    /// <param name="target">The link's target text.</param>
    /// <returns>True when the target is path-qualified.</returns>
    public static bool IsPathQualified(string target) => target.Contains(Separator);

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
        var parts = target.Split(Separator);
        var segments = candidate.Segments;

        // A bare target is handled by the same walk: one component compared against the basename,
        // with no ancestor components to agree on.
        if (parts.Length > segments.Count)
            return false;

        var offset = segments.Count - parts.Length;
        for (var i = 0; i < parts.Length - 1; i++)
            if (!string.Equals(parts[i], segments[offset + i], StringComparison.OrdinalIgnoreCase))
                return false;

        return NameMatches(parts[^1], segments[^1]);
    }

    private static bool NameMatches(string component, string segment) =>
        component.Length > 0
        && (string.Equals(component, segment, StringComparison.OrdinalIgnoreCase)
            || (segment.EndsWith(VaultName.NoteExtension, StringComparison.OrdinalIgnoreCase)
                && string.Equals(component, segment[..^VaultName.NoteExtension.Length], StringComparison.OrdinalIgnoreCase)));
}
