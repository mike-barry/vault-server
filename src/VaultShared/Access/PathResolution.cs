namespace VaultShared.Access;

/// <summary>
/// Represents the result of resolving a path the caller named: the admitted proof, or one of three
/// refusals. These records are the whole set. The constructor is private, so nothing outside this
/// type can add another.
/// </summary>
/// <remarks>
/// A refusal carries only the path as the caller wrote it. It must never carry whether the target
/// exists, the grant's roots, or which part of the path failed a check - the last of those
/// discloses the roots one call at a time.
/// </remarks>
/// <typeparam name="TPath">The admitted proof: <see cref="ReadPath"/> or <see cref="WritePath"/>.</typeparam>
public abstract record PathResolution<TPath> where TPath : class
{
    private PathResolution() { }

    /// <summary>The path resolved and the caller's authority reaches it.</summary>
    /// <param name="Path">The admitted proof.</param>
    public sealed record Admitted(TPath Path) : PathResolution<TPath>;

    /// <summary>The text is escaping, rooted, empty, or denotes the vault root itself.</summary>
    /// <param name="Path">The path as the caller wrote it.</param>
    /// <param name="Reason">Which rule the text broke.</param>
    public sealed record Malformed(string Path, string Reason) : PathResolution<TPath>;

    /// <summary>
    /// The path has a dot-prefixed segment. No grant reaches such a path, so this refusal is
    /// independent of scope and is decided before scope is consulted.
    /// </summary>
    /// <param name="Path">The path as the caller wrote it.</param>
    public sealed record Excluded(string Path) : PathResolution<TPath>;

    /// <summary>The path is well-formed and inside the vault, but the caller's authority does not reach it.</summary>
    /// <param name="Path">The path as the caller wrote it.</param>
    public sealed record OutOfScope(string Path) : PathResolution<TPath>;
}
