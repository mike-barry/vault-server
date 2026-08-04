using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VaultMcpServer.Health;
using VaultShared.Health;

namespace VaultMcpServer.Health;

/// <summary>Represents one problem to record via
/// <see cref="ServerHealthState.ReplaceDegraded(string, IReadOnlyList{DegradedProblem})"/>.</summary>
/// <param name="Detail">The human-readable description of what is wrong.</param>
/// <param name="SubjectPath">The path of the file the problem concerns, or null when it concerns
/// no particular file.</param>
public sealed record DegradedProblem(string Detail, string? SubjectPath = null);

/// <summary>
/// Records the configuration problems found at startup and by runtime re-scans, so the server
/// can start with bad configuration instead of hard-stopping. A config loader calls
/// <see cref="RecordFatal"/> or <see cref="RecordDegraded"/> instead of throwing and returns an
/// empty instance; <see cref="Auth.BearerGrantMiddleware"/> reads
/// <see cref="IsFatallyDegraded"/> and rejects every request with 503 while a fatal problem is
/// outstanding.
/// </summary>
/// <remarks>
/// <para>
/// There are two backing collections, one per problem lifetime. Startup-load problems
/// (<see cref="RecordFatal"/>, <see cref="RecordDegraded"/>) go into an append-only queue:
/// vaults.json, op-classes.json, and grants.json load exactly once per process, so their
/// problems never need to be retracted. Sources that re-scan at runtime - today only the
/// script-tool loader - use the keyed, replaceable per-source sets of
/// <c>ReplaceDegraded</c>, so a fixed script stops appearing as a problem and a
/// newly-broken one appears. A replaceable set is always Degraded, so it can never affect
/// <see cref="IsFatallyDegraded"/>.
/// </para>
/// <para>
/// <see cref="Problems"/> is the combined view across both, and is what the <c>health_check</c>
/// tool renders and what the MCP <c>initialize</c> instructions summarize. It and
/// <see cref="IsFatallyDegraded"/> are the whole of <see cref="IHealthState"/>, which lets a
/// tool report on process health without knowing which front-end it is running in.
/// </para>
/// <para>
/// Both collections are thread-safe, because problems are recorded both from the startup
/// thread, before the DI container exists, and from the tools directory watcher's thread.
/// </para>
/// <para>
/// Problems recorded before the host is built are flushed to the logger once by
/// <c>Program.cs</c>. Problems recorded after <see cref="AttachLogger"/> has run are logged as
/// they are recorded, so no second flush pass is needed.
/// </para>
/// </remarks>
/// <param name="timeProvider">Supplies the timestamp on each recorded problem. Defaults to
/// <see cref="TimeProvider.System"/>.</param>
public sealed class ServerHealthState(TimeProvider? timeProvider = null) : IConfigProblemSink, IHealthState
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentQueue<HealthProblem> _problems = new();
    private readonly ConcurrentDictionary<string, HealthProblem[]> _currentBySource = new(StringComparer.Ordinal);
    private int _fatalCount;
    private volatile ILogger? _logger;
    private volatile string _instructionsSummary = string.Empty;

    /// <summary>
    /// Gets a value indicating whether at least one <see cref="ProblemSeverity.Fatal"/> problem
    /// has been recorded. The count is read with <c>Volatile.Read</c> so a
    /// concurrent <see cref="RecordFatal"/> on another thread is observed without a lock.
    /// Replaceable per-source sets never contribute, because they are always Degraded.
    /// </summary>
    public bool IsFatallyDegraded => Volatile.Read(ref _fatalCount) > 0;

    /// <summary>
    /// Gets a point-in-time snapshot of the current problems: every append-only startup problem
    /// oldest first, then each replaceable source's current problem set, with sources in ordinal
    /// order. A source whose last <c>ReplaceDegraded</c> call passed an empty set
    /// contributes nothing.
    /// </summary>
    public IReadOnlyCollection<HealthProblem> Problems =>
        _problems
            .ToArray()
            .Concat(_currentBySource.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value))
            .ToArray();

    /// <summary>
    /// Sets the logger used to record problems as they arrive. <c>Program.cs</c> calls this
    /// after flushing the problems recorded during startup, so everything recorded before the
    /// call is logged once by the flush and everything recorded after is logged here.
    /// </summary>
    /// <param name="logger">The logger to write problem records to.</param>
    public void AttachLogger(ILogger logger) => _logger = logger;

    /// <summary>
    /// Gets a cached one-line summary of the current problems, or an empty string when there
    /// are none. It is recomputed on every mutation and never assembled on read, because
    /// <c>Program.cs</c> copies it into the MCP <c>initialize</c> response's
    /// <c>instructions</c> field from a callback that runs on every HTTP request under the
    /// stateless transport.
    /// </summary>
    public string InstructionsSummary => _instructionsSummary;

    /// <summary>Records a failure that invalidates a whole file.</summary>
    /// <param name="source">The subsystem the problem belongs to, for example "VaultConfig",
    /// "OpClassRegistry", or "GrantStore".</param>
    /// <param name="detail">The human-readable description of what failed.</param>
    public void RecordFatal(string source, string detail)
    {
        _problems.Enqueue(new HealthProblem(ProblemSeverity.Fatal, source, detail, _timeProvider.GetUtcNow()));
        Interlocked.Increment(ref _fatalCount);
        _logger?.LogError("Config problem [Fatal] {Source}: {Detail}", source, detail);
        RecomputeInstructionsSummary();
    }

    /// <summary>
    /// Records a skipped entry that does not invalidate the rest of the file it came from. The
    /// record is append-only, so this suits startup-only loads such as vaults.json entries and
    /// grants.json rows. A source that re-scans at runtime should call
    /// <c>ReplaceDegraded</c> instead, so a later scan can retract a fixed problem.
    /// </summary>
    /// <param name="source">The subsystem the problem belongs to.</param>
    /// <param name="detail">The human-readable description of what was skipped.</param>
    public void RecordDegraded(string source, string detail)
    {
        _problems.Enqueue(new HealthProblem(ProblemSeverity.Degraded, source, detail, _timeProvider.GetUtcNow()));
        _logger?.LogWarning("Config problem [Degraded] {Source}: {Detail}", source, detail);
        RecomputeInstructionsSummary();
    }

    /// <summary>
    /// Replaces a source's current Degraded problem set wholesale. This is the recording
    /// surface for anything recomputed by a runtime re-scan, today the per-script problems from
    /// the tools directory loader. Recorded problems are always Degraded, so
    /// <see cref="IsFatallyDegraded"/> is never affected.
    /// </summary>
    /// <param name="source">The subsystem whose problem set is being replaced.</param>
    /// <param name="details">The problems found by this scan. An empty list clears the source's
    /// problems from <see cref="Problems"/>, retracting whatever the previous scan reported.</param>
    public void ReplaceDegraded(string source, IReadOnlyList<string> details) =>
        ReplaceDegraded(source, details.Select(d => new DegradedProblem(d)).ToList());

    /// <inheritdoc cref="ReplaceDegraded(string, IReadOnlyList{string})"/>
    /// <param name="source">The subsystem whose problem set is being replaced.</param>
    /// <param name="problems">The problems found by this scan. Each may carry the path of the
    /// file it concerns, which is preserved onto <see cref="HealthProblem.SubjectPath"/>. An
    /// empty list clears the source's problems.</param>
    public void ReplaceDegraded(string source, IReadOnlyList<DegradedProblem> problems)
    {
        var now = _timeProvider.GetUtcNow();
        var replacement = problems.Select(p => new HealthProblem(ProblemSeverity.Degraded, source, p.Detail, now, p.SubjectPath)).ToArray();
        var hadProblems = _currentBySource.TryGetValue(source, out var previous) && previous.Length > 0;
        _currentBySource[source] = replacement;

        var logger = _logger;
        if (logger is not null)
        {
            foreach (var problem in replacement)
                logger.LogWarning("Config problem [Degraded] {Source}: {Detail}", problem.Source, problem.Detail);
            if (replacement.Length == 0 && hadProblems)
                logger.LogInformation("All {Source} problems cleared by a re-scan.", source);
        }

        RecomputeInstructionsSummary();
    }

    /// <summary>
    /// Rebuilds <see cref="InstructionsSummary"/> from the current <see cref="Problems"/> view.
    /// Called at the end of every mutation and never from a request path.
    /// </summary>
    private void RecomputeInstructionsSummary()
    {
        var problems = Problems;
        if (problems.Count == 0)
        {
            _instructionsSummary = string.Empty;
            return;
        }

        var fatalCount = problems.Count(p => p.Severity == ProblemSeverity.Fatal);
        var degradedCount = problems.Count - fatalCount;
        var fatalNote = fatalCount > 0
            ? " While any fatal problem exists, every request is rejected with 503 until it is fixed."
            : "";
        _instructionsSummary =
            $"This vault-mcp-server instance currently has {fatalCount} fatal and {degradedCount} degraded " +
            $"configuration problem(s) recorded.{fatalNote} Call the health_check tool for full details.";
    }
}
