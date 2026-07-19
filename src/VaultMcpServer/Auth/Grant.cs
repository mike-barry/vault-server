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
/// Folder-prefix roots (vault-relative, forward-slash, canonicalized at load time) the grant
/// is restricted to, distinct from <paramref name="Vaults"/> - path scope restricts *within*
/// a vault the grant already has <see cref="Vaults"/> access to. Empty means unscoped: full
/// access within its granted vaults, the same meaning as a <c>"*"</c> row in
/// <c>grants.json</c>'s <c>scopeRoots</c> or an omitted <c>scopeRoots</c> field entirely - see
/// <see cref="GrantStore.Load"/>. Checked via
/// <see cref="VaultMcpServer.Vault.VaultAccess.IsPathInScope"/>.
/// </param>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc, IReadOnlySet<string> ScopeRoots)
{
    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>True once <paramref name="now"/> has reached the grant's <see cref="ExpiresAtUtc"/>; always false for an infinite (<c>"*"</c>) ttl.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;
}
