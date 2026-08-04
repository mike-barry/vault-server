namespace VaultShared.Tools;

/// <summary>
/// What a tool answers with. Two tiers, split by what the output is for: text a model reads
/// directly, and structure a caller branches on. Neither knows a transport - each front-end renders
/// both, and a tool never formats for one.
///
/// The hierarchy is closed by a private constructor, so the two tiers are the whole answer.
/// </summary>
public abstract record ToolResult
{
    private ToolResult() { }

    /// <summary>
    /// The content tier: note text, search hits, a path list. Returned in the plain shape an agent
    /// already consumes, because structure there costs tokens and buys nothing.
    /// </summary>
    public sealed record Content(string Text) : ToolResult;

    /// <summary>
    /// The structured tier: every mutation, and any assessment whose purpose is a decision.
    /// <paramref name="Value"/> is serialized under <see cref="NativeToolArguments.Options"/>, so
    /// one naming policy governs results and arguments alike and nothing translates between a sent
    /// name and a returned one.
    /// </summary>
    public sealed record Data(object Value) : ToolResult;
}
