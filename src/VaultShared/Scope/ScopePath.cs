namespace VaultShared.Scope;

/// <summary>
/// Normalizes the vault-relative paths the scope model handles: a scope root as configured, and a
/// candidate path being tested against one. This is pure string work and never touches the
/// filesystem, so it applies equally to a path that does not exist yet.
/// </summary>
public static class ScopePath
{
    /// <summary>
    /// Splits a scope root or candidate vault-relative path into normalized segments, resolving
    /// "." and ".." and preserving case.
    /// </summary>
    /// <param name="path">The vault-relative path.</param>
    /// <returns>The path's segments.</returns>
    /// <exception cref="ArgumentException">The path is empty or whitespace, rooted, or escapes the
    /// vault, or it resolves to the vault root.</exception>
    /// <remarks>
    /// Configured scope roots are validated and canonicalized through this at load time, and a
    /// candidate path is normalized through it again at request time, so both sides read a root the
    /// same way.
    ///
    /// One leading separator is dropped, so <c>/Projects/Widget.md</c> names the same note as
    /// <c>Projects/Widget.md</c>. Every path here is measured from the vault root already, so a
    /// leading separator has the one meaning and reinterpreting it silently costs nothing. Two
    /// leading separators are refused instead: <c>//server/share</c> is a UNC path, and admitting it
    /// would turn the server's name into an ordinary folder segment.
    /// </remarks>
    public static string[] Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Scope path is empty.", nameof(path));

        if (IsSeparator(path[0]))
        {
            if (path.Length > 1 && IsSeparator(path[1]))
                throw new ArgumentException($"Scope path must be vault-relative, not rooted: {path}", nameof(path));
            path = path[1..];
        }

        // Both separators are tested by hand rather than through the platform's rooted check, so a
        // path spelled with backslashes and one spelled with forward slashes are answered the same
        // way. The check below still runs, and is what refuses a drive-qualified path, which carries
        // no leading separator to drop.
        if (Path.IsPathRooted(path))
            throw new ArgumentException($"Scope path must be vault-relative, not rooted: {path}", nameof(path));

        var segments = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/'))
        {
            if (raw.Length == 0 || raw == ".")
                continue;
            if (raw == "..")
            {
                if (segments.Count == 0)
                    throw new ArgumentException($"Scope path escapes the vault: {path}", nameof(path));
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(raw);
        }
        if (segments.Count == 0)
            throw new ArgumentException($"Scope path resolves to the vault root: {path}", nameof(path));
        return [.. segments];
    }

    /// <summary>Returns whether the character separates path segments in either spelling.</summary>
    /// <param name="character">The character to test.</param>
    /// <returns>True for a forward slash or a backslash.</returns>
    private static bool IsSeparator(char character) => character is '/' or '\\';
}
