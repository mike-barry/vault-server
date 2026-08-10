using VaultShared.Health;

namespace VaultShared.Tests;

/// <summary>
/// Implements <see cref="IConfigProblemSink"/> by keeping every problem it is handed, so a loader
/// test can assert what a load recorded without standing up a host's health state.
/// </summary>
public sealed class RecordingProblemSink : IConfigProblemSink
{
    private readonly List<HealthProblem> _problems = [];

    /// <summary>Gets the problems recorded so far, in the order they were recorded.</summary>
    public IReadOnlyList<HealthProblem> Problems => _problems;

    /// <summary>
    /// Gets a value indicating whether any <see cref="ProblemSeverity.Fatal"/> problem has been
    /// recorded.
    /// </summary>
    public bool IsFatallyDegraded => _problems.Any(p => p.Severity == ProblemSeverity.Fatal);

    /// <summary>Records a fatal problem.</summary>
    /// <param name="source">The configuration source the problem came from.</param>
    /// <param name="detail">The description of the problem.</param>
    public void RecordFatal(string source, string detail) => Record(ProblemSeverity.Fatal, source, detail);

    /// <summary>Records a degraded problem.</summary>
    /// <param name="source">The configuration source the problem came from.</param>
    /// <param name="detail">The description of the problem.</param>
    public void RecordDegraded(string source, string detail) => Record(ProblemSeverity.Degraded, source, detail);

    private void Record(ProblemSeverity severity, string source, string detail) =>
        _problems.Add(new HealthProblem(severity, source, detail, DateTimeOffset.UtcNow));
}
