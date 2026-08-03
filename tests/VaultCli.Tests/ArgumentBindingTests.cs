using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// What an option's text becomes, and what <c>--args</c> does instead. The rule is that an option's
/// value is decided by the member's own JSON shape and by nothing this front-end knows about a
/// particular tool, so a value the shape cannot take is refused by the binder in the tool
/// vocabulary rather than by the parser as a usage error.
/// </summary>
public sealed class ArgumentBindingTests(CliFixture fixture) : IClassFixture<CliFixture>
{
    [Theory]
    [InlineData("--with-content-hash")]
    [InlineData("--with_content_hash")]
    public void BothSpellingsOfAnArgument_Parse(string spelling)
    {
        var run = fixture.Run(["read", "--path", "Projects/Widget/Widget.md", spelling]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("content_hash", run.Output);
    }

    /// <summary>A boolean is a flag, and an omitted one is absent from the argument map rather than
    /// present as false - so the record's own default is what applies.</summary>
    [Fact]
    public void AnOmittedFlag_LeavesTheRecordsOwnDefault()
    {
        var run = fixture.Run(["read", "--path", "Projects/Widget/Widget.md"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain("content_hash", run.Output);
    }

    /// <summary>
    /// An enumerated value is carried across as its wire spelling and never checked here: an
    /// unrecognized one is refused in the tool's own vocabulary, naming the argument, exactly as it
    /// is for a caller on the other front-end.
    /// </summary>
    [Fact]
    public void AnUnrecognizedEnumeratedValue_IsAToolRefusalNamingTheArgument()
    {
        var run = fixture.Run(["read", "--path", "Projects/Widget/Widget.md", "--part", "sideways"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
        Assert.Equal("part", run.Envelope.GetProperty("data").GetProperty("argument").GetString());
    }

    [Fact]
    public void ARecognizedEnumeratedValue_Binds()
    {
        var run = fixture.Run(["read", "--path", "Projects/Widget/Widget.md", "--part", "frontmatter"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain("# Widget.md", run.Output);
    }

    /// <summary>An omitted required argument is the record's own fault, naming the argument - the
    /// same answer an MCP caller that omitted it gets.</summary>
    [Fact]
    public void AnOmittedRequiredArgument_IsRefusedByTheRecord()
    {
        var run = fixture.Run(["read"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
        Assert.Equal("path", run.Envelope.GetProperty("data").GetProperty("argument").GetString());
    }

    [Fact]
    public void Args_BindsTheArgumentRecordDirectly()
    {
        var run = fixture.Run(["read", "--args", """{"path":"Projects/Widget/Widget.md","part":"body"}"""]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.StartsWith("# Widget.md", run.Output);
    }

    /// <summary>
    /// The reason <c>--args</c> is general rather than a convenience: an ordered list of operations
    /// with nested anchors has no flat option surface, and this is the shape that proves the record
    /// arrives structured.
    /// </summary>
    /// <summary>
    /// The reason <c>--args</c> is general rather than a convenience: an ordered list of operations
    /// with a nested anchor and a per-variant entry payload has no flat option surface. The refusal
    /// asserted here comes from the anchor, which is the innermost part of that structure - nothing
    /// but a genuinely nested payload reaches it.
    /// </summary>
    [Fact]
    public void Args_CarriesAStructureNoFlatOptionSurfaceExpresses()
    {
        var run = fixture.Run(["edit", "--args", $$"""{"path":"Projects/Widget/Widget.md","operations":[{{AnchoredAdd}}]}"""]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.EntryNotFound, run.Code);
    }

    /// <summary>A structured member has the same escape hatch per argument, so the flat surface is
    /// never the reason a call cannot be made.</summary>
    [Fact]
    public void AStructuredOption_TakesJson()
    {
        var run = fixture.Run(["edit", "--path", "Projects/Widget/Widget.md", "--operations", $"[{AnchoredAdd}]"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.EntryNotFound, run.Code);
    }

    /// <summary>An entry of the declared variant's fields, placed by an anchor - the shape whose
    /// innermost member is what the refusal above names.</summary>
    private const string AnchoredAdd =
        """{"op":"add_entry","key":"nav.contents","entry":{"link":"[[Gizmo-Reference]]","description":"d","load_when":"w"},"anchor":{"op":"before","target":"[[No-Such-Note]]"}}""";

    [Fact]
    public void AStructuredOptionCarryingSomethingThatIsNotJson_IsRefusedNamingTheArgument()
    {
        var run = fixture.Run(["edit", "--path", "Projects/Widget/Widget.md", "--operations", "set nav.contents"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
        Assert.Equal("operations", run.Envelope.GetProperty("data").GetProperty("argument").GetString());
    }

    [Fact]
    public void ArgsCarryingSomethingThatIsNotAnObject_IsRefused()
    {
        var run = fixture.Run(["read", "--args", "[1,2,3]"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
    }

    /// <summary>
    /// <c>--args</c> is the whole record, so an option beside it could only be a second, partial
    /// statement of the same record. Which one won would be a rule this build invented, so the
    /// combination is refused - and it is refused as a usage error, because it is a statement about
    /// the command line rather than about a tool.
    /// </summary>
    [Fact]
    public void Args_BesideAnOptionFormArgument_IsAUsageError()
    {
        var run = fixture.Run(["read", "--args", """{"path":"a.md"}""", "--path", "b.md"]);

        Assert.Equal(ExitCodes.UsageError, run.Exit);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("--args", run.Error);
    }

    /// <summary>The session filter is cosmetic, and this is what it rests on: a tool reached by name
    /// through <c>--args</c> still meets the execute path's own check.</summary>
    [Fact]
    public void Args_DoesNotReachATooNotInTheTree()
    {
        var run = fixture.Run(["mint_token", "--args", """{"op_class":"content:read"}"""]);

        Assert.Equal(ExitCodes.UsageError, run.Exit);
    }
}
