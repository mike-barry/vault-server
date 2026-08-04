using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests <c>WikilinkTarget.Matches</c>, which decides whether a wikilink's target names a given
/// path.
/// </summary>
/// <remarks>
/// Both ways of getting this wrong are silent: a link a repair fails to match is left pointing at
/// nothing, and a link a report fails to match is an orphan nobody hears about. Every form is
/// therefore tested for both a match and a non-match. Nothing here touches the filesystem, so the
/// vault is a value rather than a real directory tree.
/// </remarks>
public sealed class WikilinkMatchingTests
{
    private static readonly Vault Vault = new("test", Path.Combine(Path.GetTempPath(), "no-such-vault"), PathPolicy.None);

    private static VaultPath At(string relativePath) =>
        VaultPath.TryCreate(Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    // ---- Path-qualified targets: a suffix of whole components ----

    [Fact]
    public void APathQualifiedTargetMatchesASuffixOfWholeComponents()
    {
        // The editor writes partial paths, so the target need not start at the vault root.
        Assert.True(WikilinkTarget.Matches(
            "Redesign/Spec-Tool-Capabilities",
            At("Projects/Vault-MCP-Server/Inbox/Redesign/Spec-Tool-Capabilities.md")));
    }

    [Fact]
    public void APathQualifiedTargetMatchesTheWholePathToo()
    {
        Assert.True(WikilinkTarget.Matches("Notes/Sub/Alpha", At("Notes/Sub/Alpha.md")));
    }

    [Fact]
    public void APartialComponentIsNotASuffix()
    {
        // A component matches whole or not at all, so "sign" does not match "Redesign".
        Assert.False(WikilinkTarget.Matches(
            "sign/Spec-Tool-Capabilities",
            At("Projects/Vault-MCP-Server/Inbox/Redesign/Spec-Tool-Capabilities.md")));
    }

    [Fact]
    public void ASuffixOfComponentsIsNotAnArbitrarySubsequence()
    {
        Assert.False(WikilinkTarget.Matches("Projects/Alpha", At("Projects/Notes/Alpha.md")));
    }

    [Fact]
    public void ATargetLongerThanThePathMatchesNothing()
    {
        Assert.False(WikilinkTarget.Matches("Vault/Notes/Alpha", At("Notes/Alpha.md")));
    }

    [Fact]
    public void ARootedOrEmptyComponentMatchesNothing()
    {
        Assert.False(WikilinkTarget.Matches("/Alpha", At("Alpha.md")));
        Assert.False(WikilinkTarget.Matches("Notes//Alpha", At("Notes/Alpha.md")));
    }

    // ---- Bare targets: the file name alone ----

    [Fact]
    public void ABareTargetMatchesOnBasenameAtAnyDepth()
    {
        Assert.True(WikilinkTarget.Matches("Alpha", At("Alpha.md")));
        Assert.True(WikilinkTarget.Matches("Alpha", At("Notes/Sub/Alpha.md")));
    }

    [Fact]
    public void ABareTargetMatchesNoAncestorSegment()
    {
        // A bare target matches the file name only, never a folder along the way. Choosing between
        // several files with the same name is ranking's job, not this method's.
        Assert.False(WikilinkTarget.Matches("Notes", At("Notes/Alpha.md")));
    }

    [Fact]
    public void ABareTargetDoesNotMatchADifferentBasename()
    {
        Assert.False(WikilinkTarget.Matches("Alph", At("Notes/Alpha.md")));
        Assert.False(WikilinkTarget.Matches("Alphabet", At("Notes/Alpha.md")));
    }

    [Fact]
    public void TheFormIsDecidedByTheSeparator()
    {
        Assert.True(WikilinkTarget.IsPathQualified("Notes/Alpha"));
        Assert.False(WikilinkTarget.IsPathQualified("Alpha"));
    }

    // ---- The file extension ----

    [Fact]
    public void ATargetMayOmitTheNoteExtension()
    {
        Assert.True(WikilinkTarget.Matches("Alpha", At("Notes/Alpha.md")));
        Assert.True(WikilinkTarget.Matches("Notes/Alpha", At("Notes/Alpha.md")));
    }

    [Fact]
    public void ATargetMayCarryTheExtensionItNames()
    {
        // The editor writes every non-note target with its extension, and accepts ".md" being
        // spelled out on a note.
        Assert.True(WikilinkTarget.Matches("Alpha.md", At("Notes/Alpha.md")));
        Assert.True(WikilinkTarget.Matches("Diagram.png", At("Attachments/Diagram.png")));
    }

    [Fact]
    public void ATargetOmittingAnExtensionThatIsNotTheNoteOneMatchesNothing()
    {
        // Only ".md" may be omitted. Any other extension cannot be recovered from the target text.
        Assert.False(WikilinkTarget.Matches("Diagram", At("Attachments/Diagram.png")));
    }

    [Fact]
    public void AWrongExtensionMatchesNothing()
    {
        Assert.False(WikilinkTarget.Matches("Alpha.txt", At("Notes/Alpha.md")));
    }

    // ---- Letter case ----

    [Fact]
    public void MatchingIsCaseInsensitiveThroughout()
    {
        // Two spellings that differ only in case name one file, which is how path equality and
        // scope already work, and a link whose case drifted still opens the file in the editor.
        Assert.True(WikilinkTarget.Matches("notes/alpha", At("Notes/Alpha.md")));
        Assert.True(WikilinkTarget.Matches("ALPHA.MD", At("Notes/Alpha.md")));
    }
}
