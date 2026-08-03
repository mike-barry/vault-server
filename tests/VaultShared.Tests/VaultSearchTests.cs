using VaultShared.Access;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Content search at its three granularities, against a real engine over a real tree. The
/// granularities are separate capabilities, so each is asserted on its own answer rather than on a
/// projection of the richest one, and the four outcomes are asserted on all three: collapsing a
/// caller's bad pattern into "search failed" is unrecoverable above the layer.
/// </summary>
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
    }

    // ---- The domain the three share ----

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

    // ---- The four outcomes, on every granularity ----

    [Fact]
    public void NoMatchIsAnEmptyResultRatherThanAFailure()
    {
        // An empty answer is the correct answer to a well-formed question. A caller needing exactly
        // one match enforces that itself.
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
        // Both arrive as the engine's refusal of the invocation, so one classification covers them.
        Assert.IsType<SearchOutcome<MatchLine>.BadPattern>(_vault.Access.SearchLines("needle", "["));
        Assert.IsType<SearchOutcome<VaultPath>.BadPattern>(_vault.Access.SearchFiles("needle", "["));
        Assert.IsType<SearchOutcome<MatchCount>.BadPattern>(_vault.Access.SearchCounts("needle", "["));
    }

    [Fact]
    public void AFileThatCannotBeReadFailsTheWholeSearch()
    {
        // Skipping it would turn a permissions problem into a wrong answer the caller cannot
        // distinguish from a correct one, so nothing partial comes back.
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
        // The case a completion marker exists for: with no results to show, a run that failed on a
        // file must still be told from a pattern the engine refused.
        using var held = _vault.Unreadable("Notes/Alpha.md");

        Assert.IsType<SearchOutcome<VaultPath>.Unreadable>(_vault.Access.SearchFiles("haystack"));
        Assert.IsType<SearchOutcome<MatchCount>.Unreadable>(_vault.Access.SearchCounts("haystack"));
        Assert.IsType<SearchOutcome<MatchLine>.Unreadable>(_vault.Access.SearchLines("haystack"));
    }

    [Fact]
    public void AnUnreadableFileUnderADotDirectoryDoesNotFailADefaultSearch()
    {
        // With dot entries off the walk is pruned at the engine, so a file a concurrent git
        // operation holds locked is never read by a search that was never going to include it.
        // Asked for dot entries, the same search reads the file and fails whole.
        using var held = _vault.Unreadable(".obsidian/Deep.md");

        Assert.Equal(
            ["Notes/Alpha.md", "Notes/Sub/Nested.md", "Projects/Widget/Widget.md"],
            Files(_vault.Access.SearchFiles("needle")));

        Assert.IsType<SearchOutcome<VaultPath>.Unreadable>(_vault.Access.SearchFiles("needle", includeDotEntries: true));
    }

    [Fact]
    public void AnEngineThatCannotBeRunIsAnOperationalFailure()
    {
        using var missing = Layout(new ScratchVault(engine: new SearchEngine(Path.Combine(_vault.Root, "no-such-engine"))));

        Assert.IsType<SearchOutcome<MatchLine>.Failed>(missing.Access.SearchLines("needle"));
    }
}
