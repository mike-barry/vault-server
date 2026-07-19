using System.Collections.Concurrent;

namespace VaultMcpServer.Health;

/// <summary>How serious a recorded <see cref="HealthProblem"/> is.</summary>
public enum ProblemSeverity
{
    /// <summary>A per-entry skip - one grants.json row, one vaults.json entry, one tool
    /// script - that doesn't affect the rest of that file. Nothing consumes this severity
    /// yet in this batch (no loader downgrades a validation failure to per-entry-skip
    /// here); the recording API exists now so a later batch can start using it without
    /// another surface change.</summary>
    Degraded,

    /// <summary>Whole-file failure of vaults.json, grants.json, or op-classes.json - the
    /// file is missing, unparseable, or structurally invalid. Any fatal problem puts the
    /// server in the "fatally degraded" state <see cref="ServerHealthState.IsFatallyDegraded"/>
    /// reports.</summary>
    Fatal,
}

/// <summary>One recorded config problem: how bad it is, what subsystem found it, and a
/// human-readable detail message safe to put in a log line.</summary>
public sealed record HealthProblem(ProblemSeverity Severity, string Source, string Detail, DateTimeOffset RecordedAtUtc);

/// <summary>
/// Process-wide record of config problems found at startup (and, in a later batch, by a
/// file-watcher thread) - the server's replacement for hard-stopping on bad config. A
/// config loader that would previously have thrown now calls <see cref="RecordFatal"/> or
/// <see cref="RecordDegraded"/> instead and returns an inert/empty instance, so the process
/// always starts; <see cref="BearerGrantMiddleware"/> consults <see cref="IsFatallyDegraded"/>
/// to short-circuit every request with 503 while any fatal problem is outstanding.
///
/// Recording can happen at startup (the main thread, before the DI container exists) and
/// later from a file-watcher thread (a later batch), so the backing collection is
/// thread-safe and lock-free for both appends and enumeration.
///
/// Deliberately just a recording/enumeration surface for now - no cached summary string.
/// A later batch renders <see cref="Problems"/> into the health_check tool response and the
/// initialize instructions text; <see cref="Problems"/> returning a stable, iterable
/// snapshot is what that summary will be built from, so adding it later doesn't need
/// another change here.
/// </summary>
public sealed class ServerHealthState(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentQueue<HealthProblem> _problems = new();
    private int _fatalCount;

    /// <summary>True once at least one <see cref="ProblemSeverity.Fatal"/> problem has been
    /// recorded. Read with <see cref="Volatile.Read(ref int)"/> so it observes a concurrent
    /// <see cref="RecordFatal"/> from another thread without needing a lock.</summary>
    public bool IsFatallyDegraded => Volatile.Read(ref _fatalCount) > 0;

    /// <summary>A point-in-time snapshot of every problem recorded so far, oldest first.</summary>
    public IReadOnlyCollection<HealthProblem> Problems => _problems.ToArray();

    /// <summary>Records a whole-file failure. <paramref name="source"/> identifies the
    /// subsystem (e.g. "VaultConfig", "OpClassRegistry", "GrantStore"); <paramref name="detail"/>
    /// is the human-readable message - typically the same message the old thrown exception
    /// carried.</summary>
    public void RecordFatal(string source, string detail)
    {
        _problems.Enqueue(new HealthProblem(ProblemSeverity.Fatal, source, detail, _timeProvider.GetUtcNow()));
        Interlocked.Increment(ref _fatalCount);
    }

    /// <summary>Records a per-entry skip that doesn't invalidate the rest of the file it
    /// came from. Not yet called by any loader in this batch - see the type doc.</summary>
    public void RecordDegraded(string source, string detail) =>
        _problems.Enqueue(new HealthProblem(ProblemSeverity.Degraded, source, detail, _timeProvider.GetUtcNow()));
}
