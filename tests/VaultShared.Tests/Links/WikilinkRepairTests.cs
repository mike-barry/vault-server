using VaultShared.Access;
using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// Verifies the repair pass a relocation runs: every link naming the moved item is re-emitted in
/// canonical form, every link the move would otherwise re-aim is pinned to its current target, and
/// nothing else in any note changes.
/// </summary>
/// <remarks>
/// Each test builds a real directory tree, because the pass enumerates and reads files and what is
/// on disk is what the test is about.
/// </remarks>
public sealed class WikilinkRepairTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    private LinkPassOutcome<WikilinkPlan> Move(string from, string to) =>
        WikilinkMaintenance.PlanNoteRelocation(_vault.Access.BypassingScope(), _vault.At(from), _vault.At(to));

    private LinkPassOutcome<WikilinkPlan> MoveFolder(string from, string to) =>
        WikilinkMaintenance.PlanFolderRelocation(_vault.Access.BypassingScope(), _vault.At(from), _vault.At(to));

    // ---- The vault root ----

    [Fact]
    public void RelocatingANoteToTheVaultRootPinsTheLinksItWouldTake()
    {
        // At the root the moved note's basename becomes its whole vault path, so every bare link
        // to that basename would follow it there. The bystanders are pinned to the note they open
        // now, and only the link naming the moved note is re-aimed.
        _vault
            .WithNote("Xx/Dupe.md", "# The one that moves\n")
            .WithNote("Zz/Deep/Dupe.md", "# The one they open now\n")
            .WithNote("Zz/Deep/Near.md", "See [[Dupe]].\n")
            .WithNote("Xx/Caller.md", "See [[Xx/Dupe]].\n");

        var plan = LinkPass.Plan(Move("Xx/Dupe.md", "Dupe.md"));

        Assert.Equal(["Zz/Deep/Near.md:1 Dupe -> Zz/Deep/Dupe"], LinkPass.Render(plan.Pins));
        Assert.Equal(["Xx/Caller.md:1 Xx/Dupe -> Dupe"], LinkPass.Render(plan.Repairs));
    }

    [Fact]
    public void RelocatingANoteAwayFromTheVaultRootReleasesTheLinksItHeld()
    {
        // Off the root the basename stops being a whole path, so the links it held fall to
        // ranking. They named the moved note, so they are repairs rather than pins.
        _vault
            .WithNote("Dupe.md", "# The root copy\n")
            .WithNote("Zz/Deep/Dupe.md", "# The other copy\n")
            .WithNote("Zz/Deep/Near.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Move("Dupe.md", "Xx/Dupe.md"));

        Assert.Equal(["Zz/Deep/Near.md:1 Dupe -> Xx/Dupe"], LinkPass.Render(plan.Repairs));
        Assert.Empty(plan.Pins);
    }

    // ---- Anchored targets ----

    [Fact]
    public void ARelativeLinkToTheRelocatedNoteIsRepairedLikeAnyOther()
    {
        // Before anchored targets were addressed at all, this link resolved to nothing, so the move
        // left it dangling and no census would have reported it. It names the moved note, so it is
        // a repair, and it is re-emitted in canonical form: the relative spelling is not preserved,
        // exactly as a deliberately path-qualified one is not.
        _vault
            .WithNote("Delta.md", "# The root copy\n")
            .WithNote("S/T/Delta.md", "# The one that moves\n")
            .WithNote("S/T/Spellings.md", "See [[./Delta]].\n");

        var plan = LinkPass.Plan(Move("S/T/Delta.md", "Zed/Delta.md"));

        Assert.Equal(["S/T/Spellings.md:1 ./Delta -> Zed/Delta"], LinkPass.Apply(_vault, plan));
        Assert.Empty(plan.Pins);
    }

    [Fact]
    public void ARelativeLinkThatMovesWithWhatItNamesIsStillRepaired()
    {
        // The link and the note it opens travel together, so it would still open the right note if
        // it were left alone. It is re-emitted anyway, because the canonical form of a link to a
        // duplicated basename is the full vault path and the editor would rewrite anything else.
        _vault
            .WithNote("Delta.md", "# The root copy\n")
            .WithNote("S/T/Delta.md", "# Moves with the folder\n")
            .WithNote("S/T/Spellings.md", "See [[./Delta]].\n");

        var plan = LinkPass.Plan(MoveFolder("S/T", "Zed/T"));

        Assert.Equal(["S/T/Spellings.md:1 ./Delta -> Zed/T/Delta"], LinkPass.Render(plan.Repairs));
    }

    [Fact]
    public void ARelativeLinkIsMeasuredFromWhereTheLinkingNoteWillBe()
    {
        // The bystander case, and the reason a target is addressed inside resolution rather than
        // once by the caller. `../Delta` reaches `Zed/S/Delta.md` by suffix from `S/T/`, and that
        // note does not move - but the linking note does, and from `Zed/T/` the same spelling
        // addresses `Zed/Delta` and reaches nothing. The link is therefore pinned to what it opens
        // today. Addressing the target once, against the folder the linking note is leaving, would
        // find the answer unchanged and leave the link silently re-aimed.
        _vault
            .WithNote("Zed/S/Delta.md", "# Named by suffix, and not moving\n")
            .WithNote("S/T/Spellings.md", "See [[../Delta]].\n");

        var plan = LinkPass.Plan(MoveFolder("S/T", "Zed/T"));

        Assert.Equal(["S/T/Spellings.md:1 ../Delta -> Zed/S/Delta"], LinkPass.Render(plan.Pins));
        Assert.Empty(plan.Repairs);
    }

    // ---- Canonical emission ----

    [Fact]
    public void APathQualifiedLinkToAUniqueNameCollapsesToBare()
    {
        // The canonical form follows the editor's emit rule, so a deliberately qualified link is
        // not preserved when the name is unique.
        _vault
            .WithNote("Projects/Widget.md", "# Widget\n")
            .WithNote("Notes/Bare.md", "See [[Widget]].\n")
            .WithNote("Notes/Qualified.md", "See [[Projects/Widget]].\n");

        var plan = LinkPass.Plan(Move("Projects/Widget.md", "Archive/Widget.md"));

        // The bare link already says what the canonical form says, so it is not a rewrite.
        Assert.Equal(["Notes/Qualified.md:1 Projects/Widget -> Widget"], LinkPass.Apply(_vault, plan));
        Assert.Equal("See [[Widget]].\n", _vault.TextOf("Notes/Bare.md"));
        Assert.Equal("See [[Widget]].\n", _vault.TextOf("Notes/Qualified.md"));
    }

    [Fact]
    public void AQualifiedBystanderIsNotSimplifiedWhenTheMoveRemovesTheAmbiguity()
    {
        // Ambiguity ratchets one way. After the move the basename is unique again and the bare form
        // would open the same note, but the link named something that did not move and its meaning
        // did not change, so the pass leaves it alone rather than canonicalizing every link it sees.
        _vault
            .WithNote("A/Dupe.md", "# Moves away under a new name\n")
            .WithNote("B/Dupe.md", "# The one the link names, and it does not move\n")
            .WithNote("Notes/Ref.md", "See [[B/Dupe]].\n");

        var plan = LinkPass.Plan(Move("A/Dupe.md", "A/Other.md"));

        Assert.Empty(plan.Rewrites);
        Assert.Equal("See [[B/Dupe]].\n", _vault.TextOf("Notes/Ref.md"));
    }

    [Fact]
    public void ANameStillDuplicatedAfterTheMoveIsEmittedAsTheFullVaultPath()
    {
        _vault
            .WithNote("A/Dupe.md", "# One\n")
            .WithNote("B/Dupe.md", "# Two\n")
            .WithNote("Notes/Ref.md", "See [[A/Dupe]].\n");

        var plan = LinkPass.Plan(Move("A/Dupe.md", "C/Dupe.md"));

        Assert.Equal(["Notes/Ref.md:1 A/Dupe -> C/Dupe"], LinkPass.Apply(_vault, plan));
    }

    [Fact]
    public void OnlyTheTargetIsRewrittenAndEveryDecorationSurvives()
    {
        _vault
            .WithNote("A/Dupe.md", "# One\n")
            .WithNote("B/Dupe.md", "# Two\n")
            .WithNote("Notes/Ref.md", "Embed ![[A/Dupe|Alias]] and ![[A/Dupe#Heading]].\n");

        var plan = LinkPass.Plan(Move("A/Dupe.md", "C/Dupe.md"));

        Assert.Equal(
            ["Notes/Ref.md:1 A/Dupe -> C/Dupe", "Notes/Ref.md:1 A/Dupe -> C/Dupe"],
            LinkPass.Apply(_vault, plan));
        Assert.Equal("Embed ![[C/Dupe|Alias]] and ![[C/Dupe#Heading]].\n", _vault.TextOf("Notes/Ref.md"));
    }

    [Fact]
    public void APartialPathSuffixNamesTheRelocatedNoteAndIsRepairedWithTheRest()
    {
        // A target reaches its note by a character suffix of the path, so a half-cut folder name
        // names it as surely as a whole one and is repaired the same way. The bystander is named by
        // neither target and keeps its own note.
        _vault
            .WithNote("Projects/Vault/Inbox/Widget.md", "# Widget\n")
            .WithNote("Other/Widget.md", "# Bystander\n")
            .WithNote("Notes/Suffix.md", "See [[Inbox/Widget]].\n")
            .WithNote("Notes/CutComponent.md", "See [[box/Widget]].\n");

        var plan = LinkPass.Plan(Move("Projects/Vault/Inbox/Widget.md", "Archive/Widget.md"));

        Assert.Equal(
            ["Notes/CutComponent.md:1 box/Widget -> Archive/Widget", "Notes/Suffix.md:1 Inbox/Widget -> Archive/Widget"],
            LinkPass.Apply(_vault, plan));
    }

    [Fact]
    public void ATargetNamingTheRelocatedNoteWithoutOpeningItIsLeftAlone()
    {
        // `Vault/Widget` names both notes, and opens the nearer one. The move does not touch what
        // the link opens, so re-aiming it at the destination would silently redirect it.
        _vault
            .WithNote("Projects/Vault/Widget.md", "# The one that moves\n")
            .WithNote("Notes/Vault/Widget.md", "# The one it opens\n")
            .WithNote("Notes/Note.md", "See [[Vault/Widget]].\n");

        var plan = LinkPass.Plan(Move("Projects/Vault/Widget.md", "Archive/Widget.md"));

        Assert.Empty(plan.Repairs);
        Assert.Equal("See [[Vault/Widget]].\n", _vault.TextOf("Notes/Note.md"));
    }

    // ---- Links are never re-aimed ----

    [Fact]
    public void ABystandersBareLinkIsPinnedWhereTheMoveWouldChangeWhatItOpens()
    {
        // Moving a same-named file closer to the linking note would change what a bare link opens.
        // The link is pinned to the note it opens today rather than redirected, and the ambiguity
        // does not fail the pass.
        _vault
            .WithNote("Far/Widget.md", "# The bystander\n")
            .WithNote("Other/Widget.md", "# The one that moves\n")
            .WithNote("Notes/Ref.md", "See [[Widget]].\n")
            .WithNote("Notes/Direct.md", "See [[Other/Widget]].\n");

        var plan = LinkPass.Plan(Move("Other/Widget.md", "Notes/Widget.md"));

        Assert.Equal(
            [
                "Notes/Direct.md:1 Other/Widget -> Notes/Widget",
                "Notes/Ref.md:1 Widget -> Far/Widget",
            ],
            LinkPass.Apply(_vault, plan));
    }

    [Fact]
    public void ABareLinkWhoseAnswerTheMoveDoesNotChangeIsLeftAlone()
    {
        _vault
            .WithNote("Far/Widget.md", "# The bystander\n")
            .WithNote("Other/Widget.md", "# The one that moves\n")
            .WithNote("Notes/Ref.md", "See [[Widget]].\n");

        // The move takes the other copy further away, so the nearest candidate is the one it
        // already was.
        var plan = LinkPass.Plan(Move("Other/Widget.md", "Other/Deeper/Still/Widget.md"));

        Assert.Empty(plan.Rewrites);
        Assert.Equal("See [[Widget]].\n", _vault.TextOf("Notes/Ref.md"));
    }

    [Fact]
    public void ABareLinkIsRewrittenOnlyWhereItResolvesToTheRelocatedNote()
    {
        // The same bare link text means a different file in each note, so matching a bare link is
        // a question of resolution rather than of the name alone.
        _vault
            .WithNote("Aa/Bb/Dupe.md", "# Subtree copy\n")
            .WithNote("Zz/Dupe.md", "# Elsewhere\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n")
            .WithNote("Zz/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Move("Aa/Bb/Dupe.md", "Aa/Bb/Renamed.md"));

        Assert.Equal(["Aa/Note.md:1 Dupe -> Renamed"], LinkPass.Apply(_vault, plan));
        Assert.Equal("See [[Dupe]].\n", _vault.TextOf("Zz/Note.md"));
    }

    [Fact]
    public void ADeadTieIsBrokenByOrdinalComparisonAsTheEditorRanksIt()
    {
        // `Bb/Dupe.md` and `Zz/Dupe.md` share no folder with `Aa/Note.md`, so neither is nearer and
        // the ordinal comparison of the vault paths decides which one the link names.
        _vault
            .WithNote("Bb/Dupe.md", "# The one it opens\n")
            .WithNote("Zz/Dupe.md", "# The other copy\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Move("Bb/Dupe.md", "Bb/Renamed.md"));

        Assert.Equal(["Aa/Note.md:1 Dupe -> Renamed"], LinkPass.Apply(_vault, plan));
    }

    // ---- Folder relocation ----

    [Fact]
    public void AFolderRelocationIsOnePrefixSubstitutionAndRenamesItsFolderNote()
    {
        _vault
            .WithNote("Old/Old.md", "The folder note.\n")
            .WithNote("Old/Inner.md", "See [[Old]].\n")
            .WithNote("Elsewhere/Inner.md", "# A same-named bystander\n")
            .WithNote("Notes/Ref.md", "See [[Old/Inner]] and [[Old/Old]].\n");

        var plan = LinkPass.Plan(MoveFolder("Old", "New"));

        Assert.Equal("New/New.md", plan.FolderNote?.Value);
        Assert.Equal(
            ["Old/Inner.md -> New/Inner.md", "Old/Old.md -> New/New.md"],
            plan.Relocated.Select(r => $"{r.From.Value} -> {r.To.Value}").Order(StringComparer.Ordinal));

        Assert.Equal(
            [
                "Notes/Ref.md:1 Old/Inner -> New/Inner",
                "Notes/Ref.md:1 Old/Old -> New",
                "Old/Inner.md:1 Old -> New",
            ],
            LinkPass.Apply(_vault, plan));
    }

    [Fact]
    public void AFolderWithNoFolderNoteGetsNoneCreated()
    {
        _vault
            .WithNote("Old/Inner.md", "# Contained\n")
            .WithNote("Notes/Ref.md", "See [[Old/Inner]].\n");

        var plan = LinkPass.Plan(MoveFolder("Old", "New"));

        Assert.Null(plan.FolderNote);
        // The name is unique after the move, so the canonical form is the short one.
        Assert.Equal(["Notes/Ref.md:1 Old/Inner -> Inner"], LinkPass.Apply(_vault, plan));
    }

    [Fact]
    public void AContainedNotesOwnLinksAreMeasuredFromWhereItWillBe()
    {
        // The linking note moves too, so a bare link to a note that did not move can still change
        // what it opens. It is pinned like any other bystander.
        _vault
            .WithNote("Old/Note.md", "See [[Dupe]].\n")
            .WithNote("Aa/Dupe.md", "# The one it opens today\n")
            .WithNote("Zz/Dupe.md", "# The one it would open from the destination\n");

        var plan = LinkPass.Plan(MoveFolder("Old", "Zz/Deep"));

        Assert.Equal(["Old/Note.md:1 Dupe -> Aa/Dupe"], LinkPass.Apply(_vault, plan));
    }

    // ---- Rewriting the note text ----

    [Fact]
    public void ANoteWhoseFrontmatterWillNotParseIsStillRepaired()
    {
        _vault
            .WithNote("A/Dupe.md", "# One\n")
            .WithNote("B/Dupe.md", "# Two\n")
            .WithNote("Notes/Broken.md", "---\nkey: [unclosed\nnav: \"[[A/Dupe]]\"\n---\n\nBody.\n");

        var plan = LinkPass.Plan(Move("A/Dupe.md", "C/Dupe.md"));

        Assert.Equal(["Notes/Broken.md:3 A/Dupe -> C/Dupe"], LinkPass.Apply(_vault, plan));
        Assert.Equal("---\nkey: [unclosed\nnav: \"[[C/Dupe]]\"\n---\n\nBody.\n", _vault.TextOf("Notes/Broken.md"));
    }

    [Fact]
    public void ANoteStoredWithTwoCharacterTerminatorsIsRepairedWithoutCorruption()
    {
        // Link extents index the text as the pass decoded it, terminators included, and the splice
        // happens in that same string, so no offset drifts past a carriage return. The note is
        // stored back with LF terminators, as every write in the access layer does.
        _vault
            .WithNote("A/Dupe.md", "# One\n")
            .WithNote("B/Dupe.md", "# Two\n")
            .WithNote("Notes/Crlf.md", "---\r\nnav: \"[[A/Dupe]]\"\r\n---\r\n\r\nSee [[A/Dupe]] twice.\r\n");

        var plan = LinkPass.Plan(Move("A/Dupe.md", "C/Dupe.md"));

        Assert.Equal(
            ["Notes/Crlf.md:2 A/Dupe -> C/Dupe", "Notes/Crlf.md:5 A/Dupe -> C/Dupe"],
            LinkPass.Apply(_vault, plan));
        Assert.Equal(
            "---\nnav: \"[[C/Dupe]]\"\n---\n\nSee [[C/Dupe]] twice.\n",
            _vault.TextOf("Notes/Crlf.md"));
    }

    [Fact]
    public void ANoteWithNothingToChangeIsNotWritten()
    {
        _vault
            .WithNote("Projects/Widget.md", "# Widget\n")
            .WithNote("Notes/Untouched.md", "Nothing to see.\n");

        var plan = LinkPass.Plan(Move("Projects/Widget.md", "Archive/Widget.md"));

        // An empty plan gives the apply nothing to write. A pass that ran and changed no link is
        // still reported, so emptiness is a property of the plan rather than a missing plan.
        Assert.True(plan.IsEmpty);
        Assert.Empty(LinkPass.Apply(_vault, plan));
        Assert.Equal("Nothing to see.\n", _vault.TextOf("Notes/Untouched.md"));
    }

    // ---- Applying a plan ----

    [Fact]
    public void APlanNamesEveryNoteByThePathItHasNow()
    {
        // A note inside a moved folder is written where the plan found it, so the caller must apply
        // the plan before it performs the relocation.
        _vault
            .WithNote("Old/Inner.md", "# Contained\n")
            .WithNote("Old/Ref.md", "See [[Old/Inner]].\n");

        var plan = LinkPass.Plan(MoveFolder("Old", "New"));

        Assert.Equal("Old/Ref.md", plan.Rewrites.Single().Path);
        LinkPass.Apply(_vault, plan);
        Assert.Equal("See [[Inner]].\n", _vault.TextOf("Old/Ref.md"));
    }

    [Fact]
    public void AWriteThatFailsNamesWhatLandedAndWhatDidNot()
    {
        // The lock keeps other tools from interleaving but does not roll anything back, so the
        // outcome lists what was written and what was not for the caller to report.
        _vault
            .WithNote("A/Dupe.md", "# One\n")
            .WithNote("B/Dupe.md", "# Two\n")
            .WithNote("Notes/Ref.md", "See [[A/Dupe]].\n");

        var plan = LinkPass.Plan(Move("A/Dupe.md", "C/Dupe.md"));
        File.Delete(_vault.Absolute("Notes/Ref.md"));

        var outcome = Assert.IsType<LinkApplyOutcome.Failed>(
            WikilinkMaintenance.Apply(_vault.Access.BypassingScope(), plan));

        Assert.Equal("Notes/Ref.md", outcome.Path);
        Assert.IsType<WriteOutcome.NotFound>(outcome.Outcome);
        Assert.Empty(outcome.Written);
        Assert.Equal(["Notes/Ref.md:1 A/Dupe -> C/Dupe"], outcome.Unwritten.Select(r => $"{r.Path}:{r.Line} {r.From} -> {r.To}"));
    }

    /// <summary>
    /// Checks that a plan separates its two kinds of rewrite. A repair re-aims a link at where its
    /// target went; a pin holds a bystander's link on the note it already opens. The emitted text
    /// looks the same either way, so the plan records which is which at the point of decision.
    /// </summary>
    [Fact]
    public void APlanSaysWhichOfItsRewritesWereRepairsAndWhichWerePins()
    {
        _vault
            .WithNote("Reference/Guide.md", "# The guide\n")
            .WithNote("Inbox/Note.md", "See [[Guide]] and [[Draft]].\n")
            .WithNote("Inbox/Draft.md", "# Draft\n");

        var plan = LinkPass.Plan(Move("Inbox/Draft.md", "Inbox/Guide.md"));

        Assert.Equal(["Inbox/Note.md:1 Draft -> Inbox/Guide"], LinkPass.Render(plan.Repairs));
        Assert.Equal(["Inbox/Note.md:1 Guide -> Reference/Guide"], LinkPass.Render(plan.Pins));
        Assert.Equal(plan.Rewrites.Count, plan.Repairs.Count + plan.Pins.Count);
    }

    /// <summary>
    /// Checks that a creation plans only pins. Nothing has moved, so no link is re-aimed.
    /// </summary>
    [Fact]
    public void ACreationsPlanIsAllPinsAndNoRepairs()
    {
        _vault
            .WithNote("Reference/Guide.md", "# The guide\n")
            .WithNote("Inbox/Note.md", "See [[Guide]].\n");

        var plan = LinkPass.Plan(
            WikilinkMaintenance.PlanCreation(_vault.Access.BypassingScope(), _vault.At("Inbox/Guide.md")));

        Assert.Empty(plan.Repairs);
        Assert.Equal(["Inbox/Note.md:1 Guide -> Reference/Guide"], LinkPass.Render(plan.Pins));
    }
}
