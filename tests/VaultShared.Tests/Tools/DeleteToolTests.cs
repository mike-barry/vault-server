using System.Text.Json;
using VaultShared.Access;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>delete</c> over a real tree: the destruction plan the file census builds, the three guards,
/// the backlink census, the preview gate, and what a removal that stops partway reports.
/// </summary>
public sealed class DeleteToolTests : IDisposable
{
    private readonly ScratchVault _vault = Scratch();

    public void Dispose() => _vault.Dispose();

    private static ScratchVault Scratch(PathPolicy? policy = null, params string[] opClasses) =>
        new ScratchVault(opClasses: opClasses.Length > 0 ? opClasses : ["content:write"], policy: policy)
            .WithNote("Projects/Widget/Widget.md", "# Widget\n")
            .WithNote("Projects/Widget/Notes.md", "# Notes\n")
            .WithNote("Reference/Guide.md", "# Guide\n\nSee [[Widget]] and [[Projects/Widget/Notes]].\n")
            .WithNote("Projects/Widget/Assets/Diagram.png", "not a note")
            .WithFolder("Projects/Empty");

    private DeleteResult Delete(object arguments) => Delete(_vault, arguments);

    private static DeleteResult Delete(ScratchVault vault, object arguments) =>
        Assert.IsType<DeleteResult>(Assert.IsType<ToolResult.Data>(
            VaultToolHarness.Invoke(new DeleteTool(), DeleteTool.Declaration, vault, arguments)).Value);

    private ToolRefusal Refusal(object arguments) => Refusal(_vault, arguments);

    private static ToolRefusal Refusal(ScratchVault vault, object arguments) =>
        VaultToolHarness.Refusal(new DeleteTool(), DeleteTool.Declaration, vault, arguments);

    private static string[] Strings(ToolRefusal refusal, string member) =>
        [.. JsonDocument.Parse(VaultToolHarness.Data(refusal)![member]).RootElement.EnumerateArray().Select(v => v.GetString()!)];

    // ---- The declaration ----

    /// <summary>Removal takes no destructive class of its own: it is authorized as a mutation like
    /// any other.</summary>
    [Fact]
    public void DeclaresTheOrdinaryWriteFloorAndMutates()
    {
        Assert.Equal(new Floor.Class("content:write"), DeleteTool.Declaration.OpClassFloor);
        Assert.True(DeleteTool.Declaration.Mutates);
    }

    // ---- The preview gate ----

    /// <summary>The routine call reports and removes nothing. A plan is a faithful prediction of its
    /// own result, so this shape is the one the applied call answers.</summary>
    [Fact]
    public void PreviewsByDefaultAndRemovesNothing()
    {
        var plan = Delete(new { path = "Projects/Widget/Notes.md", kind = "note" });

        Assert.False(plan.Applied);
        Assert.Equal(["Projects/Widget/Notes.md"], plan.Removed);
        Assert.True(_vault.HasFile("Projects/Widget/Notes.md"));
    }

    /// <summary>
    /// The parity claim, whole: a plan and the result of the call it authorized differ in exactly one
    /// member. Everything else - the set, the census, the asserted authority - is computed the same
    /// way under the same lock and the same validation.
    /// </summary>
    [Fact]
    public void APlanAndItsResultDifferInExactlyTheDiscriminatingMember()
    {
        var plan = Delete(new { path = "Projects/Widget", kind = "folder", recursive = true });
        var applied = Delete(new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        VaultToolHarness.AssertPreviewParity(plan, applied);
        Assert.False(_vault.HasFolder("Projects/Widget"));
    }

    // ---- The destruction plan ----

    /// <summary>
    /// The plan comes from the file census, not note enumeration: a non-markdown file is still on
    /// disk and still destroyed, and a folder holding only such files would otherwise read as empty.
    /// Each folder's contents come before the folder, deepest first.
    /// </summary>
    [Fact]
    public void ThePlanIsEveryFileUnderTheFolderThenTheFoldersDeepestFirst()
    {
        var plan = Delete(new { path = "Projects/Widget", kind = "folder", recursive = true });

        Assert.Equal(
            [
                "Projects/Widget/Assets/Diagram.png",
                "Projects/Widget/Notes.md",
                "Projects/Widget/Widget.md",
                "Projects/Widget/Assets",
                "Projects/Widget",
            ],
            plan.Removed);
    }

    /// <summary>A note's plan is itself.</summary>
    [Fact]
    public void ANotesPlanIsItself()
    {
        Assert.Equal(["Reference/Guide.md"], Delete(new { path = "Reference/Guide.md", kind = "note" }).Removed);
    }

    /// <summary>A missing sole target is a failure, never a skip - and the preview says so rather
    /// than predicting a removal that cannot happen.</summary>
    [Fact]
    public void AMissingNoteIsAFailureEvenInThePreview()
    {
        Assert.Equal(ToolCodes.PathNotFound, Refusal(new { path = "Reference/Absent.md", kind = "note" }).Code);
    }

    // ---- The guards ----

    /// <summary>The first guard, run over the plan the census built with dot entries admitted -
    /// either a note-only enumeration or a policy filter would hide the thing the check exists to
    /// find.</summary>
    [Fact]
    public void AGitDirectoryAnywhereInTheSetRefusesTheWholeRemoval()
    {
        _vault.WithNote("Projects/Widget/Vendored/.git/HEAD", "ref: refs/heads/main\n");

        var refusal = Refusal(new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        Assert.Equal(ToolCodes.GitDirectoryPresent, refusal.Code);
        Assert.Equal(["Projects/Widget/Vendored/.git"], Strings(refusal, "paths"));
        Assert.True(_vault.HasFile("Projects/Widget/Widget.md"));
    }

    /// <summary>The second guard. The refusal carries the full set a recursive removal would
    /// destroy, so the caller retries against the actual file list rather than the folder name.</summary>
    [Fact]
    public void APlainFolderRemovalMeetingContentsRefusesCarryingThePlan()
    {
        var refusal = Refusal(new { path = "Projects/Widget", kind = "folder", apply = true });

        Assert.Equal(ToolCodes.NotEmpty, refusal.Code);
        Assert.Contains("Projects/Widget/Notes.md", Strings(refusal, "plan"));
        Assert.True(_vault.HasFile("Projects/Widget/Notes.md"));
    }

    [Fact]
    public void APlainFolderRemovalOfAnEmptyFolderIsTheFolderAlone()
    {
        var result = Delete(new { path = "Projects/Empty", kind = "folder", apply = true });

        Assert.Equal(["Projects/Empty"], result.Removed);
        Assert.False(_vault.HasFolder("Projects/Empty"));
    }

    /// <summary>The placeholder is the tooling's own rather than content a caller put there, so a
    /// folder holding nothing else counts as empty and a plain call removes both.</summary>
    [Fact]
    public void AFolderHoldingOnlyTheGitKeepCountsAsEmptyAndAPlainCallRemovesBoth()
    {
        _vault.WithNote("Projects/Held/.gitkeep", "");

        var result = Delete(new { path = "Projects/Held", kind = "folder", apply = true });

        Assert.Equal(["Projects/Held/.gitkeep", "Projects/Held"], result.Removed);
        Assert.False(_vault.HasFolder("Projects/Held"));
    }

    /// <summary>A second placeholder-like file is not the placeholder case: the folder holds
    /// content.</summary>
    [Fact]
    public void AFolderHoldingTheGitKeepAndSomethingElseIsNotEmpty()
    {
        _vault.WithNote("Projects/Held/.gitkeep", "").WithNote("Projects/Held/Thing.md", "# Thing\n");

        Assert.Equal(ToolCodes.NotEmpty, Refusal(new { path = "Projects/Held", kind = "folder", apply = true }).Code);
    }

    /// <summary>The third guard: the whole plan is validated before anything is removed, so a
    /// refusal cannot arrive with part of the work already done.</summary>
    [Fact]
    public void TheWholePlanIsValidatedBeforeAnythingIsRemoved()
    {
        var policy = Synthetic.Policy("""[ { "path": "Projects/Widget/Notes.md", "mutation_requires": "agentic:write" } ]""");
        using var denied = Scratch(policy, "content:write");

        var refusal = Refusal(denied, new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.Equal(["Projects/Widget/Notes.md"], Strings(refusal, "paths"));
        // Nothing was removed, including the entries whose own removal the policy never priced.
        Assert.True(denied.HasFile("Projects/Widget/Widget.md"));
        Assert.True(denied.HasFile("Projects/Widget/Assets/Diagram.png"));
    }

    /// <summary>There is no force, no purge and no skip: nothing in the argument surface waives a
    /// guard.</summary>
    [Fact]
    public void NoArgumentWaivesAGuard()
    {
        var advertised = NativeToolArguments.SchemaFor(typeof(DeleteArguments))
            .GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["path", "kind", "vault", "recursive", "apply"], advertised);
    }

    // ---- The backlink census ----

    /// <summary>Removal reports orphaning rather than repairing it, one entry per surviving
    /// occurrence, because a caller fixing them visits each one.</summary>
    [Fact]
    public void ReportsEverySurvivingWikilinkIntoTheRemovedSet()
    {
        var plan = Delete(new { path = "Projects/Widget", kind = "folder", recursive = true });

        Assert.Equal(
            ["Reference/Guide.md:3 Widget", "Reference/Guide.md:3 Projects/Widget/Notes"],
            plan.Backlinks.Select(b => $"{b.Path}:{b.Line} {b.Target}"));
        // Nothing was rewritten: the caller's intent for a surviving link is not knowable here.
        Assert.Contains("[[Widget]]", _vault.TextOf("Reference/Guide.md"));
    }

    /// <summary>A census that ran and found nothing is a finding, so the member is an empty list and
    /// never absent.</summary>
    [Fact]
    public void ACensusThatFoundNothingIsAnEmptyListRatherThanAnAbsence()
    {
        var plan = Delete(new { path = "Projects/Empty", kind = "folder" });

        Assert.NotNull(plan.Backlinks);
        Assert.Empty(plan.Backlinks);
    }

    // ---- Applying ----

    [Fact]
    public void RemovesANote()
    {
        var result = Delete(new { path = "Projects/Widget/Notes.md", kind = "note", apply = true });

        Assert.True(result.Applied);
        Assert.False(_vault.HasFile("Projects/Widget/Notes.md"));
        Assert.True(_vault.HasFile("Projects/Widget/Widget.md"));
    }

    /// <summary>A recursive removal destroys every dot entry under the folder it was given: the plan
    /// is the operation's footprint rather than the caller's perimeter.</summary>
    [Fact]
    public void ARecursiveRemovalDestroysTheDotEntriesUnderIt()
    {
        _vault.WithNote("Projects/Widget/.notes/Scratch.md", "# Scratch\n");

        var result = Delete(new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        Assert.Contains("Projects/Widget/.notes/Scratch.md", result.Removed);
        Assert.False(_vault.HasFolder("Projects/Widget"));
    }

    /// <summary>The mutex means no other tool interleaved; it does not mean rollback. A removal that
    /// stops partway names exactly what landed and what did not, and carries the ordered way out.</summary>
    [Fact]
    public void ARemovalThatStopsPartwayRaisesThePartialMutationVocabulary()
    {
        // Held open, so its removal fails after the entries ordered before it have already gone.
        using var held = _vault.Unreadable("Projects/Widget/Widget.md");

        var refusal = Refusal(new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        Assert.Equal(ToolCodes.PartialMutation, refusal.Code);
        Assert.Contains("Projects/Widget/Notes.md", Strings(refusal, "applied"));
        Assert.Contains("Projects/Widget/Widget.md", Strings(refusal, "not_applied"));
        Assert.NotEmpty(Strings(refusal, "recovery"));
        Assert.True(_vault.HasFile("Projects/Widget/Widget.md"));
    }

    /// <summary>With nothing yet removed the same condition is a clean refusal rather than a partial
    /// mutation: there is no partial state to report.</summary>
    [Fact]
    public void ARemovalThatFailsOnItsOnlyEntryIsACleanRefusal()
    {
        using var held = _vault.Unreadable("Projects/Widget/Notes.md");

        var refusal = Refusal(new { path = "Projects/Widget/Notes.md", kind = "note", apply = true });

        Assert.Equal(ToolCodes.WriteFailed, refusal.Code);
        Assert.True(_vault.HasFile("Projects/Widget/Notes.md"));
    }

    // ---- Operands ----

    [Fact]
    public void AFolderKindAssertionOnANotePathIsTheKindRefusal()
    {
        Assert.Equal(ToolCodes.WrongKind, Refusal(new { path = "Reference/Guide.md", kind = "folder" }).Code);
    }

    [Fact]
    public void ANoteKindAssertionOnAnExtensionlessPathIsRefusedWithTheSpelling()
    {
        var refusal = Refusal(new { path = "Reference/Guide", kind = "note" });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Reference/Guide.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }
}
