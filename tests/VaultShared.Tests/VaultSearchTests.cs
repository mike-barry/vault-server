using System.Text;
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

    /// <summary>Holds the stand-in engines <see cref="Faked"/> writes, deleted with the fixture.</summary>
    private readonly string _standIns = Directory.CreateTempSubdirectory("vault-stand-in-engine-").FullName;

    private int _standInCount;

    public void Dispose()
    {
        _vault.Dispose();
        try
        {
            Directory.Delete(_standIns, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    // ---- An engine that answers something the layer cannot use ----

    /// <summary>
    /// Builds a vault whose search engine is a stand-in script: it ignores every argument, copies
    /// fixed bytes to each stream, and exits with a fixed code. A real engine cannot be made to
    /// produce a broken answer, and the engine's location is the seam that lets one be substituted,
    /// so this is how the failed outcome is reached without a hook in the layer under test.
    /// </summary>
    /// <remarks>
    /// The payloads are copied out of files rather than echoed by the script, because the cases below
    /// need a NUL separator and text a shell would otherwise interpret. No payload carries a newline,
    /// so nothing here depends on how the command shell's copy command treats line terminators.
    ///
    /// The vault is empty. Nothing walks it, because the stand-in never looks at the directory it was
    /// started in, and a vault path is well-formed or not by its text alone.
    /// </remarks>
    /// <param name="standardOutput">What the stand-in writes to standard output.</param>
    /// <param name="standardError">What the stand-in writes to standard error.</param>
    /// <param name="exitCode">The code the stand-in exits with.</param>
    /// <returns>The vault, whose access runs the stand-in. Dispose it.</returns>
    private ScratchVault Faked(string standardOutput = "", string standardError = "", int exitCode = 0)
    {
        var name = $"stand-in-{_standInCount++}";
        var script = Path.Combine(_standIns, name + ".cmd");
        var outPath = Path.Combine(_standIns, name + ".out");
        var errPath = Path.Combine(_standIns, name + ".err");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(outPath, standardOutput, utf8);
        File.WriteAllText(errPath, standardError, utf8);

        string[] lines = ["@echo off", $"type \"{outPath}\"", $"type \"{errPath}\" 1>&2", $"exit {exitCode}"];

        File.WriteAllText(script, string.Join('\n', lines) + "\n", utf8);

        return new ScratchVault(engine: new SearchEngine(script));
    }

    /// <summary>
    /// Checks that an exit code outside the engine's own vocabulary is a failed search at every
    /// granularity, and that the code reaches the reason. Codes 0, 1 and 2 mean matched, did not
    /// match, and completed with a problem; anything else is a run this layer cannot interpret, and
    /// reporting it as a bad pattern would tell a caller to fix a pattern that was fine.
    /// </summary>
    [Fact]
    public void AnExitCodeTheEngineDoesNotDefineIsAFailedSearch()
    {
        using var broken = Faked(exitCode: 7);

        Assert.Equal(
            "The search engine exited with code 7.",
            Assert.IsType<SearchOutcome<MatchLine>.Failed>(broken.Access.SearchLines("needle")).Reason);
        Assert.Equal(
            "The search engine exited with code 7.",
            Assert.IsType<SearchOutcome<VaultPath>.Failed>(broken.Access.SearchFiles("needle")).Reason);
        Assert.Equal(
            "The search engine exited with code 7.",
            Assert.IsType<SearchOutcome<MatchCount>.Failed>(broken.Access.SearchCounts("needle")).Reason);
    }

    /// <summary>
    /// Checks that a diagnostic naming no entry is a failed search rather than an unreadable file.
    /// The unreadable outcome carries the entry it was about, so a diagnostic about the engine itself
    /// has no entry to put there and must not be reported as one.
    /// </summary>
    [Fact]
    public void ADiagnosticNamingNoEntryIsAFailedSearchRatherThanAnUnreadableFile()
    {
        using var broken = Faked(standardError: "rg: unexpected internal failure");

        Assert.Equal(
            "rg: unexpected internal failure",
            Assert.IsType<SearchOutcome<MatchLine>.Failed>(broken.Access.SearchLines("needle")).Reason);
        Assert.Equal(
            "rg: unexpected internal failure",
            Assert.IsType<SearchOutcome<VaultPath>.Failed>(broken.Access.SearchFiles("needle")).Reason);
        Assert.Equal(
            "rg: unexpected internal failure",
            Assert.IsType<SearchOutcome<MatchCount>.Failed>(broken.Access.SearchCounts("needle")).Reason);
    }

    /// <summary>
    /// Checks that a path the engine emitted which is not inside this vault fails the whole search at
    /// every granularity, naming the text that was emitted. Each granularity reads a different output
    /// shape and so has its own check, and no granularity may answer with the paths it could parse:
    /// a result set that dropped one silently would be a wrong answer the caller cannot tell from a
    /// right one.
    /// </summary>
    [Fact]
    public void APathTheEngineEmittedThatIsNotInThisVaultFailsTheWholeSearch()
    {
        const string Prefix = "The search engine emitted \"../outside.md\", which is not a path in this vault:";

        // The file granularity reads NUL-separated paths with its statistics block after the last
        // separator, so the trailing chunk stands in for that block.
        using var files = Faked(standardOutput: "../outside.md\0statistics");
        // The count granularity reads path and count as alternating NUL-separated fields.
        // The count is concatenated rather than written into the literal, because a NUL escape
        // followed by a digit reads as a five-digit escape and is not one.
        using var counts = Faked(standardOutput: "../outside.md\0" + "1");
        // The line granularity reads one JSON event per line, with the path in its own field.
        using var lines = Faked(
            standardOutput: """{"type":"match","data":{"path":{"text":"../outside.md"},"line_number":1,"lines":{"text":"needle here"}}}""");

        Assert.StartsWith(Prefix, Assert.IsType<SearchOutcome<VaultPath>.Failed>(files.Access.SearchFiles("needle")).Reason);
        Assert.StartsWith(Prefix, Assert.IsType<SearchOutcome<MatchCount>.Failed>(counts.Access.SearchCounts("needle")).Reason);
        Assert.StartsWith(Prefix, Assert.IsType<SearchOutcome<MatchLine>.Failed>(lines.Access.SearchLines("needle")).Reason);
    }

    /// <summary>
    /// Checks that output the count granularity cannot read as a count fails the search rather than
    /// being skipped or counted as zero. This one is specific to that granularity, because it is the
    /// only shape carrying a number the layer has to parse.
    /// </summary>
    [Fact]
    public void ACountTheEngineEmittedThatIsNotANumberFailsTheSearch()
    {
        using var broken = Faked(standardOutput: "Notes/Alpha.md\0not a number");

        Assert.Equal(
            "The search engine emitted \"not a number\" where a count was expected.",
            Assert.IsType<SearchOutcome<MatchCount>.Failed>(broken.Access.SearchCounts("needle")).Reason);
    }
}
