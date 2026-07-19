namespace VaultMcpServer.Auth;

/// <summary>
/// A grants.json row: an identity plus the op classes and vaults it may operate against.
/// Op-class names are validated against <see cref="VaultMcpServer.Vault.OpClassRegistry"/>
/// at load time (see <see cref="GrantStore"/>), not constrained by the C# type here.
/// </summary>
/// <param name="ExpiresAtUtc">
/// Absolute expiry instant computed at load time from the row's <c>ttl</c> (minutes from the
/// load-time "now"), or <c>null</c> for a <c>"*"</c> (infinite) ttl.
/// </param>
/// <param name="ScopeRoots">
/// Per-vault folder-prefix roots (vault-relative, forward-slash, canonicalized at load time)
/// the grant is restricted to, distinct from <paramref name="Vaults"/> - path scope restricts
/// *within* a vault the grant already has <see cref="Vaults"/> access to. Keyed by vault name
/// (case-insensitive); each value is that vault's list of folder-prefix roots. Also the shape
/// used for grants minted at runtime by <see cref="GrantStore.Mint"/>.
///
/// Required and always fully populated, for both a file-loaded grant (see
/// <see cref="GrantStore.Load"/>) and one minted at runtime (see <see cref="GrantStore.Mint"/>):
/// every vault in <see cref="Vaults"/> must have a corresponding key here, so this map is never
/// <c>null</c> and never missing a vault's entry - a <c>grants.json</c> row that omits
/// <c>scopeRoots</c> entirely, or that lists a vault in <c>vaults</c> without a matching
/// <c>scopeRoots</c> key, fails to load rather than silently granting that vault full access,
/// and <see cref="GrantStore.Mint"/> enforces the same coverage at runtime. There is no longer
/// any "missing key" or "missing map" state to interpret - the only way to grant a vault full
/// access is the explicit empty-set entry below.
///
/// An entry's value being empty or exactly <c>["*"]</c> means that vault is explicitly
/// unscoped - full access within it - still the same meaning as before, just no longer the
/// default for an absent key. The whole-grant wildcard <c>{ "*": ["*"] }</c> means "every vault
/// in <c>vaults</c>, fully open" and is expanded at load time into a concrete empty-set entry
/// per vault, mirroring how <c>vaults: ["*"]</c> itself expands. <c>MintTokenTool</c> always
/// supplies a concrete per-vault map derived from its required <c>scope_roots</c> argument, with
/// an explicit empty set (not a missing key) representing a minted grant's whole-vault access.
///
/// An empty/blank scope-root string is not a valid element of a set; it is treated as
/// malformed input, not as a "matches everything" sentinel - see
/// <see cref="VaultMcpServer.Vault.VaultAccess.NormalizeScopeSegments"/>.
/// Resolve a vault's flat root list via
/// <see cref="VaultMcpServer.Vault.VaultAccess.ResolveVaultScopeRoots"/> before checking with
/// <see cref="VaultMcpServer.Vault.VaultAccess.IsPathInScope"/> - given this map, that resolution
/// step now throws rather than falling back to "unscoped" if a queried vault has no entry, since
/// that should be structurally unreachable for any grant built through <see cref="GrantStore.Load"/>
/// or <see cref="GrantStore.Mint"/>. (Passing a <c>null</c> map to
/// <see cref="VaultMcpServer.Vault.VaultAccess.ResolveVaultScopeRoots"/> is a separate,
/// still-supported case - it means "no grant/scope map was supplied to this call at all", used
/// by <see cref="VaultMcpServer.Vault.VaultAccess"/>'s own optional <c>scopeRoots</c> parameters,
/// not something a real <see cref="Grant"/> ever produces.)
/// </param>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc, IReadOnlyDictionary<string, IReadOnlySet<string>> ScopeRoots)
{
    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>True once <paramref name="now"/> has reached the grant's <see cref="ExpiresAtUtc"/>; always false for an infinite (<c>"*"</c>) ttl.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;
}
