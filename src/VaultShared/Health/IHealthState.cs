namespace VaultShared.Health;

/// <summary>
/// Exposes a front-end's health picture to something reporting on it.
/// <see cref="IConfigProblemSink"/> is the same picture as seen by something writing into it. One
/// type implements both, and they are separate interfaces so the reporting side cannot record.
/// </summary>
/// <remarks>
/// These two members are all the <c>health_check</c> tool reads, which is what lets that tool live in
/// this assembly rather than in the server.
///
/// Each front-end implements this for its own process. The CLI reports the configuration load that
/// this invocation ran, not a server's. A front-end that refuses to start on a fatal problem
/// therefore never reports one, because any process that can answer is one that started.
/// </remarks>
public interface IHealthState
{
    /// <summary>True once any <see cref="ProblemSeverity.Fatal"/> problem has been recorded.</summary>
    bool IsFatallyDegraded { get; }

    /// <summary>A point-in-time snapshot of the current problem picture.</summary>
    IReadOnlyCollection<HealthProblem> Problems { get; }
}
