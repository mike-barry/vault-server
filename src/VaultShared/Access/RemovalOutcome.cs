namespace VaultShared.Access;

/// <summary>
/// The answer to removing one entry. The hierarchy is closed by a private constructor.
///
/// A removal runs against a plan built a moment earlier, so an entry that vanished in between is a
/// different answer from one that could not be removed, and only the caller holding that plan can
/// decide which of those matters to it. Both distinctions come from the attempt: neither an
/// existence probe nor an emptiness check precedes it.
/// </summary>
public abstract record RemovalOutcome
{
    private RemovalOutcome() { }

    /// <summary>The entry is gone.</summary>
    public sealed record Removed : RemovalOutcome;

    /// <summary>Nothing there to remove.</summary>
    public sealed record NotFound(string Path) : RemovalOutcome;

    /// <summary>
    /// The folder holds something. The layer removes what it is named and never walks a tree: the
    /// census above already produces the file list a removal works from, and a layer that recursed
    /// would hold the only record of how far it got.
    /// </summary>
    public sealed record NotEmpty(string Path) : RemovalOutcome;

    /// <summary>
    /// There, and the removal failed anyway. <paramref name="Reason"/> is what the platform said,
    /// with locations rendered vault-relative.
    /// </summary>
    public sealed record Failed(string Path, string Reason) : RemovalOutcome;
}
