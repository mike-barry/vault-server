namespace VaultShared.Health;

/// <summary>How serious a recorded <see cref="HealthProblem"/> is.</summary>
public enum ProblemSeverity
{
    /// <summary>A per-entry skip - one grants.json row, one vaults.json entry, one tool
    /// script - that doesn't affect the rest of that file. Recorded either append-only
    /// (<see cref="IConfigProblemSink.RecordDegraded"/>, for startup-only loads that never
    /// re-run) or, by a host whose sources re-scan at runtime, as a replaceable per-source
    /// set that a later scan can clear.</summary>
    Degraded,

    /// <summary>Whole-file failure of vaults.json, grants.json, or op-classes.json - the
    /// file is missing, unparseable, or structurally invalid. What a host does about one is
    /// its own decision: the server serves 503 until it is fixed, and a single-invocation
    /// front-end fails outright.</summary>
    Fatal,
}

/// <summary>One recorded config problem: how bad it is, what subsystem found it, and a
/// human-readable detail message safe to put in a log line. <paramref name="SubjectPath"/> is
/// the optional file this problem is about (e.g. the tool script that failed to load) - carried
/// as structured data so consumers that need the path (the broken-tool listing in
/// the MCP front-end's script-tool dispatcher) read it directly instead of parsing it back out of
/// <paramref name="Detail"/>.</summary>
public sealed record HealthProblem(ProblemSeverity Severity, string Source, string Detail, DateTimeOffset RecordedAtUtc, string? SubjectPath = null);
