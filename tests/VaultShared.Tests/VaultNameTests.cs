using VaultShared.Access;

namespace VaultShared.Tests;

/// <summary>
/// The grammar every name the layer brings into existence must satisfy. It is checked on one
/// segment and nowhere else, so these cases are about the segment alone.
/// </summary>
public sealed class VaultNameTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("9")]
    [InlineData("Projects")]
    [InlineData("Vault-MCP-Server")]
    [InlineData("C+P")]
    [InlineData("R&D")]
    [InlineData("2026-Q1")]
    [InlineData("A-B-C")]
    public void IsFolderName_AcceptsGroupsJoinedBySingleDashes(string name)
    {
        Assert.True(VaultName.IsFolderName(name));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("widget", "first character is not upper-case or a digit")]
    [InlineData("-Widget", "leading dash")]
    [InlineData("Widget-", "trailing dash")]
    [InlineData("Widget--Notes", "doubled dash")]
    [InlineData("Widget Notes", "space")]
    [InlineData("Widget_Notes", "underscore")]
    [InlineData("Widget.Notes", "other punctuation")]
    [InlineData("Wídget", "non-ASCII")]
    [InlineData("Widget/Notes", "separator")]
    [InlineData("Widget\\Notes", "separator")]
    [InlineData(".gitkeep", "the tooling's own name is not a caller-supplied one")]
    public void IsFolderName_RefusesEverythingElse(string name, string why)
    {
        Assert.False(VaultName.IsFolderName(name), why);
    }

    [Theory]
    [InlineData("Widget\"Quote")]
    [InlineData("Widget[Bracket]")]
    [InlineData("Widget|Alias")]
    [InlineData("Widget\nBreak")]
    [InlineData("Widget\r")]
    public void IsFolderName_RefusesWhatCouldEndTheConstructAroundASubstitutedName(string name)
    {
        // The grammar exists for containment: a name is written into note text and into quoted
        // frontmatter values without parsing either, so a conforming name needs no escaping.
        Assert.False(VaultName.IsFolderName(name));
        Assert.False(VaultName.IsNoteName(name + VaultName.NoteExtension));
    }

    [Theory]
    [InlineData("Widget.md")]
    [InlineData("Vault-MCP-Server.md")]
    [InlineData("9.md")]
    public void IsNoteName_AcceptsAConformingStemAndTheExtension(string name)
    {
        Assert.True(VaultName.IsNoteName(name));
    }

    [Theory]
    [InlineData("Widget", "no extension")]
    [InlineData(".md", "empty stem")]
    [InlineData("widget.md", "stem outside the grammar")]
    [InlineData("Widget.MD", "the extension is one spelling")]
    [InlineData("Widget.markdown", "not the extension")]
    [InlineData("Widget.md.md", "the stem carries a dot")]
    public void IsNoteName_RefusesEverythingElse(string name, string why)
    {
        Assert.False(VaultName.IsNoteName(name), why);
    }

    [Fact]
    public void AFolderNameIsNotANoteNameAndTheReverse()
    {
        // Folders share the operand space with notes, so the two are separate questions rather than
        // one with an inferred extension.
        Assert.True(VaultName.IsFolderName("Widget"));
        Assert.False(VaultName.IsNoteName("Widget"));
        Assert.True(VaultName.IsNoteName("Widget.md"));
        Assert.False(VaultName.IsFolderName("Widget.md"));
    }

    // ---- The derived spelling ----

    /// <summary>
    /// Where a conforming spelling can be derived from what the caller sent, the refusal names it,
    /// so a miss costs one retry rather than a reading of the grammar. Every run of characters the
    /// grammar does not admit becomes a single dash, and a leading lower-case letter is raised.
    /// </summary>
    [Theory]
    [InlineData("new thought", "New-thought")]
    [InlineData("my_note", "My-note")]
    [InlineData("widget", "Widget")]
    [InlineData("Widget  Gizmo!!", "Widget-Gizmo")]
    [InlineData("-Widget-", "Widget")]
    [InlineData("Café", "Caf")]
    [InlineData("new thought.md", "New-thought.md")]
    public void Suggest_DerivesAConformingSpelling(string sent, string expected)
    {
        Assert.Equal(expected, VaultName.Suggest(sent));
        Assert.True(sent.EndsWith(".md", StringComparison.Ordinal)
            ? VaultName.IsNoteName(expected)
            : VaultName.IsFolderName(expected));
    }

    /// <summary>Nothing is transliterated and nothing is invented: a name carrying no conforming
    /// character has no derivation, and choosing one would be this build deciding what a note is
    /// called.</summary>
    [Theory]
    [InlineData("...")]
    [InlineData("中文")]
    [InlineData("+&+")]
    public void Suggest_OffersNothingWhereNothingCanBeDerived(string sent)
    {
        Assert.Null(VaultName.Suggest(sent));
    }

    /// <summary>A name that already conforms has nothing to suggest, so the refusal carries no
    /// payload rather than echoing the caller's own text back.</summary>
    [Fact]
    public void Suggest_OffersNothingForANameThatAlreadyConforms()
    {
        Assert.Null(VaultName.Suggest("Widget-Gizmo"));
        Assert.Null(VaultName.Suggest("Widget.md"));
    }
}
