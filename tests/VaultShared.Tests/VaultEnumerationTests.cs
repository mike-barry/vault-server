using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests the two enumeration surfaces, <c>EnumerateNotes</c> and <c>EnumerateFiles</c>, against a
/// real search engine over a real directory tree.
/// </summary>
/// <remarks>
/// Enumeration returns a list, so a wrong answer looks the same to a caller as a right one. These
/// tests pin the parts a caller cannot check: which set of files was enumerated, how a pattern
/// anchors, whether dot entries came back, and what a narrow scope withheld.
/// </remarks>
public sealed class VaultEnumerationTests : IDisposable
{
    private readonly ScratchVault _vault = Layout(new ScratchVault());

    public void Dispose() => _vault.Dispose();

    /// <summary>
    /// Seeds a vault with notes at three depths, a non-markdown file beside one of them, and a
    /// dot directory holding both a note and a non-markdown file.
    /// </summary>
    /// <param name="vault">The vault to seed.</param>
    /// <returns>The same vault, so the call can be inlined.</returns>
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

    // ---- The two file sets ----

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
        // This surface backs operations where omitting a file gives a wrong answer rather than a
        // narrower one, such as reporting what a recursive removal destroys.
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
        // Anchored and unanchored matching give different answers and a caller cannot tell which
        // it got, so the anchoring is pinned here rather than left to the engine's default.
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

    [Fact]
    public void APatternLeadingWithAHyphenIsMatchedAsAPatternRatherThanTakenAsAnOption()
    {
        // Reaching the engine as an option instead of as the caller's glob, "-i" would leave the
        // walk unnarrowed and answer with every note in the vault.
        Assert.Empty(Values(_vault.Access.EnumerateNotes("-i")));
    }

    // ---- The markdown restriction ----

    [Fact]
    public void TheMarkdownRestrictionDoesNotRideOnTheCallersPattern()
    {
        // Appending ".md" to the caller's "Notes/**" would give "Notes/**.md", which the engine
        // accepts but treats as a single "*", dropping the nested note. The markdown restriction
        // is applied as a filter over the results instead, so the pattern is unchanged.
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
        // This is what lets a removal preview see a nested .git and report every file the removal
        // would take with it.
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

        // The list is the in-scope subset with no indication of what was withheld: no refusal, no
        // count, nothing naming what the caller may not read.
        Assert.Equal(["Projects/Widget/Widget.md"], Values(narrow.Access.EnumerateNotes()));
        Assert.Equal(["Projects/Widget/Widget.md"], Values(narrow.Access.EnumerateFiles()));
    }

    [Fact]
    public void APatternMatchingOnlyOutOfScopePathsAnswersEmptyRatherThanRefusing()
    {
        using var narrow = Layout(new ScratchVault(VaultScope.Of(["Projects/Widget"])));

        Assert.Empty(Values(narrow.Access.EnumerateNotes("Inbox/**")));
    }

    // ---- Outcomes other than results ----

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

    /// <summary>
    /// An engine that is not where it was said to be is its own outcome rather than the general
    /// operational failure, because no repetition of the identical call can start an executable that
    /// is not there.
    /// </summary>
    [Fact]
    public void AnEngineThatCannotBeRunIsItsOwnOutcomeRatherThanAGeneralFailure()
    {
        using var missing = Layout(new ScratchVault(engine: new SearchEngine(Path.Combine(_vault.Root, "no-such-engine"))));

        var failure = Assert.IsType<SearchOutcome<VaultPath>.EngineUnavailable>(missing.Access.EnumerateNotes());

        Assert.NotEmpty(failure.Reason);
    }

    // ---- What enumeration hands out ----

    [Fact]
    public void AnEnumeratedPathIsQuotableBackIntoALaterCall()
    {
        var path = Assert.IsType<SearchOutcome<VaultPath>.Results>(_vault.Access.EnumerateNotes("Nested.md")).Items.Single();

        // An enumerated path is vault-relative with forward slashes. Enumeration proves containment
        // rather than admission, so naming the path back goes through the scope check like any
        // other caller-named path.
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
