using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests <c>WikilinkTarget.Address</c>, which measures a link's target from the vault root before
/// anything tries to resolve it.
/// </summary>
/// <remarks>
/// The linking note is <c>S/T/Spellings.md</c> throughout, which is where every spelling here was
/// measured against Obsidian on 2026-08-04. These cases assert the address alone; what each address
/// then opens is asserted by the census tests, which resolve against a real vault.
///
/// Nothing here touches the filesystem, so the vault is a value rather than a real directory tree.
/// </remarks>
public sealed class WikilinkAnchoringTests
{
    private static readonly Vault Vault = new("test", Path.Combine(Path.GetTempPath(), "no-such-vault"), PathPolicy.None);

    private static VaultPath At(string relativePath) =>
        VaultPath.TryCreate(Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    private static string? Address(string target, string linkingNote = "S/T/Spellings.md") =>
        WikilinkTarget.Address(target, At(linkingNote));

    // ---- Targets that are not anchored ----

    [Theory]
    [InlineData("Delta")]
    [InlineData("Delta.md")]
    [InlineData("S/T/Delta")]
    public void AnUnanchoredTargetIsAlreadyMeasuredFromTheVaultRoot(string target)
    {
        // Returned exactly as written, so every target the editor writes today keeps the address it
        // has today, character for character.
        Assert.Equal(target, Address(target));
    }

    [Theory]
    [InlineData("S/../Delta")]
    [InlineData("..Delta")]
    [InlineData(".\\Delta")]
    [InlineData(".hidden/Delta")]
    public void OnlyALeadingComponentDecidesAnAnchor(string target)
    {
        // Interior dot-segments, a dot-prefixed folder name and a backslash are ordinary characters
        // that no file's path contains, so each of these comes back untouched and goes on to name
        // nothing. Folding them into an anchor would make links resolve that the editor leaves
        // unresolved.
        Assert.Equal(target, Address(target));
    }

    // ---- Anchored at the vault root ----

    [Fact]
    public void ALeadingSeparatorAnchorsAtTheVaultRoot()
    {
        Assert.Equal("Delta", Address("/Delta"));
        Assert.Equal("S/T/Delta", Address("/S/T/Delta"));
    }

    // ---- Anchored at the linking note's folder ----

    [Fact]
    public void ALeadingDotAnchorsAtTheLinkingNotesOwnFolder()
    {
        Assert.Equal("S/T/Delta", Address("./Delta"));
        Assert.Equal("S/T/Delta.md", Address("./Delta.md"));
    }

    [Fact]
    public void EachDotDotClimbsOneFolder()
    {
        Assert.Equal("S/Delta", Address("../Delta"));
        Assert.Equal("Delta", Address("../../Delta"));
    }

    [Fact]
    public void ClimbingPastTheVaultRootIsIgnored()
    {
        // Two folders deep and four climbs, which lands at the root rather than failing. A target
        // that over-climbs still names a file, and one that named nothing would be repaired by no
        // relocation and reported by no census.
        Assert.Equal("Delta", Address("../../../../Delta"));
        Assert.Equal("Delta", Address("../Delta", "Root.md"));
    }

    [Fact]
    public void AnAnchoredTargetIsMeasuredFromTheNoteGiven()
    {
        // The same spelling addresses a different file from a different folder, which is what makes
        // the linking note a parameter rather than something the address could be computed without.
        Assert.Equal("S/T/Delta", Address("./Delta"));
        Assert.Equal("Zed/Delta", Address("./Delta", "Zed/Other.md"));
    }

    // ---- Spellings that name no file ----

    [Theory]
    [InlineData("//Delta")]
    [InlineData("./Delta/")]
    [InlineData("./")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("../")]
    public void AnEmptyComponentAnywhereButTheAnchorNamesNothing(string target)
    {
        // Only the empty text before a leading separator is an anchor. Every other empty component,
        // including a trailing one, leaves a target the editor does not resolve.
        Assert.Null(Address(target));
    }

    [Fact]
    public void AnAnchorThatClimbsToTheRootAndNamesNothingElseNamesNothing()
    {
        Assert.Null(Address("..", "Root.md"));
    }

    // ---- Spellings that address a folder ----

    [Fact]
    public void ATargetAddressingAFolderComesBackAsThatFolder()
    {
        // `.` and `..` are unresolved in the editor, and they need no rule of their own to be: each
        // addresses a folder, and no note bears a folder's path. Adding a special case here would
        // state the same outcome twice and let the two statements drift.
        Assert.Equal("S/T", Address("."));
        Assert.Equal("S", Address(".."));
    }
}
