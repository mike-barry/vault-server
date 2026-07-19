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
/// (case-insensitive); each value is that vault's list of folder-prefix roots. A vault present
/// in <see cref="Vaults"/> but absent from this map's keys - and the whole map being
/// <c>null</c> - means unscoped: full access within that vault, the same meaning as an entry's
/// value being empty or exactly <c>["*"]</c> in <c>grants.json</c>'s <c>scopeRoots</c> object,
/// or the <c>scopeRoots</c> field being omitted entirely - see <see cref="GrantStore.Load"/>.
/// Resolve a vault's flat root list via
/// <see cref="VaultMcpServer.Vault.VaultAccess.ResolveVaultScopeRoots"/> before checking with
/// <see cref="VaultMcpServer.Vault.VaultAccess.IsPathInScope"/>.
/// </param>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc, IReadOnlyDictionary<string, IReadOnlySet<string>>? ScopeRoots)
{
    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>True once <paramref name="now"/> has reached the grant's <see cref="ExpiresAtUtc"/>; always false for an infinite (<c>"*"</c>) ttl.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;
}
