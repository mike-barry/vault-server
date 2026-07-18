namespace VaultMcpServer.Auth;

/// <summary>
/// A grants.json row: an identity plus the op classes and vaults it may operate against.
/// Op-class names are validated against <see cref="VaultMcpServer.Vault.OpClassRegistry"/>
/// at load time (see <see cref="GrantStore"/>), not constrained by the C# type here.
/// </summary>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults)
{
    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);
}
