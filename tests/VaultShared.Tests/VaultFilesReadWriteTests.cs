using System.Text;
using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// Tests reading, the two write forms, and folder creation, against a real directory tree.
/// </summary>
/// <remarks>
/// The behaviour these tests pin: an outcome is classified from the failure the filesystem raised
/// rather than from an existence check run first; no operation creates a parent folder it passes
/// through; and every write emits UTF-8 without a byte order mark and with line feeds.
/// </remarks>
public sealed class VaultFilesReadWriteTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    // ---- Reading ----

    [Fact]
    public void Read_ReturnsTheNotesContent()
    {
        _vault.WithNote("Projects/Widget.md", "# Widget\n\nBody.\n");

        var outcome = Assert.IsType<ReadOutcome.Content>(VaultFiles.Read(_vault.ToRead("Projects/Widget.md")));

        Assert.Equal("# Widget\n\nBody.\n", outcome.Text);
    }

    [Fact]
    public void Read_DecodesUtf8AndDropsBomBytes()
    {
        Directory.CreateDirectory(_vault.Absolute("Projects"));
        File.WriteAllText(_vault.Absolute("Projects/Widget.md"), "# Wídget\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var outcome = Assert.IsType<ReadOutcome.Content>(VaultFiles.Read(_vault.ToRead("Projects/Widget.md")));

        Assert.Equal("# Wídget\n", outcome.Text);
    }

    [Fact]
    public void Read_AMissingNoteIsNothingThereToRead()
    {
        _vault.WithFolder("Projects");

        var outcome = Assert.IsType<ReadOutcome.NotFound>(VaultFiles.Read(_vault.ToRead("Projects/Widget.md")));

        Assert.Equal("Projects/Widget.md", outcome.Path);
    }

    [Fact]
    public void Read_AMissingFolderOnTheWayIsAlsoNothingThereToRead()
    {
        Assert.IsType<ReadOutcome.NotFound>(VaultFiles.Read(_vault.ToRead("Projects/Widget.md")));
    }

    [Fact]
    public void Read_ThereAndSomethingWentWrongIsTheOtherAnswer()
    {
        // A folder is present where a note was named. "Not found" and "failed" are told apart by
        // the failure the filesystem raised, with no existence check run first, so a caller never
        // has to recover the distinction from message text.
        _vault.WithFolder("Projects/Widget.md");

        var outcome = Assert.IsType<ReadOutcome.Failed>(VaultFiles.Read(_vault.ToRead("Projects/Widget.md")));

        Assert.Equal("Projects/Widget.md", outcome.Path);
        Assert.NotEmpty(outcome.Reason);
    }

    [Fact]
    public void Read_ANoteAnotherHandleHoldsOpenIsAFailureRatherThanNothingThereToRead()
    {
        // A note that is on disk but cannot be opened right now is a failure, never an absence.
        // Widening the not-found case to every I/O error would report a note held by another writer
        // as gone and let a caller conclude the name is free.
        //
        // The mechanism differs by platform. On Windows the note is held with no sharing, which
        // raises a plain I/O error, and that is the case this assertion distinguishes from the two
        // not-found kinds. Elsewhere the note's mode bits are cleared instead, which raises an
        // access error that reaches the same failure classification by a different route.
        _vault.WithNote("Projects/Widget.md", "# Widget\n");
        using var held = _vault.Unreadable("Projects/Widget.md");

        var outcome = Assert.IsType<ReadOutcome.Failed>(VaultFiles.Read(_vault.ToRead("Projects/Widget.md")));

        Assert.Equal("Projects/Widget.md", outcome.Path);
        Assert.NotEmpty(outcome.Reason);
    }

    [Fact]
    public void Read_AFailureReasonCarriesNoAbsolutePath()
    {
        _vault.WithFolder("Projects/Widget.md");

        var outcome = Assert.IsType<ReadOutcome.Failed>(VaultFiles.Read(_vault.ToRead("Projects/Widget.md")));

        Assert.DoesNotContain(_vault.Root, outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Replacing ----

    [Fact]
    public void Replace_OverwritesExistingContentWholesale()
    {
        _vault.WithNote("Projects/Widget.md", "# Widget\n\nA long original body.\n");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.Replace(_vault.ToWrite("Projects/Widget.md"), "# New\n"));

        Assert.Equal("# New\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Replace_RefusesATargetThatIsNotThereAndCreatesNothing()
    {
        // Create is the form that brings a note into existence. If Replace also created, the two
        // forms would be the same operation.
        _vault.WithFolder("Projects");

        var outcome = Assert.IsType<WriteOutcome.NotFound>(VaultFiles.Replace(_vault.ToWrite("Projects/Widget.md"), "# New\n"));

        Assert.Equal("Projects/Widget.md", outcome.Path);
        Assert.False(_vault.HasFile("Projects/Widget.md"));
    }

    [Fact]
    public void Replace_DoesNotCheckTheNameGrammar()
    {
        // Names already on disk are never checked, so a note predating the grammar stays writable.
        _vault.WithNote("Projects/old widget notes.md", "# Old\n");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.Replace(_vault.ToWrite("Projects/old widget notes.md"), "# New\n"));
    }

    // ---- Creating ----

    [Fact]
    public void Create_WritesANoteThatWasNotThere()
    {
        _vault.WithFolder("Projects");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.Create(_vault.ToWrite("Projects/Widget.md"), "# Widget\n"));

        Assert.Equal("# Widget\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Create_RefusesAnExistingTargetDistinguishablyAndDestroysNothing()
    {
        // The refusal comes from an atomic create rather than a check run first, which closes the
        // window in which something else creates the file between the check and the write.
        _vault.WithNote("Projects/Widget.md", "# Original\n");

        var outcome = Assert.IsType<WriteOutcome.AlreadyExists>(VaultFiles.Create(_vault.ToWrite("Projects/Widget.md"), "# New\n"));

        Assert.Equal("Projects/Widget.md", outcome.Path);
        Assert.Equal("# Original\n", _vault.TextOf("Projects/Widget.md"));
    }

    [Fact]
    public void Create_RefusesANameOutsideTheGrammarAndWritesNothing()
    {
        _vault.WithFolder("Projects");

        var outcome = Assert.IsType<WriteOutcome.InvalidName>(VaultFiles.Create(_vault.ToWrite("Projects/new widget.md"), "# New\n"));

        Assert.Equal("new widget.md", outcome.Name);
        Assert.False(_vault.HasFile("Projects/new widget.md"));
    }

    [Fact]
    public void Create_ChecksOnlyTheSegmentItBringsIntoExistence()
    {
        // Only the final segment is new, so the name grammar is not applied to the folders the
        // path passes through.
        _vault.WithFolder("old projects");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.Create(_vault.ToWrite("old projects/Widget.md"), "# Widget\n"));
    }

    // ---- A missing parent is never created ----

    [Fact]
    public void Create_NamesTheMissingParentInVaultRelativeTermsAndMaterializesNothing()
    {
        var outcome = Assert.IsType<WriteOutcome.MissingParent>(VaultFiles.Create(_vault.ToWrite("Projects/Widget/Widget.md"), "# Widget\n"));

        Assert.Equal("Projects/Widget", outcome.Parent);
        Assert.False(_vault.HasFolder("Projects"));
        Assert.False(_vault.HasFolder("Projects/Widget"));
    }

    [Fact]
    public void Replace_NamesTheMissingParentTheSameWay()
    {
        var outcome = Assert.IsType<WriteOutcome.MissingParent>(VaultFiles.Replace(_vault.ToWrite("Projects/Widget/Widget.md"), "# Widget\n"));

        Assert.Equal("Projects/Widget", outcome.Parent);
        Assert.False(_vault.HasFolder("Projects"));
    }

    [Fact]
    public void CreateFolder_NamesTheMissingParentAndMaterializesNothing()
    {
        var outcome = Assert.IsType<WriteOutcome.MissingParent>(VaultFiles.CreateFolder(_vault.ToWrite("Projects/Widget/Notes")));

        Assert.Equal("Projects/Widget", outcome.Parent);
        Assert.False(_vault.HasFolder("Projects"));
    }

    // ---- Folder creation ----

    [Fact]
    public void CreateFolder_CreatesOneFolderUnderAnExistingParent()
    {
        _vault.WithFolder("Projects");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.CreateFolder(_vault.ToWrite("Projects/Widget")));

        Assert.True(_vault.HasFolder("Projects/Widget"));
    }

    [Fact]
    public void CreateFolder_CreatesDirectlyUnderTheRoot()
    {
        Assert.IsType<WriteOutcome.Written>(VaultFiles.CreateFolder(_vault.ToWrite("Projects")));

        Assert.True(_vault.HasFolder("Projects"));
    }

    /// <summary>
    /// A folder already at the target is reported as such rather than as a creation. The platform's
    /// directory creation is idempotent, so without the probe the call would report making something
    /// that was already there.
    /// </summary>
    [Fact]
    public void CreateFolder_ReportsAnOccupiedTargetRatherThanACreationThatDidNotHappen()
    {
        _vault.WithFolder("Projects/Widget");

        var outcome = Assert.IsType<WriteOutcome.AlreadyExists>(VaultFiles.CreateFolder(_vault.ToWrite("Projects/Widget")));

        Assert.Equal("Projects/Widget", outcome.Path);
    }

    /// <summary>
    /// A file occupying the folder's path is not a folder that is already there, so it is reported
    /// from the attempt like any other write failure rather than as an occupied target.
    /// </summary>
    [Fact]
    public void CreateFolder_OverAFileIsAFailureRatherThanAnOccupiedTarget()
    {
        _vault.WithNote("Projects/Widget", "Not a folder.\n");

        var outcome = Assert.IsType<WriteOutcome.Failed>(VaultFiles.CreateFolder(_vault.ToWrite("Projects/Widget")));

        Assert.Equal("Projects/Widget", outcome.Path);
    }

    [Fact]
    public void CreateFolder_RefusesANameOutsideTheGrammar()
    {
        _vault.WithFolder("Projects");

        var outcome = Assert.IsType<WriteOutcome.InvalidName>(VaultFiles.CreateFolder(_vault.ToWrite("Projects/new widget")));

        Assert.Equal("new widget", outcome.Name);
        Assert.False(_vault.HasFolder("Projects/new widget"));
    }

    [Fact]
    public void CreateFolder_IsNotAWriteAndSeedsNoFile()
    {
        _vault.WithFolder("Projects");

        VaultFiles.CreateFolder(_vault.ToWrite("Projects/Widget"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_vault.Absolute("Projects/Widget")));
    }

    // ---- Encoding and line endings ----

    [Fact]
    public void AWriteEmitsUtf8WithNoByteOrderMark()
    {
        _vault.WithFolder("Projects");

        VaultFiles.Create(_vault.ToWrite("Projects/Widget.md"), "# Wídget\n");

        var bytes = _vault.BytesOf("Projects/Widget.md");
        Assert.NotEqual<byte[]>([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(new UTF8Encoding(false).GetBytes("# Wídget\n"), bytes);
    }

    [Theory]
    [InlineData("# Widget\r\n\r\nBody.\r\n")]
    [InlineData("# Widget\n\nBody.\n")]
    public void AWriteEmitsLineFeedsWhateverItWasHanded(string content)
    {
        // Notes are stored with LF line endings. Nothing detects the line endings already in a
        // file or preserves another convention.
        _vault.WithFolder("Projects");

        VaultFiles.Create(_vault.ToWrite("Projects/Widget.md"), content);

        Assert.Equal("# Widget\n\nBody.\n", Encoding.UTF8.GetString(_vault.BytesOf("Projects/Widget.md")));
    }

    [Fact]
    public void ReplaceEmitsTheSameWayCreateDoes()
    {
        _vault.WithNote("Projects/Widget.md", "# Old\n");

        VaultFiles.Replace(_vault.ToWrite("Projects/Widget.md"), "# Widget\r\nBody.\r\n");

        Assert.Equal(new UTF8Encoding(false).GetBytes("# Widget\nBody.\n"), _vault.BytesOf("Projects/Widget.md"));
    }
}
