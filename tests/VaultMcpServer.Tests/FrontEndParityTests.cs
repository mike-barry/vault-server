using ModelContextProtocol.Protocol;
using VaultMcpServer.Tools;
using VaultShared.Tests.Tools;
using VaultShared.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// This front-end's half of the parity contract (<see cref="FrontEndParity"/>): every refusal
/// vocabulary arrives in the text of an <c>isError</c> result as exactly the bytes the contract
/// names.
///
/// The CLI's suite asserts the same values against standard error, which is what makes
/// "byte-identical whichever front-end answered" a checked claim rather than an intention. Result
/// rendering over this transport is <c>VaultToolDispatchTests</c>', where a real call produces it.
/// </summary>
public sealed class FrontEndParityTests
{
    public static TheoryData<string> RefusalNames() => [.. FrontEndParity.Refusals.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(RefusalNames))]
    public void ARefusal_IsTheWholeTextOfAnErrorResult(string name)
    {
        var refusalCase = FrontEndParity.Refusals.Single(c => c.Name == name);

        var result = ToolError.Result(ToolRefusalEnvelope.Refusal(refusalCase.Raised)!);

        Assert.True(result.IsError);
        Assert.Equal(refusalCase.Envelope, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

}
