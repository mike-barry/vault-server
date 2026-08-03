using VaultShared.Access;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>glob</c> and <c>grep</c> over a real tree and a real engine: the rendering of each tier, the
/// empty result that is never a failure, and the codes the layer's four outcomes reach a caller as.
///
/// Both tools name no path, so nothing here is gated loudly - what a caller may not read is filtered
/// out by construction, which is the counterpart of <c>read</c>'s named operand.
/// </summary>
public sealed class SearchToolTests : IDisposable
{
    private readonly ScratchVault _vault = Layout(new ScratchVault());

    public void Dispose() => _vault.Dispose();

    private static ScratchVault Layout(ScratchVault vault) => vault
        .WithNote("Projects/Widget/Widget.md", "needle here\nplain\nneedle again\n")
        .WithNote("Projects/Widget/Tasks.md", "needle in tasks\n")
        .WithNote("Inbox/Loose.md", "nothing of interest\n")
        .WithNote("Attachments/Data.txt", "needle in something that is not a note\n")
        .WithNote(".obsidian/Deep.md", "needle under a dot\n");

    private string Glob(ScratchVault vault, object arguments) =>
        VaultToolHarness.Text(new GlobTool(), GlobTool.Declaration, vault, arguments);

    private string Grep(ScratchVault vault, object arguments) =>
        VaultToolHarness.Text(new GrepTool(), GrepTool.Declaration, vault, arguments);

    private static string[] Lines(string text) =>
        text.Length == 0 ? [] : [.. text.Split('\n').Order()];

    // ---- The declarations ----

    [Theory]
    [InlineData("glob")]
    [InlineData("grep")]
    public void DeclareAReadFloorAndNeitherMutatesNorNeedsASession(string name)
    {
        var declaration = name == "glob" ? GlobTool.Declaration : GrepTool.Declaration;

        Assert.Equal(new Floor.Class("content:read"), declaration.OpClassFloor);
        Assert.False(declaration.Mutates);
        Assert.False(declaration.RequiresClientSession);
    }

    // ---- glob ----

    [Fact]
    public void Glob_AnswersNewlineJoinedVaultRelativePaths()
    {
        Assert.Equal(
            ["Projects/Widget/Tasks.md", "Projects/Widget/Widget.md"],
            Lines(Glob(_vault, new { pattern = "Projects/**" })));
    }

    /// <summary>Note enumeration, not the census: the markdown domain is the tool's semantic choice,
    /// and an attachment is nothing any tool here operates on.</summary>
    [Fact]
    public void Glob_AnswersTheMarkdownDomainOnly()
    {
        Assert.DoesNotContain("Attachments/Data.txt", Lines(Glob(_vault, new { pattern = "**" })));
    }

    /// <summary>The restriction filters the engine's results and is never appended to the caller's
    /// pattern: <c>Projects/**</c> with <c>.md</c> appended degrades to a single <c>*</c> and drops
    /// every nested note, which is exactly what this asserts did not happen.</summary>
    [Fact]
    public void Glob_ARecursivePatternStillReachesNestedNotes()
    {
        Assert.Contains("Projects/Widget/Widget.md", Lines(Glob(_vault, new { pattern = "Projects/**" })));
    }

    [Fact]
    public void Glob_LeavesDotDirectoriesOut()
    {
        Assert.DoesNotContain(".obsidian/Deep.md", Lines(Glob(_vault, new { pattern = "**" })));
    }

    /// <summary>Any count is a result: an existence check must not require a caller to catch.</summary>
    [Fact]
    public void Glob_NoMatchIsEmptyTextRatherThanAFailure()
    {
        Assert.Equal("", Glob(_vault, new { pattern = "No-Such-Name.md" }));
    }

    /// <summary>Filtering is silent. A note the grant cannot read is simply absent, with nothing
    /// said about it - the opposite of what a named operand gets.</summary>
    [Fact]
    public void Glob_FiltersWhatTheGrantCannotReadWithoutSayingSo()
    {
        using var scoped = Layout(new ScratchVault(VaultScope.Of(["Projects"])));

        var paths = Lines(Glob(scoped, new { pattern = "**" }));

        Assert.Contains("Projects/Widget/Widget.md", paths);
        Assert.DoesNotContain("Inbox/Loose.md", paths);
    }

    // ---- grep ----

    [Fact]
    public void Grep_Lines_AnswersPathLineText()
    {
        Assert.Equal(
            ["Projects/Widget/Widget.md:1:needle here", "Projects/Widget/Widget.md:3:needle again"],
            Lines(Grep(_vault, new { pattern = "needle", path_glob = "Widget.md" })));
    }

    [Fact]
    public void Grep_Files_AnswersOnePathPerLine()
    {
        Assert.Equal(
            ["Projects/Widget/Tasks.md", "Projects/Widget/Widget.md"],
            Lines(Grep(_vault, new { pattern = "needle", granularity = "files" })));
    }

    [Fact]
    public void Grep_Counts_AnswersPathCount()
    {
        Assert.Equal(
            ["Projects/Widget/Tasks.md:1", "Projects/Widget/Widget.md:2"],
            Lines(Grep(_vault, new { pattern = "needle", granularity = "counts" })));
    }

    [Fact]
    public void Grep_WithNoGranularity_AnswersLines()
    {
        Assert.Equal(
            Lines(Grep(_vault, new { pattern = "needle", granularity = "lines" })),
            Lines(Grep(_vault, new { pattern = "needle" })));
    }

    [Theory]
    [InlineData("lines")]
    [InlineData("files")]
    [InlineData("counts")]
    public void Grep_NoMatchIsEmptyTextAtEveryGranularity(string granularity)
    {
        Assert.Equal("", Grep(_vault, new { pattern = "haystack", granularity }));
    }

    /// <summary>The path glob narrows which notes are searched, and is filtered rather than
    /// gated.</summary>
    [Fact]
    public void Grep_ThePathGlobNarrowsWhichNotesAreSearched()
    {
        Assert.Equal(
            ["Projects/Widget/Tasks.md"],
            Lines(Grep(_vault, new { pattern = "needle", granularity = "files", path_glob = "Tasks.md" })));
    }

    [Fact]
    public void Grep_AnswersNoteContentAndNotTheRestOfTheVault()
    {
        Assert.DoesNotContain("Attachments/Data.txt", Lines(Grep(_vault, new { pattern = "needle", granularity = "files" })));
    }

    // ---- The four outcomes, over the real engine ----

    [Fact]
    public void Glob_AMalformedPatternIsACallerError()
    {
        Assert.Equal(
            ToolCodes.InvalidPattern,
            VaultToolHarness.Refusal(new GlobTool(), GlobTool.Declaration, _vault, new { pattern = "[" }).Code);
    }

    [Theory]
    [InlineData("lines")]
    [InlineData("files")]
    [InlineData("counts")]
    public void Grep_AMalformedRegexIsACallerErrorAtEveryGranularity(string granularity)
    {
        Assert.Equal(
            ToolCodes.InvalidPattern,
            VaultToolHarness.Refusal(new GrepTool(), GrepTool.Declaration, _vault, new { pattern = "(", granularity }).Code);
    }

    /// <summary>
    /// A file the search had to read and could not fails the whole search, and does so as a search
    /// failure rather than as a read failure: <c>read_failed</c> names a path, and the file the
    /// engine met is one this surface would otherwise have filtered out silently.
    ///
    /// <c>grep</c> only, because it is the only one of the two that reads content - enumeration
    /// walks names, and an unreadable note still has a name.
    /// </summary>
    [Theory]
    [InlineData("lines")]
    [InlineData("files")]
    [InlineData("counts")]
    public void ASearchThatCannotReadAFileFailsWholeAsASearchFailure(string granularity)
    {
        using var held = _vault.Unreadable("Projects/Widget/Widget.md");

        var refusal = VaultToolHarness.Refusal(new GrepTool(), GrepTool.Declaration, _vault, new { pattern = "needle", granularity });

        Assert.Equal(ToolCodes.SearchFailed, refusal.Code);
        var detail = VaultToolHarness.Data(refusal)!["detail"];
        Assert.NotEmpty(detail);
        // The engine met the file before scope filtering, so the diagnostic is scrubbed of its
        // path: a surface that filters silently must not name what it withheld through a failure.
        Assert.DoesNotContain("Projects/Widget/Widget.md", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnreadableFileIsScrubbedFromTheDetailItWouldOtherwiseRideOut()
    {
        var refusal = ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Unreadable("Notes/Hidden.md", "rg: Notes/Hidden.md: denied"));

        Assert.Equal("rg: <unreadable file>: denied", VaultToolHarness.Data(refusal)!["detail"]);
    }

    [Fact]
    public void AnEngineThatCannotBeRunIsASearchFailureCarryingWhatItSaid()
    {
        using var broken = Layout(new ScratchVault(engine: new SearchEngine(Path.Combine(_vault.Root, "no-such-engine"))));

        var refusal = VaultToolHarness.Refusal(new GlobTool(), GlobTool.Declaration, broken, new { pattern = "**" });

        Assert.Equal(ToolCodes.SearchFailed, refusal.Code);
        Assert.NotEmpty(VaultToolHarness.Data(refusal)!["detail"]);
    }

    // ---- The mapping itself ----

    /// <summary>
    /// One site names the layer's outcomes, so no tool re-decides which of them is a caller error.
    /// Asserted against constructed outcomes as well as the live ones above, because the split has to
    /// hold for every granularity's result type and there is no engine state that produces all four
    /// on demand.
    /// </summary>
    [Fact]
    public void TheOutcomeMappingNamesACallerErrorAndAnOperationalFailureApart()
    {
        Assert.Equal(ToolCodes.InvalidPattern, ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.BadPattern("bad glob")).Code);
        Assert.Equal(ToolCodes.SearchFailed, ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Unreadable("A.md", "denied")).Code);
        Assert.Equal(ToolCodes.SearchFailed, ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Failed("engine gone")).Code);
    }

    /// <summary>A caller's own malformed pattern carries no <c>detail</c> payload of engine text to
    /// lift: the pattern it names is already the caller's.</summary>
    [Fact]
    public void OnlyTheOperationalFailuresCarryEngineDetail()
    {
        Assert.Null(VaultToolHarness.Data(ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.BadPattern("bad glob"))));
        Assert.Equal("denied", VaultToolHarness.Data(ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Unreadable("A.md", "denied")))!["detail"]);
        Assert.Equal("engine gone", VaultToolHarness.Data(ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Failed("engine gone")))!["detail"]);
    }

    [Fact]
    public void AResultIsNotARefusalToTranslate()
    {
        Assert.Throws<ArgumentException>(() => ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Results([])));
    }
}
