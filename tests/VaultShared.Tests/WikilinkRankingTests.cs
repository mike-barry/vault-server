using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// The editor's observed resolution rule (<c>Spec-Obsidian-Link-Behavior.md</c>), which is a
/// requirement rather than a design choice: the vault's existing content already depends on which
/// file a bare wikilink opens. These cases are the measurement written down, so a re-measurement
/// that changes the comparator has something to disagree with.
///
/// Ranking finds nothing itself and touches no filesystem, so the vault here is a value rather than
/// a tree and the candidate sets are handed in.
/// </summary>
public sealed class WikilinkRankingTests
{
    private static readonly Vault Vault = new("test", Path.Combine(Path.GetTempPath(), "no-such-vault"), PathPolicy.None);

    private static VaultPath At(string relativePath) =>
        VaultPath.TryCreate(Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    private static IReadOnlyList<string> Ranked(string linkingNote, params string[] candidates) =>
        [.. WikilinkRanking.Rank(At(linkingNote), candidates.Select(At)).Select(p => p.Value)];

    // ---- Nearest by relative path ----

    [Fact]
    public void TheCandidateInTheLinkingFilesOwnFolderIsNearest()
    {
        Assert.Equal(
            ["Notes/Dupe.md", "Other/Dupe.md"],
            Ranked("Notes/Note.md", "Other/Dupe.md", "Notes/Dupe.md"));
    }

    [Fact]
    public void TwoShortHopsBeatOneLongOne()
    {
        // Pure character count: `..` components carry no extra weight, so climbing twice out of a
        // deep folder beats one long descent. `../../Notes/Dupe.md` is nineteen characters and
        // `Reference-Material/Dupe.md` is twenty-six.
        Assert.Equal(
            ["Notes/Dupe.md", "Projects/Widget/Reference-Material/Dupe.md"],
            Ranked("Projects/Widget/Note.md", "Projects/Widget/Reference-Material/Dupe.md", "Notes/Dupe.md"));
    }

    [Fact]
    public void ResolutionIsRelativeRatherThanVaultGlobal()
    {
        // A deeper-nested candidate inside the linking file's own subtree beats a globally shorter
        // path elsewhere.
        Assert.Equal(
            ["Aa/Bb/Cc/Dd/Dupe.md", "Dupe.md"],
            Ranked("Aa/Bb/Cc/Note.md", "Dupe.md", "Aa/Bb/Cc/Dd/Dupe.md"));
    }

    // ---- The tie-break ----

    [Fact]
    public void ADeadTieIsBrokenByOrdinalComparisonOfTheVaultRelativePaths()
    {
        // `Bb/Dupe.md` and `../Dupe.md` are both ten characters. Comparing the relative forms would
        // hand the tie to the parent-folder copy, `..` sorting before letters; the vault-relative
        // comparison gives it to the subtree copy, which is what the editor does.
        Assert.Equal(
            ["Aa/Bb/Dupe.md", "Dupe.md"],
            Ranked("Aa/Note.md", "Dupe.md", "Aa/Bb/Dupe.md"));
    }

    [Fact]
    public void TheTieBreakIsOrdinalRatherThanCultural()
    {
        // Both are one hop down from the linking file's folder and equal in length.
        Assert.Equal(
            ["Aa/Dupe.md", "aa/Dupe.md"],
            Ranked("Note.md", "aa/Dupe.md", "Aa/Dupe.md"));
    }

    // ---- What ranking is ----

    [Fact]
    public void EveryCandidateComesBack()
    {
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
        // It orders the candidates it is handed, so an empty set resolves to nothing rather than to
        // whatever is on disk.
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
