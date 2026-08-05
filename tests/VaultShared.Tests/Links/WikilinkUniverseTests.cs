using VaultShared.Access;
using VaultShared.Links;
using VaultShared.Scope;

namespace VaultShared.Tests.Links;

/// <summary>
/// Tests <see cref="WikilinkUniverse"/> directly, for behaviour that turns on the order the notes
/// were indexed in.
/// </summary>
/// <remarks>
/// The three maintenance passes each build their own index from note enumeration, whose order is
/// the search engine's, so a test reaching resolution through a pass cannot choose that order and
/// an order-dependent answer looks correct on whichever machine happens to enumerate favourably.
/// Constructing the index here is what lets both orders be asserted. Resolution touches no
/// filesystem, so the vault is a value and the notes are passed in.
/// </remarks>
public sealed class WikilinkUniverseTests
{
    private static readonly Vault Vault = new("test", Path.Combine(Path.GetTempPath(), "no-such-vault"), PathPolicy.None);

    private static VaultPath At(string relativePath) =>
        VaultPath.TryCreate(Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    /// <summary>Returns the note a target opens, indexed in the order the notes are given.</summary>
    /// <param name="linkingNote">The note the link is written in.</param>
    /// <param name="target">The link's target text.</param>
    /// <param name="notes">The notes to index, in the order to index them.</param>
    /// <returns>The opened note's vault-relative path, or null when the target opens nothing.</returns>
    private static string? Opened(string linkingNote, string target, params string[] notes)
    {
        var universe = new WikilinkUniverse([.. notes.Select(At)]);
        return universe.Resolve(At(linkingNote), target) is { } position ? universe[position].Value : null;
    }

    [Fact]
    public void AWholePathBeatsAnotherNotesExtensionlessPath()
    {
        // "Rep/Report.md" is one note's whole path and the other's path with the note extension
        // left off. The target goes to the note whose whole path it is, whichever was indexed
        // first - indexing both forms in one pass would give it to whichever came last.
        Assert.Equal("Rep/Report.md", Opened("Rep/Ref.md", "Rep/Report.md", "Rep/Report.md", "Rep/Report.md.md"));
        Assert.Equal("Rep/Report.md", Opened("Rep/Ref.md", "Rep/Report.md", "Rep/Report.md.md", "Rep/Report.md"));
    }

    [Fact]
    public void EachOfThePairIsStillReachableByItsOwnUncontestedSpelling()
    {
        // Only the one contested key is at stake. The spelling that names a single note names it in
        // either order.
        Assert.Equal("Rep/Report.md", Opened("Rep/Ref.md", "Rep/Report", "Rep/Report.md", "Rep/Report.md.md"));
        Assert.Equal("Rep/Report.md.md", Opened("Rep/Ref.md", "Rep/Report.md.md", "Rep/Report.md", "Rep/Report.md.md"));
        Assert.Equal("Rep/Report.md.md", Opened("Rep/Ref.md", "Rep/Report.md.md", "Rep/Report.md.md", "Rep/Report.md"));
    }

    [Fact]
    public void ABareTargetOfTheContestedNameRanksRatherThanCollides()
    {
        // Both notes answer the bare name, because one carries it as its basename and the other as
        // that basename's stem. Neither spelling is a whole vault path, so the two are ranked, and
        // at equal depth in one folder the ordinal comparison settles it either way round.
        Assert.Equal("Rep/Report.md", Opened("Rep/Ref.md", "Report.md", "Rep/Report.md", "Rep/Report.md.md"));
        Assert.Equal("Rep/Report.md", Opened("Rep/Ref.md", "Report.md", "Rep/Report.md.md", "Rep/Report.md"));
    }
}
