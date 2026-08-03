using System.Text.Json;
using VaultShared.Access;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>move</c> over a real tree: both kinds, the repair-before-move order, the preview gate, the two
/// artifact rules a relocation splits by, and what a move that stops partway reports.
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
        // Nothing was repaired either: the plan reports what it would rewrite.
        Assert.Equal("# Widget\n\nSee [[Notes]].\n", _vault.TextOf("Projects/Widget/Widget.md"));
    }

    /// <summary>
    /// The parity claim, whole. Every path-valued member names a post-mutation path on both, which is
    /// what lets a caller compare them at all - a plan naming pre-mutation paths would differ from
    /// its own result everywhere.
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

    /// <summary>The same claim for the folder form, whose plan carries a whole relocation map and a
    /// folder-note rename.</summary>
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

    /// <summary>Repair is what the move owed; pinning is protection it applied to links it was never
    /// asked about. The two are reported apart because they are different events.</summary>
    [Fact]
    public void RepairsLinksThatNamedTheMovedNote()
    {
        var result = Move(new { path = "Projects/Widget/Notes.md", destination = "Archive/Jottings.md", apply = true });

        Assert.Equal(["Projects/Widget/Widget.md:3 Notes -> Jottings"], Rendered(result.RepairedLinks));
        Assert.Contains("[[Jottings]]", _vault.TextOf("Projects/Widget/Widget.md"));

        // A relocation always runs the pass, so the pinned member is a list rather than an absence -
        // an empty one here, since nothing else could have been re-aimed.
        Assert.NotNull(result.PinnedLinks);
        Assert.Empty(result.PinnedLinks);
    }

    /// <summary>
    /// Pinning is the other half, and it is reported apart from repair: a link nobody asked about,
    /// frozen to what it opens today because the move would otherwise have changed the answer.
    /// </summary>
    [Fact]
    public void ABystanderLinkTheMoveWouldReAimIsPinnedRatherThanRepaired()
    {
        // The link in Inbox/Note.md names a note that does not move. The rename puts a nearer
        // same-named candidate beside it, which would silently change what the link opens.
        using var vault = new ScratchVault(opClasses: ["content:write"])
            .WithNote("Reference/Guide.md", "# The guide\n")
            .WithNote("Inbox/Note.md", "# Note\n\nSee [[Guide]].\n")
            .WithNote("Inbox/Draft.md", "# Draft\n");

        var result = Move(vault, new { path = "Inbox/Draft.md", destination = "Inbox/Guide.md", apply = true });

        // Pinning freezes and never redirects: the link still opens what it opened before.
        Assert.Empty(result.RepairedLinks);
        Assert.Equal(["Inbox/Note.md:3 Guide -> Reference/Guide"], Rendered(result.PinnedLinks!));
        Assert.Contains("[[Reference/Guide]]", vault.TextOf("Inbox/Note.md"));
    }

    /// <summary>
    /// The plan names every note by the path it has now, so the repair must be written before
    /// anything moves. A move that cannot land still leaves the repair behind - which is exactly the
    /// partial state the vocabulary exists to report.
    /// </summary>
    [Fact]
    public void RepairIsWrittenBeforeTheMoveAndAFailedMoveReportsThePartialState()
    {
        // The destination is occupied, so the relocation cannot land - but the graph was repaired
        // against it first.
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

    /// <summary>With nothing repaired there is no partial state, and the occupied-destination
    /// refusal stays distinguishable - a caller's next move after one is entirely different.</summary>
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

    /// <summary>Git does not see a case-only rename under the platform's default case handling and
    /// repository operations are outside this build, so the move reports it for a later pickup to
    /// stage.</summary>
    [Fact]
    public void ACaseOnlyRenameIsPerformedAndReported()
    {
        var result = Move(new { path = "Reference/Guide.md", destination = "Reference/GUIDE.md", apply = true });

        Assert.True(result.CaseOnly);
        Assert.Equal("GUIDE.md", _vault.SpellingOf("Reference/Guide.md"));
    }

    /// <summary>A destination identical to the source is not a rename at all, and is refused on the
    /// argument rather than left to the platform.</summary>
    [Fact]
    public void ADestinationIdenticalToTheSourceIsRefused()
    {
        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Reference/Guide.md" });

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        Assert.Equal("destination", VaultToolHarness.Data(refusal)!["argument"]);
    }

    // ---- The folder form ----

    /// <summary>
    /// One directory rename. Contained names do not change, so the folder note is the only name
    /// event: it is renamed to match its new folder, and the notes the folder carries are reported
    /// where they land.
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
        // The folder note's rename is the tool's, over the map the plan produced.
        Assert.True(_vault.HasFile("Archive/Gizmo/Gizmo.md"));
    }

    /// <summary>A folder that has no folder note gets none created.</summary>
    [Fact]
    public void AFolderWithNoFolderNoteGetsNoneCreated()
    {
        _vault.WithNote("Projects/Loose/Thing.md", "# Thing\n");

        var result = Move(new { path = "Projects/Loose", destination = "Archive/Loose", apply = true });

        Assert.Null(result.FolderNote);
        Assert.False(_vault.HasFile("Archive/Loose/Loose.md"));
    }

    /// <summary>Links into the folder are repaired against where every note lands.</summary>
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

    /// <summary>The extension rule runs on the constructed operand too: a note never lands under a
    /// folder-shaped name.</summary>
    [Fact]
    public void ANoteDestinationCarryingNoExtensionIsRefusedWithTheSpelling()
    {
        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Archive/Guide" });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Archive/Guide.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>And read the other way: a folder never acquires one.</summary>
    [Fact]
    public void AFolderDestinationCarryingTheExtensionIsTheKindRefusal()
    {
        Assert.Equal(ToolCodes.WrongKind, Refusal(new { path = "Projects/Widget", destination = "Archive/Gizmo.md" }).Code);
    }

    /// <summary>The destination is the one segment brought into existence, so it is the one held to
    /// the name grammar - and the refusal names a spelling that would have worked.</summary>
    [Fact]
    public void ADestinationOutsideTheNameGrammarIsRefusedNamingASpellingThatWouldWork()
    {
        var refusal = Refusal(new { path = "Reference/Guide.md", destination = "Archive/new guide.md", apply = true });

        Assert.Equal(ToolCodes.InvalidName, refusal.Code);
        Assert.Equal("New-guide.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    // ---- Path policy: the two artifact rules ----

    /// <summary>A path entry always contributes on relocation - otherwise the protection is trivially
    /// avoided by moving the file aside, editing it, and moving it back.</summary>
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

    /// <summary>A structural rule contributes where artifact status changes: a note renamed to match
    /// its folder acquires the shape.</summary>
    [Fact]
    public void AStructuralRuleContributesWhereANoteBecomesAFolderNote()
    {
        var policy = Synthetic.Policy("""[ { "rule": "folder_note", "mutation_requires": "agentic:write" } ]""");
        using var denied = Scratch(policy, "content:write");

        // Neither end is a folder note, so nothing is contributed and the move is priced at the floor.
        Assert.Empty(Move(denied, new { path = "Projects/Widget/Notes.md", destination = "Projects/Widget/Jottings.md" }).AssertedAuthority);

        using var acquiring = Scratch(policy, "content:write").WithFolder("Archive/Gizmo");
        Assert.Equal(
            ToolCodes.AuthorityRequired,
            Refusal(acquiring, new { path = "Reference/Guide.md", destination = "Archive/Gizmo/Gizmo.md" }).Code);
    }

    /// <summary>And does not contribute where it does not change: a folder note carried along by its
    /// own folder's rename is still a folder note.</summary>
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
