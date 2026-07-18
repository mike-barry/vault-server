namespace VaultMcpServer.Vault;

/// <summary>
/// The vault-access chokepoint: ALL vault I/O in the C# host flows through this class -
/// path resolution, wikilink resolution, reads, writes, and the grant check. No other
/// code touches vault paths directly. The PowerShell counterpart for script tools is
/// powershell/VaultAccess/VaultAccess.psm1, which mirrors these operations and semantics.
///
/// Vault conventions (wikilink forms, filename rules, layout) are encoded only here and
/// in the PowerShell module; they implement the vault's Document-Conventions.md and
/// Vault-Guide.md (Resources/Vault/Guides/ in the vault repo).
/// </summary>
public sealed class VaultAccess(VaultConfig config)
{
    /// <summary>Directory segments never treated as vault content.</summary>
    private static bool IsHiddenSegment(string segment) => segment.StartsWith('.');

    /// <summary>
    /// Grant-check hook. Phase 1 stub: always allows. Op-class enforcement lands with the
    /// bearer middleware (grants.json); per-path scoping is the phase 2 hook this signature
    /// reserves. Every operation below calls this before touching the file system.
    /// </summary>
    private static void EnsureAllowed(string op, string vault, string relativePath)
    {
    }

    public (string Name, string Root) ResolveVault(string? vault) => config.Resolve(vault);

    /// <summary>
    /// Resolves a vault-relative path to an absolute path, rejecting rooted paths and
    /// any path that escapes the vault root.
    /// </summary>
    public string ResolvePath(string? vault, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Path is empty.", nameof(relativePath));
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException($"Path must be vault-relative, not rooted: {relativePath}", nameof(relativePath));

        var (name, root) = config.Resolve(vault);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path escapes vault \"{name}\": {relativePath}", nameof(relativePath));
        return full;
    }

    /// <summary>Enumerates all notes in a vault as vault-relative, forward-slash paths, skipping dot-directories (.git, .obsidian, ...).</summary>
    public IReadOnlyList<string> EnumerateNotes(string? vault)
    {
        var (name, root) = config.Resolve(vault);
        EnsureAllowed(OpClass.Read, name, ".");

        var notes = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/')[..^1].Any(IsHiddenSegment))
                continue;
            notes.Add(relative);
        }
        return notes;
    }

    /// <summary>
    /// Resolves a wikilink to a vault-relative note path. Accepts every convention form:
    /// [[Name]], bare Name, [[Folder/Name]] (path-qualified, matched on trailing path
    /// segments), and either with #heading and/or |display suffixes, which are ignored.
    /// Returns null when nothing matches; throws when the link is ambiguous.
    /// </summary>
    public string? ResolveWikilink(string? vault, string link)
    {
        var target = ParseWikilinkTarget(link);
        if (target.Length == 0)
            throw new ArgumentException($"Empty wikilink: {link}", nameof(link));

        var qualified = target.Contains('/');
        var matches = new List<string>();
        foreach (var note in EnumerateNotes(vault))
        {
            var noteNoExt = note[..^".md".Length];
            var hit = qualified
                ? noteNoExt.Equals(target, StringComparison.OrdinalIgnoreCase) || noteNoExt.EndsWith("/" + target, StringComparison.OrdinalIgnoreCase)
                : GetLastSegment(noteNoExt).Equals(target, StringComparison.OrdinalIgnoreCase);
            if (hit)
                matches.Add(note);
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException($"Ambiguous wikilink [[{target}]]: {string.Join(", ", matches)}"),
        };
    }

    public string ReadNote(string? vault, string relativePath)
    {
        var (name, _) = config.Resolve(vault);
        EnsureAllowed(OpClass.Read, name, relativePath);
        return File.ReadAllText(ResolvePath(vault, relativePath));
    }

    /// <summary>Writes a note as UTF-8 without BOM, creating parent directories as needed.</summary>
    public void WriteNote(string? vault, string relativePath, string content)
    {
        var (name, _) = config.Resolve(vault);
        EnsureAllowed(OpClass.Write, name, relativePath);
        var full = ResolvePath(vault, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Moves/renames a note from one vault-relative path to another, creating parent
    /// directories as needed. Throws if the source note does not exist or the
    /// destination already exists.
    /// </summary>
    public void MoveNote(string? vault, string relativePath, string newRelativePath)
    {
        var (name, _) = config.Resolve(vault);
        EnsureAllowed(OpClass.Write, name, relativePath);
        EnsureAllowed(OpClass.Write, name, newRelativePath);

        var fullOld = ResolvePath(vault, relativePath);
        var fullNew = ResolvePath(vault, newRelativePath);

        if (!File.Exists(fullOld))
            throw new FileNotFoundException($"Source note not found: {relativePath}", fullOld);
        if (File.Exists(fullNew))
            throw new IOException($"Destination already exists: {newRelativePath}");

        Directory.CreateDirectory(Path.GetDirectoryName(fullNew)!);
        File.Move(fullOld, fullNew);
    }

    /// <summary>Strips [[ ]] delimiters, |display text, and #heading fragments, normalizing separators to forward slashes.</summary>
    private static string ParseWikilinkTarget(string link)
    {
        var target = link.Trim();
        if (target.StartsWith("[[") && target.EndsWith("]]"))
            target = target[2..^2];
        target = target.Split('|')[0].Split('#')[0].Trim();
        return target.Replace('\\', '/').Trim('/');
    }

    private static string GetLastSegment(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }
}
