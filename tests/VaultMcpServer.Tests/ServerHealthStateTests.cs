using VaultMcpServer.Health;

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

    /// <summary>
    /// Recording happens at startup (the main thread) and, in a later batch, from a
    /// file-watcher thread - this proves the backing collection tolerates concurrent
    /// writers without losing entries or throwing, which is the property that later
    /// concurrent use depends on.
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
