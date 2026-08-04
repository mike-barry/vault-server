namespace VaultShared.Tools;

/// <summary>
/// Represents what a tool returns: either text a model reads directly, or structured data a caller
/// branches on. The private constructor closes the hierarchy to these two cases.
/// </summary>
/// <remarks>
/// Neither case knows anything about a transport. Each front-end renders both, and a tool never
/// formats for a particular front-end.
/// </remarks>
public abstract record ToolResult
{
    private ToolResult() { }

    /// <summary>
    /// Represents a textual result: note text, search hits, a path list. Returned in the plain shape
    /// an agent already consumes, because structure here costs tokens and buys nothing.
    /// </summary>
    /// <param name="Text">The result text.</param>
    public sealed record Content(string Text) : ToolResult;

    /// <summary>
    /// Represents a structured result, used for every mutation and for any assessment whose purpose
    /// is a decision.
    /// </summary>
    /// <param name="Value">The value to serialize. It is serialized with
    /// <see cref="NativeToolArguments.Options"/>, so one naming policy governs results and arguments
    /// alike and nothing translates between a name sent and a name returned.</param>
    public sealed record Data(object Value) : ToolResult;
}
