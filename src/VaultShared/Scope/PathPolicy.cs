namespace VaultShared.Scope;

/// <summary>
/// One vault's path policy: modifiers attached to targets within that vault, declared in its
/// <c>vaults.json</c> entry and parsed by <c>PathPolicyParser</c>. A target is a vault-relative
/// path or a named structural rule; a modifier says something the tooling must do about that
/// target.
///
/// Two modifiers are specified and only <c>always_readable</c> is carried here - the read-class
/// scope floor. Its target unions into a grant's effective read scope for this vault, so an agent
/// under any grant reaches the material it is instructed to obey without that grant widening
/// anywhere else. <see cref="EffectiveReadScope"/> is where that union is applied.
///
/// <c>always_readable</c> is path-only: it is unioned into scope roots and those are folder
/// prefixes, so "every folder note is readable" is not expressible as one. That leaves
/// <c>mutation_requires</c> as the only modifier a <c>rule</c> target could carry, and nothing
/// here acts on it - which is why the parser refuses both rather than accepting a policy this
/// type would silently drop.
///
/// Why the floor is a modifier here rather than its own configuration: a path that instructs
/// agents is special in both directions for one reason, and stating that once per path keeps the
/// two halves from drifting into a path everyone reads that nobody protected.
///
/// This type is a scope value rather than a configuration one. Its input is a vault's
/// configuration and its output is a <see cref="VaultScope"/>, and the read path is its only
/// consumer, so it sits with the scope model and the parser that builds it sits with the file it
/// parses.
/// </summary>
public sealed class PathPolicy
{
    /// <summary>A vault that declares nothing. Every grant's read scope in it is exactly its own.</summary>
    public static PathPolicy None { get; } = new([]);

    /// <summary>Canonicalized, de-duplicated <c>always_readable</c> targets: vault-relative,
    /// forward-slash, in declared order.</summary>
    public IReadOnlyList<string> AlwaysReadableRoots { get; }

    private PathPolicy(IReadOnlyList<string> alwaysReadableRoots) => AlwaysReadableRoots = alwaysReadableRoots;

    /// <summary>
    /// Builds a policy over already-canonicalized targets. Internal because construction is the
    /// parser's alone: the targets have to be normalized and de-duplicated before they mean
    /// anything as scope roots, and a second producer is a second place that can skip it.
    /// </summary>
    internal static PathPolicy Of(IReadOnlyList<string> alwaysReadableRoots) =>
        alwaysReadableRoots.Count == 0 ? None : new PathPolicy(alwaysReadableRoots);

    /// <summary>
    /// The effective read scope for a grant whose own scope in this vault is
    /// <paramref name="grantScope"/>: the union of that scope and the always-readable roots.
    ///
    /// An unscoped grant is returned unchanged - it already reaches everything. With no policy
    /// this is the identity, so a read caller can call it unconditionally rather than branching on
    /// whether a floor exists. It returns a <see cref="VaultScope"/> rather than answering a
    /// containment question, so enumeration runs over the same scope a containment check uses and
    /// needs no special case of its own.
    ///
    /// Applied here, at check time, rather than copied into a grant's roots when the grant is
    /// built - which is what makes it a floor. A copy would be attenuable, and a floor a minted
    /// token can drop is not one.
    ///
    /// <b>Read-class callers only.</b> Every write-class path - and every authority decision,
    /// including mint's attenuation check - stays on the grant's own scope. A grant that can read
    /// the always-readable roots must not thereby be able to write them, nor to mint a token
    /// rooted at them.
    ///
    /// The policy must be the one belonging to the vault the path is in - the same discipline
    /// <see cref="VaultShared.Auth.Grant.ScopeIn"/> enforces for scope roots.
    /// </summary>
    public VaultScope EffectiveReadScope(VaultScope grantScope)
    {
        if (AlwaysReadableRoots.Count == 0 || grantScope is not VaultScope.RootedScope rooted)
            return grantScope;

        var union = new HashSet<string>(rooted.Roots, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(AlwaysReadableRoots);
        return VaultScope.Of(union);
    }
}
