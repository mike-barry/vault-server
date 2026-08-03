namespace VaultShared.Access;

/// <summary>
/// A path the caller's authority admits for reading: inside the vault, and under the effective read
/// scope - the grant's own roots unioned with the vault's always-readable roots.
///
/// Every read-class primitive takes one of these, so the authority a primitive requires is its
/// signature rather than a check it must remember. Construction is
/// <see cref="VaultAccess"/>'s alone: a second producer is a second place the scope check can be
/// skipped.
/// </summary>
public sealed record ReadPath
{
    internal ReadPath(VaultPath path) => Path = path;

    public VaultPath Path { get; }

    public override string ToString() => Path.Value;
}

/// <summary>
/// A path the caller's grant admits for writing, checked against the grant's own roots and nothing
/// wider. An always-readable root therefore admits a read and refuses a write.
///
/// <see cref="AsRead"/> widens; nothing narrows the other way, so no expression can hand a
/// merely-readable path to a write.
/// </summary>
public sealed record WritePath
{
    internal WritePath(VaultPath path) => Path = path;

    public VaultPath Path { get; }

    /// <summary>Anything writable is readable.</summary>
    public ReadPath AsRead() => new(Path);

    public override string ToString() => Path.Value;
}
