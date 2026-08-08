namespace VaultShared.Frontmatter;

/// <summary>
/// Represents a note split into its frontmatter block and its body. This is the only definition of
/// that split, and every consumer uses it: the readers that return parts of a note, the patcher
/// that anchors against body text, the tools that echo back what they wrote, and the write path's
/// own guard. Sharing one definition is what makes the frontmatter a tool echoes back
/// byte-identical to what a reader returns.
///
/// A note with no closed block is a well-formed value here: <see cref="HasBlock"/> is false, the
/// inner text is empty, and the whole note is the body.
/// </summary>
/// <param name="HasBlock">Whether a closed frontmatter block was found.</param>
/// <param name="InnerText">
/// The block's raw, unparsed text, without the fence lines or the final inner line's terminator.
/// Empty for a note with no block, and empty for a fence pair with nothing between.
/// </param>
/// <param name="Body">Everything from the character after the closing fence line.</param>
/// <param name="BodyOffset">
/// Where the body begins in the normalized content, taken from the closing fence line's terminator.
/// Trailing whitespace on the fence belongs to the fence, so a body-only splice at this offset
/// disturbs no frontmatter byte.
/// </param>
public readonly record struct NoteSplit(bool HasBlock, string InnerText, string Body, int BodyOffset)
{
    /// <summary>
    /// Returns <paramref name="content"/> with CRLF line terminators replaced by LF. Nothing is
    /// detected or reported: notes are stored with LF and the repository normalizes every text file
    /// on checkout, so no other convention survives a round trip.
    /// </summary>
    /// <param name="content">The note content to normalize.</param>
    /// <returns>The content with LF terminators.</returns>
    /// <remarks>
    /// Normalize on the way in, never on the way out. The write path compares the block before and
    /// after composing to detect a body write that disturbed it, and normalizing between those two
    /// reads would trip that guard on terminators alone. A caller holding content across both reads
    /// normalizes once here, and every offset it then uses - <see cref="BodyOffset"/> above all -
    /// indexes the string this returned. The method is idempotent, so calling it and then
    /// <see cref="Of"/> gives the same offsets as calling <see cref="Of"/> alone.
    /// </remarks>
    public static string Normalize(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Splits <paramref name="content"/> into its frontmatter block and its body, over the
    /// normalized form of the content.
    /// </summary>
    /// <param name="content">The note content to split.</param>
    /// <returns>The split of the note.</returns>
    /// <remarks>
    /// Detection is a bounded scan for fence lines and uses no markdown library. The opening fence
    /// must be the note's first line and exactly <c>---</c> once trailing whitespace is discounted;
    /// the closing fence is a later line that is exactly <c>---</c> or <c>...</c> under the same
    /// rule. An opening fence that is never closed is not a block and the whole file is body,
    /// because treating a dangling opener as an unterminated block would silently swallow the
    /// note's content.
    /// </remarks>
    public static NoteSplit Of(string content)
    {
        var text = Normalize(content);

        var openerEnd = LineTextEnd(text, 0);
        if (!IsFence(text, 0, openerEnd, opening: true))
            return NoBlock(text);

        // A note that is nothing but `---` has no line after the opener to close the block.
        var innerStart = NextLineStart(text, openerEnd);
        if (innerStart < 0)
            return NoBlock(text);

        for (var lineStart = innerStart; lineStart <= text.Length;)
        {
            var textEnd = LineTextEnd(text, lineStart);
            if (IsFence(text, lineStart, textEnd, opening: false))
            {
                // The inner region runs up to the start of the closing fence line, so it ends with
                // the final inner line's terminator, which the block's text does not include.
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

    /// <summary>Returns where the text of the line starting at <paramref name="from"/> ends, before its terminator.</summary>
    /// <param name="text">The content being scanned.</param>
    /// <param name="from">The index the line starts at.</param>
    /// <returns>The index of the line's terminator, or the length of the text for the last line.</returns>
    private static int LineTextEnd(string text, int from)
    {
        var newline = text.IndexOf('\n', from);
        return newline < 0 ? text.Length : newline;
    }

    /// <summary>Returns where the next line starts, given where this one's text ended.</summary>
    /// <param name="text">The content being scanned.</param>
    /// <param name="textEnd">Where the current line's text ended.</param>
    /// <returns>The index the next line starts at, or -1 when this line carried no terminator and
    /// so is the last.</returns>
    private static int NextLineStart(string text, int textEnd) =>
        textEnd < text.Length ? textEnd + 1 : -1;

    /// <summary>
    /// Returns whether the line spanning <paramref name="start"/> to <paramref name="textEnd"/> is
    /// a fence once trailing whitespace is discounted.
    /// </summary>
    /// <param name="text">The content being scanned.</param>
    /// <param name="start">Where the line starts.</param>
    /// <param name="textEnd">Where the line's text ends, before its terminator.</param>
    /// <param name="opening">True to test for an opening fence, which must be <c>---</c>; false for
    /// a closing fence, which may also be <c>...</c>.</param>
    /// <returns>True when the line is a fence of the requested kind.</returns>
    /// <remarks>
    /// Trailing whitespace is every character <see cref="char.IsWhiteSpace(char)"/> accepts, which
    /// is wider than the space, tab and carriage return a narrow reading would allow - so <c>---</c>
    /// followed by a non-breaking space is a fence here. Keep the wide class. Narrowing it makes
    /// such a line ordinary text, and a note that has frontmatter today would read as having none
    /// with its whole content in the body facet.
    /// </remarks>
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
