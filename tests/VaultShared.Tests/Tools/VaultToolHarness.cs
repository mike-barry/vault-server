using System.Text.Json;
using VaultShared.Auth;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Runs a single vault tool against a real directory tree without standing up a host. It performs
/// the two execute-path steps a tool's own answer depends on - binding the arguments onto the
/// declared record and calling <c>Validate</c> - and passes in the access object the execute path
/// would have built.
///
/// The session check, the op class floor and vault resolution are deliberately left out. They belong
/// to the execute path and are covered by its own tests, and a test that had to satisfy the floor
/// could not run a tool under a grant narrower than it.
/// </summary>
internal static class VaultToolHarness
{
    /// <summary>Binds and validates the arguments, then invokes the tool.</summary>
    /// <param name="tool">The tool to invoke.</param>
    /// <param name="declaration">The declaration naming the tool's argument record.</param>
    /// <param name="vault">The scratch vault supplying the access object and the grant.</param>
    /// <param name="arguments">The arguments, as an anonymous object.</param>
    /// <returns>The result the tool returned.</returns>
    /// <exception cref="ToolRefusal">The arguments failed validation, or the tool refused.</exception>
    public static ToolResult Invoke(ITool tool, ToolDeclaration declaration, ScratchVault vault, object arguments)
    {
        var bound = NativeToolArguments.Bind(declaration.Arguments, Args(arguments));
        if (bound.Validate() is { } fault)
            throw new ToolRefusal(ToolCodes.InvalidArgument, fault.Message, new { argument = fault.Argument });

        // The tool is handed the grant the vault was built with, so a test narrows authority in one
        // place and any mid-operation check sees the same grant the scope check saw.
        return tool.Invoke(new ToolCall(bound, vault.Access, vault.Grant, null));
    }

    /// <summary>Invokes the tool and returns the text of a content result.</summary>
    /// <param name="tool">The tool to invoke.</param>
    /// <param name="declaration">The declaration naming the tool's argument record.</param>
    /// <param name="vault">The scratch vault supplying the access object and the grant.</param>
    /// <param name="arguments">The arguments, as an anonymous object.</param>
    /// <returns>The result's text.</returns>
    public static string Text(ITool tool, ToolDeclaration declaration, ScratchVault vault, object arguments) =>
        Assert.IsType<ToolResult.Content>(Invoke(tool, declaration, vault, arguments)).Text;

    /// <summary>Invokes the tool and returns the refusal it raised, failing the test if it did
    /// not.</summary>
    /// <param name="tool">The tool to invoke.</param>
    /// <param name="declaration">The declaration naming the tool's argument record.</param>
    /// <param name="vault">The scratch vault supplying the access object and the grant.</param>
    /// <param name="arguments">The arguments, as an anonymous object.</param>
    /// <returns>The refusal the call raised.</returns>
    public static ToolRefusal Refusal(ITool tool, ToolDeclaration declaration, ScratchVault vault, object arguments) =>
        Assert.Throws<ToolRefusal>(() => Invoke(tool, declaration, vault, arguments));

    /// <summary>Reads a refusal's <c>data</c> off the rendered envelope, so the member names are the
    /// ones a caller sees.</summary>
    /// <param name="refusal">The refusal to read.</param>
    /// <returns>Each member's name mapped to its raw JSON, or null if the refusal carries no
    /// payload.</returns>
    public static Dictionary<string, string>? Data(ToolRefusal refusal)
    {
        using var document = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        return document.RootElement.TryGetProperty("data", out var data)
            ? data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString())
            : null;
    }

    /// <summary>
    /// Asserts that a plan and the result of the applied call have the same members and differ only
    /// in the discriminating one.
    ///
    /// It compares the serialized results rather than the records themselves, because a mutation
    /// result contains lists and record equality over a list is reference equality. It also compares
    /// what a caller would see, under the same naming policy.
    /// </summary>
    /// <param name="plan">The result of the call made without <c>apply</c>.</param>
    /// <param name="applied">The result of the same call made with <c>apply</c>.</param>
    /// <param name="discriminator">The wire name of the member expected to differ.</param>
    public static void AssertPreviewParity(object plan, object applied, string discriminator = "applied")
    {
        var planned = Members(plan);
        var real = Members(applied);

        Assert.Equal("false", planned[discriminator]);
        Assert.Equal("true", real[discriminator]);

        planned.Remove(discriminator);
        real.Remove(discriminator);
        Assert.Equal(planned, real);
    }

    private static Dictionary<string, string> Members(object result)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, NativeToolArguments.Options));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText());
    }

    /// <summary>Builds the raw argument map a front-end would hand to the execute path.</summary>
    /// <param name="value">The arguments, as an anonymous object.</param>
    /// <returns>Each argument's wire name mapped to its JSON value.</returns>
    public static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
}
