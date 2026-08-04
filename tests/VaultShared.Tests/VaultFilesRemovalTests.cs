using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// Tests <c>VaultFiles.RemoveFile</c> and <c>VaultFiles.RemoveEmptyFolder</c>.
/// </summary>
/// <remarks>
/// A path does not say whether it names a file or a folder, so neither operation guesses: each
/// removes only what it expects and fails on the other. Both refuse from the attempt rather than
/// from a check run first, and neither walks a tree. A caller removing a tree holds the validated
/// plan and drives it entry by entry.
/// </remarks>
public sealed class VaultFilesRemovalTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    // ---- Removing a file ----

    [Fact]
    public void RemoveFile_RemovesTheNote()
    {
        _vault.WithNote("Projects/Widget.md", "# Widget\n");

        Assert.IsType<RemovalOutcome.Removed>(VaultFiles.RemoveFile(_vault.ToWrite("Projects/Widget.md")));

        Assert.False(_vault.HasFile("Projects/Widget.md"));
        Assert.True(_vault.HasFolder("Projects"));
    }

    [Fact]
    public void RemoveFile_NothingThereToRemoveIsItsOwnAnswer()
    {
        // A removal runs against a plan built a moment earlier, so an entry that vanished in the
        // meantime must be a different outcome from one that could not be removed.
        _vault.WithFolder("Projects");

        var outcome = Assert.IsType<RemovalOutcome.NotFound>(VaultFiles.RemoveFile(_vault.ToWrite("Projects/Widget.md")));

        Assert.Equal("Projects/Widget.md", outcome.Path);
    }

    [Fact]
    public void RemoveFile_AMissingFolderOnTheWayIsAlsoNothingThereToRemove()
    {
        Assert.IsType<RemovalOutcome.NotFound>(VaultFiles.RemoveFile(_vault.ToWrite("Projects/Widget.md")));
    }

    [Fact]
    public void RemoveFile_RefusesAFolderAndLeavesItAlone()
    {
        _vault.WithNote("Projects/Widget/Widget.md", "# Widget\n");

        var outcome = Assert.IsType<RemovalOutcome.Failed>(VaultFiles.RemoveFile(_vault.ToWrite("Projects/Widget")));

        Assert.Equal("Projects/Widget", outcome.Path);
        Assert.True(_vault.HasFolder("Projects/Widget"));
        Assert.True(_vault.HasFile("Projects/Widget/Widget.md"));
    }

    [Fact]
    public void RemoveFile_AFailureReasonCarriesNoAbsolutePath()
    {
        _vault.WithFolder("Projects/Widget");

        var outcome = Assert.IsType<RemovalOutcome.Failed>(VaultFiles.RemoveFile(_vault.ToWrite("Projects/Widget")));

        Assert.DoesNotContain(_vault.Root, outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Removing an empty folder ----

    [Fact]
    public void RemoveEmptyFolder_RemovesAFolderWithNothingInIt()
    {
        _vault.WithFolder("Projects/Widget");

        Assert.IsType<RemovalOutcome.Removed>(VaultFiles.RemoveEmptyFolder(_vault.ToWrite("Projects/Widget")));

        Assert.False(_vault.HasFolder("Projects/Widget"));
        Assert.True(_vault.HasFolder("Projects"));
    }

    [Fact]
    public void RemoveEmptyFolder_RefusesAFolderHoldingAFileAndDestroysNothing()
    {
        // The refusal comes from the removal attempt, not from an emptiness check run first.
        _vault.WithNote("Projects/Widget/Widget.md", "# Widget\n");

        var outcome = Assert.IsType<RemovalOutcome.NotEmpty>(VaultFiles.RemoveEmptyFolder(_vault.ToWrite("Projects/Widget")));

        Assert.Equal("Projects/Widget", outcome.Path);
        Assert.True(_vault.HasFile("Projects/Widget/Widget.md"));
    }

    [Fact]
    public void RemoveEmptyFolder_RefusesAFolderHoldingAFolderAndNeverWalksTheTree()
    {
        _vault.WithFolder("Projects/Widget/Notes");

        Assert.IsType<RemovalOutcome.NotEmpty>(VaultFiles.RemoveEmptyFolder(_vault.ToWrite("Projects/Widget")));

        Assert.True(_vault.HasFolder("Projects/Widget/Notes"));
    }

    [Fact]
    public void RemoveEmptyFolder_NothingThereToRemoveIsItsOwnAnswer()
    {
        _vault.WithFolder("Projects");

        var outcome = Assert.IsType<RemovalOutcome.NotFound>(VaultFiles.RemoveEmptyFolder(_vault.ToWrite("Projects/Widget")));

        Assert.Equal("Projects/Widget", outcome.Path);
    }

    [Fact]
    public void RemoveEmptyFolder_RefusesAFileAndLeavesItAlone()
    {
        _vault.WithNote("Projects/Widget.md", "# Widget\n");

        Assert.IsType<RemovalOutcome.Failed>(VaultFiles.RemoveEmptyFolder(_vault.ToWrite("Projects/Widget.md")));

        Assert.True(_vault.HasFile("Projects/Widget.md"));
    }
}
