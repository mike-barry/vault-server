namespace VaultMcpServer.Vault;

/// <summary>
/// Operation class a vault operation belongs to. Every tool declares one; grants
/// authorize per class. Phase 1 has exactly read and write.
/// </summary>
public enum OpClass
{
    Read,
    Write,
}
