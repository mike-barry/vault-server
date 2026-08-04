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
    /// </remarks>
    public static string[] Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Scope path is empty.", nameof(path));
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
}
