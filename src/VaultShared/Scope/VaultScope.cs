namespace VaultShared.Scope;

/// <summary>
/// Represents one vault's path scope under a grant: either unscoped, meaning the whole vault, or a
/// non-empty set of folder-prefix roots. The private constructor closes the hierarchy to those two,
/// and <see cref="Of"/> refuses an empty sequence, so an empty root set cannot be constructed.
/// </summary>
/// <remarks>
/// Refusing the empty set is deliberate. If the broadest authority were encoded as an empty
/// collection, the widest possible grant would be indistinguishable from what a dropped entry, a
/// filtered-to-nothing list, or a default-constructed field produces, and the only defence would be
/// that every producer remembers to check. A caller holding a <see cref="VaultScope"/> holds one of
/// two meanings, both of them chosen.
///
/// The configuration file works the same way: whole-vault access is spelled <c>["*"]</c>, and an
/// empty list is a load-time error rather than a second spelling of it. There is no layer at which
/// losing an entry widens a grant.
/// </remarks>
public abstract record VaultScope
{
    private VaultScope() { }

    /// <summary>The whole vault. Distinct from a root set that happens to match everything.</summary>
    public static VaultScope Unscoped { get; } = new UnscopedScope();

    /// <summary>Creates a scope confined to the given roots.</summary>
    /// <param name="roots">The folder-prefix roots, compared case-insensitively.</param>
    /// <returns>The confined scope.</returns>
    /// <exception cref="ArgumentException">The sequence is empty. Promoting that to
    /// <see cref="Unscoped"/> would restore the sentinel this type exists to remove; a caller that
    /// means the whole vault uses <see cref="Unscoped"/>.</exception>
    public static VaultScope Of(IEnumerable<string> roots)
    {
        var set = roots as IReadOnlySet<string> ?? new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
            throw new ArgumentException("A rooted scope needs at least one root; use VaultScope.Unscoped for whole-vault access.", nameof(roots));
        return new RootedScope(set);
    }

    /// <summary>
    /// Reports whether a path falls under this scope.
    /// </summary>
    /// <param name="relativePath">The vault-relative path to test.</param>
    /// <returns>True where the path lies under one of the roots, or always for an unscoped
    /// scope.</returns>
    /// <exception cref="ArgumentException">The path, or any configured root, is not a well-formed
    /// vault-relative path.</exception>
    /// <remarks>
    /// Containment is matched by whole path segment rather than by raw string prefix, so a root of
    /// "Projects/Foo" does not match "Projects/Foo-Other".
    ///
    /// Every root and the path itself are normalized before any match is attempted, and the roots are
    /// normalized eagerly, so one malformed root makes the whole check throw even where a different
    /// root would have matched. A grant whose configured scope cannot be fully resolved is treated as
    /// broken rather than partly trusted.
    /// </remarks>
    public bool Admits(string relativePath)
    {
        if (this is not RootedScope rooted)
            return true;

        var pathSegments = ScopePath.Normalize(relativePath);
        return rooted.NormalizedRoots.Any(rootSegments =>
            rootSegments.Length <= pathSegments.Length &&
            !rootSegments.Where((s, i) => !string.Equals(s, pathSegments[i], StringComparison.OrdinalIgnoreCase)).Any());
    }

    /// <summary>Represents the whole-vault case.</summary>
    public sealed record UnscopedScope : VaultScope;

    /// <summary>
    /// Represents the confined case, over a root set that is never empty.
    /// </summary>
    /// <param name="Roots">The folder-prefix roots.</param>
    /// <remarks>
    /// Equality is written by hand and compares the root sets by value. The compiler-generated
    /// version compares the set by reference, so two scopes over identical roots would test unequal.
    /// </remarks>
    public sealed record RootedScope(IReadOnlySet<string> Roots) : VaultScope
    {
        private string[][]? _normalizedRoots;

        /// <summary>
        /// Every root in normalized segment form, computed once per instance because the roots are
        /// immutable and a whole-vault enumeration filters every candidate against the same scope. A
        /// malformed root makes the computation throw, and the failure is not cached, so it repeats
        /// on every access and no check ever answers from a partly resolved scope.
        /// </summary>
        internal string[][] NormalizedRoots => _normalizedRoots ??= Roots.Select(ScopePath.Normalize).ToArray();

        /// <summary>Compares two rooted scopes by their root sets.</summary>
        /// <param name="other">The scope to compare with.</param>
        /// <returns>True where both hold the same roots.</returns>
        public bool Equals(RootedScope? other) => other is not null && Roots.SetEquals(other.Roots);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = 0;
            foreach (var root in Roots)
                hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(root);
            return hash;
        }
    }
}
