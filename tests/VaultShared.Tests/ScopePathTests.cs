using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// The grammar under every vault-relative path in the scope model, asserted directly rather than
/// through a containment answer. Both sides of the scope check read a path through this one member -
/// a grant's configured roots when they are canonicalized at load, and a candidate path when it is
/// tested against them at request time - so a case that only ever asks <c>Admits</c> cannot tell an
/// accepted path from one the grammar quietly reshaped into something else.
///
/// Three things it answers, and each is a group below: what a path normalizes to, what is refused,
/// and that the work is textual.
/// </summary>
public sealed class ScopePathTests
{
    // ---- What normalizes, and to what ----

    [Theory]
    // A plain relative path is its own segments.
    [InlineData("Projects", new[] { "Projects" })]
    [InlineData("Projects/Widget", new[] { "Projects", "Widget" })]
    [InlineData("Projects/Widget/Widget.md", new[] { "Projects", "Widget", "Widget.md" })]
    // Either separator reads the same, so a configuration written on one platform means the same
    // thing on the other.
    [InlineData(@"Projects\Widget", new[] { "Projects", "Widget" })]
    [InlineData("Projects\\Widget/Gizmo", new[] { "Projects", "Widget", "Gizmo" })]
    // An empty segment is not a segment, whether it comes from a doubled or a trailing separator.
    [InlineData("Projects//Widget", new[] { "Projects", "Widget" })]
    [InlineData("Projects/", new[] { "Projects" })]
    // Relative navigation resolves away rather than being refused, as long as it stays inside.
    [InlineData("./Projects/./Widget", new[] { "Projects", "Widget" })]
    [InlineData("Projects/Elsewhere/../Widget", new[] { "Projects", "Widget" })]
    // Case is preserved, not folded: the comparison is case-insensitive, the segments are not
    // rewritten, so a root keeps the spelling the operator configured.
    [InlineData("projects/WIDGET", new[] { "projects", "WIDGET" })]
    public void Normalize_SegmentsAContainedRelativePath(string path, string[] expected)
    {
        Assert.Equal(expected, ScopePath.Normalize(path));
    }

    /// <summary>
    /// The one property both callers depend on: a root and a candidate spelled differently normalize
    /// to the same segments, so load-time canonicalization and request-time comparison cannot
    /// disagree about what a root is.
    /// </summary>
    [Theory]
    [InlineData("Projects/Widget", @"Projects\Widget")]
    [InlineData("Projects/Widget", "./Projects/Widget/")]
    [InlineData("Projects/Widget", "Projects/Gizmo/../Widget")]
    public void Normalize_TwoSpellingsOfOneRootAgree(string one, string other)
    {
        Assert.Equal(ScopePath.Normalize(one), ScopePath.Normalize(other));
    }

    // ---- What is refused ----

    /// <summary>
    /// Everything that cannot be read as a well-formed vault-relative path. Each is a refusal rather
    /// than a value clamped into range: escaping text is refused, never normalized into something
    /// else.
    /// </summary>
    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace only")]
    [InlineData("\t", "whitespace only")]
    [InlineData("/Projects", "rooted")]
    [InlineData(@"C:\Projects", "rooted")]
    [InlineData("../outside", "escapes the vault")]
    [InlineData("Projects/../../outside", "escapes by way of a segment it had entered")]
    [InlineData("..", "escapes the vault")]
    public void Normalize_RefusesWhatIsNotAContainedRelativePath(string path, string why)
    {
        Assert.True(Record.Exception(() => ScopePath.Normalize(path)) is ArgumentException, why);
    }

    /// <summary>
    /// The vault root is not a path. A whole-vault operation takes the vault as its operand, so
    /// nothing spelled here denotes the root and the refusal needs no carve-out - a path with no
    /// segments would be one value admitted under every scope.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("Projects/..")]
    public void Normalize_RefusesTheRootRatherThanAnsweringNoSegments(string path)
    {
        Assert.Throws<ArgumentException>(() => ScopePath.Normalize(path));
    }

    /// <summary>
    /// The refusal names the caller's own text and identifies the argument, so a refusal built from
    /// one discloses nothing the caller did not already hold.
    /// </summary>
    [Fact]
    public void Normalize_NamesTheCallersOwnTextAndNothingElse()
    {
        var refusal = Assert.Throws<ArgumentException>(() => ScopePath.Normalize("../outside"));

        Assert.Contains("../outside", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("path", refusal.ParamName);
    }

    // ---- That the work is textual ----

    /// <summary>
    /// Containment is decided textually and never touches the filesystem, which is what makes the
    /// check cheap, applicable to every path, and identical for a path that does not exist yet.
    /// </summary>
    [Fact]
    public void Normalize_TouchesNoFilesystem()
    {
        Assert.Equal(["Nothing", "Here", "Exists.md"], ScopePath.Normalize("Nothing/Here/Exists.md"));
    }
}
