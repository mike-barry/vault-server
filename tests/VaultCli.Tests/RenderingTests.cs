using System.Text.Json;
using VaultShared.Tests.Tools;
using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// Verifies what each output stream carries and what the exit code reports. Standard output carries
/// the answer and nothing else, so a caller can pipe it; standard error carries the refusal
/// envelope with its machine-readable code.
/// </summary>
/// <remarks>
/// The parity cases come from <see cref="FrontEndParity"/>, which the MCP server's suite asserts
/// against as well, so the two front-ends are checked against the same expected bytes.
/// </remarks>
public sealed class RenderingTests(CliFixture fixture) : IClassFixture<CliFixture>
{
    [Fact]
    public void AContentResult_GoesToStandardOutputAsItIs()
    {
        var run = fixture.Run(["glob", "--pattern", "Widget.md"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("Projects/Widget/Widget.md", run.Output.TrimEnd('\r', '\n'));
        Assert.Equal(string.Empty, run.Error);
    }

    /// <summary>A structured result prints as JSON even in text mode, because any text rendering of
    /// a mutation result would just restate the same fields.</summary>
    [Fact]
    public void AStructuredResult_GoesToStandardOutputAsJson()
    {
        var run = fixture.Run(["delete", "--path", "Projects/Widget/Tasks.md", "--kind", "note"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        using var document = JsonDocument.Parse(run.Output);
        Assert.False(document.RootElement.GetProperty("applied").GetBoolean());
    }

    /// <summary>Under <c>--output json</c> a content result is wrapped in an envelope as well, so a
    /// script can parse the output without first working out which result tier answered.</summary>
    [Fact]
    public void OutputJson_RendersAContentResultAsADocumentToo()
    {
        var run = fixture.Run(["glob", "--pattern", "Widget.md", "--output", "json"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        using var document = JsonDocument.Parse(run.Output);
        Assert.Equal("Projects/Widget/Widget.md", document.RootElement.GetProperty("content").GetString());
    }

    /// <summary>The structured tier is already a document and is not wrapped a second time.</summary>
    [Fact]
    public void OutputJson_LeavesAStructuredResultAlone()
    {
        var text = fixture.Run(["delete", "--path", "Projects/Widget/Tasks.md", "--kind", "note"]);
        var json = fixture.Run(["delete", "--path", "Projects/Widget/Tasks.md", "--kind", "note", "--output", "json"]);

        // Both runs have to have answered before their outputs are compared, or two refusals that
        // wrote nothing to standard output would satisfy the comparison.
        Assert.Equal(ExitCodes.Success, text.Exit);
        Assert.Equal(ExitCodes.Success, json.Exit);
        Assert.Equal(text.Output, json.Output);
    }

    [Fact]
    public void AnUnrecognizedOutputFormat_IsRefused()
    {
        var run = fixture.Run(["glob", "--pattern", "*", "--output", "yaml"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
    }

    /// <summary>
    /// A refusal is written to standard error as the shared envelope in both output modes, so a
    /// caller branches on <c>code</c> whichever format it asked for.
    /// </summary>
    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    public void ARefusal_IsTheEnvelopeOnStandardErrorInEitherMode(string format)
    {
        var run = fixture.Run(["read", "--path", "Nowhere.md", "--output", format]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(string.Empty, run.Output);
        Assert.Equal(ToolCodes.PathNotFound, run.Code);
    }

    /// <summary>
    /// The CLI's half of the parity contract. The MCP server's suite asserts the same expected
    /// string of the text in an <c>isError</c> result.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusalNames))]
    public void ARefusalOfEveryVocabulary_IsWrittenAsTheContractedEnvelope(string name)
    {
        var refusalCase = FrontEndParity.Refusals.Single(c => c.Name == name);
        var error = new StringWriter();

        var exit = CliRendering.Answer(() => throw refusalCase.Raised, OutputFormat.Text, TextWriter.Null, error);

        Assert.Equal(ExitCodes.Refused, exit);
        Assert.Equal(refusalCase.Envelope, error.ToString().TrimEnd('\r', '\n'));
    }

    public static TheoryData<string> RefusalNames() => [.. FrontEndParity.Refusals.Select(c => c.Name)];

    [Fact]
    public void TheStructuredTier_IsSerializedUnderTheSharedOptions()
    {
        var output = new StringWriter();

        CliRendering.Answer(() => FrontEndParity.Structured, OutputFormat.Text, output, TextWriter.Null);

        Assert.Equal(FrontEndParity.StructuredJson, output.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>A content result is written through unchanged, matching the text the MCP front-end
    /// puts in its text block.</summary>
    [Fact]
    public void TheContentTier_IsWrittenUnchanged()
    {
        var output = new StringWriter();

        CliRendering.Answer(() => FrontEndParity.Content, OutputFormat.Text, output, TextWriter.Null);

        Assert.Equal("Projects/Widget/Widget.md\nProjects/Widget/Tasks.md", output.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// Asserts the three exit codes together, so no two conditions collapse onto the same one.
    /// </summary>
    [Fact]
    public void TheThreeExitCodes_AreSuccessRefusalAndUsage()
    {
        Assert.Equal(ExitCodes.Success, fixture.Run(["glob", "--pattern", "Widget.md"]).Exit);
        Assert.Equal(ExitCodes.Refused, fixture.Run(["read", "--path", "Nowhere.md"]).Exit);
        Assert.Equal(ExitCodes.UsageError, fixture.Run(["read", "--nonsense"]).Exit);
        Assert.Equal(ExitCodes.UsageError, fixture.Run(["no_such_tool"]).Exit);
    }

    /// <summary>A narrow grant refuses a write at the operation-class floor, before arguments are
    /// bound. The operator's remedy is to rerun with <c>--grant</c> naming a wider grant.</summary>
    [Fact]
    public void AWriteUnderANarrowGrant_IsRefusedAtTheFloor()
    {
        var run = fixture.Run(
        [
            "--grant", CliFixture.ReadOnlyGrant,
            "delete", "--path", "Projects/Widget/Tasks.md", "--kind", "note", "--apply",
        ]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.AuthorityRequired, run.Code);
        Assert.Equal("content:write", run.Envelope.GetProperty("data").GetProperty("required_op_class").GetString());
    }

    [Fact]
    public void AReadUnderTheSameNarrowGrant_Succeeds()
    {
        var run = fixture.Run(["--grant", CliFixture.ReadOnlyGrant, "glob", "--pattern", "Widget.md"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
    }
}
