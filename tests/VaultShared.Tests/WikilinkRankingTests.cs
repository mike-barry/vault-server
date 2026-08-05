using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests <see cref="WikilinkRanking"/>, which orders the candidate files a bare wikilink could
/// resolve to.
/// </summary>
/// <remarks>
/// The ordering reproduces how Obsidian resolves a bare wikilink, which is a requirement rather
/// than a choice, because the vault's existing content already depends on which file a link opens.
/// These tests are the record of that behaviour, measured on 2026-08-04 against Obsidian
/// configured with "New link format" set to "shortest path when possible" and "Automatically
/// update internal links" turned on. A later measurement that disagrees will fail here.
///
/// Ranking is the editor's second resolution tier only. A target naming a whole vault path is
/// taken by the note at that path before ranking is consulted, which the pass-level tests cover.
///
/// Ranking finds no files itself and touches no filesystem, so the vault is a value rather than a
/// real directory tree and the candidates are passed in.
/// </remarks>
public sealed class WikilinkRankingTests
{
    private static readonly Vault Vault = new("test", Path.Combine(Path.GetTempPath(), "no-such-vault"), PathPolicy.None);

    private static VaultPath At(string relativePath) =>
        VaultPath.TryCreate(Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    private static IReadOnlyList<string> Ranked(string linkingNote, params string[] candidates) =>
        [.. WikilinkRanking.Rank(At(linkingNote), candidates.Select(At)).Select(p => p.Value)];

    // ---- Nearest by shared folder prefix ----

    [Fact]
    public void TheCandidateInTheLinkingFilesOwnFolderIsNearest()
    {
        Assert.Equal(
            ["Notes/Dupe.md", "Other/Dupe.md"],
            Ranked("Notes/Note.md", "Other/Dupe.md", "Notes/Dupe.md"));
    }

    [Fact]
    public void ADescentIntoTheLinkingFilesSubtreeBeatsAClimbOutOfIt()
    {
        // Distance is the shared folder prefix, not the length of the relative path. The subtree
        // copy shares two folders with the linking file and the other shares none, so it wins even
        // though "../../Notes/Dupe.md" is the shorter path to write.
        Assert.Equal(
            ["Projects/Widget/Reference-Material/Dupe.md", "Notes/Dupe.md"],
            Ranked("Projects/Widget/Note.md", "Notes/Dupe.md", "Projects/Widget/Reference-Material/Dupe.md"));
    }

    [Fact]
    public void ADescentOfAnyDepthBeatsASingleClimb()
    {
        // Descending costs nothing, so depth below the linking file does not accumulate against
        // the candidate. Three folders down still outranks one folder up.
        Assert.Equal(
            ["Aa/Bb/Cc/Dd/Dupe.md", "Dupe.md"],
            Ranked("Aa/Bb/Note.md", "Dupe.md", "Aa/Bb/Cc/Dd/Dupe.md"));
    }

    [Fact]
    public void ADeeperCandidateOutranksTheVaultRootCopy()
    {
        // Ranking alone hands the link to the subtree copy. The root copy nonetheless keeps every
        // bare link to its basename, because a bare target spells that copy's whole vault path and
        // the path tier takes it before these candidates are ever gathered.
        Assert.Equal(
            ["Aa/Bb/Cc/Dd/Dupe.md", "Dupe.md"],
            Ranked("Aa/Bb/Cc/Note.md", "Dupe.md", "Aa/Bb/Cc/Dd/Dupe.md"));
    }

    [Fact]
    public void TheLinkingFilesOwnFolderBeatsASubfolderOfIt()
    {
        // Both candidates climb no folders, so the one that descends least wins. The subfolder is
        // named to sort ahead of the basename, which is what the ordinal comparison would seize on
        // if descent were not weighed first.
        Assert.Equal(
            ["Aa/Bb/Dupe.md", "Aa/Bb/Aaa/Dupe.md"],
            Ranked("Aa/Bb/Note.md", "Aa/Bb/Aaa/Dupe.md", "Aa/Bb/Dupe.md"));
    }

    [Fact]
    public void TheOwnFolderWinsWhicheverWayTheSubfolderSorts()
    {
        // The same pair with the subfolder sorting behind the basename. Ranking must not depend on
        // the subfolder's name at all.
        Assert.Equal(
            ["Aa/Bb/Dupe.md", "Aa/Bb/Zzz/Dupe.md"],
            Ranked("Aa/Bb/Note.md", "Aa/Bb/Zzz/Dupe.md", "Aa/Bb/Dupe.md"));
    }

    // ---- Breaking a tie ----

    [Fact]
    public void CandidatesSharingTheSameDepthAreOrderedByTheirVaultPaths()
    {
        // Neither candidate shares a folder with the linking file, so the ordinal comparison of
        // the vault paths decides.
        Assert.Equal(
            ["Bb/Dupe.md", "Zz/Dupe.md"],
            Ranked("Aa/Note.md", "Zz/Dupe.md", "Bb/Dupe.md"));
    }

    [Fact]
    public void TheTieBreakIsOrdinalRatherThanCultural()
    {
        // Both are one hop down from the linking file's folder and share no folder with it.
        Assert.Equal(
            ["Aa/Dupe.md", "aa/Dupe.md"],
            Ranked("Note.md", "aa/Dupe.md", "Aa/Dupe.md"));
    }

    // ---- What ranking returns ----

    [Fact]
    public void EveryCandidateComesBack()
    {
        // `Notes/Dupe.md` shares a folder with the linking note; the other two share none and are
        // ordered by how far they descend.
        Assert.Equal(
            ["Notes/Dupe.md", "Aa/Dupe.md", "Aa/Bb/Cc/Dupe.md"],
            Ranked("Notes/Note.md", "Aa/Bb/Cc/Dupe.md", "Notes/Dupe.md", "Aa/Dupe.md"));
    }

    [Fact]
    public void ResolutionIsTheFirstOfTheRanking()
    {
        var candidates = new[] { At("Other/Dupe.md"), At("Notes/Dupe.md") };

        Assert.Equal("Notes/Dupe.md", WikilinkRanking.Resolve(At("Notes/Note.md"), candidates)?.Value);
    }

    [Fact]
    public void RankingFindsNothingItself()
    {
        // Ranking only orders the candidates it is handed, so an empty set resolves to nothing
        // rather than to whatever is on disk.
        Assert.Null(WikilinkRanking.Resolve(At("Notes/Note.md"), []));
        Assert.Empty(WikilinkRanking.Rank(At("Notes/Note.md"), []));
    }

    [Fact]
    public void ACandidateFromAnotherVaultIsALayerDefect()
    {
        var elsewhere = new Vault("other", Path.Combine(Path.GetTempPath(), "no-such-other"), PathPolicy.None);
        Assert.True(VaultPath.TryCreate(elsewhere, "Notes/Dupe.md", out var foreign, out _));

        Assert.Throws<ArgumentException>(() =>
            WikilinkRanking.Rank(At("Notes/Note.md"), [At("Notes/Dupe.md"), foreign]));
    }
}
