using System.Text.Json;
using VaultShared.Access;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the <c>move</c> tool against a real directory tree: the note and folder forms, the
/// repair-before-move order, the preview gate, the two kinds of path policy rule a relocation is
/// priced by, and what a move that stops partway reports.
/// </summary>
public sealed class MoveToolTests : IDisposable
{
    private readonly ScratchVault _vault = Scratch();

    public void Dispose() => _vault.Dispose();

    private static ScratchVault Scratch(PathPolicy? policy = null, params string[] opClasses) =>
        new ScratchVault(opClasses: opClasses.Length > 0 ? opClasses : ["content:write"], policy: policy)
            .WithNote("Projects/Widget/Widget.md", "# Widget\n\nSee [[Notes]].\n")
            .WithNote("Projects/Widget/Notes.md", "# Notes\n")
            .WithNote("Reference/Guide.md", "# Guide\n\nSee [[Widget]].\n")
            .WithFolder("Archive");

    private MoveResult Move(object arguments) => Move(_vault, arguments);

    private static MoveResult Move(ScratchVault vault, object arguments) =>
        Assert.IsType<MoveResult>(Assert.IsType<ToolResult.Data>(
            VaultToolHarness.Invoke(new MoveTool(), MoveTool.Declaration, vault, arguments)).Value);

    private ToolRefusal Refusal(object arguments) => Refusal(_vault, arguments);

    private static ToolRefusal Refusal(ScratchVault vault, object arguments) =>
        VaultToolHarness.Refusal(new MoveTool(), MoveTool.Declaration, vault, arguments);

    private static string[] Strings(ToolRefusal refusal, string member) =>
        [.. JsonDocument.Parse(VaultToolHarness.Data(refusal)![member]).RootElement.EnumerateArray().Select(v => v.GetString()!)];

    private static string[] Rendered(IEnumerable<VaultShared.Links.LinkRewrite> rewrites) =>
        [.. rewrites.Select(r => $"{r.Path}:{r.Line} {r.From} -> {r.To}").Order(StringComparer.Ordinal)];

    // ---- The declaration ----

    [Fact]
    public void DeclaresAWriteFloorAndMutates()
    {
        Assert.Equal(new Floor.Class("content:write"), MoveTool.Declaration.OpClassFloor);
        Assert.True(MoveTool.Declaration.Mutates);
    }

    // ---- The preview gate ----

    [Fact]
    public void PreviewsByDefaultAndMovesNothing()
    {
        var plan = Move(new { path = "Projects/Widget/Notes.md", destination = "Archive/Notes.md" });

        Assert.False(plan.Applied);
        Assert.True(_vault.HasFile("Projects/Widget/Notes.md"));
        Assert.False(_vault.HasFile("Archive/Notes.md"));
        // No link was repaired either; the plan only reports what it would rewrite.
        Assert.Equal("# Widget\n\nSee [[Notes]].\n", _vault.TextOf("Projects/Widget/Widget.md"));
    }

    /// <summary>
    /// A plan and the applied result differ only in the <c>Applied</c> flag. Every path-valued
    /// member of a plan names the path the file will have after the move, not the one it has now,
    /// which is what makes the two comparable.
    /// </summary>
    [Fact]
    public void APlanAndItsResultDifferInExactlyTheDiscriminatingMember()
    {
        using var previewed = Scratch();
        using var performed = Scratch();

        var plan = Move(previewed, new { path = "Projects/Widget/Notes.md", destination = "Archive/Notes.md" });
        var applied = Move(performed, new { path = "Projects/Widget/Notes.md", destination = "Archive/Notes.md", apply = true });

        VaultToolHarness.AssertPreviewParity(plan, applied);
        Assert.True(performed.HasFile("Archive/Notes.md"));
    }

    /// <summary>The same holds for the folder form, whose plan carries a relocation map for every
    /// contained note plus the folder note's rename.</summary>
    [Fact]
    public void AFolderPlanAndItsResultDifferInExactlyTheDiscriminatingMember()
    {
        using var previewed = Scratch();
        using var performed = Scratch();

        var plan = Move(previewed, new { path = "Projects/Widget", destination = "Archive/Gizmo" });
        var applied = Move(performed, new { path = "Projects/Widget", destination = "Archive/Gizmo", apply = true });

        VaultToolHarness.AssertPreviewParity(plan, applied);
    }

    // ---- The note form ----

    [Fact]
    public void MovesANoteAndAnswersTheHashAReadOfItReturns()
    {
        var result = Move(new { path = "Projects/Widget/Notes.md", destination = "Archive/Notes.md", apply = true });

        Assert.True(result.Applied);
        Assert.Equal("Archive/Notes.md", result.Path);
        Assert.Equal(["Archive/Notes.md"], result.Moved);
        Assert.False(_vault.HasFile("Projects/Widget/Notes.md"));
        Assert.Equal(
            Assert.IsType<ReadOutcome.Content>(VaultFiles.Read(_vault.ToRead("Archive/Notes.md"))).ContentHash,
            result.ContentHash);
    }

    /// <summary>Links that named the moved note are repaired to follow it. Repaired and pinned links
    /// are reported separately: a repair follows the moved note, a pin protects an unrelated
    /// link.</summary>
    [Fact]
    public void RepairsLinksThatNamedTheMovedNote()
    {
        var result = Move(new { path = "Projects/Widget/Notes.md", destination = "Archive/Jottings.md", apply = true });

        Assert.Equal(["Projects/Widget/Widget.md:3 Notes -> Jottings"], Rendered(result.RepairedLinks));
        Assert.Contains("[[Jottings]]", _vault.TextOf("Projects/Widget/Widget.md"));

        // A move always runs the pinning pass, so the member is a list rather than null - empty
        // here, because no other link could have been re-aimed.
        Assert.NotNull(result.PinnedLinks);
        Assert.Empty(result.PinnedLinks);
    }

    /// <summary>
    /// A link the move does not target, but whose resolution the move would change, is rewritten to
    /// name what it resolves to today and reported as pinned rather than repaired.
    /// </summary>
    [Fact]
    public void ABystanderLinkTheMoveWouldReAimIsPinnedRatherThanRepaired()
    {
        // The link in Inbox/Note.md names a note that does not move. The rename puts a nearer
        // note of the same name beside it, which would silently change what the link opens.
        using var vault = new ScratchVault(opClasses: ["content:write"])
            .WithNote("Reference/Guide.md", "# The guide\n")
            .WithNote("Inbox/Note.md", "# Note\n\nSee [[Guide]].\n")
            .WithNote("Inbox/Draft.md", "# Draft\n");

        var result = Move(vault, new { path = "Inbox/Draft.md", destination = "Inbox/Guide.md", apply = true });

        // A pin never redirects: the link still opens the note it opened before the move.
        Assert.Empty(result.RepairedLinks);
        Assert.Equal(["Inbox/Note.md:3 Guide -> Reference/Guide"], Rendered(result.PinnedLinks!));
        Assert.Contains("[[Reference/Guide]]", vault.TextOf("Inbox/Note.md"));
    }

    /// <summary>
    /// Link repairs are written before the files move, because the repair pass locates links by the
    /// paths the notes still have. A move that then fails leaves those repairs in place and reports
    /// the partial state.
    /// </summary>
    [Fact]
    public void RepairIsWrittenBeforeTheMoveAndAFailedMoveReportsThePartialState()
    {
        // The destination is occupied, so the move cannot land, but the links were repaired first.
        _vault.WithNote("Archive/Notes.md", "# Occupant\n");

        var refusal = Refusal(new { path = "Projects/Widget/Notes.md", destination = "Archive/Notes.md", apply = true });

        Assert.Equal(ToolCodes.PartialMutation, refusal.Code);
        Assert.Contains("already there", refusal.Message, StringComparison.Ordinal);
        Assert.NotEmpty(Strings(refusal, "applied"));
        Assert.Contains("move Projects/Widget/Notes.md -> Archive/Notes.md", Strings(refusal, "not_applied"));
        Assert.NotEmpty(Strings(refusal, "recovery"));
        Assert.True(_vault.HasFile("Projects/Widget/Notes.md"));
        Assert.Equal("# Occupant\n", _vault.TextOf("Archive/Notes.md"));
    }

    /// <summary>When no link needed repairing there is no partial state, so an occupied destination
    /// is reported as an ordinary already-exists refusal.</summary>
    [Fact]
    public void AnOccupiedDestinationWithNothingToRepairIsACleanRefusal()
    {
        _vault.WithNote("Archive/Guide.md", "# Occupant\n");

        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Archive/Guide.md", apply = true });

        Assert.Equal(ToolCodes.AlreadyExists, refusal.Code);
        Assert.Equal("# Occupant\n", _vault.TextOf("Archive/Guide.md"));
    }

    [Fact]
    public void AMissingDestinationParentIsNamedInVaultRelativeTerms()
    {
        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Nowhere/Deep/Guide.md", apply = true });

        Assert.Equal(ToolCodes.ParentMissing, refusal.Code);
        Assert.Equal("Nowhere/Deep", VaultToolHarness.Data(refusal)!["parent"]);
    }

    [Fact]
    public void AMissingSourceIsAFailureEvenInThePreview()
    {
        Assert.Equal(ToolCodes.PathNotFound, Refusal(new { path = "Reference/Absent.md", destination = "Archive/Absent.md" }).Code);
    }

    /// <summary>A rename that changes only letter case is performed and flagged in the result. Git
    /// does not notice such a rename on a case-insensitive file system, and nothing here runs git,
    /// so the flag is how a caller learns it has to stage the rename itself.</summary>
    [Fact]
    public void ACaseOnlyRenameIsPerformedAndReported()
    {
        var result = Move(new { path = "Reference/Guide.md", destination = "Reference/GUIDE.md", apply = true });

        Assert.True(result.CaseOnly);
        Assert.Equal("GUIDE.md", _vault.SpellingOf("Reference/Guide.md"));
    }

    /// <summary>A destination identical to the source moves nothing, and is refused as a bad
    /// argument rather than handed to the file system.</summary>
    [Fact]
    public void ADestinationIdenticalToTheSourceIsRefused()
    {
        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Reference/Guide.md" });

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        Assert.Equal("destination", VaultToolHarness.Data(refusal)!["argument"]);
    }

    // ---- The folder form ----

    /// <summary>
    /// A folder move is a single directory rename. The names of contained notes do not change,
    /// except for the folder note, which is renamed to match the folder's new name. The result
    /// lists the contained notes at their new paths.
    /// </summary>
    [Fact]
    public void MovesAFolderWithEverythingInItAndRenamesItsFolderNote()
    {
        var result = Move(new { path = "Projects/Widget", destination = "Archive/Gizmo", apply = true });

        Assert.Equal("Archive/Gizmo", result.Path);
        Assert.Equal("Archive/Gizmo/Gizmo.md", result.FolderNote);
        Assert.Equal(["Archive/Gizmo/Gizmo.md", "Archive/Gizmo/Notes.md"], result.Moved);
        Assert.Null(result.ContentHash);
        Assert.False(_vault.HasFolder("Projects/Widget"));
        Assert.True(_vault.HasFile("Archive/Gizmo/Notes.md"));
        // The folder note is renamed by the tool, using the relocation map the plan produced.
        Assert.True(_vault.HasFile("Archive/Gizmo/Gizmo.md"));
    }

    /// <summary>Moving a folder that has no folder note does not create one.</summary>
    [Fact]
    public void AFolderWithNoFolderNoteGetsNoneCreated()
    {
        _vault.WithNote("Projects/Loose/Thing.md", "# Thing\n");

        var result = Move(new { path = "Projects/Loose", destination = "Archive/Loose", apply = true });

        Assert.Null(result.FolderNote);
        Assert.False(_vault.HasFile("Archive/Loose/Loose.md"));
    }

    /// <summary>Links into a relocated folder are repaired to name where each note lands.</summary>
    [Fact]
    public void LinksIntoARelocatedFolderAreRepaired()
    {
        var result = Move(new { path = "Projects/Widget", destination = "Archive/Gizmo", apply = true });

        Assert.Equal(["Reference/Guide.md:3 Widget -> Gizmo"], Rendered(result.RepairedLinks));
        Assert.Contains("[[Gizmo]]", _vault.TextOf("Reference/Guide.md"));
    }

    [Fact]
    public void AFolderKindAssertionOnANotePathIsTheKindRefusal()
    {
        Assert.Equal(
            ToolCodes.WrongKind,
            Refusal(new { path = "Reference/Guide.md", destination = "Archive/Guide.md", kind = "folder" }).Code);
    }

    /// <summary>The extension rule applies to the destination as well as the source, so a note
    /// never lands at a path without the markdown extension.</summary>
    [Fact]
    public void ANoteDestinationCarryingNoExtensionIsRefusedWithTheSpelling()
    {
        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Archive/Guide" });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Archive/Guide.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>The same rule in reverse: a folder never lands at a path carrying the markdown
    /// extension.</summary>
    [Fact]
    public void AFolderDestinationCarryingTheExtensionIsTheKindRefusal()
    {
        Assert.Equal(ToolCodes.WrongKind, Refusal(new { path = "Projects/Widget", destination = "Archive/Gizmo.md" }).Code);
    }

    /// <summary>The destination is the only new name a move creates, so it is the one held to the
    /// name grammar. The refusal suggests a spelling that would have worked.</summary>
    [Fact]
    public void ADestinationOutsideTheNameGrammarIsRefusedNamingASpellingThatWouldWork()
    {
        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Archive/new guide.md", apply = true });

        Assert.Equal(ToolCodes.InvalidName, refusal.Code);
        Assert.Equal("New-guide.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    // ---- Path policy: path entries and structural rules ----

    /// <summary>A policy entry that names a path applies to a move out of that path as well as to a
    /// move into it. Otherwise the protection could be avoided by moving the file elsewhere,
    /// editing it, and moving it back.</summary>
    [Fact]
    public void APathEntryContributesWhereverTheRelocationGoes()
    {
        var policy = Synthetic.Policy("""[ { "path": "Reference", "mutation_requires": "agentic:write" } ]""");
        using var denied = Scratch(policy, "content:write");

        var refusal = Refusal(denied, new { path = "Reference/Guide.md", destination = "Archive/Guide.md" });

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.True(denied.HasFile("Reference/Guide.md"));

        using var granted = Scratch(policy, "content:write", "agentic:write");
        Assert.Equal(
            ["agentic:write"],
            Move(granted, new { path = "Reference/Guide.md", destination = "Archive/Guide.md" }).AssertedAuthority);
    }

    /// <summary>A structural rule applies where a move changes what a note is: renaming a note to
    /// match its containing folder makes it that folder's folder note.</summary>
    [Fact]
    public void AStructuralRuleContributesWhereANoteBecomesAFolderNote()
    {
        var policy = Synthetic.Policy("""[ { "rule": "folder_note", "mutation_requires": "agentic:write" } ]""");
        using var denied = Scratch(policy, "content:write");

        // Neither end is a folder note, so the rule adds nothing and the move needs only the floor.
        Assert.Empty(Move(denied, new { path = "Projects/Widget/Notes.md", destination = "Projects/Widget/Jottings.md" }).AssertedAuthority);

        using var acquiring = Scratch(policy, "content:write").WithFolder("Archive/Gizmo");
        Assert.Equal(
            ToolCodes.AuthorityRequired,
            Refusal(acquiring, new { path = "Reference/Guide.md", destination = "Archive/Gizmo/Gizmo.md" }).Code);
    }

    /// <summary>The rule adds nothing where the status does not change: a folder note carried along
    /// by its own folder's rename is still a folder note afterwards.</summary>
    [Fact]
    public void AFolderNoteCarriedByItsFoldersRenameContributesNothing()
    {
        var policy = Synthetic.Policy("""[ { "rule": "folder_note", "mutation_requires": "agentic:write" } ]""");
        using var vault = Scratch(policy, "content:write");

        var result = Move(vault, new { path = "Projects/Widget", destination = "Archive/Gizmo" });

        Assert.Equal("Archive/Gizmo/Gizmo.md", result.FolderNote);
        Assert.Empty(result.AssertedAuthority);
    }
}
