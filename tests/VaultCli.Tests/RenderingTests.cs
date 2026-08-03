using System.Text.Json;
using VaultShared.Tests.Tools;
using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// What each stream carries, and what the exit code says. Standard output carries the answer and
/// nothing else, so a caller can pipe it; standard error carries the refusal envelope, which is
/// where the machine-readable vocabulary lives.
///
/// The parity cases come from <see cref="FrontEndParity"/>, which the MCP server's suite asserts
/// against as well - the two together are what makes "byte-identical whichever front-end answered" a
/// checked claim.
/// </summary>
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

    /// <summary>A structured result prints as JSON, because there is no text rendering of a mutation
    /// result that is not a second statement of it.</summary>
    [Fact]
    public void AStructuredResult_GoesToStandardOutputAsJson()
    {
        var run = fixture.Run(["delete", "--path", "Projects/Widget/Tasks.md", "--kind", "note"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        using var document = JsonDocument.Parse(run.Output);
        Assert.False(document.RootElement.GetProperty("applied").GetBoolean());
    }

    /// <summary>Under <c>--output json</c> a content result is an envelope too, so a script consuming
    /// this never has to sniff which tier answered before it can parse.</summary>
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
        var text = fixture.Run(["delete", "--path", "Projects/Widget/Tasks.md", "--kind", "note"]).Output;
        var json = fixture.Run(["delete", "--path", "Projects/Widget/Tasks.md", "--kind", "note", "--output", "json"]).Output;

        Assert.Equal(text, json);
    }

    [Fact]
    public void AnUnrecognizedOutputFormat_IsRefused()
    {
        var run = fixture.Run(["glob", "--pattern", "*", "--output", "yaml"]);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.InvalidArgument, run.Code);
    }

    /// <summary>
    /// A refusal goes to standard error as the shared envelope, in both output modes: a caller
    /// branches on <c>code</c> whichever way it asked for the answer.
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
    /// The CLI's half of the parity contract: the envelope it writes for a refusal is the bytes the
    /// contract names, which is the same string the MCP server's suite asserts of an <c>isError</c>
    /// result's text.
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

    /// <summary>The content tier is the answer itself, and nothing here reformats it - the same text
    /// the other front-end puts in its text block.</summary>
    [Fact]
    public void TheContentTier_IsWrittenUnchanged()
    {
        var output = new StringWriter();

        CliRendering.Answer(() => FrontEndParity.Content, OutputFormat.Text, output, TextWriter.Null);

        Assert.Equal("Projects/Widget/Widget.md\nProjects/Widget/Tasks.md", output.ToString().TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// The three exit codes, together, so the set is asserted as a set: nothing else answers, and no
    /// two conditions collapse onto one code.
    /// </summary>
    [Fact]
    public void TheThreeExitCodes_AreSuccessRefusalAndUsage()
    {
        Assert.Equal(ExitCodes.Success, fixture.Run(["glob", "--pattern", "Widget.md"]).Exit);
        Assert.Equal(ExitCodes.Refused, fixture.Run(["read", "--path", "Nowhere.md"]).Exit);
        Assert.Equal(ExitCodes.UsageError, fixture.Run(["read", "--nonsense"]).Exit);
        Assert.Equal(ExitCodes.UsageError, fixture.Run(["no_such_tool"]).Exit);
    }

    /// <summary>A narrow grant refuses a write at the floor, before anything is bound - and the
    /// operator's remedy is to rerun naming a wider one, which is what elevation is here.</summary>
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
