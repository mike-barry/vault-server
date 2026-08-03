using System.Security.Cryptography;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>read</c> over a real tree: which facet comes back, where the content hash comes from, and how
/// a miss, a failure and a path the caller may not name are told apart.
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
    /// A read floor, and no more. Every path the tool touches is the one the caller named, admitted
    /// against the effective read scope before anything is read, so there is no outcome-dependent
    /// authority for it to assert.
    /// </summary>
    [Fact]
    public void DeclaresAReadFloorAndNeitherMutatesNorNeedsASession()
    {
        Assert.Equal(new Floor.Class("content:read"), ReadTool.Declaration.OpClassFloor);
        Assert.False(ReadTool.Declaration.Mutates);
        Assert.False(ReadTool.Declaration.RequiresClientSession);
    }

    // ---- The three facets ----

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

    /// <summary>The absent block is a well-formed value in the one split, not a case this tool
    /// writes: empty text, and the whole note as body.</summary>
    [Fact]
    public void ANoteWithNoBlock_AnswersFrontmatterWithEmptyTextAndBodyWithTheWholeNote()
    {
        Assert.Equal("", Read(new { path = "Inbox/Loose.md", part = "frontmatter" }));
        Assert.Equal(Unfenced, Read(new { path = "Inbox/Loose.md", part = "body" }));
    }

    // ---- The content hash ----

    /// <summary>Content and hash come back together, as the one content-tier result that carries
    /// structure.</summary>
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
    /// Whole-file whichever part was read, so nothing authorizes a write from a partial read. The
    /// three facets therefore answer one value, and it is the value a mutation of the same file
    /// would return.
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
    /// The hash describes the bytes the content was decoded from and nothing later. A second read
    /// would hash whatever is on disk by then, which is the window the guard exists to close.
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

    // ---- Miss versus failure ----

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
    /// There, and unreadable: a distinct code carrying the path, taken from the layer's
    /// classification of the failure rather than from a probe this tool runs first.
    /// </summary>
    [Fact]
    public void ANoteThatIsThereAndCannotBeRead_IsAReadFailureNamingIt()
    {
        using var held = _vault.Unreadable("Inbox/Loose.md");

        var refusal = Refusal(_vault, new { path = "Inbox/Loose.md" });

        Assert.Equal(ToolCodes.ReadFailed, refusal.Code);
        Assert.Equal("Inbox/Loose.md", VaultToolHarness.Data(refusal)!["path"]);
    }

    // ---- Named is loud ----

    /// <summary>
    /// A named path outside the grant refuses rather than reading as a miss. Enumeration and search
    /// filter silently; an operand does not, and the two answers are what a caller acts on
    /// differently.
    /// </summary>
    [Fact]
    public void ANamedPathOutsideTheGrant_RefusesLoudlyRatherThanReadingAsAMiss()
    {
        using var scoped = new ScratchVault(VaultScope.Of(["Projects"]))
            .WithNote("Projects/Widget/Widget.md", Fenced)
            .WithNote("Inbox/Loose.md", Unfenced);

        var refusal = VaultToolHarness.Refusal(new ReadTool(), ReadTool.Declaration, scoped, new { path = "Inbox/Loose.md" });

        Assert.Equal(ToolCodes.OutOfScope, refusal.Code);
        // No payload: the only remedy is elevation, so there is no corrected call to lift one into.
        Assert.Null(VaultToolHarness.Data(refusal));
        // And no oracle: the refusal says nothing about whether the note is there.
        Assert.DoesNotContain("Loose", ToolRefusalEnvelope.Json(refusal).Replace("Inbox/Loose.md", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>A dot-directory segment is the perimeter rather than a scope answer, and no grant
    /// changes it - this vault's grant is unscoped.</summary>
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
    /// The extension rule is decided before the layer sees the path, because it is a statement about
    /// the caller's own text alone and it costs one retry to fix.
    /// </summary>
    [Fact]
    public void AnExtensionlessOperand_RefusesNamingTheSpellingThatWouldHaveWorked()
    {
        var refusal = Refusal(_vault, new { path = "Projects/Widget/Widget" });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Projects/Widget/Widget.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>An unrecognized enumerated value is the tool's own refusal, coded and naming the
    /// argument, rather than whatever the binder would have raised.</summary>
    [Fact]
    public void AnUnrecognizedPart_ReachesTheCallerAsACodedArgumentRefusal()
    {
        var refusal = Refusal(_vault, new { path = "Inbox/Loose.md", part = "middle" });

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        Assert.Equal("part", VaultToolHarness.Data(refusal)!["argument"]);
    }
}
