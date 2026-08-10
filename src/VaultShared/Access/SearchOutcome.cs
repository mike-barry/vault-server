namespace VaultShared.Access;

/// <summary>
/// Represents the result of an enumeration or a content search: a possibly empty list of results,
/// or one of four failures. The private constructor closes the hierarchy, so a caller that handles
/// every nested record has handled every case.
/// </summary>
/// <remarks>
/// An empty <see cref="Results"/> is the correct answer to a well-formed question that matched
/// nothing. It is not a failure, so a caller never has to read an error message to find out whether
/// anything went wrong.
/// </remarks>
/// <typeparam name="TResult">
/// One result: a <see cref="VaultPath"/> for the two enumeration surfaces and for the file
/// granularity of search, a <see cref="MatchLine"/> for matching lines, a <see cref="MatchCount"/>
/// for counts.
/// </typeparam>
public abstract record SearchOutcome<TResult>
{
    private SearchOutcome() { }

    /// <summary>The search completed.</summary>
    /// <param name="Items">
    /// The results in the engine's order, filtered to what the caller may read. May be empty.
    /// </param>
    public sealed record Results(IReadOnlyList<TResult> Items) : SearchOutcome<TResult>;

    /// <summary>
    /// The engine does not accept the caller's pattern. A malformed glob and a malformed regex
    /// arrive the same way and are one case. This is a caller error and is distinct from the engine
    /// failing.
    /// </summary>
    /// <param name="Reason">
    /// The engine's refusal. It describes the caller's own pattern, so it discloses nothing else.
    /// </param>
    public sealed record BadPattern(string Reason) : SearchOutcome<TResult>;

    /// <summary>
    /// Something the search had to read could not be read, so the whole search failed. Skipping the
    /// file instead would turn a permissions problem into a wrong answer the caller cannot tell
    /// from a correct one.
    /// </summary>
    /// <param name="Path">The entry that could not be read, vault-relative.</param>
    /// <param name="Reason">What the engine reported.</param>
    public sealed record Unreadable(string Path, string Reason) : SearchOutcome<TResult>;

    /// <summary>
    /// Anything else the engine reported once it was running. This is an operational failure of one
    /// call, and this layer writes nothing to health state for it.
    /// </summary>
    /// <param name="Reason">What the engine reported.</param>
    public sealed record Failed(string Reason) : SearchOutcome<TResult>;

    /// <summary>
    /// The engine could not be started at all: it is not where the host said it was, or the platform
    /// refused to launch it. It is separate from <see cref="Failed"/> because repeating the identical
    /// call cannot change the answer, while a run that started and failed may succeed on a retry.
    /// </summary>
    /// <param name="Reason">
    /// Why the launch failed, as the platform reported it. It names the executable the host
    /// configured, so it belongs in an operator-facing log rather than in an answer to a caller.
    /// </param>
    public sealed record EngineUnavailable(string Reason) : SearchOutcome<TResult>;
}

/// <summary>Represents one matching line and where it is.</summary>
/// <param name="Path">The note the line is in. Vault-relative, and quotable back into a later call.</param>
/// <param name="LineNumber">The line's position in the file, one-based, as the engine counts.</param>
/// <param name="Text">The line's text without its terminator.</param>
public sealed record MatchLine(VaultPath Path, int LineNumber, string Text);

/// <summary>Represents how many lines in one file matched.</summary>
/// <param name="Path">The note the matches are in. Vault-relative, and quotable back into a later call.</param>
/// <param name="Count">
/// The number of matching lines, not of matches, so it agrees with what the line granularity would
/// return for the same search.
/// </param>
public sealed record MatchCount(VaultPath Path, int Count);
