using ModelContextProtocol.Protocol;
using VaultMcpServer.Tools;
using VaultShared.Tests.Tools;
using VaultShared.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Checks this front-end's half of the parity contract in <see cref="FrontEndParity"/>. Every
/// refusal arrives in the text of an <c>isError</c> result as exactly the bytes the contract names.
/// </summary>
/// <remarks>
/// Rendering of successful results over this transport is covered by
/// <c>VaultToolDispatchTests</c>, where a real call produces one.
/// </remarks>
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
