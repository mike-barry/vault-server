namespace VaultShared.Health;

/// <summary>
/// Exposes a front-end's health picture to something reporting on it.
/// <see cref="IConfigProblemSink"/> is the same picture as seen by something writing into it. One
/// type implements both, and they are separate interfaces because the reporting side must not be able
/// to record, and the recording side is the only half a configuration loader sees.
/// </summary>
/// <remarks>
/// These two members are all the <c>health_check</c> tool reads, which is what lets that tool live in
/// this assembly rather than in the server. The MCP server's own health state carries things no other
/// front-end has - a logger, a cached <c>initialize</c> instructions summary, and per-source problem
/// sets that a runtime re-scan can retract - and moving that type here would drag a logging
/// dependency and the MCP transport's vocabulary with it.
///
/// Each front-end implements this for its own process. The CLI reports the configuration load that
/// this invocation ran, not a server's. A front-end that refuses to start on a fatal problem
/// therefore never reports one, because any process that can answer is one that started.
/// </remarks>
public interface IHealthState
{
    /// <summary>True once any <see cref="ProblemSeverity.Fatal"/> problem has been recorded. What a
    /// host does about it is the host's decision: the server returns 503 until it is fixed, and a
    /// single-invocation front-end fails before any tool runs.</summary>
    bool IsFatallyDegraded { get; }

    /// <summary>A point-in-time snapshot of the current problem picture.</summary>
    IReadOnlyCollection<HealthProblem> Problems { get; }
}
