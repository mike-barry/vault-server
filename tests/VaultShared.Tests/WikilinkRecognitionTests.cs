using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// The one grammar four consumers share. What is asserted is what a consumer cannot recover for
/// itself: which forms are occurrences, where the two extents fall exactly, and that a note whose
/// target extents are overwritten is a correct note afterwards. Recognition takes text and answers
/// occurrences, so nothing here needs a vault.
/// </summary>
public sealed class WikilinkRecognitionTests
{
    /// <summary>The whole link and the target, as the text they cover.</summary>
    private static (string Link, string Target) Extents(string text, WikilinkOccurrence occurrence) =>
        (text.Substring(occurrence.Start, occurrence.Length), occurrence.TargetIn(text));

    private static (string Link, string Target) Single(string text)
    {
        var occurrence = Assert.Single(Wikilink.Recognize(text));
        return Extents(text, occurrence);
    }

    // ---- The forms ----

    [Fact]
    public void ABareLinkIsWholeLinkAndTarget()
    {
        Assert.Equal(("[[Widget]]", "Widget"), Single("See [[Widget]] for more.\n"));
    }

    [Fact]
    public void AnAliasIsADecorationOutsideTheTarget()
    {
        Assert.Equal(("[[Notes/Old-Name|Alias]]", "Notes/Old-Name"), Single("See [[Notes/Old-Name|Alias]].\n"));
    }

    [Fact]
    public void TheEmbeddingFormIsRecognizedAndItsMarkIsPartOfTheWholeLink()
    {
        Assert.Equal(("![[Notes/Old-Name|Alias]]", "Notes/Old-Name"), Single("![[Notes/Old-Name|Alias]]\n"));
    }

    [Fact]
    public void APathQualifiedTargetIsTheWholeQualification()
    {
        Assert.Equal(("[[Projects/Widget/Widget]]", "Projects/Widget/Widget"), Single("[[Projects/Widget/Widget]]\n"));
    }

    [Fact]
    public void ASubpathIsADecorationOutsideTheTarget()
    {
        // Only the target portion is rewritten; what a link carries beyond it survives verbatim.
        Assert.Equal(("[[Widget#Design]]", "Widget"), Single("[[Widget#Design]]\n"));
        Assert.Equal(("[[Widget#^a1b2c3|Alias]]", "Widget"), Single("[[Widget#^a1b2c3|Alias]]\n"));
    }

    [Fact]
    public void PaddingAroundTheTargetIsNotPartOfIt()
    {
        Assert.Equal(("[[ Widget | Alias]]", "Widget"), Single("[[ Widget | Alias]]\n"));
    }

    [Fact]
    public void EveryOccurrenceOnALineIsReported()
    {
        var text = "[[One]] and [[Two|Second]] and ![[Three]]\n";

        Assert.Equal(
            ["One", "Two", "Three"],
            Wikilink.Recognize(text).Select(o => o.TargetIn(text)));
    }

    // ---- Frontmatter ----

    [Fact]
    public void RecognitionRunsOverTheFrontmatterBlockToo()
    {
        // The vault's navigation graph lives in frontmatter values, so a grammar stopping at the
        // body would under-report to every consumer.
        var text = "---\nparent: \"[[Projects/Widget]]\"\nrelated:\n  - \"[[Notes/Alpha]]\"\n---\n\n# Body\n\n[[Notes/Beta]]\n";

        Assert.Equal(
            ["Projects/Widget", "Notes/Alpha", "Notes/Beta"],
            Wikilink.Recognize(text).Select(o => o.TargetIn(text)));
    }

    // ---- What is not a link ----

    [Fact]
    public void ALinkNeverCrossesALineBoundary()
    {
        // This is what keeps a substitution from creating or moving a frontmatter fence.
        Assert.Empty(Wikilink.Recognize("[[Widget\n]]\n"));
        Assert.Empty(Wikilink.Recognize("---\ntitle: [[Broken\n---\n"));
    }

    [Fact]
    public void AnUnclosedLinkIsNotAnOccurrence()
    {
        Assert.Empty(Wikilink.Recognize("[[Widget and then nothing\n"));
    }

    [Fact]
    public void ASingleBracketPairIsNotALink()
    {
        Assert.Empty(Wikilink.Recognize("[Widget](Projects/Widget.md)\n"));
    }

    [Fact]
    public void ALinkNamingNothingIsNotAnOccurrence()
    {
        // A heading reference within the same note names no file, and an empty target has no extent
        // a rewrite could be written over.
        Assert.Empty(Wikilink.Recognize("[[#Design]]\n"));
        Assert.Empty(Wikilink.Recognize("[[|Alias]]\n"));
        Assert.Empty(Wikilink.Recognize("[[   ]]\n"));
    }

    [Fact]
    public void AStrayBracketBeforeALinkDoesNotHideIt()
    {
        Assert.Equal(("[[Widget]]", "Widget"), Single("[[[Widget]]\n"));
    }

    [Fact]
    public void ALinksInnerTextHoldsNoBracket()
    {
        Assert.Equal(("[[Beta]]", "Beta"), Single("[[Alpha[[Beta]]\n"));
    }

    [Fact]
    public void ALinkInsideACodeFenceIsAnOccurrenceLikeAnyOther()
    {
        // The grammar knows no markdown structure, and cannot: it runs over frontmatter as well as
        // body, and tracking fences would mean parsing the note - the one thing every consumer here
        // exists to avoid. Over-reporting to the census and rewriting a name inside a code sample
        // are both the loud direction of being wrong.
        var text = "```\n[[Widget]]\n```\n";

        Assert.Equal("Widget", Assert.Single(Wikilink.Recognize(text)).TargetIn(text));
    }

    // ---- Byte-range exactness ----

    [Fact]
    public void TheTargetExtentIsExactlyTheTarget()
    {
        const string text = "See ![[Notes/Old-Name|Alias]] here.\n";

        var occurrence = Assert.Single(Wikilink.Recognize(text));

        Assert.Equal(text.IndexOf("![[", StringComparison.Ordinal), occurrence.Start);
        Assert.Equal("![[Notes/Old-Name|Alias]]".Length, occurrence.Length);
        Assert.Equal(text.IndexOf("Notes/", StringComparison.Ordinal), occurrence.TargetStart);
        Assert.Equal("Notes/Old-Name".Length, occurrence.TargetLength);
    }

    [Fact]
    public void OverwritingEveryTargetExtentProducesACorrectNote()
    {
        // The consumer's whole contract: it replaces bytes and parses nothing, so every byte outside
        // a replaced target survives.
        const string text =
            "---\nparent: \"[[Old-Name]]\"\n---\n\n# Note\n\nSee [[Notes/Old-Name|the old one]] and ![[Old-Name#Design]].\n";

        Assert.Equal(
            "---\nparent: \"[[New-Name]]\"\n---\n\n# Note\n\nSee [[New-Name|the old one]] and ![[New-Name#Design]].\n",
            Rewrite(text, "New-Name"));
    }

    [Fact]
    public void ARewriteLeavesANoteWhoseLinksAreRecognizedAgainUnchangedInCount()
    {
        const string text = "[[A]] [[B|x]] ![[C#h]]\n";

        var rewritten = Rewrite(text, "Longer-Name");

        Assert.Equal(3, Wikilink.Recognize(rewritten).Count);
        Assert.Equal("[[Longer-Name]] [[Longer-Name|x]] ![[Longer-Name#h]]\n", rewritten);
    }

    /// <summary>
    /// What a consumer does: write over each target extent, last first so the earlier extents still
    /// describe the text they were taken from.
    /// </summary>
    private static string Rewrite(string text, string replacement)
    {
        var rewritten = text;
        foreach (var occurrence in Wikilink.Recognize(text).Reverse())
            rewritten = string.Concat(
                rewritten.AsSpan(0, occurrence.TargetStart),
                replacement,
                rewritten.AsSpan(occurrence.TargetStart + occurrence.TargetLength));

        return rewritten;
    }
}
