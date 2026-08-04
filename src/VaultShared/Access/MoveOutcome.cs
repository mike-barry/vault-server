namespace VaultShared.Access;

/// <summary>
/// Represents the result of moving one entry. The private constructor closes the hierarchy, so a
/// caller that handles every nested record has handled every case.
/// </summary>
/// <remarks>
/// Only the relocation is here. Wikilink repair, the folder note's rename, protective pinning, and
/// partial-failure reporting are composed above it.
/// </remarks>
public abstract record MoveOutcome
{
    private MoveOutcome() { }

    /// <summary>The entry is at its destination.</summary>
    /// <param name="CaseOnly">
    /// True when the two paths named one file and differed only in spelling. Git does not see such
    /// a change under the platform's default case handling, and repository operations are outside
    /// this layer, so the move reports it and a later git step stages the rename.
    /// </param>
    public sealed record Moved(bool CaseOnly) : MoveOutcome;

    /// <summary>
    /// Nothing is at the source. Also the answer when the source names a folder rather than a note.
    /// </summary>
    /// <param name="Path">The source, vault-relative.</param>
    public sealed record SourceMissing(string Path) : MoveOutcome;

    /// <summary>
    /// Something is already at the destination. A move never overwrites and never merges. The
    /// source itself is not an occupant, because occupancy is compared against the resolved source
    /// ordinally, so a case-only rename is not a collision.
    /// </summary>
    /// <param name="Path">The destination, vault-relative.</param>
    public sealed record DestinationOccupied(string Path) : MoveOutcome;

    /// <summary>The destination's name does not satisfy the name grammar (<see cref="VaultName"/>).</summary>
    /// <param name="Name">The final segment of the destination, as the caller spelled it.</param>
    public sealed record InvalidName(string Name) : MoveOutcome;

    /// <summary>
    /// The destination's containing folder does not exist. A move creates no folder.
    /// </summary>
    /// <param name="Parent">The containing folder, vault-relative.</param>
    public sealed record MissingParent(string Parent) : MoveOutcome;

    /// <summary>The move failed for a reason none of the other cases names.</summary>
    /// <param name="Path">The path the failure is about, vault-relative.</param>
    /// <param name="Reason">
    /// What the platform reported, with any locations in it rendered vault-relative.
    /// </param>
    public sealed record Failed(string Path, string Reason) : MoveOutcome;
}
