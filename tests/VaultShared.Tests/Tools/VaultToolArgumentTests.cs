using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The three read tools' argument records: what <c>Validate</c> refuses, and that an enumerated
/// value the vocabulary does not carry gets there at all.
///
/// The last one is the whole point of <see cref="EnumArgument"/>. The serializer's own enum handling
/// throws, and a refusal escaping that way arrives shaped by the binder rather than by the
/// catalogue - so these cases bind first, exactly as the execute path does, and then assert on the
/// fault.
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

    /// <summary>An absent vault is the caller asking for the configured default, which resolution
    /// answers above the tool - not an argument fault.</summary>
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

    /// <summary>Trimmed and case-folded before comparison, as the argument rules require.</summary>
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
        // The message names what the vocabulary does carry, so the retry is one step.
        Assert.Contains("\"all\"", fault.Message, StringComparison.Ordinal);
        Assert.Contains("\"frontmatter\"", fault.Message, StringComparison.Ordinal);
        Assert.Contains("\"body\"", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>An enumerated argument sent as something that is not a string at all is the same
    /// refusal: the record still binds, and the tool's own vocabulary still decides.</summary>
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

    // ---- The never-inferred extension ----

    /// <summary>
    /// The refusal is <c>invalid_path</c> rather than an argument fault, because the catalogue puts
    /// the missing extension there and gives it a payload an argument fault has nowhere to carry.
    /// </summary>
    [Fact]
    public void ANoteOperandWithoutAnExtension_RefusesNamingTheSpellingThatWouldHaveWorked()
    {
        var refusal = Assert.Throws<ToolRefusal>(() => ToolPaths.RequireNoteExtension("Projects/Widget/Widget"));

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Projects/Widget/Widget.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>
    /// A path carrying some other extension gets no suggestion: appending <c>.md</c> to it would
    /// name a third thing the caller did not mean, and a payload is only there to be lifted into a
    /// corrected call.
    /// </summary>
    [Fact]
    public void ANoteOperandCarryingAnotherExtension_RefusesWithNoSuggestion()
    {
        var refusal = Assert.Throws<ToolRefusal>(() => ToolPaths.RequireNoteExtension("Attachments/Diagram.png"));

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Null(VaultToolHarness.Data(refusal));
    }

    /// <summary>A dot in a folder name is not an extension on the note.</summary>
    [Fact]
    public void ADottedFolderNameDoesNotStandInForTheNotesOwnExtension()
    {
        var refusal = Assert.Throws<ToolRefusal>(() => ToolPaths.RequireNoteExtension("v1.2/Widget"));

        Assert.Equal("v1.2/Widget.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>Matched case-insensitively, because enumeration hands back a note spelled
    /// <c>.MD</c> and a path this surface produces has to be one it takes back.</summary>
    [Theory]
    [InlineData("Widget.md")]
    [InlineData("Widget.MD")]
    [InlineData("Widget.Md")]
    public void ANoteOperandCarryingTheExtension_IsAccepted(string path)
    {
        ToolPaths.RequireNoteExtension(path);
    }
}
