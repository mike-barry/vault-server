using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests <c>ScopePath.Normalize</c>, which parses every vault-relative path in the scope model.
/// </summary>
/// <remarks>
/// Both sides of a scope check go through this one method: a grant's configured roots when they
/// are canonicalized at load, and a candidate path when it is tested against them at request
/// time. Testing it directly rather than through a containment answer is what distinguishes an
/// accepted path from one that normalization reshaped into something else.
/// </remarks>
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
    [InlineData("./Projects/Widget/", new[] { "Projects", "Widget" })]
    [InlineData("Projects/Elsewhere/../Widget", new[] { "Projects", "Widget" })]
    // Case is preserved, not folded: the comparison is case-insensitive, the segments are not
    // rewritten, so a root keeps the spelling the operator configured.
    [InlineData("projects/WIDGET", new[] { "projects", "WIDGET" })]
    // One leading separator is dropped, in either spelling. Every path here is measured from the
    // vault root already, so the separator names the place the path was going to be read from.
    [InlineData("/Projects/Widget.md", new[] { "Projects", "Widget.md" })]
    [InlineData(@"\Projects\Widget.md", new[] { "Projects", "Widget.md" })]
    public void Normalize_SegmentsAContainedRelativePath(string path, string[] expected)
    {
        Assert.Equal(expected, ScopePath.Normalize(path));
    }

    // ---- What is refused ----

    /// <summary>
    /// Checks the text that cannot be read as a well-formed vault-relative path. Each case throws
    /// rather than being clamped into range; escaping text is never normalized into something
    /// that would be admitted.
    /// </summary>
    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace only")]
    [InlineData("\t", "whitespace only")]
    [InlineData(@"C:\Projects", "drive-qualified, and carries no leading separator to drop")]
    [InlineData("C:/Projects", "drive-qualified in the other spelling")]
    [InlineData("//server/share", "two leading separators name a host, not a folder")]
    [InlineData(@"\\server\share", "the same in the other spelling")]
    [InlineData("../outside", "escapes the vault")]
    [InlineData("Projects/../../outside", "escapes by way of a segment it had entered")]
    [InlineData("..", "escapes the vault")]
    [InlineData("/..", "one separator drops, and what is left still escapes the vault")]
    public void Normalize_RefusesWhatIsNotAContainedRelativePath(string path, string why)
    {
        Assert.True(Record.Exception(() => ScopePath.Normalize(path)) is ArgumentException, why);
    }

    /// <summary>
    /// Checks that text denoting the vault root is refused rather than normalized to zero
    /// segments, since a path with no segments would be admitted under every scope. A whole-vault
    /// operation names the vault as its operand instead of spelling a path.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("Projects/..")]
    [InlineData("/")]
    public void Normalize_RefusesTheRootRatherThanAnsweringNoSegments(string path)
    {
        Assert.Throws<ArgumentException>(() => ScopePath.Normalize(path));
    }

    /// <summary>
    /// Checks that the exception quotes the caller's own text and names the argument, so a message
    /// built from it discloses nothing the caller did not already have.
    /// </summary>
    [Fact]
    public void Normalize_NamesTheCallersOwnTextAndNothingElse()
    {
        var refusal = Assert.Throws<ArgumentException>(() => ScopePath.Normalize("../outside"));

        Assert.Contains("../outside", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("path", refusal.ParamName);
    }
}
