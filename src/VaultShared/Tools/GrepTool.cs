using System.ComponentModel;
using System.Globalization;
using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>How much of a content search's answer comes back. The three the layer offers, and the
/// three the spec fixes.</summary>
public enum GrepGranularity
{
    /// <summary>Every matching line, with its path and line number.</summary>
    Lines,

    /// <summary>The set of notes containing a match.</summary>
    Files,

    /// <summary>How many lines matched in each note that has a match.</summary>
    Counts,
}

/// <summary>
/// <see cref="GrepTool"/>'s arguments. <see cref="Granularity"/> defaults to the richest form, which
/// is what a caller reading results wants; <see cref="PathGlob"/> is absent by default and then the
/// whole note domain is searched.
/// </summary>
public sealed record GrepArguments(
    [property: Description("Regular expression, in the search engine's syntax, matched against note content.")]
    string Pattern,
    [property: Description("The vault to search in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("How much of the answer to return: every matching line, the notes containing a match, or a match count per note. Defaults to lines.")]
    GrepGranularity Granularity = GrepGranularity.Lines,
    [property: Description("Optional glob narrowing which notes are searched, matched the same way glob's pattern is.")]
    string? PathGlob = null) : IVaultArguments
{
    /// <inheritdoc cref="ReadArguments.Validate"/>
    public ArgumentFault? Validate() =>
        ArgumentRules.Missing(Pattern, "pattern")
        ?? ArgumentRules.Unrecognized(Granularity, "granularity");
}

/// <summary>
/// The only content-search surface, at the three granularities the layer offers.
///
/// Like <see cref="GlobTool"/> it treats any count as a result and asks the note domain, and like it
/// it names no path: <c>path_glob</c> narrows which notes are searched and is filtered rather than
/// gated, so an out-of-scope note is simply absent from the answer.
/// </summary>
public sealed class GrepTool : ITool
{
    public const string ToolName = "grep";

    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Searches note content by regular expression. Returns \"path:line:text\" per match, one " +
            "path per line at the file granularity, or \"path:count\" for counts - and empty output " +
            "when nothing matched.",
        Arguments = typeof(GrepArguments),
        OpClassFloor = new Floor.Class("content:read"),
    };

    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<GrepArguments>();
        var vault = call.RequireVault(ToolName);

        // Each granularity is its own capability on the layer rather than the richest one with the
        // detail discarded, so a caller asking for a file list does not pay to materialize every
        // match in order to throw them away.
        return arguments.Granularity switch
        {
            GrepGranularity.Lines => Render(
                vault.SearchLines(arguments.Pattern, arguments.PathGlob),
                match => $"{match.Path.Value}:{match.LineNumber.ToString(CultureInfo.InvariantCulture)}:{match.Text}"),

            GrepGranularity.Files => Render(
                vault.SearchFiles(arguments.Pattern, arguments.PathGlob),
                path => path.Value),

            GrepGranularity.Counts => Render(
                vault.SearchCounts(arguments.Pattern, arguments.PathGlob),
                count => $"{count.Path.Value}:{count.Count.ToString(CultureInfo.InvariantCulture)}"),

            var other => throw new InvalidOperationException($"Unhandled grep granularity {other}."),
        };
    }

    /// <summary>One line per result, and empty text for the empty result - never a failure.</summary>
    private static ToolResult Render<TResult>(SearchOutcome<TResult> outcome, Func<TResult, string> line) =>
        outcome is SearchOutcome<TResult>.Results results
            ? new ToolResult.Content(string.Join("\n", results.Items.Select(line)))
            : throw ToolRefusalEnvelope.Translate(outcome);
}
