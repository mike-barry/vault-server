using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VaultMcpServer.Health;
using VaultShared.Health;

namespace VaultMcpServer.Health;

/// <summary>A single problem to record via <see cref="ServerHealthState.ReplaceDegraded(string, IReadOnlyList{DegradedProblem})"/>:
/// its human-readable detail plus, optionally, the path of the file it concerns.</summary>
public sealed record DegradedProblem(string Detail, string? SubjectPath = null);

/// <summary>
/// Process-wide record of config problems found at startup and by runtime re-scans (the
/// tools/ file watcher) - the server's alternative to hard-stopping on bad config. A
/// config loader calls <see cref="RecordFatal"/> or
/// <see cref="RecordDegraded"/> rather than throwing, and returns an inert/empty instance, so the process
/// always starts; the MCP front-end's bearer middleware consults <see cref="IsFatallyDegraded"/>
/// to short-circuit every request with 503 while any fatal problem is outstanding.
///
/// Two backing collections, matching the two lifetimes a problem can have:
/// <list type="bullet">
/// <item>An append-only queue for startup-load problems (<see cref="RecordFatal"/> /
/// <see cref="RecordDegraded"/>). vaults.json, op-classes.json, and grants.json load
/// exactly once per process, so their problems never need to be retracted.</item>
/// <item>A keyed, replaceable per-source set (<see cref="ReplaceDegraded"/>) for sources
/// that re-scan at runtime - today the script-tool loader, whose per-script problems are
/// recomputed on every tools/ re-scan: a fixed script must stop appearing as a problem and
/// a newly-broken one must appear, which an append-only queue cannot express. Replaceable
/// sets are Degraded-only by construction, so they can never influence
/// <see cref="IsFatallyDegraded"/>.</item>
/// </list>
/// <see cref="Problems"/> returns the coherent current picture across both - the view a
/// later batch renders into the health_check tool response and initialize instructions.
///
/// Recording can happen at startup (the main thread, before the DI container exists) and
/// from the tools/ file-watcher thread, so both collections are thread-safe.
///
/// Problems recorded before the host is built are flushed to the logger once by
/// <c>Program.cs</c>; problems recorded after <see cref="AttachLogger"/> has been called
/// (runtime re-scans) are logged as they are recorded, so nothing depends on a second
/// flush pass.
/// </summary>
public sealed class ServerHealthState(TimeProvider? timeProvider = null) : IConfigProblemSink
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentQueue<HealthProblem> _problems = new();
    private readonly ConcurrentDictionary<string, HealthProblem[]> _currentBySource = new(StringComparer.Ordinal);
    private int _fatalCount;
    private volatile ILogger? _logger;
    private volatile string _instructionsSummary = string.Empty;

    /// <summary>True once at least one <see cref="ProblemSeverity.Fatal"/> problem has been
    /// recorded. Read with <see cref="Volatile.Read(ref int)"/> so it observes a concurrent
    /// <see cref="RecordFatal"/> from another thread without needing a lock. Replaceable
    /// per-source sets (<see cref="ReplaceDegraded"/>) never contribute - they are
    /// Degraded-only.</summary>
    public bool IsFatallyDegraded => Volatile.Read(ref _fatalCount) > 0;

    /// <summary>
    /// A point-in-time snapshot of the current problem picture: every append-only startup
    /// problem (oldest first), followed by each replaceable source's <em>current</em>
    /// problem set (sources in ordinal order). A source whose last
    /// <see cref="ReplaceDegraded"/> call passed an empty set contributes nothing - its
    /// earlier problems are gone from this view, which is the point of the replaceable
    /// bucket.
    /// </summary>
    public IReadOnlyCollection<HealthProblem> Problems =>
        _problems
            .ToArray()
            .Concat(_currentBySource.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value))
            .ToArray();

    /// <summary>
    /// Hooks up runtime logging once the host (and therefore an <see cref="ILogger"/>) exists.
    /// <c>Program.cs</c> calls this immediately <em>after</em> flushing the startup-recorded
    /// problems, so everything recorded before this call is logged exactly once by the flush
    /// and everything recorded after (watcher re-scans) is logged here at record time.
    /// </summary>
    public void AttachLogger(ILogger logger) => _logger = logger;

    /// <summary>
    /// A pre-rendered, cached summary of the current problem picture - recomputed once per
    /// mutation (<see cref="RecordFatal"/>, <see cref="RecordDegraded"/>,
    /// <see cref="ReplaceDegraded"/>), never assembled on read. Copied verbatim into the MCP
    /// <c>initialize</c> response's <c>instructions</c> field by <c>Program.cs</c>'s
    /// <c>ConfigureSessionOptions</c> callback, which runs on every HTTP request under the
    /// stateless transport and therefore must stay cheap - a plain field read, not a
    /// recompute. Empty when there are no problems, so callers can treat "empty/absent" and
    /// "no problems" as the same thing.
    /// </summary>
    public string InstructionsSummary => _instructionsSummary;

    /// <summary>Records a whole-file failure. <paramref name="source"/> identifies the
    /// subsystem (e.g. "VaultConfig", "OpClassRegistry", "GrantStore"); <paramref name="detail"/>
    /// is the human-readable message describing what failed.</summary>
    public void RecordFatal(string source, string detail)
    {
        _problems.Enqueue(new HealthProblem(ProblemSeverity.Fatal, source, detail, _timeProvider.GetUtcNow()));
        Interlocked.Increment(ref _fatalCount);
        _logger?.LogError("Config problem [Fatal] {Source}: {Detail}", source, detail);
        RecomputeInstructionsSummary();
    }

    /// <summary>Records a per-entry skip that doesn't invalidate the rest of the file it came
    /// from, append-only - for startup-only loads (vaults.json entries, grants.json rows)
    /// that never re-run within a process lifetime. A source that re-scans at runtime should
    /// use <see cref="ReplaceDegraded"/> instead, so a later scan can retract a fixed
    /// problem.</summary>
    public void RecordDegraded(string source, string detail)
    {
        _problems.Enqueue(new HealthProblem(ProblemSeverity.Degraded, source, detail, _timeProvider.GetUtcNow()));
        _logger?.LogWarning("Config problem [Degraded] {Source}: {Detail}", source, detail);
        RecomputeInstructionsSummary();
    }

    /// <summary>
    /// Replaces <paramref name="source"/>'s current Degraded problem set wholesale - the
    /// recording surface for anything recomputed by a runtime re-scan (today: per-script
    /// problems from the tools/ loader). Passing an empty <paramref name="details"/> clears
    /// the source's problems from <see cref="Problems"/> entirely - a re-scan that found
    /// nothing wrong retracts whatever the previous scan reported. Always Degraded severity:
    /// a replaceable problem can never be Fatal, so <see cref="IsFatallyDegraded"/> is
    /// unaffected by construction.
    /// </summary>
    public void ReplaceDegraded(string source, IReadOnlyList<string> details) =>
        ReplaceDegraded(source, details.Select(d => new DegradedProblem(d)).ToList());

    /// <inheritdoc cref="ReplaceDegraded(string, IReadOnlyList{string})"/>
    /// <remarks>Structured overload: each <see cref="DegradedProblem"/> may carry the path of
    /// the file it concerns, preserved onto the recorded <see cref="HealthProblem.SubjectPath"/>.</remarks>
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
    /// Called once at the end of every mutation, never from a request path - see
    /// <see cref="InstructionsSummary"/>'s remarks for why that split matters.
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
