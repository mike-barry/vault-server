using VaultShared.Access;
using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// Repair on relocation: every link naming the relocated item is re-emitted canonically, every link
/// the move would have re-aimed is pinned instead, and nothing else in any note changes.
///
/// Real trees, because the pass enumerates and reads for real and the cases are about what it finds.
/// Each builds its own, since what is on disk is the case.
/// </summary>
public sealed class WikilinkRepairTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    private LinkPassOutcome<WikilinkPlan> Move(string from, string to) =>
        WikilinkMaintenance.PlanNoteRelocation(_vault.Access.BypassingScope(), _vault.At(from), _vault.At(to));

    private LinkPassOutcome<WikilinkPlan> MoveFolder(string from, string to) =>
        WikilinkMaintenance.PlanFolderRelocation(_vault.Access.BypassingScope(), _vault.At(from), _vault.At(to));

    // ---- Emission is canonical rather than minimal ----

    [Fact]
    public void APathQualifiedLinkToAUniqueNameCollapsesToBare()
    {
        // The editor's own emit rule: a deliberately qualified form is not preserved.
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
    public void APartialPathSuffixNamesTheRelocatedNoteAndAMidComponentOneDoesNot()
    {
        _vault
            .WithNote("Projects/Vault/Inbox/Widget.md", "# Widget\n")
            .WithNote("Other/Widget.md", "# Bystander\n")
            .WithNote("Notes/Suffix.md", "See [[Inbox/Widget]].\n")
            .WithNote("Notes/MidComponent.md", "See [[box/Widget]].\n");

        var plan = LinkPass.Plan(Move("Projects/Vault/Inbox/Widget.md", "Archive/Widget.md"));

        Assert.Equal(["Notes/Suffix.md:1 Inbox/Widget -> Archive/Widget"], LinkPass.Apply(_vault, plan));
        Assert.Equal("See [[box/Widget]].\n", _vault.TextOf("Notes/MidComponent.md"));
    }

    // ---- Never re-aim ----

    [Fact]
    public void ABystandersBareLinkIsPinnedWhereTheMoveWouldChangeWhatItOpens()
    {
        // Moving a same-named file closer to the linking note is the shape the editor protects
        // against. The bystander link is frozen on what it opens today; it is never redirected, and
        // nothing fails on the ambiguity.
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
        // Two notes carry the same bare link and it means different files in each, which is what
        // makes bare matching a resolution question rather than a name question.
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
    public void TheSubtreeCopyWinsADeadTieAsTheEditorRanksIt()
    {
        // `Aa/Bb/Dupe.md` and `Dupe.md` are both ten characters from `Aa/Note.md`, and the ordinal
        // tie-break on the vault-relative paths gives it to the subtree copy.
        _vault
            .WithNote("Aa/Bb/Dupe.md", "# Subtree copy\n")
            .WithNote("Dupe.md", "# Root copy\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Move("Aa/Bb/Dupe.md", "Aa/Bb/Renamed.md"));

        Assert.Equal(["Aa/Note.md:1 Dupe -> Renamed"], LinkPass.Apply(_vault, plan));
    }

    // ---- A folder relocation ----

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
        // The linking note moves too, so a link to a note that did not move can still be re-aimed
        // by the folder's own relocation. It is pinned like any other bystander.
        _vault
            .WithNote("Old/Note.md", "See [[Dupe]].\n")
            .WithNote("Aa/Dupe.md", "# The one it opens today\n")
            .WithNote("Zz/Dupe.md", "# The one it would open from the destination\n");

        var plan = LinkPass.Plan(MoveFolder("Old", "Zz/Deep"));

        Assert.Equal(["Old/Note.md:1 Dupe -> Aa/Dupe"], LinkPass.Apply(_vault, plan));
    }

    // ---- What the substitution does not touch ----

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
        // Extents index the text as the pass decoded it, terminators included, and the splice
        // happens in that same string - so no offset drifts past a carriage return. The stored
        // bytes come back with the layer's own line terminator, which is what every write does.
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

        // Nothing in the plan means nothing for the apply to write. A pass that ran and changed no
        // link is still a finding, which is why emptiness is a property rather than an absence.
        Assert.True(plan.IsEmpty);
        Assert.Empty(LinkPass.Apply(_vault, plan));
        Assert.Equal("Nothing to see.\n", _vault.TextOf("Notes/Untouched.md"));
    }

    // ---- The pass writes through the vault-wide pass ----

    [Fact]
    public void APlanNamesEveryNoteByThePathItHasNow()
    {
        // Which is why the consumer applies before it relocates: a note inside a moved folder is
        // written where the plan found it.
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
        // The lock means no other tool interleaved; it does not mean rollback, so the outcome is
        // what a partial-mutation refusal is composed from.
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
    /// A relocation does two different things to the graph, and the plan says which each rewrite
    /// was: a repair re-aims a link at where its target went, a pin freezes a link nobody asked
    /// about. The two are indistinguishable from the emitted text afterwards, so the discrimination
    /// is made where the decision is and carried out on the plan.
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

    /// <summary>Everything a creation plans is a pin: nothing moved, so no link is ever
    /// re-aimed.</summary>
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
