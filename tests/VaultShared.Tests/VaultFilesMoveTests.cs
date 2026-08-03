using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// The one primitive taking two paths. What is load-bearing: an occupied destination refused rather
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
            VaultFiles.Move(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.False(outcome.CaseOnly);
        Assert.False(_vault.HasFile("Inbox/Widget.md"));
        Assert.Equal("# Widget\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Move_RenamesInPlace()
    {
        _vault.WithNote("Projects/Widget.md", "# Widget\n");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.Move(_vault.ToWrite("Projects/Widget.md"), _vault.ToWrite("Projects/Gizmo.md")));

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
            VaultFiles.Move(_vault.ToWrite("Projects/widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.True(outcome.CaseOnly);
        Assert.Equal("Widget.md", _vault.SpellingOf("Projects/Widget.md"));
        Assert.Equal("# Widget\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Move_AnOccupiedDestinationIsRefusedAndNothingIsOverwritten()
    {
        _vault.WithNote("Inbox/Widget.md", "# Source\n").WithNote("Projects/Widget.md", "# Destination\n");

        var outcome = Assert.IsType<MoveOutcome.DestinationOccupied>(
            VaultFiles.Move(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.Equal("Projects/Widget.md", outcome.Path);
        Assert.Equal("# Source\n", _vault.TextOf("Inbox/Widget.md"));
        Assert.Equal("# Destination\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Move_AMissingDestinationParentIsRefusedAndNoFolderIsCreated()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n");

        var outcome = Assert.IsType<MoveOutcome.MissingParent>(
            VaultFiles.Move(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget/Widget.md")));

        Assert.Equal("Projects/Widget", outcome.Parent);
        Assert.False(_vault.HasFolder("Projects"));
        Assert.True(_vault.HasFile("Inbox/Widget.md"));
    }

    [Fact]
    public void Move_AMissingSourceIsItsOwnAnswer()
    {
        _vault.WithFolder("Inbox").WithFolder("Projects");

        var outcome = Assert.IsType<MoveOutcome.SourceMissing>(
            VaultFiles.Move(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget.md")));

        Assert.Equal("Inbox/Widget.md", outcome.Path);
    }

    [Fact]
    public void Move_RefusesADestinationNameOutsideTheGrammarAndMovesNothing()
    {
        _vault.WithNote("Inbox/Widget.md", "# Widget\n").WithFolder("Projects");

        var outcome = Assert.IsType<MoveOutcome.InvalidName>(
            VaultFiles.Move(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/new widget.md")));

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
            VaultFiles.Move(_vault.ToWrite("Inbox/Widget.md"), _vault.ToWrite("Projects/Widget")));
    }

    [Fact]
    public void Move_DoesNotCheckTheSourceName()
    {
        // A file predating the grammar can be renamed into conformance.
        _vault.WithNote("Inbox/old widget.md", "# Widget\n").WithFolder("Projects");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.Move(_vault.ToWrite("Inbox/old widget.md"), _vault.ToWrite("Projects/Widget.md")));
    }

    [Fact]
    public void Move_RefusesTwoPathsFromDifferentVaults()
    {
        // A pair that does not agree is a layer defect rather than a caller's answer.
        _vault.WithNote("Inbox/Widget.md", "# Widget\n");
        using var elsewhere = new ScratchVault();

        Assert.Throws<ArgumentException>(() =>
            VaultFiles.Move(_vault.ToWrite("Inbox/Widget.md"), elsewhere.ToWrite("Widget.md")));
    }

    [Fact]
    public void Move_RelocatesDirectlyUnderTheRoot()
    {
        // A path with one segment has no parent to name, the root being a vault rather than a path.
        _vault.WithNote("Widget.md", "# Widget\n");

        Assert.IsType<MoveOutcome.Moved>(
            VaultFiles.Move(_vault.ToWrite("Widget.md"), _vault.ToWrite("Gizmo.md")));

        Assert.True(_vault.HasFile("Gizmo.md"));
    }
}
