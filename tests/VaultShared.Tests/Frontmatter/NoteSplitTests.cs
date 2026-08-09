using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Verifies <see cref="NoteSplit"/>, the single definition of where a note's frontmatter block
/// ends and its body begins. Covers which shapes count as a block, that the inner text and body
/// together are exactly the note's bytes, and that splicing a new body in at the reported offset
/// leaves every frontmatter byte in place.
/// </summary>
public sealed class NoteSplitTests
{
    // ---- What counts as a block ----

    [Fact]
    public void ANoteWithNoBlockIsEntirelyItsOwnBody()
    {
        var split = NoteSplit.Of("Just prose.\nMore of it.\n");

        Assert.False(split.HasBlock);
        Assert.Equal(string.Empty, split.InnerText);
        Assert.Equal("Just prose.\nMore of it.\n", split.Body);
        Assert.Equal(0, split.BodyOffset);
    }

    [Fact]
    public void AnUnclosedOpeningFenceIsNotABlock()
    {
        // Treating a dangling opener as an unterminated block would swallow the note's content.
        var content = "---\ntitle: Widget\n\nProse that never met a closing fence.\n";
        var split = NoteSplit.Of(content);

        Assert.False(split.HasBlock);
        Assert.Equal(content, split.Body);
        Assert.Equal(0, split.BodyOffset);
    }

    [Fact]
    public void AFenceOpenerAloneIsNotABlock()
    {
        Assert.False(NoteSplit.Of("---").HasBlock);
        Assert.False(NoteSplit.Of("---\n").HasBlock);
    }

    [Fact]
    public void AFenceThatIsNotTheFirstLineOpensNothing()
    {
        var content = "Prose first.\n---\ntitle: Widget\n---\n";
        var split = NoteSplit.Of(content);

        Assert.False(split.HasBlock);
        Assert.Equal(content, split.Body);
    }

    [Fact]
    public void AFencePairWithNothingBetweenIsABlockWithEmptyFrontmatter()
    {
        var split = NoteSplit.Of("---\n---\nBody.\n");

        Assert.True(split.HasBlock);
        Assert.Equal(string.Empty, split.InnerText);
        Assert.Equal("Body.\n", split.Body);
    }

    [Fact]
    public void ADotClosingFenceClosesTheBlock()
    {
        var split = NoteSplit.Of("---\ntitle: Widget\n...\nBody.\n");

        Assert.True(split.HasBlock);
        Assert.Equal("title: Widget", split.InnerText);
        Assert.Equal("Body.\n", split.Body);
    }

    [Fact]
    public void ADotOpeningFenceOpensNothing()
    {
        // Only the closing form may be `...`; the opening fence is exactly `---`.
        Assert.False(NoteSplit.Of("...\ntitle: Widget\n---\nBody.\n").HasBlock);
    }

    [Fact]
    public void TrailingWhitespaceOnEitherFenceIsDiscounted()
    {
        var split = NoteSplit.Of("---  \ntitle: Widget\n---\t\nBody.\n");

        Assert.True(split.HasBlock);
        Assert.Equal("title: Widget", split.InnerText);
        Assert.Equal("Body.\n", split.Body);
    }

    [Fact]
    public void LeadingWhitespaceOnAFenceIsNotDiscounted()
    {
        // The rule discounts trailing whitespace only; an indented `---` is content.
        Assert.False(NoteSplit.Of(" ---\ntitle: Widget\n---\n").HasBlock);
        Assert.False(NoteSplit.Of("---\ntitle: Widget\n ---\n").HasBlock);
    }

    [Fact]
    public void MoreThanThreeDashesIsNotAFence()
    {
        Assert.False(NoteSplit.Of("----\ntitle: Widget\n----\n").HasBlock);
    }

    [Fact]
    public void TheFirstQualifyingLineCloses()
    {
        var split = NoteSplit.Of("---\na: 1\n---\nBody\n---\nMore\n");

        Assert.Equal("a: 1", split.InnerText);
        Assert.Equal("Body\n---\nMore\n", split.Body);
    }

    // ---- The block at end of file ----

    [Fact]
    public void ABlockAtEndOfFileWithATrailingNewlineHasAnEmptyBody()
    {
        var content = "---\ntitle: Widget\n---\n";
        var split = NoteSplit.Of(content);

        Assert.True(split.HasBlock);
        Assert.Equal("title: Widget", split.InnerText);
        Assert.Equal(string.Empty, split.Body);
        Assert.Equal(content.Length, split.BodyOffset);
    }

    [Fact]
    public void ABlockAtEndOfFileWithNoTrailingNewlineHasAnEmptyBody()
    {
        var content = "---\ntitle: Widget\n---";
        var split = NoteSplit.Of(content);

        Assert.True(split.HasBlock);
        Assert.Equal("title: Widget", split.InnerText);
        Assert.Equal(string.Empty, split.Body);
        Assert.Equal(content.Length, split.BodyOffset);
    }

    [Fact]
    public void AnEmptyNoteSplitsWithoutABlock()
    {
        var split = NoteSplit.Of(string.Empty);

        Assert.False(split.HasBlock);
        Assert.Equal(string.Empty, split.Body);
        Assert.Equal(0, split.BodyOffset);
    }

    // ---- Inner text and body are the note's own bytes ----

    [Fact]
    public void InnerTextIsRawAndCarriesNeitherFenceNorItsFinalTerminator()
    {
        var split = NoteSplit.Of("---\n# a comment\n\ntags:\n  - a\n---\nBody.\n");

        Assert.Equal("# a comment\n\ntags:\n  - a", split.InnerText);
    }

    [Fact]
    public void BlankLinesInsideTheBlockSurviveTheSplit()
    {
        // The split never parses the block. Blank lines are dropped later, by the emitter.
        var split = NoteSplit.Of("---\n\na: 1\n\n\n---\nBody\n");

        Assert.Equal("\na: 1\n\n", split.InnerText);
    }

    [Fact]
    public void TheTwoFacetsPlusTheFencesAreTheWholeNote()
    {
        var content = "---\ntitle: Widget\ntags:\n  - a\n---\nBody text.\n\nMore.\n";
        var split = NoteSplit.Of(content);

        Assert.Equal(content, $"---\n{split.InnerText}\n---\n{split.Body}");
    }

    // ---- Line terminators ----

    [Fact]
    public void TerminatorsNormalizeToLfOnEntry()
    {
        var split = NoteSplit.Of("---\r\ntitle: Widget\r\n---\r\nBody.\r\nMore.\r\n");

        Assert.Equal("title: Widget", split.InnerText);
        Assert.Equal("Body.\nMore.\n", split.Body);
    }

    [Fact]
    public void NormalizationIsIdempotentAndAgreesWithTheSplit()
    {
        var content = "---\r\na: 1\r\n---\r\nBody\r\n";
        var once = NoteSplit.Normalize(content);

        Assert.Equal(once, NoteSplit.Normalize(once));
        Assert.Equal(NoteSplit.Of(content), NoteSplit.Of(once));
    }

    // ---- The offset ----

    [Fact]
    public void TheBodyOffsetIsWhereTheBodyStartsInTheNormalizedContent()
    {
        var content = "---\ntitle: Widget\n---\nBody.\n";
        var split = NoteSplit.Of(content);

        Assert.Equal("Body.\n", split.Body);
        Assert.Equal(split.Body, content[split.BodyOffset..]);
    }

    [Fact]
    public void TheOffsetComesFromTheClosingFenceLinesTerminatorNotItsText()
    {
        // Trailing whitespace on the closing fence belongs to the fence. Taking the offset before
        // it would leak that fragment into the body and show up as a spurious body change.
        var content = "---\ntitle: Widget\n---   \nBody.\n";
        var split = NoteSplit.Of(content);

        Assert.Equal("Body.\n", split.Body);
        Assert.Equal(content.Length - "Body.\n".Length, split.BodyOffset);
    }

    [Fact]
    public void SplicingANewBodyAtTheOffsetDisturbsNoFrontmatterByte()
    {
        var content = "---\ntitle: Widget\ntags:\n  - a\n\n# comment\n---   \nOld body.\n";
        var split = NoteSplit.Of(content);

        var spliced = content[..split.BodyOffset] + "New body.\n";

        Assert.Equal("---\ntitle: Widget\ntags:\n  - a\n\n# comment\n---   \nNew body.\n", spliced);
        Assert.Equal(split.InnerText, NoteSplit.Of(spliced).InnerText);
    }
}
