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
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc)
{
    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>True once <paramref name="now"/> has reached the grant's <see cref="ExpiresAtUtc"/>; always false for an infinite (<c>"*"</c>) ttl.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;
}
