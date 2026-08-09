using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Verifies what each value constraint kind accepts, and that an unsatisfied result describes what
/// the constraint wanted rather than only reporting a mismatch.
/// </summary>
public sealed class ValueConstraintTests
{
    [Theory]
    [InlineData("2026-08-02", true)]
    [InlineData("2026-8-2", false)]
    [InlineData("02/08/2026", false)]
    [InlineData("2026-08-02T00:00:00", false)]
    [InlineData("not a date", false)]
    [InlineData("2026-13-45", false)]
    [InlineData("2026-02-30", false)]
    public void Date_IsAFormatCheckOnTheTextAsWritten(string text, bool satisfied)
    {
        // The check is on the text as written rather than on a parsed date, because parsing and
        // reformatting would test a value the caller never wrote.
        //
        // Matching is done by the date parser rather than by a pattern, so text fitting the
        // format's shape while naming no real day is rejected along with text that does not fit
        // the shape at all. Nothing downstream re-checks the value, so a date admitted here is one
        // a later sort or range query has to parse.
        Assert.Equal(satisfied, new DateConstraint(DateConstraint.IsoDate).Check(text).IsSatisfied);
    }

    [Fact]
    public void Enumeration_ComparesOrdinally()
    {
        var constraint = new EnumerationConstraint(["open", "closed"]);

        Assert.True(constraint.Check("open").IsSatisfied);
        Assert.False(constraint.Check("Open").IsSatisfied);
    }

    [Theory]
    [InlineData("note-type", true)]
    [InlineData("", false)]
    [InlineData("note type", false)]
    [InlineData("note4type", false)]
    [InlineData("a-very-long-name-indeed-beyond-the-bound", false)]
    public void CharClass_BoundsBothTheCharactersAndTheLength(string text, bool satisfied)
    {
        Assert.Equal(satisfied, new CharClassConstraint(CharacterClass.AlphaDash, 1, 20).Check(text).IsSatisfied);
    }

    [Theory]
    [InlineData("[[Notes/Target]]", true)]
    [InlineData("[[Target|Alias]]", true)]
    [InlineData("Target", false)]
    [InlineData("see [[Target]] here", false)]
    [InlineData("[[A]] [[B]]", false)]
    [InlineData("![[Target]]", false)]
    public void Wikilink_RequiresTheBracketFormAndNothingElse(string text, bool satisfied)
    {
        Assert.Equal(satisfied, new WikilinkConstraint().Check(text).IsSatisfied);
    }

    [Fact]
    public void String_AssertsNothing()
    {
        Assert.True(new StringConstraint().Check("anything at all").IsSatisfied);
    }

    [Fact]
    public void AnUnsatisfiedResultNamesWhatTheConstraintWanted()
    {
        // Each named kind can describe what it wanted. A raw-regex kind, which the format does not
        // offer, could only report that something did not match.
        var result = new EnumerationConstraint(["open", "closed"]).Check("ajar");

        Assert.False(result.IsSatisfied);
        Assert.Contains("open, closed", result.Wanted);
    }

    [Fact]
    public void ASatisfiedResultWantedNothing()
    {
        Assert.Null(new StringConstraint().Check("x").Wanted);
    }
}
