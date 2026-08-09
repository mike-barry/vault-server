using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests content search at its three granularities - matching lines, matching files, and per-file
/// match counts - against a real search engine over a real directory tree.
/// </summary>
/// <remarks>
/// Each granularity is asserted on its own results rather than on a projection of the richest one,
/// and every outcome is asserted on all three. A caller cannot recover from a bad pattern being
/// reported as a failed search, so the two must stay distinct at every granularity.
/// </remarks>
public sealed class VaultSearchTests : IDisposable
{
    private readonly ScratchVault _vault = Layout(new ScratchVault());

    public void Dispose() => _vault.Dispose();

    private static ScratchVault Layout(ScratchVault vault) => vault
        .WithNote("Notes/Alpha.md", "needle here\nplain\nneedle again\n")
        .WithNote("Notes/Sub/Nested.md", "needle nested\n")
        .WithNote("Notes/Data.txt", "needle in something that is not a note\n")
        .WithNote("Projects/Widget/Widget.md", "needle in scope\n")
        .WithNote("Inbox/Loose.md", "nothing of interest\n")
        .WithNote(".obsidian/Deep.md", "needle under a dot\n");

    private static IReadOnlyList<string> Paths<TResult>(SearchOutcome<TResult> outcome, Func<TResult, VaultPath> of) =>
        [.. Assert.IsType<SearchOutcome<TResult>.Results>(outcome).Items.Select(item => of(item).Value).Order()];

    private static IReadOnlyList<string> Files(SearchOutcome<VaultPath> outcome) => Paths(outcome, p => p);

    // ---- The three granularities ----

    [Fact]
    public void SearchLines_AnswersEachMatchingLineWithItsLocation()
    {
        var items = Assert.IsType<SearchOutcome<MatchLine>.Results>(_vault.Access.SearchLines("needle", "Alpha.md")).Items;

        Assert.Collection(
            items,
            first =>
            {
                Assert.Equal("Notes/Alpha.md", first.Path.Value);
                Assert.Equal(1, first.LineNumber);
                Assert.Equal("needle here", first.Text);
            },
            second =>
            {
                Assert.Equal(3, second.LineNumber);
                Assert.Equal("needle again", second.Text);
            });
    }

    [Fact]
    public void SearchFiles_AnswersTheSetOfNotesContainingAMatch()
    {
        Assert.Equal(
            ["Notes/Alpha.md", "Notes/Sub/Nested.md", "Projects/Widget/Widget.md"],
            Files(_vault.Access.SearchFiles("needle")));
    }

    [Fact]
    public void SearchCounts_AnswersHowManyLinesMatchedPerNote()
    {
        var items = Assert.IsType<SearchOutcome<MatchCount>.Results>(_vault.Access.SearchCounts("needle")).Items;

        Assert.Equal(2, items.Single(c => c.Path.Value == "Notes/Alpha.md").Count);
        Assert.Equal(1, items.Single(c => c.Path.Value == "Notes/Sub/Nested.md").Count);
        Assert.Equal(1, items.Single(c => c.Path.Value == "Projects/Widget/Widget.md").Count);
        // Every note holding the pattern is counted. Without this, a granularity that reported only
        // some of the matching notes would still satisfy the counts above.
        Assert.Equal(3, items.Count);
    }

    // ---- Behaviour common to all three ----

    [Fact]
    public void SearchAnswersNoteContentAndNotTheRestOfTheVault()
    {
        Assert.DoesNotContain("Notes/Data.txt", Files(_vault.Access.SearchFiles("needle")));
        Assert.DoesNotContain("Notes/Data.txt", Paths(_vault.Access.SearchLines("needle"), m => m.Path));
        Assert.DoesNotContain("Notes/Data.txt", Paths(_vault.Access.SearchCounts("needle"), c => c.Path));
    }

    [Fact]
    public void DotEntriesAreOutByDefaultAndTheSwitchAdmitsThem()
    {
        Assert.DoesNotContain(".obsidian/Deep.md", Files(_vault.Access.SearchFiles("needle")));
        Assert.Contains(".obsidian/Deep.md", Files(_vault.Access.SearchFiles("needle", includeDotEntries: true)));
    }

    [Fact]
    public void AGlobNarrowsWhichNotesAreSearchedAndAnchorsAsEnumerationDoes()
    {
        Assert.Equal(["Notes/Alpha.md", "Notes/Sub/Nested.md"], Files(_vault.Access.SearchFiles("needle", "Notes/**")));
        Assert.Equal(["Notes/Alpha.md"], Files(_vault.Access.SearchFiles("needle", "Notes/*.md")));
        Assert.Equal(["Notes/Sub/Nested.md"], Files(_vault.Access.SearchFiles("needle", "Nested.md")));
    }

    [Fact]
    public void APatternThatReadsAsAnOptionOrAShellFragmentIsMatchedAsData()
    {
        using var literal = new ScratchVault()
            .WithNote("Options/Files.md", "the --files option\n")
            .WithNote("Options/Ignore.md", "the -i option\n")
            .WithNote("Options/Shell.md", "the x & echo hi line\n");

        // Handed to the engine positionally, "--files" and "-i" would be taken as its own options
        // and answered with something plausible; composed into a command line, "x & echo hi" would
        // reach an interpreter. Each comes back as the single note whose text contains it.
        Assert.Equal(["Options/Files.md"], Files(literal.Access.SearchFiles("--files")));
        Assert.Equal(["Options/Files.md"], Paths(literal.Access.SearchLines("--files"), m => m.Path));
        Assert.Equal(["Options/Files.md"], Paths(literal.Access.SearchCounts("--files"), c => c.Path));

        Assert.Equal(["Options/Ignore.md"], Files(literal.Access.SearchFiles("-i")));
        Assert.Equal(["Options/Ignore.md"], Paths(literal.Access.SearchLines("-i"), m => m.Path));
        Assert.Equal(["Options/Ignore.md"], Paths(literal.Access.SearchCounts("-i"), c => c.Path));

        Assert.Equal(["Options/Shell.md"], Files(literal.Access.SearchFiles("x & echo hi")));
        Assert.Equal(["Options/Shell.md"], Paths(literal.Access.SearchLines("x & echo hi"), m => m.Path));
        Assert.Equal(["Options/Shell.md"], Paths(literal.Access.SearchCounts("x & echo hi"), c => c.Path));
    }

    [Fact]
    public void AContentPatternIsMatchedCaseSensitivelyWhileTheGlobIsNot()
    {
        // The two halves of a search differ on purpose: which notes are searched is decided
        // case-insensitively, and what counts as a match inside them is not. A caller searching for
        // a proper noun or for TODO gets only the spelling it asked for.
        using var cased = new ScratchVault().WithNote("Notes/Cased.md", "Needle here\n");

        Assert.Equal(["Notes/Cased.md"], Files(cased.Access.SearchFiles("Needle")));
        Assert.Equal(["Notes/Cased.md"], Paths(cased.Access.SearchLines("Needle"), m => m.Path));
        Assert.Equal(["Notes/Cased.md"], Paths(cased.Access.SearchCounts("Needle"), c => c.Path));

        Assert.Empty(Assert.IsType<SearchOutcome<VaultPath>.Results>(cased.Access.SearchFiles("needle")).Items);
        Assert.Empty(Assert.IsType<SearchOutcome<MatchLine>.Results>(cased.Access.SearchLines("needle")).Items);
        Assert.Empty(Assert.IsType<SearchOutcome<MatchCount>.Results>(cased.Access.SearchCounts("needle")).Items);

        Assert.Equal(["Notes/Cased.md"], Files(cased.Access.SearchFiles("Needle", "notes/cased.MD")));
    }

    [Fact]
    public void ANarrowScopeWithholdsSilently()
    {
        using var narrow = Layout(new ScratchVault(VaultScope.Of(["Projects/Widget"])));

        Assert.Equal(["Projects/Widget/Widget.md"], Files(narrow.Access.SearchFiles("needle")));
        Assert.Equal(["Projects/Widget/Widget.md"], Paths(narrow.Access.SearchLines("needle"), m => m.Path));
        Assert.Equal(["Projects/Widget/Widget.md"], Paths(narrow.Access.SearchCounts("needle"), c => c.Path));
    }

    [Fact]
    public void AMatchedPathIsQuotableBackIntoALaterCall()
    {
        var path = Assert.IsType<SearchOutcome<VaultPath>.Results>(_vault.Access.SearchFiles("nested")).Items.Single();

        Assert.Equal("Notes/Sub/Nested.md", path.Value);
        Assert.IsType<PathResolution<ReadPath>.Admitted>(_vault.Access.ForRead(path.Value));
    }

    // ---- Outcomes, on every granularity ----

    [Fact]
    public void NoMatchIsAnEmptyResultRatherThanAFailure()
    {
        // No match is a correct answer to a well-formed question. A caller that needs exactly one
        // match checks for that itself.
        Assert.Empty(Assert.IsType<SearchOutcome<MatchLine>.Results>(_vault.Access.SearchLines("haystack")).Items);
        Assert.Empty(Assert.IsType<SearchOutcome<VaultPath>.Results>(_vault.Access.SearchFiles("haystack")).Items);
        Assert.Empty(Assert.IsType<SearchOutcome<MatchCount>.Results>(_vault.Access.SearchCounts("haystack")).Items);
    }

    [Fact]
    public void AMalformedRegexIsACallerError()
    {
        Assert.IsType<SearchOutcome<MatchLine>.BadPattern>(_vault.Access.SearchLines("("));
        Assert.IsType<SearchOutcome<VaultPath>.BadPattern>(_vault.Access.SearchFiles("("));
        Assert.IsType<SearchOutcome<MatchCount>.BadPattern>(_vault.Access.SearchCounts("("));
    }

    [Fact]
    public void AMalformedGlobIsTheSameCallerErrorAsAMalformedRegex()
    {
        // The engine refuses both the same way, so one outcome covers them.
        Assert.IsType<SearchOutcome<MatchLine>.BadPattern>(_vault.Access.SearchLines("needle", "["));
        Assert.IsType<SearchOutcome<VaultPath>.BadPattern>(_vault.Access.SearchFiles("needle", "["));
        Assert.IsType<SearchOutcome<MatchCount>.BadPattern>(_vault.Access.SearchCounts("needle", "["));
    }

    [Fact]
    public void AFileThatCannotBeReadFailsTheWholeSearch()
    {
        // Skipping the file would turn a permissions problem into a wrong answer the caller cannot
        // tell from a correct one, so no partial result comes back.
        using var held = _vault.Unreadable("Notes/Alpha.md");

        var lines = Assert.IsType<SearchOutcome<MatchLine>.Unreadable>(_vault.Access.SearchLines("needle"));
        Assert.Equal("Notes/Alpha.md", lines.Path);
        Assert.NotEmpty(lines.Reason);

        Assert.IsType<SearchOutcome<VaultPath>.Unreadable>(_vault.Access.SearchFiles("needle"));
        Assert.IsType<SearchOutcome<MatchCount>.Unreadable>(_vault.Access.SearchCounts("needle"));
    }

    [Fact]
    public void AnUnreadableFileFailsTheSearchEvenWhenNothingElseMatched()
    {
        // With no results to show, a run that failed on a file must still be distinguishable from
        // a pattern the engine refused.
        using var held = _vault.Unreadable("Notes/Alpha.md");

        Assert.IsType<SearchOutcome<VaultPath>.Unreadable>(_vault.Access.SearchFiles("haystack"));
        Assert.IsType<SearchOutcome<MatchCount>.Unreadable>(_vault.Access.SearchCounts("haystack"));
        Assert.IsType<SearchOutcome<MatchLine>.Unreadable>(_vault.Access.SearchLines("haystack"));
    }

    [Fact]
    public void AnUnreadableFileUnderADotDirectoryDoesNotFailADefaultSearch()
    {
        // With dot entries off, the engine prunes the walk, so a file held locked by a concurrent
        // git operation is never read by a search that would not have included it. Asked for dot
        // entries, the same search reads the file and fails.
        using var held = _vault.Unreadable(".obsidian/Deep.md");

        Assert.Equal(
            ["Notes/Alpha.md", "Notes/Sub/Nested.md", "Projects/Widget/Widget.md"],
            Files(_vault.Access.SearchFiles("needle")));

        Assert.IsType<SearchOutcome<VaultPath>.Unreadable>(_vault.Access.SearchFiles("needle", includeDotEntries: true));
    }

    /// <summary>
    /// An engine that is not where it was said to be is its own outcome at every granularity, so no
    /// granularity reports a permanent condition as one a retry could clear.
    /// </summary>
    [Fact]
    public void AnEngineThatCannotBeRunIsItsOwnOutcomeRatherThanAGeneralFailure()
    {
        using var missing = Layout(new ScratchVault(engine: new SearchEngine(Path.Combine(_vault.Root, "no-such-engine"))));

        Assert.IsType<SearchOutcome<MatchLine>.EngineUnavailable>(missing.Access.SearchLines("needle"));
        Assert.IsType<SearchOutcome<VaultPath>.EngineUnavailable>(missing.Access.SearchFiles("needle"));
        Assert.IsType<SearchOutcome<MatchCount>.EngineUnavailable>(missing.Access.SearchCounts("needle"));
    }
}
