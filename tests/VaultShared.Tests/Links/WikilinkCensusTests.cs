using VaultShared.Access;
using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// The census on removal: every surviving link into the set a removal destroys, reported one
/// occurrence at a time and never rewritten. The asymmetry with creation is the editor's own -
/// deleting one of two same-named notes leaves bare links resolving to the survivor, and what the
/// caller wants done about them is not knowable here.
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
        // A same-named bystander elsewhere is that link's real target, and the editor's ranking is
        // what says which is which: `Aa/Bb/Dupe.md` and `Dupe.md` are a dead tie from `Aa/Note.md`,
        // and the subtree copy takes it.
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
        // The removal's own plan asks the file census, which sees what note enumeration does not.
        // A link naming one of those is still a link into the set.
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
        // No protection on delete. Every reported link still says exactly what it said.
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
        // What may be skipped is a file with nothing left to read. Skipping one that is present and
        // unreadable would turn a failed operation into a silent success.
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
