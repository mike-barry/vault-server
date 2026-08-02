
namespace VaultShared.Scope;

/// <summary>
/// The grant-scope primitives. Pure string containment - nothing here opens a file.
/// <see cref="NormalizeScopeSegments"/> canonicalizes a scope root or a candidate path, and
/// <see cref="IsPathInScope"/> turns a scope and a path into a containment answer.
/// </summary>
public sealed class VaultAccess
{
    /// <summary>
    /// Splits a folder-prefix scope root or candidate vault-relative path into normalized,
    /// case-preserved segments - resolving "." and ".." and rejecting rooted paths, root
    /// escapes, and empty/whitespace input. Shared by <see cref="VaultShared.Auth.GrantStore.Load"/> (to
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
    /// True if <paramref name="relativePath"/> falls under <paramref name="scope"/> - folder-prefix
    /// containment matched by path segment, not raw string prefix, so a root of "Projects/Foo"
    /// does not match "Projects/Foo-Other/...". An unscoped <paramref name="scope"/> admits every
    /// path, which is a case of the type rather than a property of the root count.
    ///
    /// Every root (and the path itself) is normalized via <see cref="NormalizeScopeSegments"/>
    /// before any match is attempted - eagerly, so one malformed root makes the whole check
    /// throw regardless of whether a different root would have matched. This is the
    /// "an unscopeable query errors; it does not silently degrade" rule applied at the
    /// containment-check level: a grant whose configured scope can't be fully resolved is
    /// treated as broken, not partially trusted.
    /// </summary>
    public static bool IsPathInScope(string relativePath, VaultScope scope)
    {
        if (scope is not VaultScope.RootedScope rooted)
            return true;

        var pathSegments = NormalizeScopeSegments(relativePath);
        var rootSegmentsList = rooted.Roots.Select(NormalizeScopeSegments).ToList();
        return rootSegmentsList.Any(rootSegments =>
            rootSegments.Length <= pathSegments.Length &&
            !rootSegments.Where((s, i) => !string.Equals(s, pathSegments[i], StringComparison.OrdinalIgnoreCase)).Any());
    }

    /// <summary>
    /// The read-class counterpart of <see cref="IsPathInScope"/>: true if
    /// <paramref name="relativePath"/> falls under <paramref name="scope"/> <em>or</em> under the
    /// vault's always-readable roots in <paramref name="pathPolicy"/>.
    ///
    /// The floor is unioned into the scope rather than checked beside it, so this stays one
    /// containment test against one <see cref="VaultScope"/> - there is no second admitting rule
    /// for a caller to reason about, and enumeration over the same effective scope needs no
    /// special case. Pass <see cref="PathPolicy.None"/> where no floor applies; the union is
    /// then the identity and this is exactly <see cref="IsPathInScope"/>.
    ///
    /// <b>Read-class callers only.</b> Every write-class path - and every authority decision,
    /// including mint's attenuation check - stays on <see cref="IsPathInScope"/> against the
    /// grant's own scope. A grant that can read the always-readable roots must not thereby be
    /// able to write them, nor to mint a token rooted at them.
    ///
    /// The policy is the vault's own (<see cref="VaultShared.Configuration.VaultConfig.PathPolicyFor"/>),
    /// so a caller must pass the one belonging to the vault the path is in - the same discipline
    /// <see cref="VaultShared.Auth.Grant.ScopeIn"/> enforces for scope roots.
    /// </summary>
    public static bool IsPathInReadScope(string relativePath, VaultScope scope, PathPolicy pathPolicy) =>
        IsPathInScope(relativePath, pathPolicy.EffectiveReadScope(scope));

}
