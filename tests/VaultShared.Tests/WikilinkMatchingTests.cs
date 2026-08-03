using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Whether a link's target names a path. Both ways of getting this wrong are silent - a link repair
/// fails to match is left pointing at nothing, and a link the census fails to match is an orphan
/// nobody reports - so both directions are asserted for each form.
///
/// Nothing here touches a filesystem, so the vault is a value rather than a tree.
/// </summary>
public sealed class WikilinkMatchingTests
{
    private static readonly Vault Vault = new("test", Path.Combine(Path.GetTempPath(), "no-such-vault"), PathPolicy.None);

    private static VaultPath At(string relativePath) =>
        VaultPath.TryCreate(Vault, relativePath, out var path, out var reason) ? path : throw new ArgumentException(reason);

    // ---- Path-qualified: a suffix of whole components ----

    [Fact]
    public void APathQualifiedTargetMatchesASuffixOfWholeComponents()
    {
        // The editor writes partial paths, so the qualification need not start at the vault root.
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
        // `sign/Spec-Tool-Capabilities` names nothing: components are whole or they do not match.
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

    // ---- Bare: basename alone ----

    [Fact]
    public void ABareTargetMatchesOnBasenameAtAnyDepth()
    {
        Assert.True(WikilinkTarget.Matches("Alpha", At("Alpha.md")));
        Assert.True(WikilinkTarget.Matches("Alpha", At("Notes/Sub/Alpha.md")));
    }

    [Fact]
    public void ABareTargetMatchesNoAncestorSegment()
    {
        // Which of several same-named candidates a bare target resolves to is ranking's question,
        // not this one - but a folder's name is never the basename.
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

    // ---- The extension ----

    [Fact]
    public void ATargetMayOmitTheNoteExtension()
    {
        Assert.True(WikilinkTarget.Matches("Alpha", At("Notes/Alpha.md")));
        Assert.True(WikilinkTarget.Matches("Notes/Alpha", At("Notes/Alpha.md")));
    }

    [Fact]
    public void ATargetMayCarryTheExtensionItNames()
    {
        // The editor writes every non-note target with its own extension, and tolerates the note
        // one being spelled out.
        Assert.True(WikilinkTarget.Matches("Alpha.md", At("Notes/Alpha.md")));
        Assert.True(WikilinkTarget.Matches("Diagram.png", At("Attachments/Diagram.png")));
    }

    [Fact]
    public void ATargetOmittingAnExtensionThatIsNotTheNoteOneMatchesNothing()
    {
        // Only `.md` is the target's to omit; nothing else would be recoverable from the target.
        Assert.False(WikilinkTarget.Matches("Diagram", At("Attachments/Diagram.png")));
    }

    [Fact]
    public void AWrongExtensionMatchesNothing()
    {
        Assert.False(WikilinkTarget.Matches("Alpha.txt", At("Notes/Alpha.md")));
    }

    // ---- Case ----

    [Fact]
    public void MatchingIsCaseInsensitiveThroughout()
    {
        // Two spellings name one file, which is the layer's own path equality and the scope
        // semantics behind it; a link whose case drifted still opens the file in the editor.
        Assert.True(WikilinkTarget.Matches("notes/alpha", At("Notes/Alpha.md")));
        Assert.True(WikilinkTarget.Matches("ALPHA.MD", At("Notes/Alpha.md")));
    }
}
