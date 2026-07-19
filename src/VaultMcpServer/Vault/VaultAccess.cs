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
    /// Grant-check hook: checks a resolved vault-relative path against the grant's
    /// folder-prefix <paramref name="scopeRoots"/> before a read, write, or enumeration
    /// proceeds. Op-class enforcement happens separately in the bearer middleware /
    /// <c>ScriptToolDispatcher</c> (grants.json); this hook is path scope only, distinct from
    /// which vault a grant may touch at all (<see cref="VaultConfig.Resolve"/>).
    ///
    /// <paramref name="scopeRoots"/> null or empty means the caller's grant is unscoped: full
    /// access within its granted vaults, so this is a no-op. The literal path <c>"."</c> - used
    /// by <see cref="EnumerateNotes"/> to gate the listing operation itself, not any one note -
    /// is always allowed here even under a scoped grant; per-note filtering happens inside
    /// <see cref="EnumerateNotes"/> after listing, via <see cref="IsPathInScope"/>.
    /// </summary>
    private static void EnsureAllowed(string op, string vault, string relativePath, IReadOnlyCollection<string>? scopeRoots)
    {
        if (scopeRoots is not { Count: > 0 })
            return;
        if (relativePath == ".")
            return;
        if (!IsPathInScope(relativePath, scopeRoots))
            throw new UnauthorizedAccessException($"Path is outside the grant's scope for vault \"{vault}\": {relativePath}");
    }

    /// <summary>
    /// Resolves a grant's per-vault <see cref="Auth.Grant.ScopeRoots"/> map down to the flat,
    /// already-vault-scoped root list for one specific <paramref name="vault"/> - the shape
    /// every other chokepoint function in this class (and <see cref="EnsureAllowed"/>,
    /// <see cref="IsPathInScope"/>) expects. This is where "a scope root configured for one
    /// vault must not leak into another vault the same grant can access" is enforced: only the
    /// entry keyed to <paramref name="vault"/> is ever consulted.
    ///
    /// <paramref name="scopeRootsByVault"/> being <c>null</c> means no scope map was supplied to
    /// this call at all (e.g. a caller with no grant context, such as a test calling this class
    /// directly) - returns an empty set, same meaning as an explicit empty list. This is
    /// distinct from a <em>non-null</em> map having no entry for <paramref name="vault"/>, which
    /// is now a defensive-throw case: every real <see cref="Auth.Grant"/> (whether file-loaded
    /// via <see cref="Auth.GrantStore.Load"/> or minted via <see cref="Auth.GrantStore.Mint"/>)
    /// is guaranteed to have a <see cref="Auth.Grant.ScopeRoots"/> entry for every vault it can
    /// access, so a non-null map missing the queried vault's key means that invariant was
    /// violated somewhere upstream - it must fail loudly rather than silently falling back to
    /// full access.
    /// </summary>
    public static IReadOnlySet<string> ResolveVaultScopeRoots(string vault, IReadOnlyDictionary<string, IReadOnlySet<string>>? scopeRootsByVault)
    {
        if (scopeRootsByVault is null)
            return EmptyScopeRoots;

        foreach (var (key, roots) in scopeRootsByVault)
        {
            if (string.Equals(key, vault, StringComparison.OrdinalIgnoreCase))
                return roots;
        }

        throw new InvalidOperationException(
            $"Grant configuration invariant violated: vault \"{vault}\" has no scope-roots entry in a non-null scope-roots map. " +
            "Every vault a grant can access must have an explicit entry (an empty set for full access) - this should be impossible for any grant built through GrantStore.Load or GrantStore.Mint.");
    }

    private static readonly IReadOnlySet<string> EmptyScopeRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a folder-prefix scope root or candidate vault-relative path into normalized,
    /// case-preserved segments - resolving "." and ".." and rejecting rooted paths, root
    /// escapes, and empty/whitespace input. Shared by <see cref="Auth.GrantStore.Load"/> (to
    /// validate/canonicalize a grant's configured <c>scopeRoots</c> at load time) and
    /// <see cref="IsPathInScope"/> (to test a path against them at request time). Throws
    /// <see cref="ArgumentException"/> for anything that cannot be normalized to a well-formed
    /// relative path.
    /// </summary>
    public static string[] NormalizeScopeSegments(string path)
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

    /// <summary>
    /// True if <paramref name="relativePath"/> falls under any of <paramref name="scopeRoots"/> -
    /// folder-prefix containment matched by path segment, not raw string prefix, so a root of
    /// "Projects/Foo" does not match "Projects/Foo-Other/...". An empty <paramref name="scopeRoots"/>
    /// means unscoped (always in scope).
    ///
    /// Every root (and the path itself) is normalized via <see cref="NormalizeScopeSegments"/>
    /// before any match is attempted - eagerly, so one malformed root makes the whole check
    /// throw regardless of whether a different root would have matched. This is the
    /// "an unscopeable query errors; it does not silently degrade" rule applied at the
    /// containment-check level: a grant whose configured scope can't be fully resolved is
    /// treated as broken, not partially trusted.
    /// </summary>
    public static bool IsPathInScope(string relativePath, IReadOnlyCollection<string> scopeRoots)
    {
        if (scopeRoots.Count == 0)
            return true;

        var pathSegments = NormalizeScopeSegments(relativePath);
        var rootSegmentsList = scopeRoots.Select(NormalizeScopeSegments).ToList();
        return rootSegmentsList.Any(rootSegments =>
            rootSegments.Length <= pathSegments.Length &&
            !rootSegments.Where((s, i) => !string.Equals(s, pathSegments[i], StringComparison.OrdinalIgnoreCase)).Any());
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

    /// <summary>
    /// Enumerates all notes in a vault as vault-relative, forward-slash paths, skipping
    /// dot-directories (.git, .obsidian, ...) and - when <paramref name="scopeRoots"/> is a
    /// non-empty scoped grant - any note outside those folder-prefix roots (see
    /// <see cref="IsPathInScope"/>). Out-of-scope notes are silently dropped from the result,
    /// not surfaced as an error - a search or wikilink resolution built on this enumeration
    /// (see <see cref="ResolveWikilink"/>) sees a naturally scope-filtered result set.
    /// </summary>
    public IReadOnlyList<string> EnumerateNotes(string? vault, IReadOnlyDictionary<string, IReadOnlySet<string>>? scopeRoots = null)
    {
        var (name, root) = config.Resolve(vault);
        var vaultScopeRoots = ResolveVaultScopeRoots(name, scopeRoots);
        EnsureAllowed(OpClass.Read, name, ".", vaultScopeRoots);

        var notes = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/')[..^1].Any(IsHiddenSegment))
                continue;
            if (vaultScopeRoots.Count > 0 && !IsPathInScope(relative, vaultScopeRoots))
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
    public string? ResolveWikilink(string? vault, string link, IReadOnlyDictionary<string, IReadOnlySet<string>>? scopeRoots = null)
    {
        var target = ParseWikilinkTarget(link);
        if (target.Length == 0)
            throw new ArgumentException($"Empty wikilink: {link}", nameof(link));

        var qualified = target.Contains('/');
        var matches = new List<string>();
        foreach (var note in EnumerateNotes(vault, scopeRoots))
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

    public string ReadNote(string? vault, string relativePath, IReadOnlyDictionary<string, IReadOnlySet<string>>? scopeRoots = null)
    {
        var (name, _) = config.Resolve(vault);
        EnsureAllowed(OpClass.Read, name, relativePath, ResolveVaultScopeRoots(name, scopeRoots));
        return File.ReadAllText(ResolvePath(vault, relativePath));
    }

    /// <summary>Writes a note as UTF-8 without BOM, creating parent directories as needed.</summary>
    public void WriteNote(string? vault, string relativePath, string content, IReadOnlyDictionary<string, IReadOnlySet<string>>? scopeRoots = null)
    {
        var (name, _) = config.Resolve(vault);
        EnsureAllowed(OpClass.Write, name, relativePath, ResolveVaultScopeRoots(name, scopeRoots));
        var full = ResolvePath(vault, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Moves/renames a note from one vault-relative path to another, creating parent
    /// directories as needed. Throws if the source note does not exist or the
    /// destination already exists.
    /// </summary>
    public void MoveNote(string? vault, string relativePath, string newRelativePath, IReadOnlyDictionary<string, IReadOnlySet<string>>? scopeRoots = null)
    {
        var (name, _) = config.Resolve(vault);
        var vaultScopeRoots = ResolveVaultScopeRoots(name, scopeRoots);
        EnsureAllowed(OpClass.Write, name, relativePath, vaultScopeRoots);
        EnsureAllowed(OpClass.Write, name, newRelativePath, vaultScopeRoots);

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
