using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// Verifies how an option's text is bound to an argument record, and how <c>--args</c> binds a
/// whole record instead. An option's value is interpreted from the record member's own JSON shape
/// alone; the CLI knows nothing tool-specific. A value the shape cannot take is therefore refused
/// by the binder in the tool's own error vocabulary rather than by the parser as a usage error.
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

    /// <summary>A boolean binds as a flag. An omitted flag is absent from the argument map rather
    /// than present as false, so the record's own default applies.</summary>
    [Fact]
    public void AnOmittedFlag_LeavesTheRecordsOwnDefault()
    {
        var run = fixture.Run(["read", "--path", "Projects/Widget/Widget.md"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.DoesNotContain("content_hash", run.Output);
    }

    /// <summary>
    /// An enumerated value is passed through as its wire spelling and is not validated by the CLI.
    /// An unrecognized value is refused in the tool's own error vocabulary, naming the argument,
    /// just as it is for an MCP caller.
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
        // The frontmatter block's first key, so the value bound as "frontmatter" rather than the
        // absence of the body being satisfied by an empty answer.
        Assert.StartsWith("nav.contents:", run.Output);
        Assert.DoesNotContain("# Widget.md", run.Output);
    }

    /// <summary>A required argument left out is refused by the record's own validation, naming the
    /// argument. An MCP caller that omitted it gets the same answer.</summary>
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
    /// Shows why <c>--args</c> is general rather than a convenience: an ordered list of operations
    /// with a nested anchor and a per-variant entry payload has no flat option surface. The refusal
    /// asserted here comes from the anchor, the innermost part of that structure, so only a
    /// genuinely nested payload can produce it.
    /// </summary>
    [Fact]
    public void Args_CarriesAStructureNoFlatOptionSurfaceExpresses()
    {
        var run = fixture.Run(["edit", "--args", $$"""{"path":"Projects/Widget/Widget.md","operations":[{{AnchoredAdd}}]}"""]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.EntryNotFound, run.Code);
    }

    /// <summary>A structured member accepts JSON on its own option, so the flat option surface is
    /// never the reason a call cannot be made.</summary>
    [Fact]
    public void AStructuredOption_TakesJson()
    {
        var run = fixture.Run(["edit", "--path", "Projects/Widget/Widget.md", "--operations", $"[{AnchoredAdd}]"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.EntryNotFound, run.Code);
    }

    /// <summary>An add_entry operation carrying the declared variant's fields and an anchor. The
    /// anchor names a note that does not exist, which is what the refusals above assert on.</summary>
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

    /// <summary>
    /// The raw-JSON escape hatch is the one route on this front-end that can carry a member no
    /// argument record declares, and such a member is refused naming it rather than dropped. The
    /// option form cannot produce one, because every option is generated from the record's own
    /// members, so an unrecognized option is a parser usage error instead.
    /// </summary>
    [Fact]
    public void ArgsCarryingAMemberTheRecordDoesNotDeclare_IsRefusedNamingIt()
    {
        var run = fixture.Run(["read", "--args", """{"path":"Projects/Widget/Widget.md","prt":"body"}"""]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
        Assert.Equal("prt", run.Envelope.GetProperty("data").GetProperty("argument").GetString());
    }

    [Fact]
    public void ArgsCarryingSomethingThatIsNotAnObject_IsRefused()
    {
        var run = fixture.Run(["read", "--args", "[1,2,3]"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
        // Named as "args" rather than as a member of the record, because nothing was bound: the
        // fault is in the option's own value.
        Assert.Equal("args", run.Envelope.GetProperty("data").GetProperty("argument").GetString());
    }

    /// <summary>
    /// <c>--args</c> supplies the whole argument record, so an option alongside it would be a
    /// second, partial statement of the same record with no defined precedence between them. The
    /// combination is refused as a usage error, because the fault is in the command line rather
    /// than in the arguments a tool received.
    /// </summary>
    [Fact]
    public void Args_BesideAnOptionFormArgument_IsAUsageError()
    {
        var run = fixture.Run(["read", "--args", """{"path":"a.md"}""", "--path", "b.md"]);

        Assert.Equal(ExitCodes.UsageError, run.Exit);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("--args", run.Error);
    }

    /// <summary>Naming a tool through <c>--args</c> does not bypass the command tree's filtering.
    /// A tool the tree omits is still not reachable this way.</summary>
    [Fact]
    public void Args_DoesNotReachATooNotInTheTree()
    {
        var run = fixture.Run(["mint_token", "--args", """{"op_class":"content:read"}"""]);

        Assert.Equal(ExitCodes.UsageError, run.Exit);
    }
}
