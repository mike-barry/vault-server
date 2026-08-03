using VaultShared.Health;

namespace VaultCli;

/// <summary>
/// This invocation's health picture: what its own load cascade recorded, and nothing else.
///
/// It answers both halves of the seam - <see cref="IConfigProblemSink"/> for the loaders, which
/// record rather than throw, and <see cref="IHealthState"/> for <c>health_check</c>, which reports.
/// The server's equivalent carries a logger, a cached <c>initialize</c> summary and per-source sets
/// a runtime re-scan can retract; a process that runs one command needs none of them, and there is
/// nothing here that re-scans.
///
/// <b>Fatal is checked by the caller, not enforced here.</b> The loaders record and carry on by
/// design, so what a fatal problem means is the front-end's: <see cref="CliHost"/> fails the
/// invocation at the first one, which is why an invocation that reaches a tool at all reports
/// <see cref="IsFatallyDegraded"/> false.
///
/// Single-threaded: one invocation, one cascade, on the thread that started.
/// </summary>
public sealed class CliHealthState(TimeProvider? timeProvider = null) : IConfigProblemSink, IHealthState
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly List<HealthProblem> _problems = [];

    public bool IsFatallyDegraded { get; private set; }

    public IReadOnlyCollection<HealthProblem> Problems => _problems;

    public void RecordFatal(string source, string detail)
    {
        IsFatallyDegraded = true;
        Record(ProblemSeverity.Fatal, source, detail);
    }

    public void RecordDegraded(string source, string detail) => Record(ProblemSeverity.Degraded, source, detail);

    /// <summary>Every fatal problem recorded so far, as one message. What the operator has to fix is
    /// the whole set a single load produced, not the first line of it.</summary>
    public string FatalSummary =>
        string.Join(" ", _problems.Where(p => p.Severity == ProblemSeverity.Fatal).Select(p => $"[{p.Source}] {p.Detail}"));

    private void Record(ProblemSeverity severity, string source, string detail) =>
        _problems.Add(new HealthProblem(severity, source, detail, _timeProvider.GetUtcNow()));
}
