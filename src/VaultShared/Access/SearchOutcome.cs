namespace VaultShared.Access;

/// <summary>
/// The answer to an enumeration or a content search: a result that may be empty, or one of three
/// failures callers need to tell apart. The hierarchy is closed by a private constructor.
///
/// One type serves both surfaces because both distinguish the same four outcomes, and because a
/// pattern-bearing enumeration is itself a name-and-path search
/// (<c>Spec-Access-Layer-Capabilities.md:Enumeration</c>).
///
/// <see cref="Results"/> being empty is the correct answer to a well-formed question that matched
/// nothing. Folding it into a failure would force every caller to interpret an error to find out
/// whether anything actually went wrong.
/// </summary>
/// <typeparam name="TResult">
/// What one answer is: a <see cref="VaultPath"/> for the two enumeration surfaces and for the
/// file granularity of search, a <see cref="MatchLine"/> for matching lines, a
/// <see cref="MatchCount"/> for counts.
/// </typeparam>
public abstract record SearchOutcome<TResult>
{
    private SearchOutcome() { }

    /// <summary>The answer, in the engine's order, filtered to what the caller may read. May be empty.</summary>
    public sealed record Results(IReadOnlyList<TResult> Items) : SearchOutcome<TResult>;

    /// <summary>
    /// The caller's pattern is not one the engine accepts - a malformed glob or a malformed regex,
    /// which arrive the same way and are one classification. A caller error, distinct from the
    /// engine failing, and the pair most easily collapsed into one "search failed".
    ///
    /// <paramref name="Reason"/> is the engine's refusal, which describes the caller's own pattern
    /// and so discloses nothing else.
    /// </summary>
    public sealed record BadPattern(string Reason) : SearchOutcome<TResult>;

    /// <summary>
    /// Something the search had to read could not be read, so the whole search failed
    /// (<c>Spec-Vault-Access-Design.md:Load-Bearing Semantics</c>). Skipping the file would turn a
    /// permissions problem into a wrong answer the caller cannot distinguish from a correct one.
    /// </summary>
    public sealed record Unreadable(string Path, string Reason) : SearchOutcome<TResult>;

    /// <summary>
    /// Anything else the engine reported, or a failure to run it at all, carrying what it said. An
    /// operational failure of this one call: the layer writes nothing to health state.
    /// </summary>
    public sealed record Failed(string Reason) : SearchOutcome<TResult>;
}

/// <summary>
/// One matching line and where it is: the richest search granularity. The path is vault-relative
/// and quotable back into a later call.
/// </summary>
/// <param name="LineNumber">One-based, as the engine counts.</param>
/// <param name="Text">The line's text without its terminator.</param>
public sealed record MatchLine(VaultPath Path, int LineNumber, string Text);

/// <summary>
/// How many lines in one file matched. Lines rather than matches, so the number agrees with what
/// the line granularity would return for the same search.
/// </summary>
public sealed record MatchCount(VaultPath Path, int Count);
