namespace VaultShared.Access;

/// <summary>
/// The answer to a move. The hierarchy is closed by a private constructor, and the layer
/// distinguishes these structurally; what each is called on the wire belongs to the tool
/// vocabulary.
///
/// Only the relocation is here. Wikilink repair, the folder note's rename, protective pinning, and
/// partial-failure reporting are composed above it.
/// </summary>
public abstract record MoveOutcome
{
    private MoveOutcome() { }

    /// <summary>
    /// The entry is at its destination. <paramref name="CaseOnly"/> says the two paths named one
    /// file and differed only in spelling: git does not see that change under the platform's
    /// default case handling, and repository operations are outside this layer, so the move reports
    /// it and a later git pickup stages the rename.
    /// </summary>
    public sealed record Moved(bool CaseOnly) : MoveOutcome;

    /// <summary>Nothing at the source. Also the answer when the source names a folder rather than a note.</summary>
    public sealed record SourceMissing(string Path) : MoveOutcome;

    /// <summary>
    /// Something is already at the destination. Move never overwrites and never merges. The source
    /// itself is not an occupant: occupancy is decided against the resolved source ordinally, so a
    /// case-only rename is not a collision.
    /// </summary>
    public sealed record DestinationOccupied(string Path) : MoveOutcome;

    /// <summary>The destination's name does not satisfy the name grammar (<see cref="VaultName"/>).</summary>
    public sealed record InvalidName(string Name) : MoveOutcome;

    /// <summary>
    /// The destination's containing folder is not there, named in vault-relative terms. Move brings
    /// no folder into existence.
    /// </summary>
    public sealed record MissingParent(string Parent) : MoveOutcome;

    /// <summary>
    /// The move failed for a reason none of the above names. <paramref name="Reason"/> is what the
    /// platform said, with locations rendered vault-relative.
    /// </summary>
    public sealed record Failed(string Path, string Reason) : MoveOutcome;
}
