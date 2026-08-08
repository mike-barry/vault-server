namespace VaultShared.Health;

/// <summary>How serious a recorded <see cref="HealthProblem"/> is.</summary>
public enum ProblemSeverity
{
    /// <summary>A partial loss that leaves the host serving what the failure does not touch: one
    /// skipped grants.json row, one skipped vaults.json entry, one tool script that would not load, or
    /// one configured dependency that is not where configuration said it was, which refuses the
    /// operations needing it and no others. A startup-only load records it append-only through
    /// <see cref="IConfigProblemSink.RecordDegraded"/>; a host whose sources re-scan at runtime holds
    /// it in a replaceable per-source set that a later scan can clear.</summary>
    Degraded,

    /// <summary>A whole-file failure of vaults.json, grants.json, or op-classes.json: the file is
    /// missing, unparseable, or structurally invalid. What a host does about it is the host's
    /// decision, so the server returns 503 until it is fixed and a single-invocation front-end fails
    /// outright.</summary>
    Fatal,
}

/// <summary>Represents one recorded configuration problem.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Source">The subsystem that found it.</param>
/// <param name="Detail">A human-readable message, safe to put in a log line.</param>
/// <param name="RecordedAtUtc">When it was recorded.</param>
/// <param name="SubjectPath">The file the problem is about, such as a tool script that failed to
/// load, or null. It is carried as its own member so a consumer that needs the path reads it directly
/// instead of parsing it back out of <paramref name="Detail"/>.</param>
public sealed record HealthProblem(ProblemSeverity Severity, string Source, string Detail, DateTimeOffset RecordedAtUtc, string? SubjectPath = null);
