using System.Text.Json;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the part of the front-end parity contract that needs neither front-end: that
/// <see cref="ToolRefusalEnvelope"/> renders each catchable exception to the expected bytes, and
/// that a result has a single serialization. Each front-end's own tests then compare its output
/// against the same expected values in <see cref="FrontEndParity"/>.
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
    /// The set of exceptions that translate into a refusal is decided here rather than in each
    /// front-end. Anything else returns null, so a front-end lets it propagate as a failure instead
    /// of wrapping it in an envelope with an invented code.
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
    /// A content result is the answer itself. Neither front-end may reformat it, so the expected
    /// value is the text unchanged.
    /// </summary>
    [Fact]
    public void TheContentTier_IsTextNothingRenders()
    {
        var content = Assert.IsType<ToolResult.Content>(FrontEndParity.Content);

        Assert.Equal("Projects/Widget/Widget.md\nProjects/Widget/Tasks.md", content.Text);
    }
}
