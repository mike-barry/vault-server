using System.Text.Json;
using VaultShared.Access;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the <c>delete</c> tool against a real directory tree: the removal plan built from the file
/// census, the three guards, the backlink census, the preview gate, and what a removal that stops
/// partway reports.
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

    /// <summary>Deletion has no op class of its own. It is authorized as an ordinary mutation.</summary>
    [Fact]
    public void DeclaresTheOrdinaryWriteFloorAndMutates()
    {
        Assert.Equal(new Floor.Class("content:write"), DeleteTool.Declaration.OpClassFloor);
        Assert.True(DeleteTool.Declaration.Mutates);
    }

    // ---- The preview gate ----

    /// <summary>A call without <c>apply</c> reports the plan and removes nothing. The plan has the
    /// same shape as the result the applied call returns.</summary>
    [Fact]
    public void PreviewsByDefaultAndRemovesNothing()
    {
        var plan = Delete(new { path = "Projects/Widget/Notes.md", kind = "note" });

        Assert.False(plan.Applied);
        Assert.Equal(["Projects/Widget/Notes.md"], plan.Removed);
        Assert.True(_vault.HasFile("Projects/Widget/Notes.md"));
    }

    /// <summary>
    /// A plan and the applied result differ only in the <c>Applied</c> flag. The removed set, the
    /// backlink census and the asserted authority are computed the same way, under the same lock
    /// and the same validation.
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
    /// The plan comes from a file census rather than from enumerating notes. A non-markdown file is
    /// on disk and is destroyed, and a folder holding only such files would otherwise look empty.
    /// Each folder's contents are listed before the folder itself, deepest first.
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

    /// <summary>A note's plan contains only the note itself.</summary>
    [Fact]
    public void ANotesPlanIsItself()
    {
        Assert.Equal(["Reference/Guide.md"], Delete(new { path = "Reference/Guide.md", kind = "note" }).Removed);
    }

    /// <summary>A missing target is refused rather than skipped, and the preview refuses too rather
    /// than predicting a removal that cannot happen.</summary>
    [Fact]
    public void AMissingNoteIsAFailureEvenInThePreview()
    {
        Assert.Equal(ToolCodes.PathNotFound, Refusal(new { path = "Reference/Absent.md", kind = "note" }).Code);
    }

    /// <summary>
    /// A missing folder is refused too. Path admission reads no filesystem and a folder holding no
    /// file is invisible to the census, so a folder that is not there would otherwise preview as
    /// clean and empty and refuse only once the caller authorized the removal. An extensionless
    /// file is not a folder and answers the same way.
    /// </summary>
    [Fact]
    public void AMissingFolderIsAFailureEvenInThePreview()
    {
        Assert.True(_vault.HasFolder("Projects/Widget"));
        var refusal = Refusal(new { path = "Projects/Widgets", kind = "folder" });

        Assert.Equal(ToolCodes.PathNotFound, refusal.Code);
        Assert.Contains("Projects/Widgets", refusal.Message);

        _vault.WithNote("Reference/Handbook", "not a folder");

        Assert.Equal(ToolCodes.PathNotFound, Refusal(new { path = "Reference/Handbook", kind = "folder" }).Code);
        Assert.True(_vault.HasFile("Reference/Handbook"));
    }

    // ---- The guards ----

    /// <summary>The first guard. It runs over the census with dot entries included, because a
    /// note-only enumeration or a policy filter would hide the <c>.git</c> directory it exists to
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

    /// <summary>The guard matches the path segment and never asks whether the entry is a directory.
    /// A <c>.git</c> file is the pointer a submodule or a linked worktree leaves behind, naming a
    /// repository with its own history just as a <c>.git</c> directory does, so it refuses the whole
    /// removal too.</summary>
    [Fact]
    public void AGitFileRefusesTheWholeRemovalTheSameWayAGitDirectoryDoes()
    {
        _vault.WithNote("Projects/Widget/Vendored/.git", "gitdir: ../../../.git/modules/Vendored\n");

        var refusal = Refusal(new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        Assert.Equal(ToolCodes.GitDirectoryPresent, refusal.Code);
        Assert.Equal(["Projects/Widget/Vendored/.git"], Strings(refusal, "paths"));
        Assert.True(_vault.HasFile("Projects/Widget/Vendored/.git"));
    }

    /// <summary>The second guard. A non-recursive folder removal that meets contents is refused,
    /// and the refusal carries the full set a recursive removal would destroy so the caller can
    /// retry knowing the file list.</summary>
    [Fact]
    public void APlainFolderRemovalMeetingContentsRefusesCarryingThePlan()
    {
        var refusal = Refusal(new { path = "Projects/Widget", kind = "folder", apply = true });

        Assert.Equal(ToolCodes.NotEmpty, refusal.Code);
        Assert.Equal(
            [
                "Projects/Widget/Assets/Diagram.png",
                "Projects/Widget/Notes.md",
                "Projects/Widget/Widget.md",
                "Projects/Widget/Assets",
                "Projects/Widget",
            ],
            Strings(refusal, "plan"));
        Assert.True(_vault.HasFile("Projects/Widget/Notes.md"));
    }

    [Fact]
    public void APlainFolderRemovalOfAnEmptyFolderIsTheFolderAlone()
    {
        var result = Delete(new { path = "Projects/Empty", kind = "folder", apply = true });

        Assert.Equal(["Projects/Empty"], result.Removed);
        Assert.False(_vault.HasFolder("Projects/Empty"));
    }

    /// <summary>A <c>.gitkeep</c> is the tooling's own placeholder rather than content a caller put
    /// there, so a folder holding nothing else counts as empty and a non-recursive removal takes
    /// both.</summary>
    [Fact]
    public void AFolderHoldingOnlyTheGitKeepCountsAsEmptyAndAPlainCallRemovesBoth()
    {
        _vault.WithNote("Projects/Held/.gitkeep", "");

        var result = Delete(new { path = "Projects/Held", kind = "folder", apply = true });

        Assert.Equal(["Projects/Held/.gitkeep", "Projects/Held"], result.Removed);
        Assert.False(_vault.HasFolder("Projects/Held"));
    }

    /// <summary>A folder holding a <c>.gitkeep</c> and anything else holds content, so it is not
    /// empty.</summary>
    [Fact]
    public void AFolderHoldingTheGitKeepAndSomethingElseIsNotEmpty()
    {
        _vault.WithNote("Projects/Held/.gitkeep", "").WithNote("Projects/Held/Thing.md", "# Thing\n");

        Assert.Equal(ToolCodes.NotEmpty, Refusal(new { path = "Projects/Held", kind = "folder", apply = true }).Code);
    }

    /// <summary>The exemption is the one name <c>.gitkeep</c> rather than a rule about dot-prefixed
    /// or hidden files. Any other dot-file is a caller's own content, so the folder holding it is
    /// not empty and a call that did not ask to recurse leaves it alone.</summary>
    [Fact]
    public void AFolderHoldingOnlySomeOtherDotFileIsNotEmpty()
    {
        _vault.WithNote("Projects/Held/.placeholder", "");

        Assert.Equal(ToolCodes.NotEmpty, Refusal(new { path = "Projects/Held", kind = "folder", apply = true }).Code);
        Assert.True(_vault.HasFile("Projects/Held/.placeholder"));
    }

    /// <summary>The third guard. The whole plan is authorized before anything is removed, so a
    /// refusal cannot arrive with part of the work already done.</summary>
    [Fact]
    public void TheWholePlanIsValidatedBeforeAnythingIsRemoved()
    {
        var policy = Synthetic.Policy("""[ { "path": "Projects/Widget/Notes.md", "mutation_requires": "agentic:write" } ]""");
        using var denied = Scratch(policy, "content:write");

        var refusal = Refusal(denied, new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.Equal(["Projects/Widget/Notes.md"], Strings(refusal, "paths"));
        // Nothing was removed, including the entries the policy did not require elevation for.
        Assert.True(denied.HasFile("Projects/Widget/Widget.md"));
        Assert.True(denied.HasFile("Projects/Widget/Assets/Diagram.png"));
    }

    /// <summary>The advertised arguments are exactly these five. There is no force, purge or skip
    /// argument, so nothing a caller can send waives a guard.</summary>
    [Fact]
    public void NoArgumentWaivesAGuard()
    {
        var advertised = NativeToolArguments.SchemaFor(typeof(DeleteArguments))
            .GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["path", "kind", "vault", "recursive", "apply"], advertised);
    }

    // ---- The backlink census ----

    /// <summary>Deletion reports the links it orphans rather than repairing them, one entry per
    /// surviving occurrence.</summary>
    [Fact]
    public void ReportsEverySurvivingWikilinkIntoTheRemovedSet()
    {
        var plan = Delete(new { path = "Projects/Widget", kind = "folder", recursive = true });

        Assert.Equal(
            ["Reference/Guide.md:3 Widget", "Reference/Guide.md:3 Projects/Widget/Notes"],
            plan.Backlinks.Select(b => $"{b.Path}:{b.Line} {b.Target}"));
        // Nothing was rewritten; what the caller wants a surviving link to point at is not known here.
        Assert.Contains("[[Widget]]", _vault.TextOf("Reference/Guide.md"));
    }

    /// <summary>The census covers every note in the vault rather than the part the caller's grant
    /// reaches. A link from a note the caller cannot see is exactly the one the caller cannot find
    /// or fix on its own.</summary>
    [Fact]
    public void ReportsAWikilinkFromANoteOutsideTheCallersScope()
    {
        using var scoped = new ScratchVault(VaultScope.Of(["Projects"]), opClasses: ["content:write"])
            .WithNote("Projects/Widget/Notes.md", "# Notes\n")
            .WithNote("Reference/Guide.md", "# Guide\n\nSee [[Projects/Widget/Notes]].\n");

        // The grant does not admit this note for reading, so the census reporting it below cannot be
        // explained by the scoped surface reaching it.
        Assert.IsType<PathResolution<ReadPath>.OutOfScope>(scoped.Access.ForRead("Reference/Guide.md"));

        var plan = Delete(scoped, new { path = "Projects/Widget/Notes.md", kind = "note" });

        Assert.Equal(
            ["Reference/Guide.md:3 Projects/Widget/Notes"],
            plan.Backlinks.Select(b => $"{b.Path}:{b.Line} {b.Target}"));
    }

    /// <summary>A census that ran and found no backlinks reports an empty list, never a null one.</summary>
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

    /// <summary>A recursive removal destroys dot entries under the folder it was given. The plan
    /// covers everything on disk, not only what a caller could have named.</summary>
    [Fact]
    public void ARecursiveRemovalDestroysTheDotEntriesUnderIt()
    {
        _vault.WithNote("Projects/Widget/.notes/Scratch.md", "# Scratch\n");

        var result = Delete(new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        Assert.Contains("Projects/Widget/.notes/Scratch.md", result.Removed);
        Assert.False(_vault.HasFolder("Projects/Widget"));
    }

    /// <summary>The write lock keeps other tools out but does not roll anything back. A removal
    /// that stops partway reports which entries were removed, which were not, and the steps to
    /// finish by hand.</summary>
    [Fact]
    public void ARemovalThatStopsPartwayRaisesThePartialMutationVocabulary()
    {
        // Held open, so its removal fails after the entries ordered before it have already gone.
        using var held = _vault.Unreadable("Projects/Widget/Widget.md");

        var refusal = Refusal(new { path = "Projects/Widget", kind = "folder", recursive = true, apply = true });

        // Both sets are stated whole rather than sampled. The plan removes deeper entries first and
        // orders equal depths by path, so holding Widget.md open fixes exactly which entries went
        // and which are left, and an entry appearing in neither set or in both is the failure this
        // is looking for.
        Assert.Equal(ToolCodes.PartialMutation, refusal.Code);
        Assert.Equal<string[]>(
            ["Projects/Widget/Assets/Diagram.png", "Projects/Widget/Notes.md"],
            Strings(refusal, "applied"));
        Assert.Equal<string[]>(
            ["Projects/Widget/Widget.md", "Projects/Widget/Assets", "Projects/Widget"],
            Strings(refusal, "not_applied"));
        Assert.NotEmpty(Strings(refusal, "recovery"));
        Assert.True(_vault.HasFile("Projects/Widget/Widget.md"));
    }

    /// <summary>The same failure on the first entry is an ordinary refusal rather than a partial
    /// mutation, because nothing has been removed yet.</summary>
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
