using VaultShared.Health;

namespace VaultShared.Tests;

/// <summary>
/// An <see cref="IConfigProblemSink"/> that keeps what it was handed, so a loader test can assert
/// what a load recorded without standing up a host's health state. The server's own
/// <c>ServerHealthState</c> is one implementation of the same interface and is tested on its own
/// terms - thread safety, replaceable per-source sets, the cached instructions summary - none of
/// which a loader can reach through this seam.
/// </summary>
public sealed class RecordingProblemSink : IConfigProblemSink
{
    private readonly List<HealthProblem> _problems = [];

    public IReadOnlyList<HealthProblem> Problems => _problems;

    /// <summary>True once any <see cref="ProblemSeverity.Fatal"/> problem has been recorded - the
    /// question a fail-closed caller asks after a load, and the one the server answers by refusing
    /// every request.</summary>
    public bool IsFatallyDegraded => _problems.Any(p => p.Severity == ProblemSeverity.Fatal);

    public void RecordFatal(string source, string detail) => Record(ProblemSeverity.Fatal, source, detail);

    public void RecordDegraded(string source, string detail) => Record(ProblemSeverity.Degraded, source, detail);

    private void Record(ProblemSeverity severity, string source, string detail) =>
        _problems.Add(new HealthProblem(severity, source, detail, DateTimeOffset.UtcNow));
}
