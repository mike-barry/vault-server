using System.Text.Json;
using VaultShared.Tools;

namespace VaultCli;

/// <summary>Specifies how an answer is rendered on standard output.</summary>
public enum OutputFormat
{
    /// <summary>Writes a content result as it is, and a structured result as JSON. There is no text
    /// rendering of a mutation result that would not restate it.</summary>
    Text,

    /// <summary>Writes both result tiers as a JSON document, so a script never has to detect which
    /// tier answered before parsing.</summary>
    Json,
}

/// <summary>
/// Defines the CLI's three exit codes. The machine-readable vocabulary is the <c>code</c> in the
/// refusal envelope, not the exit code: an exit code per error class would be a coarser second copy
/// of that catalogue and would drift from it.
/// </summary>
public static class ExitCodes
{
    /// <summary>The tool answered.</summary>
    public const int Success = 0;

    /// <summary>The invocation was refused, and the envelope on standard error says why. A refusal
    /// from a tool and one from this front-end's own checks mean the same thing to a caller: nothing
    /// ran, and <c>code</c> says why.</summary>
    public const int Refused = 1;

    /// <summary>The parser refused the command line itself - an unknown subcommand, an unknown
    /// option, or a missing option value. No tool was reached.</summary>
    public const int UsageError = 2;
}

/// <summary>
/// Renders a tool result and every refusal that can reach this front-end. Neither shape knows the
/// CLI exists, and the refusal envelope comes from <see cref="ToolRefusalEnvelope"/>, which is
/// shared with the MCP server, so the same refusal is byte-identical whichever front-end a caller
/// used.
/// </summary>
public static class CliRendering
{
    /// <summary>
    /// Runs <paramref name="invoke"/> and renders what comes back: the result on standard output, a
    /// refusal envelope on standard error. Standard output carries the answer and nothing else, so a
    /// caller can pipe it.
    /// </summary>
    /// <param name="invoke">Runs the tool and returns its result.</param>
    /// <param name="format">How to render a successful result.</param>
    /// <param name="output">Where the result is written.</param>
    /// <param name="error">Where a refusal envelope is written.</param>
    /// <returns><see cref="ExitCodes.Success"/> or <see cref="ExitCodes.Refused"/>.</returns>
    public static int Answer(Func<ToolResult> invoke, OutputFormat format, TextWriter output, TextWriter error)
    {
        try
        {
            Render(invoke(), format, output);
            return ExitCodes.Success;
        }
        catch (CliFailure failure)
        {
            return Refuse(failure.Refusal, error);
        }
        catch (Exception exception) when (ToolRefusalEnvelope.Refusal(exception) is { } refusal)
        {
            return Refuse(refusal, error);
        }
    }

    /// <summary>Writes the refusal envelope to standard error.</summary>
    /// <param name="refusal">The refusal to render.</param>
    /// <param name="error">Where the envelope is written.</param>
    /// <returns><see cref="ExitCodes.Refused"/>.</returns>
    public static int Refuse(ToolRefusal refusal, TextWriter error)
    {
        error.WriteLine(ToolRefusalEnvelope.Json(refusal));
        return ExitCodes.Refused;
    }

    /// <summary>Writes a successful result in the requested format.</summary>
    /// <param name="result">The tool's result.</param>
    /// <param name="format">How to render it.</param>
    /// <param name="output">Where the result is written.</param>
    /// <exception cref="InvalidOperationException">The result is of a kind this does not
    /// render.</exception>
    private static void Render(ToolResult result, OutputFormat format, TextWriter output)
    {
        switch (result)
        {
            // The text is written unchanged. The line terminator marks the end of the answer for the
            // shell and is not part of it.
            case ToolResult.Content content when format is OutputFormat.Text:
                output.WriteLine(content.Text);
                break;

            // Under --output json the content tier is wrapped so that standard output is a JSON
            // document whichever tier answered. The structured tier already is one, so it is written
            // as it is rather than wrapped a second time.
            case ToolResult.Content content:
                output.WriteLine(JsonSerializer.Serialize(new ContentEnvelope(content.Text), NativeToolArguments.Options));
                break;

            case ToolResult.Data data:
                output.WriteLine(JsonSerializer.Serialize(data.Value, NativeToolArguments.Options));
                break;

            default:
                throw new InvalidOperationException($"Unhandled tool result {result.GetType().Name}.");
        }
    }

    /// <summary>Wraps the content tier so it can be written as a JSON document. It is a record
    /// rather than an anonymous type so that its wire name comes from the shared naming policy, as a
    /// result's own members do.</summary>
    /// <param name="Content">The tool's text answer.</param>
    private sealed record ContentEnvelope(string Content);
}
