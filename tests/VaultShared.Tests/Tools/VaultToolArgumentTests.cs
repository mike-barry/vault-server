using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the argument records of the three read tools: what <c>Validate</c> refuses, and that an
/// unrecognized enum value survives binding so the record can refuse it.
///
/// That last point is what <see cref="EnumArgument"/> exists for. The serializer's own enum handling
/// throws, and a refusal raised that way is worded by the binder rather than by the tool. These
/// cases therefore bind first, as the execute path does, and then assert on the fault.
/// </summary>
public sealed class VaultToolArgumentTests
{
    private static T Bind<T>(object arguments) where T : IToolArguments =>
        (T)NativeToolArguments.Bind(typeof(T), VaultToolHarness.Args(arguments));

    // ---- Presence ----

    [Fact]
    public void Read_WithNoPath_FaultsOnThatArgument()
    {
        var fault = Assert.IsType<ArgumentFault>(Bind<ReadArguments>(new { vault = "scratch" }).Validate());

        Assert.Equal("path", fault.Argument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_WithABlankPath_FaultsOnThatArgument(string path)
    {
        Assert.Equal("path", Bind<ReadArguments>(new { path }).Validate()!.Argument);
    }

    [Fact]
    public void Glob_WithNoPattern_FaultsOnThatArgument()
    {
        Assert.Equal("pattern", Bind<GlobArguments>(new { vault = "scratch" }).Validate()!.Argument);
    }

    [Fact]
    public void Grep_WithNoPattern_FaultsOnThatArgument()
    {
        Assert.Equal("pattern", Bind<GrepArguments>(new { granularity = "files" }).Validate()!.Argument);
    }

    // ---- Defaults ----

    [Fact]
    public void Read_SayingNothingAboutPartOrTheHash_WantsTheWholeNoteAndNoHash()
    {
        var arguments = Bind<ReadArguments>(new { path = "A.md" });

        Assert.Equal(ReadPart.All, arguments.Part);
        Assert.False(arguments.WithContentHash);
        Assert.Null(arguments.Validate());
    }

    [Fact]
    public void Grep_SayingNothingAboutGranularity_WantsLines()
    {
        var arguments = Bind<GrepArguments>(new { pattern = "needle" });

        Assert.Equal(GrepGranularity.Lines, arguments.Granularity);
        Assert.Null(arguments.PathGlob);
        Assert.Null(arguments.Validate());
    }

    /// <summary>Omitting the vault asks for the configured default, which the execute path resolves
    /// before the tool runs. It is not an argument fault.</summary>
    [Fact]
    public void AnAbsentVault_IsNotAFault()
    {
        Assert.Null(Bind<ReadArguments>(new { path = "A.md" }).Vault);
        Assert.Null(Bind<GlobArguments>(new { pattern = "*.md" }).Validate());
    }

    // ---- Enumerated values ----

    [Theory]
    [InlineData("all", ReadPart.All)]
    [InlineData("frontmatter", ReadPart.Frontmatter)]
    [InlineData("body", ReadPart.Body)]
    public void Read_BindsTheWireSpellingOfEachPart(string sent, ReadPart expected)
    {
        Assert.Equal(expected, Bind<ReadArguments>(new { path = "A.md", part = sent }).Part);
    }

    /// <summary>An enum value is trimmed and compared without regard to case.</summary>
    [Theory]
    [InlineData("  body  ")]
    [InlineData("BODY")]
    [InlineData("Body")]
    public void Read_TrimsAndCaseFoldsAnEnumeratedValue(string sent)
    {
        var arguments = Bind<ReadArguments>(new { path = "A.md", part = sent });

        Assert.Equal(ReadPart.Body, arguments.Part);
        Assert.Null(arguments.Validate());
    }

    [Fact]
    public void Read_WithAnUnrecognizedPart_FaultsRatherThanFailingToBind()
    {
        var fault = Assert.IsType<ArgumentFault>(Bind<ReadArguments>(new { path = "A.md", part = "middle" }).Validate());

        Assert.Equal("part", fault.Argument);
        // The message lists the values that are accepted, so the caller can retry once and succeed.
        Assert.Contains("\"all\"", fault.Message, StringComparison.Ordinal);
        Assert.Contains("\"frontmatter\"", fault.Message, StringComparison.Ordinal);
        Assert.Contains("\"body\"", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>An enum argument sent as something other than a string is refused the same way. The
    /// record still binds, and the tool still decides.</summary>
    [Theory]
    [InlineData(7)]
    [InlineData(true)]
    public void Read_WithANonStringPart_FaultsRatherThanFailingToBind(object sent)
    {
        Assert.Equal("part", Bind<ReadArguments>(new { path = "A.md", part = sent }).Validate()!.Argument);
    }

    [Fact]
    public void Grep_WithAnUnrecognizedGranularity_FaultsOnThatArgument()
    {
        var fault = Assert.IsType<ArgumentFault>(Bind<GrepArguments>(new { pattern = "x", granularity = "paths" }).Validate());

        Assert.Equal("granularity", fault.Argument);
        Assert.Contains("\"counts\"", fault.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lines", GrepGranularity.Lines)]
    [InlineData("FILES", GrepGranularity.Files)]
    [InlineData(" counts ", GrepGranularity.Counts)]
    public void Grep_BindsTheWireSpellingOfEachGranularity(string sent, GrepGranularity expected)
    {
        Assert.Equal(expected, Bind<GrepArguments>(new { pattern = "x", granularity = sent }).Granularity);
    }

    // ---- The markdown extension, which is never inferred ----

    /// <summary>
    /// A missing extension is refused as an invalid path rather than as an argument fault, so that
    /// the refusal can carry the suggested spelling. An argument fault has nowhere to put it.
    /// </summary>
    [Fact]
    public void ANoteOperandWithoutAnExtension_RefusesNamingTheSpellingThatWouldHaveWorked()
    {
        var refusal = Assert.Throws<ToolRefusal>(() => ToolPaths.RequireNoteExtension("Projects/Widget/Widget"));

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Projects/Widget/Widget.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>
    /// A path carrying a different extension gets no suggestion. Appending <c>.md</c> to it would
    /// name a third path the caller did not ask for, and the suggestion exists only to be sent back
    /// as a corrected call.
    /// </summary>
    [Fact]
    public void ANoteOperandCarryingAnotherExtension_RefusesWithNoSuggestion()
    {
        var refusal = Assert.Throws<ToolRefusal>(() => ToolPaths.RequireNoteExtension("Attachments/Diagram.png"));

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Null(VaultToolHarness.Data(refusal));
    }

    /// <summary>
    /// A path carrying an extension that is not the note's is classified as a folder, which is what
    /// keeps a file that is not a note out of relocation and creation.
    /// </summary>
    /// <remarks>
    /// Wikilink repair and pinning enumerate notes only, so a target naming an attachment resolves
    /// to nothing in either pass. That is unreachable today because this classification sends every
    /// such path down the folder route, where it names no folder and is refused. Teaching the tools
    /// to carry a non-note operand means teaching those two passes about it in the same change; the
    /// removal census already handles one, because deleting a folder destroys the files beneath it
    /// whether they are notes or not.
    /// </remarks>
    [Fact]
    public void APathCarryingAnotherExtensionIsAFolderRatherThanANote()
    {
        Assert.Equal(OperandKind.Folder, ToolPaths.KindOf("Attachments/Diagram.png"));
        Assert.Equal(OperandKind.Note, ToolPaths.KindOf("Attachments/Diagram.md"));
    }

    /// <summary>A dot in a folder name does not count as the note's extension.</summary>
    [Fact]
    public void ADottedFolderNameDoesNotStandInForTheNotesOwnExtension()
    {
        var refusal = Assert.Throws<ToolRefusal>(() => ToolPaths.RequireNoteExtension("v1.2/Widget"));

        Assert.Equal("v1.2/Widget.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>The extension is matched without regard to case. Enumeration can return a note
    /// spelled <c>.MD</c>, and a path these tools hand out must be one they accept back.</summary>
    [Theory]
    [InlineData("Widget.md")]
    [InlineData("Widget.MD")]
    [InlineData("Widget.Md")]
    public void ANoteOperandCarryingTheExtension_IsAccepted(string path)
    {
        ToolPaths.RequireNoteExtension(path);
    }
}
