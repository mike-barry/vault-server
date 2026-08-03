using System.Text.Json;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The half of the parity claim that is provable without either front-end: that one writer produces
/// the contract's bytes from the refusal each front-end catches, and that the neutral result has one
/// rendering. Each front-end's suite then asserts its own emission against the same values, which is
/// what closes the claim at both ends.
/// </summary>
public sealed class FrontEndParityTests
{
    public static TheoryData<string> RefusalNames() => [.. FrontEndParity.Refusals.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(RefusalNames))]
    public void EveryRefusalVocabulary_RendersToTheContractedEnvelope(string name)
    {
        var refusalCase = FrontEndParity.Refusals.Single(c => c.Name == name);

        var refusal = ToolRefusalEnvelope.Refusal(refusalCase.Raised);

        Assert.NotNull(refusal);
        Assert.Equal(refusalCase.Envelope, ToolRefusalEnvelope.Json(refusal));
    }

    /// <summary>
    /// The set of vocabularies is closed, and closed here rather than in each front-end's catch. An
    /// exception that is not a refusal answers null, so a front-end lets it travel as the failure it
    /// is rather than laundering it into an envelope with an invented code.
    /// </summary>
    [Fact]
    public void SomethingThatIsNotARefusal_IsNotTranslated() =>
        Assert.Null(ToolRefusalEnvelope.Refusal(new InvalidOperationException("A wiring defect is not a refusal.")));

    [Fact]
    public void TheStructuredTier_HasOneSerialization()
    {
        var data = Assert.IsType<ToolResult.Data>(FrontEndParity.Structured);

        Assert.Equal(FrontEndParity.StructuredJson, JsonSerializer.Serialize(data.Value, NativeToolArguments.Options));
    }

    /// <summary>
    /// The content tier is the answer itself. Neither front-end may reformat it, so what the
    /// contract carries is the text unchanged - which is what both are then asserted to emit.
    /// </summary>
    [Fact]
    public void TheContentTier_IsTextNothingRenders()
    {
        var content = Assert.IsType<ToolResult.Content>(FrontEndParity.Content);

        Assert.Equal("Projects/Widget/Widget.md\nProjects/Widget/Tasks.md", content.Text);
    }
}
