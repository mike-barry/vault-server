using VaultShared.Auth;
using VaultShared.Scope;

namespace VaultShared.Access;

/// <summary>
/// One resolved vault plus the caller's grant, and the only producer of an admitted path. Holding
/// the caller's authority is the whole of what it is.
///
/// Authority is handed in rather than ambient because the constructor takes an object the caller
/// must already hold, and no member here takes a scope - a caller-supplied scope is a forged grant,
/// and the strongest defence is that no such argument exists. It takes the grant rather than a bare
/// scope because the layer owes authority two things: path admission from the grant's scope in this
/// vault, and the mid-operation key assertion from the same grant's op classes.
/// </summary>
public sealed class VaultAccess
{
    private readonly Grant _grant;
    private readonly VaultScope _grantScope;
    private readonly VaultScope _readScope;

    /// <param name="grant">
    /// The caller's grant. It must carry a scope entry for <paramref name="vault"/>; a grant that
    /// does not is an upstream defect and throws rather than being interpreted as permission.
    /// </param>
    public VaultAccess(Vault vault, Grant grant)
    {
        Vault = vault;
        _grant = grant;
        _grantScope = grant.ScopeIn(vault.Name);

        // The effective read scope is computed once, here, rather than per candidate: a whole-vault
        // enumeration filters against one scope instead of rebuilding the union for every path it
        // considers.
        _readScope = vault.Policy.EffectiveReadScope(_grantScope);
    }

    public Vault Vault { get; }

    /// <summary>
    /// Resolves a caller-named path for reading, against the effective read scope - the grant's own
    /// roots unioned with this vault's always-readable roots.
    /// </summary>
    public PathResolution<ReadPath> ForRead(string relativePath) =>
        Resolve(relativePath, _readScope, path => new ReadPath(path));

    /// <summary>
    /// Resolves a caller-named path for writing, against the grant's own roots and nothing wider.
    /// An always-readable root admits a read here and refuses a write, which is the asymmetry the
    /// two path types carry.
    /// </summary>
    public PathResolution<WritePath> ForWrite(string relativePath) =>
        Resolve(relativePath, _grantScope, path => new WritePath(path));

    /// <summary>
    /// The quiet path: whether a path the layer itself produced falls in the effective read scope.
    /// A list returns the in-scope subset without saying what was withheld.
    ///
    /// Separate from <see cref="ForRead"/> rather than the same member with a flag, because this one
    /// cannot construct a refusal and so cannot leak one. The dot rule is not applied here - the
    /// perimeter is a property of a caller-named path, and an enumeration that admits dot entries
    /// still filters them by scope.
    ///
    /// A candidate from another vault is a layer defect rather than a scope answer, and throws.
    /// </summary>
    public bool Admits(VaultPath candidate)
    {
        if (candidate.Vault != Vault)
            throw new ArgumentException(
                $"Candidate path belongs to vault \"{candidate.Vault.Name}\", not \"{Vault.Name}\".", nameof(candidate));

        return _readScope.Admits(candidate.Value);
    }

    /// <summary>
    /// Asserts a capability key mid-operation, once the outcome is known. The floor for what was
    /// <em>requested</em> is checked above the layer; this is for authority that depends on what the
    /// work turned out to touch.
    /// </summary>
    /// <exception cref="AuthorityRefusal">The grant does not hold <paramref name="opClass"/>.</exception>
    public void AssertKey(string opClass)
    {
        if (!_grant.Allows(opClass))
            throw new AuthorityRefusal(opClass);
    }

    /// <summary>
    /// The loud path, shared by both entry points. The order is fixed: text that is not a path at
    /// all, then the perimeter no grant changes, then scope. Every refusal names the path as the
    /// caller wrote it and says nothing further.
    /// </summary>
    private PathResolution<TPath> Resolve<TPath>(string relativePath, VaultScope scope, Func<VaultPath, TPath> admit)
        where TPath : class
    {
        if (!VaultPath.TryCreate(Vault, relativePath, out var path, out var reason))
            return new PathResolution<TPath>.Malformed(relativePath, reason);

        if (path.HasDotSegment)
            return new PathResolution<TPath>.Excluded(relativePath);

        if (!scope.Admits(path.Value))
            return new PathResolution<TPath>.OutOfScope(relativePath);

        return new PathResolution<TPath>.Admitted(admit(path));
    }
}
