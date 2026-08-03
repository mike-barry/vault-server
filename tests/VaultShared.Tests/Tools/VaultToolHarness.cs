using System.Text.Json;
using VaultShared.Auth;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Drives one vault tool over a real tree. It runs exactly the two execute-path steps a tool's own
/// answer depends on - binding onto the declared record, then <c>Validate</c> - and hands in the
/// access object the execute path would have constructed, so a case reads the tool's answer without
/// standing up a host.
///
/// The session check, the op-class floor and vault resolution are deliberately absent: they are the
/// execute path's, they have their own coverage, and a tool-body case that had to satisfy them could
/// not exercise a grant narrower than the tool's floor.
/// </summary>
internal static class VaultToolHarness
{
    public static ToolResult Invoke(ITool tool, ToolDeclaration declaration, ScratchVault vault, object arguments)
    {
        var bound = NativeToolArguments.Bind(declaration.Arguments, Args(arguments));
        if (bound.Validate() is { } fault)
            throw new ToolRefusal(ToolCodes.InvalidArgument, fault.Message, new { argument = fault.Argument });

        // The grant the tool is handed is the one the vault was built with, so a case narrowing
        // authority narrows it in one place and the mid-operation assertion sees the same grant the
        // scope check did.
        return tool.Invoke(new ToolCall(bound, vault.Access, vault.Grant, null));
    }

    /// <summary>The content tier's text.</summary>
    public static string Text(ITool tool, ToolDeclaration declaration, ScratchVault vault, object arguments) =>
        Assert.IsType<ToolResult.Content>(Invoke(tool, declaration, vault, arguments)).Text;

    /// <summary>The refusal a call raises, for a case whose subject is the refusal.</summary>
    public static ToolRefusal Refusal(ITool tool, ToolDeclaration declaration, ScratchVault vault, object arguments) =>
        Assert.Throws<ToolRefusal>(() => Invoke(tool, declaration, vault, arguments));

    /// <summary>The refusal's <c>data</c> as a name-to-raw-JSON map, read off the rendered envelope
    /// so the names are the wire's.</summary>
    public static Dictionary<string, string>? Data(ToolRefusal refusal)
    {
        using var document = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        return document.RootElement.TryGetProperty("data", out var data)
            ? data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString())
            : null;
    }

    /// <summary>The raw argument map, as a front-end hands one in.</summary>
    public static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
}
