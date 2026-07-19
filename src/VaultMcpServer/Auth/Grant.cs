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
/// Per-vault folder-prefix scope roots, set on grants minted by <see cref="GrantStore.Mint"/> -
/// <c>null</c> means unscoped everywhere (including for file-loaded <c>grants.json</c> rows,
/// which carry no sub-vault scoping concept). A vault key absent from the dictionary and an
/// empty set for a vault key mean the same thing: fully unscoped (unrestricted) for that
/// specific vault - there is no folder restriction to apply. An empty/blank scope-root string
/// is not a valid element of the set; it is treated as malformed input by the containment
/// check, not as a "matches everything" sentinel. Folder-prefix values are relative to the
/// vault root (e.g. <c>"Projects/Foo"</c> under key
/// <c>"primary"</c>) - a parallel branch (not yet merged) adds a
/// <c>VaultAccess.ResolveVaultScopeRoots</c> helper for resolving a given vault's effective
/// scope list from this shape. Not enforced by <c>EnsureAllowed</c>/
/// <c>Assert-VaultOperationAllowed</c> yet - that containment check is a parallel, in-flight
/// change; this field only carries the data until that check lands.
/// </param>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc, IReadOnlyDictionary<string, IReadOnlySet<string>>? ScopeRoots = null)
{
    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>True once <paramref name="now"/> has reached the grant's <see cref="ExpiresAtUtc"/>; always false for an infinite (<c>"*"</c>) ttl.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;
}
