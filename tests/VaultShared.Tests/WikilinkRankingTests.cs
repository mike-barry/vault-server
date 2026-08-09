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
/// These tests are the record of that behaviour, measured against Obsidian configured with "New
/// link format" set to "shortest path when possible" and "Automatically update internal links"
/// turned on. A later measurement that disagrees will fail here.
///
/// The behaviour recorded is what following a link does. The editor's autocomplete popup orders a
/// duplicated basename by a different comparator, so a measurement read off the popup will
/// disagree with these tests without either of them being wrong about its own subject.
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

    // ---- Inside the linking file's subtree beats outside it ----

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
        // The group is decided before either spelling is measured. The subtree copy needs no climb
        // and the other needs two, so the subtree copy wins even though "../../Notes/Dupe.md" is
        // the shorter target to write.
        Assert.Equal(
            ["Projects/Widget/Reference-Material/Dupe.md", "Notes/Dupe.md"],
            Ranked("Projects/Widget/Note.md", "Notes/Dupe.md", "Projects/Widget/Reference-Material/Dupe.md"));
    }

    [Fact]
    public void ADescentOfAnyDepthBeatsASingleClimb()
    {
        // Descending costs characters, but no amount of it moves a candidate out of the winning
        // group. Two folders down still outranks the root copy, which needs two climbs.
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
        // Both candidates are inside the subtree, so the shorter spelling wins: "Dupe.md" is seven
        // characters against "Aaa/Dupe.md" at eleven. The subfolder is named to sort ahead of the
        // basename, which is what the ordinal comparison would seize on if the spelling were not
        // measured first.
        Assert.Equal(
            ["Aa/Bb/Dupe.md", "Aa/Bb/Aaa/Dupe.md"],
            Ranked("Aa/Bb/Note.md", "Aa/Bb/Aaa/Dupe.md", "Aa/Bb/Dupe.md"));
    }

    [Fact]
    public void TheOwnFolderWinsWhicheverWayTheSubfolderSorts()
    {
        // The same pair with the subfolder sorting behind the basename, and the same eleven
        // characters against seven. Ranking must not depend on the subfolder's name at all, only on
        // its length.
        Assert.Equal(
            ["Aa/Bb/Dupe.md", "Aa/Bb/Zzz/Dupe.md"],
            Ranked("Aa/Bb/Note.md", "Aa/Bb/Zzz/Dupe.md", "Aa/Bb/Dupe.md"));
    }

    // ---- The shorter spelling ----

    [Fact]
    public void TheShorterSpellingWinsEvenWhereItDescendsFurther()
    {
        // The linking note sits at the vault root, so every candidate is inside its subtree and the
        // group cannot separate them. `a/b/Dupe.md` is eleven characters and
        // `Extended-Folder/Dupe.md` is twenty-three, so the deeper candidate wins on spelling
        // length. Ranking by how many folders a candidate sits inside would pick the other one.
        Assert.Equal(
            ["a/b/Dupe.md", "Extended-Folder/Dupe.md"],
            Ranked("Note.md", "Extended-Folder/Dupe.md", "a/b/Dupe.md"));
    }

    [Fact]
    public void TheShorterSpellingWinsEvenWhereItSharesLessOfThePrefix()
    {
        // Both candidates are outside the linking note's subtree, so both climb and the group
        // cannot separate them. `Aa/Bb/Extended-Folder/Dupe.md` shares two folders and needs one
        // climb, which is `../Extended-Folder/Dupe.md` at twenty-six characters;
        // `Aa/Tt/Dupe.md` shares one and needs two, which is `../../Tt/Dupe.md` at sixteen. The
        // shorter spelling wins despite sharing less of the prefix, so how much of the prefix a
        // candidate shares decides only its group and its climb count, never the order within a
        // group.
        Assert.Equal(
            ["Aa/Tt/Dupe.md", "Aa/Bb/Extended-Folder/Dupe.md"],
            Ranked("Aa/Bb/Cc/Note.md", "Aa/Bb/Extended-Folder/Dupe.md", "Aa/Tt/Dupe.md"));
    }

    // ---- Breaking a tie ----

    [Fact]
    public void CandidatesSharingTheSameDepthAreOrderedByTheirVaultPaths()
    {
        // Neither candidate shares a folder with the linking file, so both are outside its subtree
        // at one climb each, and "../Bb/Dupe.md" and "../Zz/Dupe.md" are the same thirteen
        // characters. The ordinal comparison of the vault paths is all that is left to decide it.
        Assert.Equal(
            ["Bb/Dupe.md", "Zz/Dupe.md"],
            Ranked("Aa/Note.md", "Zz/Dupe.md", "Bb/Dupe.md"));
    }

    [Fact]
    public void TheTieBreakIsOrdinalRatherThanCultural()
    {
        // The linking file is at the vault root, so both candidates are inside its subtree at one
        // folder down, and their spellings are the same ten characters. The tie-break is then the
        // only thing separating two names that differ in case alone, and it takes "Aa" ahead of
        // "aa" because an upper-case letter sorts lower by ordinal value. Deciding it the way the
        // shared-folder comparison does, case-insensitively, would leave the pair unordered.
        Assert.Equal(
            ["Aa/Dupe.md", "aa/Dupe.md"],
            Ranked("Note.md", "aa/Dupe.md", "Aa/Dupe.md"));
    }

    // ---- What ranking returns ----

    [Fact]
    public void EveryCandidateComesBack()
    {
        // `Notes/Dupe.md` is inside the linking note's subtree and the other two are outside it, so
        // it comes first. Between those two, "../Aa/Dupe.md" is thirteen characters and
        // "../Aa/Bb/Cc/Dupe.md" is nineteen, so the shorter spelling comes next.
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
