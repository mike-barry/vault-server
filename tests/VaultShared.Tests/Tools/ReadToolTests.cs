using System.Security.Cryptography;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the <c>read</c> tool against a real directory tree: which part of a note comes back, where
/// the content hash comes from, and how a missing note, an unreadable note and a path the caller may
/// not read are told apart.
/// </summary>
public sealed class ReadToolTests : IDisposable
{
    private const string Fenced = "---\ntype: note\ntags:\n  - widget\n---\n# Widget\n\nBody text.\n";
    private const string Unfenced = "# Loose\n\nNo block at all.\n";

    private readonly ScratchVault _vault = new ScratchVault()
        .WithNote("Projects/Widget/Widget.md", Fenced)
        .WithNote("Inbox/Loose.md", Unfenced)
        .WithNote(".obsidian/Deep.md", Fenced);

    public void Dispose() => _vault.Dispose();

    private string Read(object arguments) =>
        VaultToolHarness.Text(new ReadTool(), ReadTool.Declaration, _vault, arguments);

    private ToolResult Invoke(ScratchVault vault, object arguments) =>
        VaultToolHarness.Invoke(new ReadTool(), ReadTool.Declaration, vault, arguments);

    private ToolRefusal Refusal(ScratchVault vault, object arguments) =>
        VaultToolHarness.Refusal(new ReadTool(), ReadTool.Declaration, vault, arguments);

    private static string DigestOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- The declaration ----

    /// <summary>
    /// The declaration asks for a read floor and nothing more. The only path the tool touches is the
    /// one the caller named, checked against the effective read scope before anything is read, so
    /// there is no further authority for it to assert.
    /// </summary>
    [Fact]
    public void DeclaresAReadFloorAndNeitherMutatesNorNeedsASession()
    {
        Assert.Equal(new Floor.Class("content:read"), ReadTool.Declaration.OpClassFloor);
        Assert.False(ReadTool.Declaration.Mutates);
        Assert.False(ReadTool.Declaration.RequiresClientSession);
    }

    // ---- The three parts of a note ----

    [Fact]
    public void WithNoPart_AnswersTheWholeNote()
    {
        Assert.Equal(Fenced, Read(new { path = "Projects/Widget/Widget.md" }));
    }

    [Fact]
    public void Frontmatter_AnswersTheBlocksInnerTextWithoutItsFences()
    {
        Assert.Equal("type: note\ntags:\n  - widget", Read(new { path = "Projects/Widget/Widget.md", part = "frontmatter" }));
    }

    [Fact]
    public void Body_AnswersEverythingAfterTheClosingFence()
    {
        Assert.Equal("# Widget\n\nBody text.\n", Read(new { path = "Projects/Widget/Widget.md", part = "body" }));
    }

    /// <summary>A note with no frontmatter block is handled by the shared note split rather than by
    /// this tool: its frontmatter is empty text and its body is the whole note.</summary>
    [Fact]
    public void ANoteWithNoBlock_AnswersFrontmatterWithEmptyTextAndBodyWithTheWholeNote()
    {
        Assert.Equal("", Read(new { path = "Inbox/Loose.md", part = "frontmatter" }));
        Assert.Equal(Unfenced, Read(new { path = "Inbox/Loose.md", part = "body" }));
    }

    // ---- The content hash ----

    /// <summary>Asking for the hash returns a structured result carrying the content and the hash
    /// together.</summary>
    [Fact]
    public void WithContentHash_AnswersTheContentAndTheHashTogether()
    {
        var result = Assert.IsType<ToolResult.Data>(Invoke(_vault, new { path = "Projects/Widget/Widget.md", with_content_hash = true }));
        var read = Assert.IsType<ReadResult>(result.Value);

        Assert.Equal(Fenced, read.Content);
        Assert.Equal(DigestOf(_vault.BytesOf("Projects/Widget/Widget.md")), read.ContentHash);
    }

    [Fact]
    public void WithoutContentHash_AnswersPlainText()
    {
        Assert.IsType<ToolResult.Content>(Invoke(_vault, new { path = "Projects/Widget/Widget.md" }));
    }

    /// <summary>
    /// The hash always covers the whole file, whichever part was read, so all three parts return the
    /// same value and it is the value an edit of the same file returns. A hash of only the part read
    /// would let a partial read authorize a whole-file write.
    /// </summary>
    [Theory]
    [InlineData("all")]
    [InlineData("frontmatter")]
    [InlineData("body")]
    public void TheHashCoversTheWholeFileWhicheverPartWasRead(string part)
    {
        var result = (ToolResult.Data)Invoke(_vault, new { path = "Projects/Widget/Widget.md", part, with_content_hash = true });

        Assert.Equal(
            DigestOf(_vault.BytesOf("Projects/Widget/Widget.md")),
            ((ReadResult)result.Value).ContentHash);
    }

    /// <summary>
    /// The hash describes the bytes the returned content was decoded from, not the file's later
    /// state. Hashing the file again afterwards would describe whatever is on disk by then, which is
    /// the gap the staleness guard exists to close.
    /// </summary>
    [Fact]
    public void TheHashDescribesTheBytesTheContentCameFromRatherThanALaterState()
    {
        var before = (ToolResult.Data)Invoke(_vault, new { path = "Inbox/Loose.md", with_content_hash = true });
        var read = (ReadResult)before.Value;

        _vault.WithNote("Inbox/Loose.md", "# Rewritten by somebody else\n");

        Assert.Equal(Unfenced, read.Content);
        Assert.NotEqual(DigestOf(_vault.BytesOf("Inbox/Loose.md")), read.ContentHash);
    }

    [Fact]
    public void TheHashIsLowercaseHexOfTheStoredBytes()
    {
        var read = (ReadResult)((ToolResult.Data)Invoke(_vault, new { path = "Inbox/Loose.md", with_content_hash = true })).Value;

        Assert.Equal(64, read.ContentHash.Length);
        Assert.Equal(read.ContentHash.ToLowerInvariant(), read.ContentHash);
    }

    // ---- Missing versus unreadable ----

    [Fact]
    public void ANoteThatIsNotThere_IsAMissRatherThanAReadFailure()
    {
        var refusal = Refusal(_vault, new { path = "Projects/Widget/Absent.md" });

        Assert.Equal(ToolCodes.PathNotFound, refusal.Code);
    }

    [Fact]
    public void AMissingFolderAlongTheWay_IsTheSameMiss()
    {
        Assert.Equal(ToolCodes.PathNotFound, Refusal(_vault, new { path = "No/Such/Folder/Note.md" }).Code);
    }

    /// <summary>
    /// A note that exists but cannot be opened gets its own code, naming the path. The distinction
    /// comes from how the access layer classified the failure, not from a separate existence check
    /// run beforehand.
    /// </summary>
    [Fact]
    public void ANoteThatIsThereAndCannotBeRead_IsAReadFailureNamingIt()
    {
        using var held = _vault.Unreadable("Inbox/Loose.md");

        var refusal = Refusal(_vault, new { path = "Inbox/Loose.md" });

        Assert.Equal(ToolCodes.ReadFailed, refusal.Code);
        Assert.Equal("Inbox/Loose.md", VaultToolHarness.Data(refusal)!["path"]);
    }

    // ---- Paths the caller may not read ----

    /// <summary>
    /// A path the caller names that lies outside the grant is refused rather than reported as
    /// missing. Enumeration and search filter such paths out silently; a named path does not, so a
    /// caller can tell the two situations apart.
    /// </summary>
    [Fact]
    public void ANamedPathOutsideTheGrant_RefusesLoudlyRatherThanReadingAsAMiss()
    {
        using var scoped = new ScratchVault(VaultScope.Of(["Projects"]))
            .WithNote("Projects/Widget/Widget.md", Fenced)
            .WithNote("Inbox/Loose.md", Unfenced);

        var refusal = VaultToolHarness.Refusal(new ReadTool(), ReadTool.Declaration, scoped, new { path = "Inbox/Loose.md" });

        Assert.Equal(ToolCodes.OutOfScope, refusal.Code);
        // No data: the only remedy is a wider grant, so there is no corrected call to suggest.
        Assert.Null(VaultToolHarness.Data(refusal));
        // The refusal must not reveal whether the note exists.
        Assert.DoesNotContain("Loose", ToolRefusalEnvelope.Json(refusal).Replace("Inbox/Loose.md", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>A path segment beginning with a dot is excluded outright, not by scope. This
    /// vault's grant is unscoped and the path is still refused.</summary>
    [Fact]
    public void ANamedPathUnderADotDirectory_IsThePerimeterRatherThanAScopeDenial()
    {
        Assert.Equal(ToolCodes.PathExcluded, Refusal(_vault, new { path = ".obsidian/Deep.md" }).Code);
    }

    [Fact]
    public void ANamedPathThatEscapesTheVault_IsMalformed()
    {
        Assert.Equal(ToolCodes.InvalidPath, Refusal(_vault, new { path = "../Outside.md" }).Code);
    }

    /// <summary>
    /// The markdown extension is required, checked on the caller's text before the access layer sees
    /// the path, and the refusal names the spelling that would have worked.
    /// </summary>
    [Fact]
    public void AnExtensionlessOperand_RefusesNamingTheSpellingThatWouldHaveWorked()
    {
        var refusal = Refusal(_vault, new { path = "Projects/Widget/Widget" });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Projects/Widget/Widget.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>An unrecognized <c>part</c> is refused by the tool, with a code and the argument's
    /// name, rather than by the binder.</summary>
    [Fact]
    public void AnUnrecognizedPart_ReachesTheCallerAsACodedArgumentRefusal()
    {
        var refusal = Refusal(_vault, new { path = "Inbox/Loose.md", part = "middle" });

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        Assert.Equal("part", VaultToolHarness.Data(refusal)!["argument"]);
    }
}
