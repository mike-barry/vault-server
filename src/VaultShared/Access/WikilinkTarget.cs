namespace VaultShared.Access;

/// <summary>
/// Whether a link's target names a given path. Repair and the census ask exactly this question, so
/// one piece answers it for both: both ways of getting it wrong are silent, a link repair fails to
/// match being left pointing at nothing, and a link the census fails to match being an orphan
/// nobody reports.
///
/// A target comes in two forms and each matches its own way. Path-qualified - it holds a separator -
/// matches on a suffix of whole path components, because the editor writes partial paths:
/// <c>Redesign/Spec-Tool-Capabilities</c> names
/// <c>Projects/Vault-MCP-Server/Inbox/Redesign/Spec-Tool-Capabilities</c> and
/// <c>sign/Spec-Tool-Capabilities</c> names nothing. Bare matches on basename alone, and
/// <see cref="WikilinkRanking"/> decides which of several same-named candidates it resolves to.
/// </summary>
public static class WikilinkTarget
{
    /// <summary>
    /// The separator the editor writes, and the only one read here. A target holding anything else
    /// is a bare name that happens to contain a backslash, and names nothing.
    /// </summary>
    private const char Separator = '/';

    /// <summary>Whether <paramref name="target"/> holds a separator, and so matches as a path.</summary>
    public static bool IsPathQualified(string target) => target.Contains(Separator);

    /// <summary>
    /// Whether <paramref name="target"/> names <paramref name="candidate"/>.
    ///
    /// Comparison is case-insensitive throughout, matching the layer's own path equality and the
    /// scope semantics behind it: two spellings name one file, and a link whose case drifted from
    /// the file's still opens it in the editor.
    ///
    /// The extension is the target's to omit. The editor writes a note's target without
    /// <c>.md</c> and writes every other file's with its own extension, so a final component
    /// matches a segment either exactly or as that segment's stem when the segment is a note.
    /// </summary>
    public static bool Matches(string target, VaultPath candidate)
    {
        var parts = target.Split(Separator);
        var segments = candidate.Segments;

        // A bare target falls out of the same walk: one component compared against the basename,
        // with no ancestor to agree with.
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
