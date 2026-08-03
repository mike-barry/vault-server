namespace VaultShared.Access;

/// <summary>
/// The answer to a caller naming a path: the admitted proof, or one of three refusals callers above
/// need to tell apart - malformed text, outside the operand perimeter, and out of scope. The
/// hierarchy is closed by a private constructor, and the layer distinguishes these structurally;
/// what each is called on the wire belongs to the tool vocabulary.
///
/// A refusal carries the caller's own coordinates - the path as it was named - and nothing else.
/// Never whether the target exists, never the grant's roots, and never the subset that failed a
/// check, which is the same disclosure paid in instalments.
/// </summary>
/// <typeparam name="TPath">The admitted proof: <see cref="ReadPath"/> or <see cref="WritePath"/>.</typeparam>
public abstract record PathResolution<TPath> where TPath : class
{
    private PathResolution() { }

    /// <summary>Resolved and admitted.</summary>
    public sealed record Admitted(TPath Path) : PathResolution<TPath>;

    /// <summary>Escaping, rooted, empty, or denoting the vault root itself.</summary>
    public sealed record Malformed(string Path, string Reason) : PathResolution<TPath>;

    /// <summary>
    /// A dot-prefixed segment. The tooling's content lives outside those, and no grant changes that,
    /// so this refusal is independent of scope and is decided ahead of it.
    /// </summary>
    public sealed record Excluded(string Path) : PathResolution<TPath>;

    /// <summary>Well-formed and inside the vault; the caller's authority does not reach it.</summary>
    public sealed record OutOfScope(string Path) : PathResolution<TPath>;
}
