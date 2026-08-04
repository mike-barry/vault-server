using System.Text;
using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// Reading, the two write forms, and folder creation, against a real directory tree. What is under
/// test is what the layer makes load-bearing: classification that comes from the failure rather than
/// from a probe, a creating write whose refusal is the filesystem's, nothing materializing a parent
/// on the way past, and the encoding and terminators the composing seam states.
/// </summary>
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
        // A folder is present and is not a note. The two answers are distinguished by the failure
        // the filesystem raised, which is the whole requirement: no existence probe runs first, and
        // a caller cannot be left to recover this from message text.
        _vault.WithFolder("Projects/Widget.md");

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
        // Bringing a note into existence is the creating form's job. Replacing something absent
        // would make the two forms one.
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
        // The refusal is the filesystem's answer to an atomic create rather than a check the layer
        // ran first, which is what closes the window in which something else creates the file
        // between the check and the write.
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
        // Every other segment already exists, so the grammar has nothing to say about it.
        _vault.WithFolder("old projects");

        Assert.IsType<WriteOutcome.Written>(VaultFiles.Create(_vault.ToWrite("old projects/Widget.md"), "# Widget\n"));
    }

    // ---- Nothing creates a parent on the way past ----

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

    // ---- The composing seam ----

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
        // Notes are stored with LF. Nothing detects what was there or preserves another convention,
        // and the seam that composes bytes is where that is stated.
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

    [Fact]
    public void AReplacedNoteHoldsTheNewContentAndNoTailOfTheOld()
    {
        _vault.WithNote("Projects/Widget.md", "# Widget\n\nA much longer original body that must not survive.\n");

        VaultFiles.Replace(_vault.ToWrite("Projects/Widget.md"), "# W\n");

        Assert.Equal(new UTF8Encoding(false).GetBytes("# W\n"), _vault.BytesOf("Projects/Widget.md"));
    }
}
