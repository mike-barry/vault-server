namespace VaultShared.Scope;

/// <summary>
/// The grammar shared by every vault-relative path the scope model handles - a scope root as
/// configured, and a candidate path being tested against one. Pure string work: nothing here
/// opens a file, which is what makes it applicable to a path that does not exist yet.
/// </summary>
public static class ScopePath
{
    /// <summary>
    /// Splits a folder-prefix scope root or candidate vault-relative path into normalized,
    /// case-preserved segments - resolving "." and ".." and rejecting rooted paths, root
    /// escapes, and empty/whitespace input. Used when a grant's configured <c>scopeRoots</c> are
    /// validated and canonicalized at load time, and again when a path is tested against them at
    /// request time, so both sides read a root the same way. Throws
    /// <see cref="ArgumentException"/> for anything that cannot be normalized to a well-formed
    /// relative path.
    /// </summary>
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
