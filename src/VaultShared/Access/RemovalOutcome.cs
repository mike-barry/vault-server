namespace VaultShared.Access;

/// <summary>
/// Represents the result of removing one entry.
/// </summary>
/// <remarks>
/// A removal runs against a plan built a moment earlier, so an entry that vanished in between is a
/// different answer from one that could not be removed. Both distinctions come from the attempt:
/// neither an existence check nor an emptiness check runs first.
/// </remarks>
public abstract record RemovalOutcome
{
    private RemovalOutcome() { }

    /// <summary>The entry is gone.</summary>
    public sealed record Removed : RemovalOutcome;

    /// <summary>Nothing was there to remove.</summary>
    /// <param name="Path">The entry that was removed, vault-relative.</param>
    public sealed record NotFound(string Path) : RemovalOutcome;

    /// <summary>
    /// The folder is not empty. This layer removes only the entry it is named and never walks a
    /// tree, because a recursive removal would hold the only record of how far it got. The caller's
    /// plan, built from the whole-vault file census, supplies the entries instead.
    /// </summary>
    /// <param name="Path">The folder that was removed, vault-relative.</param>
    public sealed record NotEmpty(string Path) : RemovalOutcome;

    /// <summary>The entry is there and the removal failed anyway.</summary>
    /// <param name="Path">The entry that was removed, vault-relative.</param>
    /// <param name="Reason">
    /// What the platform reported, with any locations in it rendered vault-relative.
    /// </param>
    public sealed record Failed(string Path, string Reason) : RemovalOutcome;
}
