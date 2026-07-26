namespace VaultMcpServer.Vault;

/// <summary>
/// The vault-access chokepoint: path resolution and the grant-scope check primitives
/// (<see cref="IsPathInScope"/>, <see cref="ResolveVaultScopeRoots"/>,
/// <see cref="NormalizeScopeSegments"/>) live here - the scope primitives real callers
/// (mint, grant load, the elevation tool) use. The PowerShell counterpart for script tools,
/// powershell/VaultAccess/VaultAccess.psm1, mirrors these operations and semantics and also
/// carries the full read/write/enumerate/search/move instance-op surface for tool scripts -
/// the C# side dropped its caller-less mirror of that half (2026-07-19, see
/// Plan-Grant-Tool-Trust-Model's "Mirrored chokepoint drift surface"; the last of the mirrored
/// resolution surface, wikilink resolution, retired 2026-07-23 when read/move superseded it);
/// re-mirror by porting from the then-current PowerShell implementation when a native vault tool
/// needs it.
///
/// Vault conventions (filename rules, layout) are encoded only here and in the PowerShell
/// module; they implement the vault's Document-Conventions.md and Vault-Guide.md
/// (Resources/Vault/Guides/ in the vault repo).
/// </summary>
public sealed class VaultAccess(VaultConfig config)
{
    /// <summary>Directory segments never treated as vault content.</summary>
    private static bool IsHiddenSegment(string segment) => segment.StartsWith('.');

    /// <summary>
    /// Resolves a grant's per-vault <see cref="Auth.Grant.ScopeRoots"/> map down to the flat,
    /// already-vault-scoped root list for one specific <paramref name="vault"/> - the shape
    /// <see cref="IsPathInScope"/>, and every other scope-checking caller in this class,
    /// expects. This is where "a scope root configured for one
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
}
