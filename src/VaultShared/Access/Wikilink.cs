namespace VaultShared.Access;

/// <summary>
/// Recognizes wikilinks in a note's text. This is the one definition of what a link is. Four
/// consumers read link text - repair on move, bystander pinning on create, the census on delete,
/// and link linting - and they must all see the same links, or a rewrite silently misses one and a
/// census silently under-reports.
/// </summary>
/// <remarks>
/// It takes text and returns occurrences. No path goes in and none comes back: the consumer's own
/// enumeration holds the path and is what carries it to ranking.
/// </remarks>
public static class Wikilink
{
    /// <summary>
    /// Returns every wikilink occurrence in the text, in the order they appear. The whole text is
    /// scanned, including the frontmatter block, because the vault's navigation graph lives in
    /// frontmatter values and a scan stopping at the body would under-report to every consumer.
    /// </summary>
    /// <remarks>
    /// The grammar is small and knows nothing of markdown structure. Tracking code fences would
    /// mean parsing the note, which every consumer here is built to avoid, and the scan runs over
    /// frontmatter as well as the body in any case. A wikilink inside a code fence is therefore an
    /// occurrence like any other: the census over-reports it, and a rename rewrites a name inside a
    /// code sample. Both are visible when they happen, unlike the opposite error.
    ///
    /// One link never crosses a line boundary, so a substitution can neither create nor move a
    /// frontmatter fence, and a link's inner text contains no bracket.
    /// </remarks>
    /// <param name="text">The note's whole text.</param>
    /// <returns>The occurrences, in the order they appear.</returns>
    public static IReadOnlyList<WikilinkOccurrence> Recognize(string text)
    {
        var found = new List<WikilinkOccurrence>();
        var at = 0;

        while (at < text.Length)
        {
            var open = text.IndexOf("[[", at, StringComparison.Ordinal);
            if (open < 0)
                break;

            var end = Close(text, open + 2);
            if (end < 0)
            {
                // No link opens here. Advance by one rather than two: the second bracket may be
                // the first of a link that does close, as in `[[[Name]]`, which is a stray bracket
                // followed by a link.
                at = open + 1;
                continue;
            }

            if (Occurrence(text, open, end) is { } occurrence)
                found.Add(occurrence);

            at = end + 2;
        }

        return found;
    }

    /// <summary>
    /// Returns where a link closes, or -1 when it does not close on this line. A second <c>[[</c>
    /// before the close means the outer one was never an opening, so the scan restarts from it. Any
    /// other bracket is not text a link may contain.
    /// </summary>
    /// <param name="text">The note's whole text.</param>
    /// <param name="from">The index just past the opening <c>[[</c>.</param>
    /// <returns>The index of the closing <c>]]</c>, or -1.</returns>
    private static int Close(string text, int from)
    {
        for (var i = from; i < text.Length - 1; i++)
        {
            if (text[i] is '\n' or '\r')
                return -1;

            if (text[i] is '[' or ']')
                return text[i] == text[i + 1] && text[i] == ']' ? i : -1;
        }

        return -1;
    }

    /// <summary>
    /// Returns the two extents for a link whose delimiters are known, or null when the link names
    /// nothing. A link with an empty target, such as one to a heading within the same note, is not
    /// a reference to a file and has no target range for a rewrite to write over.
    /// </summary>
    /// <remarks>
    /// The whole-link extent covers the embedding form's leading <c>!</c>. The target extent stops
    /// at the first decoration - an alias after <c>|</c> or a subpath after <c>#</c> - and excludes
    /// padding around the name, so it covers exactly the name the link denotes and everything
    /// outside it survives a substitution unchanged.
    ///
    /// The alias extent covers the display text after the first <c>|</c>, which may follow a
    /// subpath, and runs to the closing delimiter: a second <c>|</c> is alias text rather than a
    /// further decoration. It is absent where the link carries no <c>|</c> and empty where it
    /// carries one with nothing after it.
    /// </remarks>
    /// <param name="text">The note's whole text.</param>
    /// <param name="open">The index of the opening <c>[[</c>.</param>
    /// <param name="end">The index of the closing <c>]]</c>.</param>
    /// <returns>The occurrence, or null when the link names nothing.</returns>
    private static WikilinkOccurrence? Occurrence(string text, int open, int end)
    {
        var start = open > 0 && text[open - 1] == '!' ? open - 1 : open;

        var cut = end;
        for (var i = open + 2; i < end; i++)
            if (text[i] is '|' or '#')
            {
                cut = i;
                break;
            }

        var targetStart = open + 2;
        while (targetStart < cut && text[targetStart] is ' ' or '\t')
            targetStart++;

        var targetEnd = cut;
        while (targetEnd > targetStart && text[targetEnd - 1] is ' ' or '\t')
            targetEnd--;

        if (targetEnd == targetStart)
            return null;

        var bar = -1;
        for (var i = cut; i < end; i++)
            if (text[i] == '|')
            {
                bar = i;
                break;
            }

        var aliasStart = -1;
        var aliasLength = 0;
        if (bar >= 0)
        {
            aliasStart = bar + 1;
            while (aliasStart < end && text[aliasStart] is ' ' or '\t')
                aliasStart++;

            var aliasEnd = end;
            while (aliasEnd > aliasStart && text[aliasEnd - 1] is ' ' or '\t')
                aliasEnd--;

            aliasLength = aliasEnd - aliasStart;
        }

        return new WikilinkOccurrence(
            start, end + 2 - start, targetStart, targetEnd - targetStart, aliasStart, aliasLength);
    }
}

/// <summary>
/// Represents one link occurrence as two extents into the text it was recognized in: the whole
/// link, and the target inside it. In <c>![[Notes/Old-Name|Alias]]</c> the first covers the whole
/// link and the second covers <c>Notes/Old-Name</c>.
/// </summary>
/// <remarks>
/// A consumer replaces a target by writing over its extent, so it never parses the note, re-emits
/// it, or splits it into frontmatter and body.
/// </remarks>
/// <param name="Start">Where the whole link begins, including an embedding form's <c>!</c>.</param>
/// <param name="Length">How far the whole link runs, through its closing <c>]]</c>.</param>
/// <param name="TargetStart">Where the target begins.</param>
/// <param name="TargetLength">How far the target runs. Never zero.</param>
/// <param name="AliasStart">Where the display alias begins, or -1 when the link carries none.</param>
/// <param name="AliasLength">How far the alias runs. Zero for both an absent and an empty alias.</param>
public sealed record WikilinkOccurrence(
    int Start, int Length, int TargetStart, int TargetLength, int AliasStart = -1, int AliasLength = 0)
{
    /// <summary>Gets whether the link carries a display alias with text in it.</summary>
    public bool HasAlias => AliasStart >= 0 && AliasLength > 0;

    /// <summary>Returns the target this occurrence names, sliced from the text it was recognized in.</summary>
    /// <param name="text">The text this occurrence was recognized in.</param>
    /// <returns>The target text.</returns>
    public string TargetIn(string text) => text.Substring(TargetStart, TargetLength);

    /// <summary>
    /// Returns the display alias, sliced from the text it was recognized in, or null where the link
    /// carries none.
    /// </summary>
    /// <param name="text">The text this occurrence was recognized in.</param>
    /// <returns>The alias text, or null.</returns>
    public string? AliasIn(string text) => HasAlias ? text.Substring(AliasStart, AliasLength) : null;
}
