using VaultShared.Access;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the <c>glob</c> and <c>grep</c> tools against a real directory tree and the real search
/// engine: how each granularity is rendered, that no match is an empty result rather than a failure,
/// and which code each search outcome reaches a caller as.
///
/// Neither tool takes a path from the caller, so a note the grant cannot read is filtered out of the
/// results silently rather than refused.
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

    /// <summary>Enumeration covers markdown notes only. No tool here operates on an attachment, so
    /// one is never listed.</summary>
    [Fact]
    public void Glob_AnswersTheMarkdownDomainOnly()
    {
        Assert.DoesNotContain("Attachments/Data.txt", Lines(Glob(_vault, new { pattern = "**" })));
    }

    /// <summary>The markdown restriction is applied to the engine's results and must never be
    /// appended to the caller's pattern. Appending <c>.md</c> to <c>Projects/**</c> turns the
    /// recursive wildcard into a single <c>*</c> and drops every nested note.</summary>
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

    /// <summary>No match is an empty result rather than a refusal, so a caller checking whether
    /// anything exists does not have to catch.</summary>
    [Fact]
    public void Glob_NoMatchIsEmptyTextRatherThanAFailure()
    {
        Assert.Equal("", Glob(_vault, new { pattern = "No-Such-Name.md" }));
    }

    /// <summary>A note the grant cannot read is left out of the results, and nothing in the result
    /// says it was withheld.</summary>
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

    /// <summary>The path glob narrows which notes are searched.</summary>
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

    // ---- Search outcomes, over the real engine ----

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
    /// A file the search could not read fails the whole search, and is reported as a search failure
    /// rather than a read failure. A read failure names the path it is about, and the file the
    /// engine met may be one these tools would otherwise have filtered out silently.
    ///
    /// Only <c>grep</c> can hit this, because it is the only one of the two that reads file content.
    /// Enumeration walks names, and an unreadable note still has a name.
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
        // The engine sees the file before scope filtering, so its path is scrubbed from the
        // diagnostic. A tool that filters silently must not name a withheld file in a failure.
        Assert.DoesNotContain("Projects/Widget/Widget.md", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnreadableFileIsScrubbedFromTheDetailItWouldOtherwiseRideOut()
    {
        var refusal = ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Unreadable("Notes/Hidden.md", "rg: Notes/Hidden.md: denied"));

        Assert.Equal("rg: <unreadable file>: denied", VaultToolHarness.Data(refusal)!["detail"]);
    }

    /// <summary>
    /// An engine that is not there refuses as an unavailable dependency rather than as a failed
    /// search, driven through the real tool surface rather than through the outcome mapping alone.
    /// <c>search_failed</c> is documented as retryable with the identical call, and no retry can start
    /// an executable that is not on disk, so the two must not share a code.
    ///
    /// The refusal carries no payload. The platform's message names the executable the host was
    /// configured with, which is a server-side path the caller cannot act on.
    /// </summary>
    [Theory]
    [InlineData("glob")]
    [InlineData("grep")]
    public void AnEngineThatCannotBeStartedIsRefusedAsAnUnavailableDependency(string tool)
    {
        using var broken = Layout(new ScratchVault(engine: new SearchEngine(Path.Combine(_vault.Root, "no-such-engine"))));

        var refusal = tool == "glob"
            ? VaultToolHarness.Refusal(new GlobTool(), GlobTool.Declaration, broken, new { pattern = "**" })
            : VaultToolHarness.Refusal(new GrepTool(), GrepTool.Declaration, broken, new { pattern = "needle" });

        Assert.Equal(ToolCodes.DependencyUnavailable, refusal.Code);
        Assert.Null(VaultToolHarness.Data(refusal));
        Assert.DoesNotContain("no-such-engine", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Mapping search outcomes to refusal codes ----

    /// <summary>
    /// One place maps search outcomes to codes, so no tool decides for itself which outcome is a
    /// caller error. These cases use constructed outcomes as well as the live ones above, because
    /// the mapping has to hold for every granularity's result type and no engine state produces
    /// every outcome on demand.
    /// </summary>
    [Fact]
    public void TheOutcomeMappingNamesACallerErrorAndAnOperationalFailureApart()
    {
        Assert.Equal(ToolCodes.InvalidPattern, ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.BadPattern("bad glob")).Code);
        Assert.Equal(ToolCodes.SearchFailed, ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Unreadable("A.md", "denied")).Code);
        Assert.Equal(ToolCodes.SearchFailed, ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Failed("engine gone")).Code);
        Assert.Equal(ToolCodes.DependencyUnavailable, ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.EngineUnavailable("cannot start")).Code);
    }

    /// <summary>Only the failures a caller may retry with the identical call carry engine text in
    /// <c>detail</c>. A malformed pattern is the caller's own, so there is nothing to report back
    /// about it, and an engine that is not on disk reports a server-side path the caller can neither
    /// read nor act on.</summary>
    [Fact]
    public void OnlyTheRetryableFailuresCarryEngineDetail()
    {
        Assert.Null(VaultToolHarness.Data(ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.BadPattern("bad glob"))));
        Assert.Equal("denied", VaultToolHarness.Data(ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Unreadable("A.md", "denied")))!["detail"]);
        Assert.Equal("engine gone", VaultToolHarness.Data(ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Failed("engine gone")))!["detail"]);
        Assert.Null(VaultToolHarness.Data(ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.EngineUnavailable(@"C:\engines\rg.exe not found"))));
    }

    [Fact]
    public void AResultIsNotARefusalToTranslate()
    {
        Assert.Throws<ArgumentException>(() => ToolRefusalEnvelope.Translate(new SearchOutcome<VaultPath>.Results([])));
    }
}
