using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// The two primitives taking two paths - the file form and the folder form, split for the same
/// reason removal is, because a path does not say which it names. What is load-bearing: an occupied destination refused rather
/// than overwritten, the carve-out that keeps a case-only rename from reading as a collision and
/// reports it so a later git pickup can stage it, a destination parent that must already be there,
/// and the name grammar on the one segment the move brings into existence.
/// </summary>
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
        // A path does not say which of the two it names, so both spellings satisfy the grammar and
        // the caller's own kind assertion is what decides above.
        _vault.WithNote("Inbox/Widget.md", "# Widget\n").WithFolder("Projects");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget")));
    }

    [Fact]
    public void Move_DoesNotCheckTheSourceName()
    {
        // A file predating the grammar can be renamed into conformance.
        _vault.WithNote("Inbox/old widget.md", "# Widget\n").WithFolder("Projects");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/old widget.md"), _vault.ToWrite("Projects/Widget.md")));
    }

    [Fact]
    public void Move_RefusesTwoPathsFromDifferentVaults()
    {
        // A pair that does not agree is a layer defect rather than a caller's answer.
        _vault.WithNote("Inbox/Widget.md", "# Widget\n");
        using var elsewhere = new ScratchVault();

        Assert.Throws<ArgumentException>(() =>
            VaultFiles.MoveFile(_vault.ToWrite("Inbox/Widget.md"), elsewhere.ToWrite("Widget.md")));
    }

    [Fact]
    public void Move_RelocatesDirectlyUnderTheRoot()
    {
        // A path with one segment has no parent to name, the root being a vault rather than a path.
        _vault.WithNote("Widget.md", "# Widget\n");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFile(_vault.ToWrite("Widget.md"), _vault.ToWrite("Gizmo.md")));

        Assert.True(_vault.HasFile("Gizmo.md"));
    }

    // ---- The folder form ----

    /// <summary>
    /// One directory rename, with everything inside carried across under names that do not change.
    /// That is what keeps a folder relocation from having a half-moved state of its own, and what
    /// keeps this primitive from having to create the folders a per-file relocation would need on
    /// the far side.
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

    /// <summary>The destination side is established before the attempt, because a directory rename
    /// reports a missing source and a missing destination parent as the same platform failure and
    /// the two are different answers.</summary>
    [Fact]
    public void MoveFolder_TellsAMissingSourceFromAMissingDestinationParent()
    {
        _vault.WithFolder("Projects/Widget");

        Assert.IsType<MoveOutcome.MissingParent>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Nowhere/Gizmo")));

        Assert.IsType<MoveOutcome.SourceMissing>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Absent"), _vault.ToWrite("Projects/Gizmo")));
    }

    /// <summary>A case-only folder rename is allowed and reported, exactly as a file's is: git does
    /// not see it under the platform's default case handling.</summary>
    [Fact]
    public void MoveFolder_ReportsACaseOnlyRename()
    {
        _vault.WithNote("Projects/Widget/Notes.md", "# Notes\n");

        var outcome = Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Projects/WIDGET")));

        Assert.True(outcome.CaseOnly);
        Assert.Equal("WIDGET", _vault.SpellingOf("Projects/Widget"));
    }

    /// <summary>The destination is the one segment brought into existence, and a folder's name is the
    /// grammar alone - so a note-shaped destination is refused rather than creating a folder that
    /// carries an extension.</summary>
    [Fact]
    public void MoveFolder_ChecksTheDestinationAgainstTheFolderGrammar()
    {
        _vault.WithFolder("Projects/Widget");

        Assert.IsType<MoveOutcome.InvalidName>(
            VaultFiles.MoveFolder(_vault.ToWrite("Projects/Widget"), _vault.ToWrite("Projects/Gizmo.md")));
    }

    // ---- The placeholder ----

    /// <summary>
    /// The one producer of the file no caller can name: the perimeter refuses a dot segment and the
    /// grammar refuses the spelling, both deliberately, so the placeholder is composed from the
    /// folder's own write proof rather than from caller text.
    /// </summary>
    [Fact]
    public void CreateGitKeep_SeedsAnEmptyPlaceholderInsideTheFolder()
    {
        _vault.WithFolder("Projects/Held");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.CreateGitKeep(_vault.ToWrite("Projects/Held")));

        Assert.True(_vault.HasFile("Projects/Held/.gitkeep"));
        Assert.Empty(_vault.BytesOf("Projects/Held/.gitkeep"));
    }

    /// <summary>It is a creating write like any other, so an existing placeholder is the filesystem's
    /// own answer rather than a check run first.</summary>
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
