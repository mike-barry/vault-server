namespace VaultShared.Frontmatter;

/// <summary>
/// A note cut into its two facets. One definition of the split, and every consumer takes it from
/// here - a reader returning parts of a note, a patcher anchoring against body text, a tool echoing
/// back what it wrote, and the write path's own guard. Because they share one definition, the
/// frontmatter a tool echoes back is byte-identical to what a reader returns, with nobody keeping
/// two of them in step.
///
/// A note with no closed block is a well-formed value here rather than a case each caller writes:
/// <see cref="HasBlock"/> false, empty inner text, and the whole note as body.
/// </summary>
/// <param name="HasBlock">Whether a closed block was found.</param>
/// <param name="InnerText">
/// The block's text, raw and never parsed, without the fence lines or the final inner line's
/// terminator. Empty for a note with no block, and empty for a fence pair with nothing between.
/// </param>
/// <param name="Body">Everything from the character after the closing fence line.</param>
/// <param name="BodyOffset">
/// Where the body begins in the normalized content. Taken from the closing fence line's terminator,
/// so trailing whitespace on the fence belongs to the fence and a body-only splice disturbs no
/// frontmatter byte.
/// </param>
public readonly record struct NoteSplit(bool HasBlock, string InnerText, string Body, int BodyOffset)
{
    /// <summary>
    /// Line terminators, normalized to LF with no detection of what was there. Notes are stored that
    /// way and the repository normalizes every text file on checkout, so no other convention
    /// survives a round trip regardless.
    ///
    /// This is the surface's entry seam, not its exit: the write path compares the block before and
    /// after composing to detect a body write that disturbed it, and normalizing between those reads
    /// would trip that guard on terminators alone. An entry point holding content across both reads
    /// normalizes once here, and every offset it then works with - <see cref="BodyOffset"/> above all
    /// - indexes the string this returned. Idempotent, so calling it and then <see cref="Of"/> gives
    /// the same offsets as calling <see cref="Of"/> alone.
    /// </summary>
    public static string Normalize(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// The split of <paramref name="content"/>, over its normalized form.
    ///
    /// Detection is a bounded fence scan with no markdown library involved. The opening fence is the
    /// note's first line and is exactly <c>---</c> once trailing whitespace is discounted; the
    /// closing fence is a later line that is exactly <c>---</c> or <c>...</c> under the same rule.
    /// An unclosed opening fence is not a block and the whole file is body - a parser treating a
    /// dangling opener as an unterminated block silently swallows the note's content.
    /// </summary>
    public static NoteSplit Of(string content)
    {
        var text = Normalize(content);

        var openerEnd = LineTextEnd(text, 0);
        if (!IsFence(text, 0, openerEnd, opening: true))
            return NoBlock(text);

        // Past the opening fence line's terminator. A note that is nothing but `---` has no line
        // after it to close the block.
        var innerStart = NextLineStart(text, openerEnd);
        if (innerStart < 0)
            return NoBlock(text);

        for (var lineStart = innerStart; lineStart <= text.Length;)
        {
            var textEnd = LineTextEnd(text, lineStart);
            if (IsFence(text, lineStart, textEnd, opening: false))
            {
                // The inner region runs to the start of the closing fence line and therefore ends
                // with the final inner line's terminator, which the block's text does not include.
                var innerEnd = lineStart > innerStart ? lineStart - 1 : innerStart;
                var next = NextLineStart(text, textEnd);
                var bodyOffset = next < 0 ? text.Length : next;

                return new NoteSplit(
                    HasBlock: true,
                    InnerText: text[innerStart..innerEnd],
                    Body: text[bodyOffset..],
                    BodyOffset: bodyOffset);
            }

            var following = NextLineStart(text, textEnd);
            if (following < 0)
                break;

            lineStart = following;
        }

        return NoBlock(text);
    }

    private static NoteSplit NoBlock(string text) => new(false, string.Empty, text, 0);

    /// <summary>Where the text of the line starting at <paramref name="from"/> ends, before its terminator.</summary>
    private static int LineTextEnd(string text, int from)
    {
        var newline = text.IndexOf('\n', from);
        return newline < 0 ? text.Length : newline;
    }

    /// <summary>
    /// Where the next line starts, given where this one's text ended, or -1 when this line carried no
    /// terminator and so is the last.
    /// </summary>
    private static int NextLineStart(string text, int textEnd) =>
        textEnd < text.Length ? textEnd + 1 : -1;

    /// <summary>
    /// Whether the line spanning <paramref name="start"/> to <paramref name="textEnd"/> is a fence
    /// once trailing whitespace is discounted. Only the opening form is <c>---</c> alone; a closer
    /// may also be <c>...</c>.
    /// </summary>
    private static bool IsFence(string text, int start, int textEnd, bool opening)
    {
        var end = textEnd;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        if (end - start != 3)
            return false;

        var run = text.AsSpan(start, 3);
        return run is "---" || (!opening && run is "...");
    }
}
