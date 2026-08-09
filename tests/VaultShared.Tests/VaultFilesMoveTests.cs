using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// Tests <c>VaultFiles.MoveFile</c> and <c>VaultFiles.MoveFolder</c>, which are separate because a
/// path does not say whether it names a file or a folder.
/// </summary>
/// <remarks>
/// The behaviour these tests pin: an occupied destination is refused rather than overwritten; a
/// case-only rename is allowed rather than read as a collision, and is reported so a caller can
/// stage it in git; a destination parent must already exist; and the name grammar is checked on
/// the one segment the move brings into existence.
/// </remarks>
public sealed class VaultFilesMoveTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    [Fact]
    public void Move_RelocatesTheNote()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n").WithFolder("Projects");

        var outcome = Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.False(outcome.CaseOnly);
        Assert.False(_vault.HasFile("Inbox/Widget.md"));
        Assert.Equal("# Widget\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Move_RenamesInPlace()
    {
        _vault.WithNote("Projects/Widget.md", "# Widget\n");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Projects/Widget.md"), _vault.ToWrite("Projects/Gizmo.md")));

        Assert.True(_vault.HasFile("Projects/Gizmo.md"));
        Assert.False(_vault.HasFile("Projects/Widget.md"));
    }

    [Fact]
    public void Move_ACaseOnlyRenameIsNotACollisionAndSaysSo()
    {
        // On a case-insensitive filesystem the destination already exists - it is the source. The
        // outcome reports the rename was case-only, because git does not see the change under the
        // platform's default case handling and repository operations are outside this layer.
        _vault.WithNote("Projects/widget.md", "# Widget\n");

        var outcome = Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Projects/widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.True(outcome.CaseOnly);
        Assert.Equal("Widget.md", _vault.SpellingOf("Projects/Widget.md"));
        Assert.Equal("# Widget\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Move_AnOccupiedDestinationIsRefusedAndNothingIsOverwritten()
    {
        _vault.WithNote("Inbox/Widget.md", "# Source\n").WithNote("Projects/Widget.md", "# Destination\n");

        var outcome = Assert.IsType<MoveOutcome.DestinationOccupied>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.Equal("Projects/Widget.md", outcome.Path);
        Assert.Equal("# Source\n", _vault.TextOf("Inbox/Widget.md"));
        Assert.Equal("# Destination\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Move_AMissingDestinationParentIsRefusedAndNoFolderIsCreated()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n");

        var outcome = Assert.IsType<MoveOutcome.MissingParent>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget/Widget.md")));

        Assert.Equal("Projects/Widget", outcome.Parent);
        Assert.False(_vault.HasFolder("Projects"));
        Assert.True(_vault.HasFile("Inbox/Widget.md"));
    }

    [Fact]
    public void Move_AMissingSourceIsItsOwnAnswer()
    {
        _vault.WithFolder("Inbox").WithFolder("Projects");

        var outcome = Assert.IsType<MoveOutcome.SourceMissing>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.Equal("Inbox/Widget.md", outcome.Path);
    }

    [Fact]
    public void Move_RefusesADestinationNameOutsideTheGrammarAndMovesNothing()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n").WithFolder("Projects");

        var outcome = Assert.IsType<MoveOutcome.InvalidName>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/new widget.md")));

        Assert.Equal("new widget.md", outcome.Name);
        Assert.True(_vault.HasFile("Inbox/Widget.md"));
    }

    [Fact]
    public void Move_TakesADestinationNamedAsANoteOrAsAFolder()
    {
        // A path does not say whether it names a file or a folder, so both spellings satisfy the
        // grammar here and the caller decides which it meant at a higher layer.
        _vault.WithNote("Inbox/Widget.md", "# Widget\n").WithFolder("Projects");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget")));
    }

    [Fact]
    public void Move_DoesNotCheckTheSourceName()
    {
        // A file whose name predates the grammar can be renamed into conformance.
        _vault.WithNote("Inbox/old widget.md", "# Widget\n").WithFolder("Projects");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/old widget.md"), _vault.ToWrite("Projects/Widget.md")));
    }

    [Fact]
    public void Move_RefusesTwoPathsFromDifferentVaults()
    {
        // Two paths from different vaults is a defect in the calling layer, not an answer a caller
        // can be given, so it throws.
        _vault.WithNote("Inbox/Widget.md", "# Widget\n");
        using var elsewhere = new ScratchVault();

        Assert.Throws<ArgumentException>(() =>
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), elsewhere.ToWrite("Widget.md")));
    }

    [Fact]
    public void Move_RelocatesDirectlyUnderTheRoot()
    {
        // A path with one segment has no parent to check, because the vault root is not a path.
        _vault.WithNote("Widget.md", "# Widget\n");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Widget.md"), _vault.ToWrite("Gizmo.md")));

        Assert.True(_vault.HasFile("Gizmo.md"));
        Assert.False(_vault.HasFile("Widget.md"));
    }

    // ---- Moving a folder ----

    /// <summary>
    /// Checks that a folder move is one directory rename, carrying everything inside across
    /// unchanged. A single rename has no half-moved state, and it does not have to create the
    /// destination folders a file-by-file relocation would need.
    /// </summary>
    [Fact]
    public void MoveFolder_RelocatesTheFolderAndEverythingUnderIt()
    {
        _vault.WithNote("Projects/Widget/Notes.md", "# Notes\n")
            .WithNote("Projects/Widget/Deep/Thing.md", "# Thing\n")
            .WithFolder("Archive");

        var outcome = Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Archive/Gizmo")));

        Assert.False(outcome.CaseOnly);
        Assert.False(_vault.HasFolder("Projects/Widget"));
        Assert.Equal("# Notes\n", _vault.TextOf("Archive/Gizmo/Notes.md"));
        Assert.Equal("# Thing\n", _vault.TextOf("Archive/Gizmo/Deep/Thing.md"));
    }

    [Fact]
    public void MoveFolder_RefusesAnOccupiedDestination()
    {
        _vault.WithFolder("Projects/Widget").WithFolder("Archive/Gizmo");

        Assert.IsType<MoveOutcome.DestinationOccupied>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Archive/Gizmo")));

        Assert.True(_vault.HasFolder("Projects/Widget"));
    }

    /// <summary>
    /// Checks that a missing source and a missing destination parent give different outcomes. The
    /// platform reports both as the same failure from a directory rename, so the destination
    /// parent is checked before the rename is attempted.
    /// </summary>
    [Fact]
    public void MoveFolder_TellsAMissingSourceFromAMissingDestinationParent()
    {
        _vault.WithFolder("Projects/Widget");

        Assert.IsType<MoveOutcome.MissingParent>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Nowhere/Gizmo")));

        Assert.IsType<MoveOutcome.SourceMissing>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Absent"), _vault.ToWrite("Projects/Gizmo")));
    }

    /// <summary>
    /// Checks that a case-only folder rename is allowed and reported, the same as for a file,
    /// because git does not see the change under the platform's default case handling.
    /// </summary>
    [Fact]
    public void MoveFolder_ReportsACaseOnlyRename()
    {
        _vault.WithNote("Projects/Widget/Notes.md", "# Notes\n");

        var outcome = Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Projects/WIDGET")));

        Assert.True(outcome.CaseOnly);
        Assert.Equal("WIDGET", _vault.SpellingOf("Projects/Widget"));
    }

    /// <summary>
    /// Checks that the destination segment is validated as a folder name, so a destination spelled
    /// like a note is refused rather than creating a folder with an extension.
    /// </summary>
    [Fact]
    public void MoveFolder_ChecksTheDestinationAgainstTheFolderGrammar()
    {
        _vault.WithFolder("Projects/Widget");

        Assert.IsType<MoveOutcome.InvalidName>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Projects/Gizmo.md")));
    }

    // ---- Reading a move's refusals before the move ----

    /// <summary>
    /// Checks that the probe answers the same three refusals the move itself answers, so a caller
    /// with work to do first is not made to do it for a move that cannot happen.
    /// </summary>
    [Fact]
    public void Preflight_AnswersTheOccupiedDestinationTheMoveWouldAnswer()
    {
        _vault.WithNote("Inbox/Widget.md", "# Source\n").WithNote("Projects/Widget.md", "# Destination\n");

        var outcome = Assert.IsType<MoveOutcome.DestinationOccupied>(
            VaultFiles.PreflightMoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.Equal("Projects/Widget.md", outcome.Path);
        Assert.Equal("# Destination\n", _vault.TextOf("Projects/Widget.md"));
        Assert.True(_vault.HasFile("Inbox/Widget.md"));
    }

    [Fact]
    public void Preflight_NamesAMissingDestinationParent()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n");

        var outcome = Assert.IsType<MoveOutcome.MissingParent>(
            VaultFiles.PreflightMoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget/Widget.md")));

        Assert.Equal("Projects/Widget", outcome.Parent);
        Assert.False(_vault.HasFolder("Projects"));
    }

    [Fact]
    public void Preflight_RefusesADestinationNameOutsideTheGrammar()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n").WithFolder("Projects");

        var outcome = Assert.IsType<MoveOutcome.InvalidName>(
            VaultFiles.PreflightMoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/new widget.md")));

        Assert.Equal("new widget.md", outcome.Name);
    }

    [Fact]
    public void Preflight_AnswersNothingForAMoveThatCanHappen()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n").WithFolder("Projects");

        Assert.Null(VaultFiles.PreflightMoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));
    }

    /// <summary>
    /// Checks that the source under another spelling is not an occupant, so a case-only rename is
    /// not refused before it is attempted. This matters on a case-insensitive file system, where the
    /// destination of such a rename already exists.
    /// </summary>
    [Fact]
    public void Preflight_DoesNotReadACaseOnlyRenameAsAnOccupiedDestination()
    {
        _vault.WithNote("Projects/widget.md", "# Widget\n");

        Assert.Null(VaultFiles.PreflightMoveFile(_vault.ToWrite("Projects/widget.md"), _vault.ToWrite("Projects/Widget.md")));
    }

    [Fact]
    public void PreflightFolder_AnswersTheOccupiedDestinationAndAllowsACaseOnlyRename()
    {
        _vault.WithFolder("Projects/Widget").WithFolder("Archive/Gizmo");

        Assert.IsType<MoveOutcome.DestinationOccupied>(
            VaultFiles.PreflightMoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Archive/Gizmo")));

        Assert.Null(VaultFiles.PreflightMoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Projects/WIDGET")));
    }

    /// <summary>
    /// Checks that a folder that is not there is reported ahead of every reason about the
    /// destination. Both are true of this call, and a caller whose operand is missing has to fix
    /// that before anything about where it was going matters.
    /// </summary>
    [Fact]
    public void PreflightFolder_ReportsAMissingSourceAheadOfAnOccupiedDestination()
    {
        _vault.WithFolder("Archive/Gizmo");

        var outcome = Assert.IsType<MoveOutcome.SourceMissing>(
            VaultFiles.PreflightMoveFolder(_vault.ToWrite("Projects/Absent"), _vault.ToWrite("Archive/Gizmo")));

        Assert.Equal("Projects/Absent", outcome.Path);
    }

    /// <summary>
    /// Checks that the file form reports nothing about its source. A file that is present and
    /// cannot be read is a different answer from one that is not there, and only reading it tells
    /// those apart, so the caller establishes the source itself.
    /// </summary>
    [Fact]
    public void Preflight_SaysNothingAboutAMissingSource()
    {
        _vault.WithFolder("Projects");

        Assert.Null(VaultFiles.PreflightMoveFile(_vault.ToWrite("Inbox/Absent.md"), _vault.ToWrite("Projects/Widget.md")));
    }

    /// <summary>
    /// Checks that a folder destination is held to the folder grammar alone, unlike a file
    /// destination, which may be spelled either way.
    /// </summary>
    [Fact]
    public void PreflightFolder_ChecksTheDestinationAgainstTheFolderGrammar()
    {
        _vault.WithFolder("Projects/Widget");

        Assert.IsType<MoveOutcome.InvalidName>(
            VaultFiles.PreflightMoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Projects/Gizmo.md")));
    }

    /// <summary>
    /// Checks that a file at the destination refuses a folder move. A folder move that ran would
    /// fail on it, and the probe reports the same answer.
    /// </summary>
    [Fact]
    public void PreflightFolder_CountsAFileAtTheDestinationAsAnOccupant()
    {
        _vault.WithFolder("Projects/Widget").WithNote("Archive/Gizmo", "# Not a folder\n");

        Assert.IsType<MoveOutcome.DestinationOccupied>(
            VaultFiles.PreflightMoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Archive/Gizmo")));
    }

    // ---- The .gitkeep placeholder ----

    /// <summary>
    /// Checks that <c>CreateGitKeep</c> writes an empty .gitkeep inside the folder. No caller can
    /// name that file, because the perimeter refuses a dot segment and the name grammar refuses
    /// the spelling, so this method derives the path from the folder's own write path instead of
    /// from caller text.
    /// </summary>
    [Fact]
    public void CreateGitKeep_SeedsAnEmptyPlaceholderInsideTheFolder()
    {
        _vault.WithFolder("Projects/Held");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.CreateGitKeep(_vault.ToWrite("Projects/Held")));

        Assert.True(_vault.HasFile("Projects/Held/.gitkeep"));
        Assert.Empty(_vault.BytesOf("Projects/Held/.gitkeep"));
    }

    /// <summary>
    /// Checks that an existing .gitkeep is refused. This is a creating write like any other, so
    /// the refusal comes from the atomic create rather than from an existence check run first.
    /// </summary>
    [Fact]
    public void CreateGitKeep_RefusesAnExistingPlaceholderFromTheAtomicCreate()
    {
        _vault.WithNote("Projects/Held/.gitkeep", "");

        Assert.IsType<WriteOutcome.AlreadyExists>(VaultFiles.CreateGitKeep(_vault.ToWrite("Projects/Held")));
    }

    [Fact]
    public void CreateGitKeep_NamesAMissingFolderInVaultRelativeTerms()
    {
        var missing = Assert.IsType<WriteOutcome.MissingParent>(VaultFiles.CreateGitKeep(_vault.ToWrite("Projects/Absent")));

        Assert.Equal("Projects/Absent", missing.Parent);
    }
}
