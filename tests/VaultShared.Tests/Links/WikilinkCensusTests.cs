using VaultShared.Access;
using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// Verifies the census a removal runs: every surviving link into the set being removed is reported
/// one occurrence at a time and nothing is rewritten. A removal cannot repair links the way a move
/// can, because deleting one of two same-named notes leaves bare links resolving to the survivor
/// and the right fix is the caller's to choose.
/// </summary>
public sealed class WikilinkCensusTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    private LinkPassOutcome<IReadOnlyList<LinkOccurrence>> Removing(params string[] paths) =>
        WikilinkMaintenance.Census(_vault.Access.BypassingScope(), [.. paths.Select(_vault.At)]);

    [Fact]
    public void APathQualifiedLinkNamingSomethingRemovedIsReportedOnTheSuffixRule()
    {
        _vault
            .WithNote("Projects/Vault/Inbox/Widget.md", "# Doomed\n")
            .WithNote("Notes/Suffix.md", "See [[Inbox/Widget]].\n")
            .WithNote("Notes/MidComponent.md", "See [[box/Widget]].\n");

        Assert.Equal(
            ["Notes/Suffix.md:1 Inbox/Widget"],
            LinkPass.Occurrences(Removing("Projects/Vault/Inbox/Widget.md")));
    }

    [Fact]
    public void ABareLinkIsReportedOnlyWhereItResolvedToSomethingRemoved()
    {
        // A bare link resolves by the editor's ranking, so a same-named note elsewhere can be the
        // real target: from `Aa/Note.md`, `Aa/Bb/Dupe.md` and `Dupe.md` tie and the copy in the
        // subtree wins.
        _vault
            .WithNote("Dupe.md", "# Doomed\n")
            .WithNote("Aa/Bb/Dupe.md", "# The survivor\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n")
            .WithNote("Cc/Note.md", "See [[Dupe]].\n");

        Assert.Equal(["Cc/Note.md:1 Dupe"], LinkPass.Occurrences(Removing("Dupe.md")));
    }

    [Fact]
    public void EachSurvivingOccurrenceIsReportedIndividually()
    {
        // A caller fixing them visits each one, so the report is per occurrence rather than per
        // note.
        _vault
            .WithNote("Projects/Widget.md", "# Doomed\n")
            .WithNote("Notes/Ref.md", "See [[Widget]].\n\nAnd again: [[Projects/Widget]].\n");

        Assert.Equal(
            ["Notes/Ref.md:1 Widget", "Notes/Ref.md:3 Projects/Widget"],
            LinkPass.InOrder(Removing("Projects/Widget.md")));
    }

    [Fact]
    public void LinksInsideTheRemovedSetAreNotReported()
    {
        _vault
            .WithNote("Doomed/One.md", "See [[Two]].\n")
            .WithNote("Doomed/Two.md", "See [[One]].\n")
            .WithNote("Notes/Ref.md", "See [[One]].\n");

        Assert.Equal(
            ["Notes/Ref.md:1 One"],
            LinkPass.Occurrences(Removing("Doomed/One.md", "Doomed/Two.md")));
    }

    [Fact]
    public void ANonMarkdownFileInTheRemovedSetIsMatchedByAQualifiedLink()
    {
        // The census runs over files rather than only over notes, so a link naming a non-markdown
        // file in the removed set is still reported.
        _vault
            .WithNote("Assets/Diagram.png", "not really an image\n")
            .WithNote("Notes/Ref.md", "Embed ![[Assets/Diagram.png]].\n");

        Assert.Equal(["Notes/Ref.md:1 Assets/Diagram.png"], LinkPass.Occurrences(Removing("Assets/Diagram.png")));
    }

    [Fact]
    public void ACensusThatRanAndFoundNothingIsAFinding()
    {
        _vault
            .WithNote("Projects/Widget.md", "# Doomed\n")
            .WithNote("Notes/Ref.md", "Nothing points anywhere.\n");

        Assert.Empty(LinkPass.Occurrences(Removing("Projects/Widget.md")));
    }

    [Fact]
    public void TheCensusRewritesNothing()
    {
        // A removal reports links but never repairs them, so every reported link still reads
        // exactly as it did.
        _vault
            .WithNote("Projects/Widget.md", "# Doomed\n")
            .WithNote("Other/Widget.md", "# The survivor\n")
            .WithNote("Notes/Ref.md", "See [[Projects/Widget]] and [[Widget]].\n");

        Assert.NotEmpty(LinkPass.Occurrences(Removing("Projects/Widget.md")));
        Assert.Equal("See [[Projects/Widget]] and [[Widget]].\n", _vault.TextOf("Notes/Ref.md"));
    }

    [Fact]
    public void ANoteThatIsThereAndWillNotOpenFailsThePass()
    {
        // A file that has gone away may be skipped. Skipping one that is present but unreadable
        // would turn a failed pass into a silent success.
        _vault
            .WithNote("Projects/Widget.md", "# Doomed\n")
            .WithNote("Notes/Ref.md", "See [[Widget]].\n");

        using (_vault.Unreadable("Notes/Ref.md"))
        {
            var outcome = Removing("Projects/Widget.md");

            Assert.Equal(
                "Notes/Ref.md",
                Assert.IsType<LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.ReadFailed>(outcome).Path);
        }
    }

    [Fact]
    public void AnEnumerationThatFailedIsNotAnEmptyCensus()
    {
        using var missing = new ScratchVault(engine: new SearchEngine(Path.Combine(Path.GetTempPath(), "no-such-rg")));

        var outcome = WikilinkMaintenance.Census(missing.Access.BypassingScope(), [missing.At("Projects/Widget.md")]);

        Assert.IsType<LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.EnumerationFailed>(outcome);
    }
}
