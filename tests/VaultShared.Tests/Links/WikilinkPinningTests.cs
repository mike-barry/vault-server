using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// Verifies the pinning pass a creation runs. Only a basename already in use triggers it, every
/// bare link the new note would re-aim is rewritten to the note it opens today, and no link is
/// ever aimed at the note being created.
/// </summary>
public sealed class WikilinkPinningTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    private LinkPassOutcome<WikilinkPlan> Creating(string path) =>
        WikilinkMaintenance.PlanCreation(_vault.Access.BypassingScope(), _vault.At(path));

    // ---- What triggers the pass ----

    [Fact]
    public void AUniqueBasenameRunsNoPassAtAll()
    {
        // A different outcome from a pass that ran and pinned nothing, which is what the creation
        // result's nullable pinned-links member distinguishes.
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        Assert.IsType<LinkPassOutcome<WikilinkPlan>.NotNeeded>(Creating("Aa/Widget.md"));
    }

    [Fact]
    public void ADuplicatedBasenameThatChangesNothingIsAPassThatPinnedNothing()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Zz/Note.md", "See [[Dupe]].\n");

        // The new note is further from the linking note than the one it already opens.
        var plan = LinkPass.Plan(Creating("Aa/Bb/Cc/Dupe.md"));

        Assert.True(plan.IsEmpty);
    }

    // ---- What is pinned ----

    [Fact]
    public void ABareLinkTheNewNoteWouldReAimIsPinnedToWhatItOpensNow()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# The one it opens now\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n")
            .WithNote("Zz/Other.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));

        // Only the link whose target would change is pinned; `Zz/Other.md` keeps opening its own
        // neighbour either way.
        Assert.Equal(["Aa/Note.md:1 Dupe -> Zz/Dupe"], LinkPass.Apply(_vault, plan));
        Assert.Equal("See [[Dupe]].\n", _vault.TextOf("Zz/Other.md"));
    }

    [Fact]
    public void APinIsAlwaysTheQualifiedForm()
    {
        // A pin has to name one file unambiguously, so it always writes the path-qualified form.
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));

        Assert.Equal("Zz/Dupe", plan.Rewrites.Single().To);
    }

    [Fact]
    public void APathQualifiedLinkKeepingItsTargetIsLeftAlone()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Zz/Dupe]].\n");

        Assert.True(LinkPass.Plan(Creating("Aa/Bb/Dupe.md")).IsEmpty);
    }

    // ---- Anchored targets ----

    [Fact]
    public void ARelativeLinkTheNewNoteWouldTakeIsPinned()
    {
        // `../Delta` addresses `S/Delta`, where nothing sits, so today it reaches `Zed/S/Delta.md`
        // by the character suffix rule. A note created at exactly that address takes the link, the
        // same way a note created at the path any other target spells does, so the link is pinned
        // to what it opens now.
        _vault
            .WithNote("Zed/S/Delta.md", "# The one it opens now\n")
            .WithNote("S/T/Spellings.md", "See [[../Delta]].\n");

        var plan = LinkPass.Plan(Creating("S/Delta.md"));

        Assert.Equal(["S/T/Spellings.md:1 ../Delta -> Zed/S/Delta"], LinkPass.Apply(_vault, plan));
    }

    [Fact]
    public void ARelativeLinkThatOpenedNothingIsNotPinned()
    {
        // The new note gives the link a target it never had. There is no earlier answer to
        // preserve, and pinning one would write the note the link does not open.
        _vault
            .WithNote("Delta.md", "# The root copy, which a relative target does not reach\n")
            .WithNote("S/T/Spellings.md", "See [[./Delta]].\n");

        Assert.True(LinkPass.Plan(Creating("S/T/Delta.md")).IsEmpty);
    }

    [Fact]
    public void APathQualifiedLinkIsPinnedWhereANoteIsCreatedAtThePathItSpells()
    {
        // The link reaches its note by a suffix of that note's path, so a note created at exactly
        // the path the target spells takes the link from it. Being path-qualified does not exempt
        // a link from pinning.
        _vault
            .WithNote("Deepish/Nest/Dupe.md", "# The one it opens now\n")
            .WithNote("Deepish/Note.md", "See [[Nest/Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Nest/Dupe.md"));

        Assert.Equal(["Deepish/Note.md:1 Nest/Dupe -> Deepish/Nest/Dupe"], LinkPass.Apply(_vault, plan));
    }

    // ---- The vault root ----

    [Fact]
    public void ACreationAtTheVaultRootPinsEveryBareLinkToTheDisplacedNote()
    {
        // A bare target spells the whole vault path of a root note, so a note created at the root
        // takes every bare link to its basename at once, from any distance. Ranking never comes
        // into it: the nearby link is the one a distance-only rule leaves unpinned.
        _vault
            .WithNote("Zz/Deep/Dupe.md", "# The one they open now\n")
            .WithNote("Zz/Deep/Near.md", "See [[Dupe]].\n")
            .WithNote("Aa/Far.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Dupe.md"));

        Assert.Equal(
            ["Aa/Far.md:1 Dupe -> Zz/Deep/Dupe", "Zz/Deep/Near.md:1 Dupe -> Zz/Deep/Dupe"],
            LinkPass.Apply(_vault, plan));
    }

    [Fact]
    public void ANoteAtTheVaultRootIsNotDisplacedByADeeperCreation()
    {
        // The link keeps naming the root note's whole path, so no ranking can take it and there is
        // nothing to pin. The pass still runs, because the basename is in use.
        _vault
            .WithNote("Dupe.md", "# The root copy\n")
            .WithNote("Aa/Bb/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));

        Assert.True(plan.IsEmpty);
        Assert.Equal("See [[Dupe]].\n", _vault.TextOf("Aa/Bb/Note.md"));
    }

    [Fact]
    public void APinNamesTheDisplacedNoteRatherThanTheRootNoteBeingCreated()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Zz/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Dupe.md"));

        Assert.Equal("Zz/Dupe", plan.Pins.Single().To);
    }

    // ---- Ordering against the note's creation ----

    [Fact]
    public void PinningIsComputedAndAppliedWithTheNoteStillUnwritten()
    {
        // Pinning runs before the note is created, so an interruption between the two leaves links
        // naming a target that still exists. Creating the note is the calling tool's own operation
        // and is scope-checked there.
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));
        LinkPass.Apply(_vault, plan);

        Assert.False(_vault.HasFile("Aa/Bb/Dupe.md"));
        Assert.Equal("See [[Zz/Dupe]].\n", _vault.TextOf("Aa/Note.md"));
    }
}
