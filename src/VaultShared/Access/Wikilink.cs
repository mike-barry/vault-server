namespace VaultShared.Access;

/// <summary>
/// Wikilink recognition: the one definition of what a link is, over a note's whole text. Four
/// consumers read link text - repair on move, bystander pinning on create, the census on delete, and
/// link linting - and a link one of them sees and another misses is a rewrite that silently misses a
/// link or a census that silently under-reports, which is why this is one piece rather than four.
///
/// It takes text and answers occurrences. No path goes in and none comes back: the consumer's
/// enumeration already holds the path, and is what carries it to ranking.
/// </summary>
public static class Wikilink
{
    /// <summary>
    /// Every occurrence in <paramref name="text"/>, in the order they appear, over the whole text
    /// including the frontmatter block - the vault's navigation graph lives in frontmatter values,
    /// so a grammar stopping at the body would under-report to every consumer.
    ///
    /// The grammar is deliberately small and knows no markdown structure. It cannot: it runs over
    /// frontmatter as well as body, and tracking fences would mean parsing the note, which is the
    /// one thing every consumer here is built to avoid. A wikilink inside a code fence is therefore
    /// an occurrence like any other - which over-reports to the census and rewrites a name inside a
    /// code sample, both of which are the loud direction of being wrong.
    ///
    /// One link never crosses a line boundary, so a substitution cannot create or move a frontmatter
    /// fence, and a link's inner text holds no bracket.
    /// </summary>
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
                // Not a link opening here. Advancing by one rather than two, because the second
                // bracket may be the first of a link that does close: `[[[Name]]` is a stray
                // bracket followed by a link.
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
    /// Where the link opened at <paramref name="from"/> closes, or -1 when it does not close on this
    /// line. A second <c>[[</c> before the close means the outer one was never an opening, and the
    /// scan restarts from it; any other bracket is not inner text a link may hold.
    /// </summary>
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
    /// The two extents for a link whose delimiters are known, or null when it names nothing - an
    /// empty target, as in a link to a heading within the same note, is not a reference to a file
    /// and has no target range a rewrite could be written over.
    ///
    /// The whole-link extent covers the embedding form's leading <c>!</c>. The target extent stops
    /// at the first decoration, an alias after <c>|</c> or a subpath after <c>#</c>, and excludes
    /// padding around the name, so what it covers is exactly the name the link denotes and every
    /// byte outside it survives a substitution verbatim.
    /// </summary>
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

        return targetEnd == targetStart
            ? null
            : new WikilinkOccurrence(start, end + 2 - start, targetStart, targetEnd - targetStart);
    }
}

/// <summary>
/// One link occurrence, as two extents into the text it was recognized in: the whole link, and the
/// target inside it. In <c>![[Notes/Old-Name|Alias]]</c> one covers the whole link and the other
/// covers <c>Notes/Old-Name</c>.
///
/// A consumer replaces a target by writing over its extent, so it never parses the note, re-emits
/// it, or splits it into frontmatter and body.
/// </summary>
/// <param name="Start">Where the whole link begins, including an embedding form's <c>!</c>.</param>
/// <param name="Length">How far the whole link runs, through its closing <c>]]</c>.</param>
/// <param name="TargetStart">Where the target begins.</param>
/// <param name="TargetLength">How far the target runs. Never zero.</param>
public sealed record WikilinkOccurrence(int Start, int Length, int TargetStart, int TargetLength)
{
    /// <summary>The target this occurrence names, sliced from the text it was recognized in.</summary>
    public string TargetIn(string text) => text.Substring(TargetStart, TargetLength);
}
