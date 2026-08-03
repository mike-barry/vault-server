namespace VaultShared.Scope;

/// <summary>
/// One vault's path scope under a grant: either unscoped (the whole vault) or a non-empty set of
/// folder-prefix roots. There is no third state, and in particular no empty root set - the
/// hierarchy is closed by a private constructor and <see cref="Of"/> refuses an empty sequence,
/// so "no roots" cannot be constructed at all.
///
/// That refusal is the point. Encoding the broadest authority as an empty collection makes the
/// widest grant identical to the value a dropped entry, a filtered-to-nothing list, or a
/// default-constructed field produces, and the only defence is that every producer remembers to
/// check. Here the check has nowhere to fail: a caller holding a <see cref="VaultScope"/> holds
/// one of two meanings, both of them deliberate.
///
/// The configuration surface says the same thing: whole-vault access is spelled <c>["*"]</c>, and
/// an empty list is a load-time error rather than a second spelling of it. The sentinel is gone
/// from the file format as well as the model, so there is no layer at which losing an entry
/// widens a grant.
/// </summary>
public abstract record VaultScope
{
    private VaultScope() { }

    /// <summary>The whole vault. Distinct from a root set that happens to match everything.</summary>
    public static VaultScope Unscoped { get; } = new UnscopedScope();

    /// <summary>
    /// A scope confined to <paramref name="roots"/>. Throws <see cref="ArgumentException"/> on an
    /// empty sequence: an empty root set has no meaning here, and silently promoting it to
    /// <see cref="Unscoped"/> would reintroduce the sentinel this type exists to remove. A caller
    /// that means the whole vault says <see cref="Unscoped"/>.
    /// </summary>
    public static VaultScope Of(IEnumerable<string> roots)
    {
        var set = roots as IReadOnlySet<string> ?? new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
            throw new ArgumentException("A rooted scope needs at least one root; use VaultScope.Unscoped for whole-vault access.", nameof(roots));
        return new RootedScope(set);
    }

    /// <summary>
    /// True if <paramref name="relativePath"/> falls under this scope - folder-prefix containment
    /// matched by path segment, not raw string prefix, so a root of "Projects/Foo" does not match
    /// "Projects/Foo-Other/...". An unscoped scope admits every path, which is a case of the type
    /// rather than a property of the root count.
    ///
    /// Every root (and the path itself) is normalized via <see cref="ScopePath.Normalize"/>
    /// before any match is attempted - eagerly, so one malformed root makes the whole check throw
    /// regardless of whether a different root would have matched. This is the "an unscopeable
    /// query errors; it does not silently degrade" rule applied at the containment-check level: a
    /// grant whose configured scope can't be fully resolved is treated as broken, not partially
    /// trusted.
    /// </summary>
    public bool Admits(string relativePath)
    {
        if (this is not RootedScope rooted)
            return true;

        var pathSegments = ScopePath.Normalize(relativePath);
        return rooted.NormalizedRoots.Any(rootSegments =>
            rootSegments.Length <= pathSegments.Length &&
            !rootSegments.Where((s, i) => !string.Equals(s, pathSegments[i], StringComparison.OrdinalIgnoreCase)).Any());
    }

    /// <summary>The whole-vault case.</summary>
    public sealed record UnscopedScope : VaultScope;

    /// <summary>
    /// The confined case. <see cref="Roots"/> is never empty.
    ///
    /// Equality is structural over the root set. The compiler-generated version would compare the
    /// set by reference, so two scopes over identical roots would test unequal - a quiet trap for
    /// any caller that reasonably expects a record to compare by value.
    /// </summary>
    public sealed record RootedScope(IReadOnlySet<string> Roots) : VaultScope
    {
        private string[][]? _normalizedRoots;

        /// <summary>
        /// Every root in normalized segment form, computed once per instance - the roots are
        /// immutable, and a whole-vault enumeration filters every candidate against the same
        /// scope. A malformed root makes the computation throw, and the failure repeats on every
        /// access rather than being cached, so no check that consults this ever answers from a
        /// partially-resolved scope.
        /// </summary>
        internal string[][] NormalizedRoots => _normalizedRoots ??= Roots.Select(ScopePath.Normalize).ToArray();

        public bool Equals(RootedScope? other) => other is not null && Roots.SetEquals(other.Roots);

        public override int GetHashCode()
        {
            var hash = 0;
            foreach (var root in Roots)
                hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(root);
            return hash;
        }
    }
}
