namespace VaultShared.Health;

/// <summary>
/// What a front-end's health picture looks like to something that reports on it, as opposed to
/// <see cref="IConfigProblemSink"/>, which is what it looks like to something writing into it. One
/// implementation answers both interfaces; they are separate because the reporting side must not be
/// able to record, and because the recording side is the only half a configuration loader sees.
///
/// <b>It exists so <c>health_check</c> is a tool rather than a server tool.</b> The MCP server's own
/// health state carries things no other front-end has - a logger, a cached <c>initialize</c>
/// instructions summary, and per-source problem sets a runtime re-scan can retract - and moving that
/// type into this assembly would drag a logging dependency and the MCP transport's vocabulary along
/// with it. What the tool actually reads is these two members, so these two are the seam.
///
/// <b>Each front-end answers for its own process.</b> The CLI's implementation reports the load
/// cascade that this invocation ran, not a server's; a front-end that fails outright on a fatal
/// problem therefore never reports one, because the process asking the question is the one that
/// started.
/// </summary>
public interface IHealthState
{
    /// <summary>True once any <see cref="ProblemSeverity.Fatal"/> problem has been recorded. What a
    /// host does about one is its own: the server serves 503 until it is fixed, and a
    /// single-invocation front-end fails before any tool runs.</summary>
    bool IsFatallyDegraded { get; }

    /// <summary>A point-in-time snapshot of the current problem picture.</summary>
    IReadOnlyCollection<HealthProblem> Problems { get; }
}
