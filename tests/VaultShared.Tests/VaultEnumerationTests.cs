using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// The two enumeration surfaces, against a real engine over a real tree. What is asserted here is
/// everything a caller cannot tell apart from a confidently wrong answer: which domain answered,
/// how a pattern anchors, whether dot entries came back, and whether a narrow scope withheld
/// silently.
/// </summary>
public sealed class VaultEnumerationTests : IDisposable
{
    private readonly ScratchVault _vault = Layout(new ScratchVault());

    public void Dispose() => _vault.Dispose();

    /// <summary>
    /// Notes at three depths, a non-markdown file beside one of them, and a dot-directory holding
    /// both a note and something else.
    /// </summary>
    private static ScratchVault Layout(ScratchVault vault) => vault
        .WithNote("Root-Note.md")
        .WithNote("Notes/Alpha.md")
        .WithNote("Notes/Sub/Nested.md")
        .WithNote("Notes/Data.txt")
        .WithNote("Projects/Widget/Widget.md")
        .WithNote("Inbox/Loose.md")
        .WithNote(".obsidian/Deep.md")
        .WithNote(".obsidian/workspace.json");

    private static IReadOnlyList<string> Values(SearchOutcome<VaultPath> outcome) =>
        [.. Assert.IsType<SearchOutcome<VaultPath>.Results>(outcome).Items.Select(p => p.Value).Order()];

    // ---- The two domains ----

    [Fact]
    public void EnumerateNotes_AnswersMarkdownAndNothingElse()
    {
        Assert.Equal(
            ["Inbox/Loose.md", "Notes/Alpha.md", "Notes/Sub/Nested.md", "Projects/Widget/Widget.md", "Root-Note.md"],
            Values(_vault.Access.EnumerateNotes()));
    }

    [Fact]
    public void EnumerateFiles_AnswersEveryFileWhateverItsExtension()
    {
        // The census backs passes where a silent omission is a wrong answer rather than a narrower
        // one, so the non-markdown file is in it.
        Assert.Contains("Notes/Data.txt", Values(_vault.Access.EnumerateFiles()));
    }

    [Fact]
    public void EnumerateFiles_InheritsNoIgnoreFileBehaviour()
    {
        // A gitignored file is still on disk and still destroyed.
        _vault.WithNote(".gitignore", "Notes/Ignored.md\n").WithNote("Notes/Ignored.md");

        Assert.Contains("Notes/Ignored.md", Values(_vault.Access.EnumerateFiles()));
    }

    // ---- Anchoring ----

    [Fact]
    public void APatternWithNoSeparatorMatchesTheFinalSegmentAtAnyDepth()
    {
        Assert.Equal(
            ["Inbox/Loose.md", "Notes/Alpha.md", "Notes/Sub/Nested.md", "Projects/Widget/Widget.md", "Root-Note.md"],
            Values(_vault.Access.EnumerateNotes("*.md")));
    }

    [Fact]
    public void APatternWithASeparatorIsAnchoredAtTheVaultRoot()
    {
        // The two answers differ and a caller cannot tell which it got, which is why the anchoring
        // is a requirement rather than whatever the engine happened to do.
        Assert.Equal(["Notes/Alpha.md"], Values(_vault.Access.EnumerateNotes("Notes/*.md")));
        Assert.Empty(Values(_vault.Access.EnumerateNotes("Sub/*.md")));
    }

    [Fact]
    public void APatternIsMatchedCaseInsensitively()
    {
        Assert.Equal(["Notes/Alpha.md"], Values(_vault.Access.EnumerateNotes("notes/alpha.MD")));
    }

    [Fact]
    public void APatternWithRecursiveDescentReachesEveryDepthBeneathIt()
    {
        Assert.Equal(
            ["Notes/Alpha.md", "Notes/Sub/Nested.md"],
            Values(_vault.Access.EnumerateNotes("Notes/**")));
    }

    // ---- The markdown restriction ----

    [Fact]
    public void TheMarkdownRestrictionDoesNotRideOnTheCallersPattern()
    {
        // The hazard this case exists for: "Notes/**" with ".md" appended is "Notes/**.md", which
        // the engine accepts and degrades to a single "*", so the nested note disappears. The
        // restriction is a predicate over the result, so it does not.
        var notes = Values(_vault.Access.EnumerateNotes("Notes/**"));

        Assert.Contains("Notes/Sub/Nested.md", notes);
        Assert.DoesNotContain("Notes/Data.txt", notes);
    }

    [Fact]
    public void TheMarkdownRestrictionIsCaseInsensitive()
    {
        // Names already on disk are not checked against the name grammar, so a note spelled .MD
        // stays enumerable.
        _vault.WithNote("Notes/Shouty.MD");

        Assert.Contains("Notes/Shouty.MD", Values(_vault.Access.EnumerateNotes()));
    }

    // ---- The dot rule ----

    [Theory]
    [InlineData(null)]
    [InlineData("*.md")]
    public void DotEntriesAreOutByDefaultOnBothSurfaces(string? pattern)
    {
        Assert.DoesNotContain(".obsidian/Deep.md", Values(_vault.Access.EnumerateNotes(pattern)));
        Assert.DoesNotContain(".obsidian/Deep.md", Values(_vault.Access.EnumerateFiles(pattern)));
    }

    [Fact]
    public void TheSwitchAdmitsDotEntriesOnBothSurfaces()
    {
        // What lets a destruction census see a nested .git and report every file a removal takes
        // with it.
        Assert.Contains(".obsidian/Deep.md", Values(_vault.Access.EnumerateNotes(includeDotEntries: true)));
        Assert.Contains(".obsidian/workspace.json", Values(_vault.Access.EnumerateFiles(includeDotEntries: true)));
    }

    [Fact]
    public void ADotSegmentAtAnyDepthIsDropped()
    {
        _vault.WithNote("Notes/.attachments/Buried.md");

        Assert.DoesNotContain("Notes/.attachments/Buried.md", Values(_vault.Access.EnumerateFiles()));
        Assert.Contains("Notes/.attachments/Buried.md", Values(_vault.Access.EnumerateFiles(includeDotEntries: true)));
    }

    // ---- Scope ----

    [Fact]
    public void ANarrowScopeWithholdsSilently()
    {
        using var narrow = Layout(new ScratchVault(VaultScope.Of(["Projects/Widget"])));

        // A list returns the in-scope subset without saying what was withheld: no refusal, no count,
        // nothing that names what the caller may not read.
        Assert.Equal(["Projects/Widget/Widget.md"], Values(narrow.Access.EnumerateNotes()));
        Assert.Equal(["Projects/Widget/Widget.md"], Values(narrow.Access.EnumerateFiles()));
    }

    [Fact]
    public void APatternMatchingOnlyOutOfScopePathsAnswersEmptyRatherThanRefusing()
    {
        using var narrow = Layout(new ScratchVault(VaultScope.Of(["Projects/Widget"])));

        Assert.Empty(Values(narrow.Access.EnumerateNotes("Inbox/**")));
    }

    // ---- The four outcomes ----

    [Fact]
    public void NoMatchIsAnEmptyResultRatherThanAFailure()
    {
        Assert.Empty(Values(_vault.Access.EnumerateNotes("Nothing-Named-This.md")));
    }

    [Fact]
    public void AMalformedGlobIsACallerError()
    {
        var refusal = Assert.IsType<SearchOutcome<VaultPath>.BadPattern>(_vault.Access.EnumerateNotes("["));

        Assert.NotEmpty(refusal.Reason);
    }

    [Fact]
    public void AnEngineThatCannotBeRunIsAnOperationalFailure()
    {
        using var missing = Layout(new ScratchVault(engine: new SearchEngine(Path.Combine(_vault.Root, "no-such-engine"))));

        var failure = Assert.IsType<SearchOutcome<VaultPath>.Failed>(missing.Access.EnumerateNotes());

        Assert.NotEmpty(failure.Reason);
    }

    // ---- What enumeration hands out ----

    [Fact]
    public void AnEnumeratedPathIsQuotableBackIntoALaterCall()
    {
        var path = Assert.IsType<SearchOutcome<VaultPath>.Results>(_vault.Access.EnumerateNotes("Nested.md")).Items.Single();

        // Vault-relative with one separator convention, and admitted when named back - enumeration
        // hands out containment, not admission, so the round trip goes through the scope check
        // exactly as any other caller-named path does.
        Assert.Equal("Notes/Sub/Nested.md", path.Value);
        Assert.IsType<PathResolution<ReadPath>.Admitted>(_vault.Access.ForRead(path.Value));
    }

    [Fact]
    public void AnEnumeratedPathCarriesTheVaultItWasFoundIn()
    {
        var path = Assert.IsType<SearchOutcome<VaultPath>.Results>(_vault.Access.EnumerateNotes("Nested.md")).Items.Single();

        Assert.Same(_vault.Vault, path.Vault);
    }
}
