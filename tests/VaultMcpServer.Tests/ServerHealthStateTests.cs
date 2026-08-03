using Microsoft.Extensions.Logging;
using VaultMcpServer.Health;
using VaultShared.Health;

namespace VaultMcpServer.Tests;

/// <summary>
/// Unit-level coverage for <see cref="ServerHealthState"/> itself, independent of any
/// particular loader - the loaders' own tests (<see cref="VaultConfigTests"/>,
/// <see cref="OpClassRegistryTests"/>, <see cref="GrantStoreTests"/>) cover recording from
/// a real whole-file failure; this covers the recording/enumeration surface directly.
/// </summary>
public sealed class ServerHealthStateTests
{
    [Fact]
    public void NewInstance_IsNotFatallyDegradedAndHasNoProblems()
    {
        var healthState = new ServerHealthState();

        Assert.False(healthState.IsFatallyDegraded);
        Assert.Empty(healthState.Problems);
    }

    [Fact]
    public void RecordFatal_SetsIsFatallyDegraded()
    {
        var healthState = new ServerHealthState();

        healthState.RecordFatal("TestSource", "something broke");

        Assert.True(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Fatal, problem.Severity);
        Assert.Equal("TestSource", problem.Source);
        Assert.Equal("something broke", problem.Detail);
    }

    [Fact]
    public void RecordDegraded_DoesNotSetIsFatallyDegraded()
    {
        var healthState = new ServerHealthState();

        healthState.RecordDegraded("TestSource", "a single row was skipped");

        Assert.False(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
    }

    [Fact]
    public void RecordFatal_AfterDegraded_StillSetsIsFatallyDegraded()
    {
        var healthState = new ServerHealthState();

        healthState.RecordDegraded("Source1", "a skip");
        Assert.False(healthState.IsFatallyDegraded);

        healthState.RecordFatal("Source2", "a whole-file failure");

        Assert.True(healthState.IsFatallyDegraded);
        Assert.Equal(2, healthState.Problems.Count);
    }

    [Fact]
    public void Problems_PreservesRecordingOrder()
    {
        var healthState = new ServerHealthState();

        healthState.RecordDegraded("First", "one");
        healthState.RecordFatal("Second", "two");
        healthState.RecordDegraded("Third", "three");

        var problems = healthState.Problems.ToList();
        Assert.Equal(["First", "Second", "Third"], problems.Select(p => p.Source));
    }

    [Fact]
    public void RecordFatal_MultipleCallsAllRecordedAndStillFatal()
    {
        var healthState = new ServerHealthState();

        healthState.RecordFatal("A", "one");
        healthState.RecordFatal("B", "two");

        Assert.True(healthState.IsFatallyDegraded);
        Assert.Equal(2, healthState.Problems.Count);
    }

    [Fact]
    public void ReplaceDegraded_ProblemsAppearInProblemsView()
    {
        var healthState = new ServerHealthState();

        healthState.ReplaceDegraded("ScriptTools", ["script a is broken", "script b is broken"]);

        Assert.False(healthState.IsFatallyDegraded);
        Assert.Equal(2, healthState.Problems.Count);
        Assert.All(healthState.Problems, p =>
        {
            Assert.Equal(ProblemSeverity.Degraded, p.Severity);
            Assert.Equal("ScriptTools", p.Source);
        });
    }

    [Fact]
    public void ReplaceDegraded_SecondCallReplacesNotAppends()
    {
        var healthState = new ServerHealthState();
        healthState.ReplaceDegraded("ScriptTools", ["script a is broken"]);

        healthState.ReplaceDegraded("ScriptTools", ["script b is broken"]);

        var problem = Assert.Single(healthState.Problems);
        Assert.Equal("script b is broken", problem.Detail);
    }

    [Fact]
    public void ReplaceDegraded_EmptySetClearsTheSourcesProblems()
    {
        var healthState = new ServerHealthState();
        healthState.ReplaceDegraded("ScriptTools", ["script a is broken"]);
        Assert.Single(healthState.Problems);

        healthState.ReplaceDegraded("ScriptTools", Array.Empty<string>());

        Assert.Empty(healthState.Problems);
    }

    [Fact]
    public void ReplaceDegraded_DoesNotDisturbOtherSourcesOrTheAppendOnlyQueue()
    {
        var healthState = new ServerHealthState();
        healthState.RecordDegraded("GrantStore", "a skipped row");
        healthState.RecordFatal("VaultConfig", "a whole-file failure");
        healthState.ReplaceDegraded("ScriptTools", ["script a is broken"]);
        healthState.ReplaceDegraded("OtherSource", ["other problem"]);

        healthState.ReplaceDegraded("ScriptTools", Array.Empty<string>());

        Assert.True(healthState.IsFatallyDegraded);
        Assert.Equal(3, healthState.Problems.Count);
        Assert.DoesNotContain(healthState.Problems, p => p.Source == "ScriptTools");
        Assert.Contains(healthState.Problems, p => p.Source == "OtherSource");
        Assert.Contains(healthState.Problems, p => p.Source == "GrantStore");
        Assert.Contains(healthState.Problems, p => p.Source == "VaultConfig");
    }

    [Fact]
    public void ReplaceDegraded_NeverAffectsIsFatallyDegraded()
    {
        var healthState = new ServerHealthState();

        healthState.ReplaceDegraded("ScriptTools", ["one", "two", "three"]);

        Assert.False(healthState.IsFatallyDegraded);
    }

    [Fact]
    public void AttachLogger_ProblemsRecordedAfterAttachAreLogged()
    {
        // The runtime-logging path: Program.cs flushes startup problems itself, then attaches
        // the logger; anything recorded after that (watcher re-scans) must reach ILogger at
        // record time.
        var healthState = new ServerHealthState();
        healthState.RecordDegraded("GrantStore", "recorded before attach - flushed by Program.cs, not by us");
        var logger = new CapturingLogger();

        healthState.AttachLogger(logger);
        Assert.Empty(logger.Entries);

        healthState.ReplaceDegraded("ScriptTools", ["script a is broken"]);
        healthState.RecordDegraded("SomeSource", "a runtime skip");
        healthState.RecordFatal("SomeSource", "a runtime fatal");

        Assert.Equal(3, logger.Entries.Count);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("script a is broken"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("a runtime skip"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("a runtime fatal"));
    }

    [Fact]
    public void AttachLogger_ClearingAProblemSetLogsTheClear()
    {
        var healthState = new ServerHealthState();
        healthState.ReplaceDegraded("ScriptTools", ["script a is broken"]);
        var logger = new CapturingLogger();
        healthState.AttachLogger(logger);

        healthState.ReplaceDegraded("ScriptTools", Array.Empty<string>());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("ScriptTools", entry.Message);
    }

    [Fact]
    public void InstructionsSummary_NewInstance_IsEmpty()
    {
        var healthState = new ServerHealthState();

        Assert.Equal(string.Empty, healthState.InstructionsSummary);
    }

    [Fact]
    public void InstructionsSummary_RecordDegraded_RecomputesToANonEmptySummary()
    {
        var healthState = new ServerHealthState();

        healthState.RecordDegraded("GrantStore", "a skipped row");

        Assert.NotEmpty(healthState.InstructionsSummary);
        Assert.Contains("0 fatal and 1 degraded", healthState.InstructionsSummary);
        Assert.DoesNotContain("503", healthState.InstructionsSummary);
    }

    [Fact]
    public void InstructionsSummary_RecordFatal_MentionsThe503Behavior()
    {
        var healthState = new ServerHealthState();

        healthState.RecordFatal("VaultConfig", "a whole-file failure");

        Assert.Contains("503", healthState.InstructionsSummary);
    }

    [Fact]
    public void InstructionsSummary_ReplaceDegraded_RecomputesOnEveryCall()
    {
        var healthState = new ServerHealthState();

        healthState.ReplaceDegraded("ScriptTools", ["script a is broken"]);
        Assert.Contains("0 fatal and 1 degraded", healthState.InstructionsSummary);

        healthState.ReplaceDegraded("ScriptTools", ["script a is broken", "script b is broken"]);
        Assert.Contains("2 degraded", healthState.InstructionsSummary);

        healthState.ReplaceDegraded("ScriptTools", Array.Empty<string>());
        Assert.Equal(string.Empty, healthState.InstructionsSummary);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// Recording happens at startup (the main thread) and at runtime from the tools/
    /// file-watcher thread - this proves the backing collection tolerates concurrent
    /// writers without losing entries or throwing, which is what that pair of writers
    /// depends on.
    /// </summary>
    [Fact]
    public async Task RecordFatal_IsThreadSafeUnderConcurrentWriters()
    {
        var healthState = new ServerHealthState();
        const int writersCount = 20;

        await Task.WhenAll(Enumerable.Range(0, writersCount).Select(i =>
            Task.Run(() => healthState.RecordFatal($"Source{i}", "concurrent"))));

        Assert.True(healthState.IsFatallyDegraded);
        Assert.Equal(writersCount, healthState.Problems.Count);
    }
}
