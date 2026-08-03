using System.Text.Json;
using VaultShared.Tools;

namespace VaultCli;

/// <summary>How an answer is rendered on standard output.</summary>
public enum OutputFormat
{
    /// <summary>A content result as it is; a structured result as JSON, there being no text
    /// rendering of a mutation result that is not a second statement of it.</summary>
    Text,

    /// <summary>Both tiers as a JSON document, so a script consuming this never has to sniff which
    /// tier answered before it can parse.</summary>
    Json,
}

/// <summary>
/// The CLI's three exit codes. <b>The machine-readable vocabulary is the <c>code</c> in the refusal
/// envelope</b>, not this - an exit code per error class would be a second, coarser copy of the
/// catalogue that cannot represent it and drifts from it.
/// </summary>
public static class ExitCodes
{
    /// <summary>The tool answered.</summary>
    public const int Success = 0;

    /// <summary>The invocation was refused, and the envelope on standard error says by what. A
    /// refusal from a tool and one from this front-end's own fail-closed checks are the same answer
    /// to the caller: nothing ran, and <c>code</c> says why.</summary>
    public const int Refused = 1;

    /// <summary>The parser refused the command line itself - an unknown subcommand, an unknown
    /// option, a missing option value. Nothing about a tool was reached.</summary>
    public const int UsageError = 2;
}

/// <summary>
/// The CLI's rendering of the neutral result and of every refusal that can reach it. Neither shape
/// knows this front-end exists, and the envelope itself is
/// <see cref="ToolRefusalEnvelope"/>'s - shared with the MCP server, so the same refusal is
/// byte-identical whichever front-end a caller reached the tool through.
/// </summary>
public static class CliRendering
{
    /// <summary>
    /// Runs <paramref name="invoke"/> and renders whatever comes back: the result on standard
    /// output, a refusal envelope on standard error. Standard output carries the answer and nothing
    /// else, so a caller can pipe it.
    /// </summary>
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

    /// <summary>The envelope on standard error, and the refusal exit code.</summary>
    public static int Refuse(ToolRefusal refusal, TextWriter error)
    {
        error.WriteLine(ToolRefusalEnvelope.Json(refusal));
        return ExitCodes.Refused;
    }

    private static void Render(ToolResult result, OutputFormat format, TextWriter output)
    {
        switch (result)
        {
            // The text is written unchanged; the line terminator is the shell's convention for the
            // end of an answer, not part of it.
            case ToolResult.Content content when format is OutputFormat.Text:
                output.WriteLine(content.Text);
                break;

            // Under --output json the content tier is wrapped so that standard output is a JSON
            // document whichever tier answered. The structured tier already is one and is left
            // alone rather than wrapped a second time.
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

    /// <summary>The content tier as a JSON document. A record rather than an anonymous type, so its
    /// one wire name comes from the shared naming policy exactly as a result's members do.</summary>
    private sealed record ContentEnvelope(string Content);
}
