using VaultShared.Health;

namespace VaultCli;

/// <summary>
/// Collects the problems this invocation's load cascade recorded, and reports them to
/// <c>health_check</c>. It implements both halves of the seam: <see cref="IConfigProblemSink"/> for
/// the loaders, which record rather than throw, and <see cref="IHealthState"/> for the tool, which
/// reports. Unlike the server's equivalent it holds no logger, no cached summary and no per-source
/// sets, because nothing re-scans configuration within a single command.
/// </summary>
/// <remarks>
/// Recording a fatal problem does not stop the cascade. The loaders record and carry on, and
/// <see cref="CliHost"/> is what fails the invocation at the first fatal problem, so an invocation
/// that reaches a tool at all reports <see cref="IsFatallyDegraded"/> as false.
///
/// This type is not thread-safe. One invocation runs one cascade on the thread that started it.
/// </remarks>
/// <param name="timeProvider">The clock used to stamp recorded problems, or null for
/// <see cref="TimeProvider.System"/>.</param>
public sealed class CliHealthState(TimeProvider? timeProvider = null) : IConfigProblemSink, IHealthState
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly List<HealthProblem> _problems = [];

    /// <summary>Gets a value indicating whether any fatal problem has been recorded.</summary>
    public bool IsFatallyDegraded { get; private set; }

    /// <summary>Gets the problems recorded so far, in the order they were recorded.</summary>
    public IReadOnlyCollection<HealthProblem> Problems => _problems;

    /// <summary>Records a fatal problem and sets <see cref="IsFatallyDegraded"/>.</summary>
    /// <param name="source">The configuration source the problem came from.</param>
    /// <param name="detail">What went wrong.</param>
    public void RecordFatal(string source, string detail)
    {
        IsFatallyDegraded = true;
        Record(ProblemSeverity.Fatal, source, detail);
    }

    /// <summary>Records a non-fatal problem.</summary>
    /// <param name="source">The configuration source the problem came from.</param>
    /// <param name="detail">What went wrong.</param>
    public void RecordDegraded(string source, string detail) => Record(ProblemSeverity.Degraded, source, detail);

    /// <summary>Gets every fatal problem recorded so far as one message. An operator has to fix the
    /// whole set a single load produced, not just the first of them.</summary>
    public string FatalSummary =>
        string.Join(" ", _problems.Where(p => p.Severity == ProblemSeverity.Fatal).Select(p => $"[{p.Source}] {p.Detail}"));

    private void Record(ProblemSeverity severity, string source, string detail) =>
        _problems.Add(new HealthProblem(severity, source, detail, _timeProvider.GetUtcNow()));
}
