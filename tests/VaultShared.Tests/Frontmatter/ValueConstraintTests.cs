using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// The closed constraint vocabulary's checking member. What each kind admits, and that a refusal
/// says what the constraint wanted rather than only that something did not match - which is the
/// whole reason these are records with a member rather than a table of predicates.
/// </summary>
public sealed class ValueConstraintTests
{
    [Theory]
    [InlineData("2026-08-02", true)]
    [InlineData("2026-8-2", false)]
    [InlineData("02/08/2026", false)]
    [InlineData("2026-08-02T00:00:00", false)]
    [InlineData("not a date", false)]
    public void Date_IsAFormatCheckOnTheTextAsWritten(string text, bool satisfied)
    {
        // A format check rather than a parse: coercing the value would test something the caller
        // never wrote, and the round trip could rewrite their text.
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
        // A named kind reports what it wanted; the regex kind this vocabulary refuses to have could
        // only report that something did not match.
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
