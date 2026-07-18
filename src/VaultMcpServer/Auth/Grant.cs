using VaultMcpServer.Vault;

namespace VaultMcpServer.Auth;

/// <summary>A grants.json row: an identity plus the op classes and vaults it may operate against.</summary>
public sealed record Grant(string Id, IReadOnlySet<OpClass> OpClasses, IReadOnlySet<string> Vaults)
{
    public bool Allows(OpClass op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);
}
