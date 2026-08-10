using System.ComponentModel;
using System.Globalization;
using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>Selects how much of a content search's answer comes back. These three granularities are
/// the whole set; the access layer offers no others.</summary>
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
/// Represents <see cref="GrepTool"/>'s arguments.
/// </summary>
/// <param name="Pattern">The regular expression to match against note content.</param>
/// <param name="Vault">The vault to search, or null for the configured default.</param>
/// <param name="Granularity">How much of the answer to return. Defaults to the richest form, which
/// is what a caller reading results wants.</param>
/// <param name="PathGlob">A glob narrowing which notes are searched, or null to search every
/// note.</param>
public sealed record GrepArguments(
    [property: Description("Regular expression, in the search engine's syntax, matched against note content. Matching is case-sensitive; prefix the pattern with (?i) to match without regard to case.")]
    string Pattern,
    [property: Description("The vault to search in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("How much of the answer to return: every matching line, the notes containing a match, or a match count per note. Defaults to lines.")]
    GrepGranularity Granularity = GrepGranularity.Lines,
    [property: Description("Optional glob narrowing which notes are searched, matched the same way glob's pattern is.")]
    string? PathGlob = null) : IVaultArguments
{
    /// <summary>Checks that a pattern was supplied and that the granularity is one this tool
    /// declares.</summary>
    /// <returns>The first fault found, or null. Both are refused here rather than by the binder, so
    /// each reaches the caller as a coded refusal naming the argument.</returns>
    public ArgumentFault? Validate() =>
        ArgumentRules.Missing(Pattern, "pattern")
        ?? ArgumentRules.Unrecognized(Granularity, "granularity");
}

/// <summary>
/// Searches note content by regular expression, at the three granularities the access layer offers.
/// This is the only content search.
/// </summary>
/// <remarks>
/// It searches notes only and treats any number of matches as a result. The caller names no path.
/// <c>path_glob</c> narrows which notes are searched and its results are filtered against the read
/// scope, so a note the caller may not reach is simply absent from the answer rather than refused.
/// </remarks>
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

        // Each granularity is its own call on the access layer rather than the richest one with the
        // detail discarded, so a caller asking for a file list does not pay to materialize every
        // match and throw it away.
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

    /// <summary>Renders a search outcome as one line per result.</summary>
    /// <typeparam name="TResult">The result type for this granularity.</typeparam>
    /// <param name="outcome">The search outcome.</param>
    /// <param name="line">Formats one result as a line.</param>
    /// <returns>The joined lines, or empty text where nothing matched. No match is never a
    /// failure.</returns>
    /// <exception cref="ToolRefusal">The search failed or the pattern was refused.</exception>
    private static ToolResult Render<TResult>(SearchOutcome<TResult> outcome, Func<TResult, string> line) =>
        outcome is SearchOutcome<TResult>.Results results
            ? new ToolResult.Content(string.Join("\n", results.Items.Select(line)))
            : throw ToolRefusalEnvelope.Translate(outcome);
}
