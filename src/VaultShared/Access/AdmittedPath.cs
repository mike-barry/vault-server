namespace VaultShared.Access;

/// <summary>
/// A path the caller's authority admits for reading: inside the vault, and under the effective read
/// scope - the grant's own roots unioned with the vault's always-readable roots.
///
/// Every read-class primitive takes one of these, so the authority a primitive requires is its
/// signature rather than a check it must remember. <see cref="VaultAccess"/> is the only producer
/// that consults a scope, and <see cref="VaultWidePass"/> - reachable only by a call naming what it
/// bypasses - is the only other producer at all. Anything further is a place the scope check can be
/// skipped without saying so.
/// </summary>
public sealed record ReadPath
{
    internal ReadPath(VaultPath path) => Path = path;

    public VaultPath Path { get; }

    public override string ToString() => Path.Value;
}

/// <summary>
/// A path admitted for writing: from <see cref="VaultAccess"/>, checked against the grant's own
/// roots and nothing wider, so an always-readable root admits a read and refuses a write; or from
/// <see cref="VaultWidePass"/>, where scope has nothing to bind and the seam is the bypass.
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
