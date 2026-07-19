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
/// Folder-prefix scope roots (e.g. <c>"primary"</c> for a whole vault, or
/// <c>"primary/Projects/Foo"</c> for a subtree), set on grants minted by
/// <see cref="GrantStore.Mint"/> - <c>null</c> for file-loaded <c>grants.json</c> rows, which
/// carry no sub-vault scoping concept. Not enforced by <c>EnsureAllowed</c>/
/// <c>Assert-VaultOperationAllowed</c> yet - that containment check is a parallel, in-flight
/// change; this field only carries the data until that check lands. Integration note: the
/// field name/shape here (<c>IReadOnlyList&lt;string&gt;?</c>) may need reconciling with
/// however that change shapes the same concept on <c>Grant</c>/<c>grants.json</c>.
/// </param>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc, IReadOnlyList<string>? ScopeRoots = null)
{
    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>True once <paramref name="now"/> has reached the grant's <see cref="ExpiresAtUtc"/>; always false for an infinite (<c>"*"</c>) ttl.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;
}
