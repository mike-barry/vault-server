using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// Tests <c>Wikilink.Recognize</c>, the single wikilink grammar every consumer shares.
/// </summary>
/// <remarks>
/// A consumer receives offsets and cannot check them for itself, so these tests pin which forms
/// are occurrences, exactly where the whole-link and target extents fall, and that overwriting
/// every target extent leaves a correct note. Recognition takes text and returns occurrences, so
/// no test here needs a vault.
/// </remarks>
public sealed class WikilinkRecognitionTests
{
    /// <summary>Returns the text covered by an occurrence's whole-link and target extents.</summary>
    /// <param name="text">The text the occurrence was recognized in.</param>
    /// <param name="occurrence">The occurrence to take the extents from.</param>
    /// <returns>The whole link and the target, as the text each covers.</returns>
    private static (string Link, string Target) Extents(string text, WikilinkOccurrence occurrence) =>
        (text.Substring(occurrence.Start, occurrence.Length), occurrence.TargetIn(text));

    /// <summary>Recognizes text expected to hold exactly one link and returns its extents.</summary>
    /// <param name="text">The text to recognize.</param>
    /// <returns>The whole link and the target, as the text each covers.</returns>
    private static (string Link, string Target) Single(string text)
    {
        var occurrence = Assert.Single(Wikilink.Recognize(text));
        return Extents(text, occurrence);
    }

    // ---- The link forms ----

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
        // Only the target extent is rewritten, so anything a link carries beyond it survives.
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
        // The vault's navigation links live in frontmatter values, so recognizing only the body
        // would under-report to every consumer.
        var text = "---\nparent: \"[[Projects/Widget]]\"\nrelated:\n  - \"[[Notes/Alpha]]\"\n---\n\n# Body\n\n[[Notes/Beta]]\n";

        Assert.Equal(
            ["Projects/Widget", "Notes/Alpha", "Notes/Beta"],
            Wikilink.Recognize(text).Select(o => o.TargetIn(text)));
    }

    // ---- What is not a link ----

    [Fact]
    public void ALinkNeverCrossesALineBoundary()
    {
        // Confining a link to one line is what keeps a rewrite from creating or moving a
        // frontmatter fence.
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
        // A heading reference within the same note names no file, and an empty target has no
        // extent for a rewrite to write over.
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
        // The grammar knows nothing about markdown structure. It runs over frontmatter as well as
        // the body, and tracking code fences would mean parsing the note, which is what every
        // consumer relies on not happening. Over-reporting a link, or rewriting a name inside a
        // code sample, is visible to whoever reads the note; silently missing one is not.
        var text = "```\n[[Widget]]\n```\n";

        Assert.Equal("Widget", Assert.Single(Wikilink.Recognize(text)).TargetIn(text));
    }

    // ---- Exactness of the extents ----

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
        // A consumer replaces characters and parses nothing, so everything outside a replaced
        // target survives unchanged.
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
    /// Replaces every link target in the text, the way a consumer does: writing over each target
    /// extent from last to first, so the earlier extents still describe the text they came from.
    /// </summary>
    /// <param name="text">The text to rewrite.</param>
    /// <param name="replacement">The target to write in place of each recognized one.</param>
    /// <returns>The rewritten text.</returns>
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
