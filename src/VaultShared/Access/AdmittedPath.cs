namespace VaultShared.Access;

/// <summary>
/// Represents a path the caller's authority admits for reading: inside the vault and under the
/// effective read scope, which is the grant's own roots unioned with the vault's always-readable
/// roots.
/// </summary>
/// <remarks>
/// Every read primitive takes one of these, so the authority it requires is part of its signature.
/// Only <see cref="VaultAccess"/> and <see cref="VaultWidePass"/> construct one. Do not add another
/// producer. Each one is a place the scope check can be skipped without saying so.
/// </remarks>
public sealed record ReadPath
{
    internal ReadPath(VaultPath path) => Path = path;

    /// <summary>The contained path this proof admits.</summary>
    public VaultPath Path { get; }

    /// <summary>Returns the vault-relative path.</summary>
    public override string ToString() => Path.Value;
}

/// <summary>
/// Represents a path the caller's authority admits for writing. <see cref="VaultAccess"/> checks it
/// against the grant's own roots and nothing wider, so an always-readable root admits a read and
/// refuses a write; <see cref="VaultWidePass"/> also produces one, and that is the deliberate
/// bypass.
/// </summary>
/// <remarks>
/// There is no conversion the other way, so no expression can hand a merely-readable path to a
/// write.
/// </remarks>
public sealed record WritePath
{
    internal WritePath(VaultPath path) => Path = path;

    /// <summary>The contained path this proof admits.</summary>
    public VaultPath Path { get; }

    /// <summary>Returns the same path as a read proof. Anything writable is readable.</summary>
    public ReadPath AsRead() => new(Path);

    /// <summary>Returns the vault-relative path.</summary>
    public override string ToString() => Path.Value;
}
