namespace VaultShared.Health;

/// <summary>
/// Receives what a configuration loader could not use. It is write-only and deliberately narrow:
/// the loaders for <c>vaults.json</c>, <c>op-classes.json</c> and <c>grants.json</c> record
/// whole-file failures and per-entry skips here instead of throwing, and return an inert or partial
/// result, so a loader never decides what its host should do about a bad file.
/// </summary>
/// <remarks>
/// That decision differs by host, which is why this is an interface. A long-running server records
/// problems, starts anyway, and refuses requests while any fatal one stands. A single-invocation
/// front-end has nothing to stay up for and fails instead, because configuration that will not load
/// must never admit an invocation unrestricted. Both use the same loaders.
///
/// Only the append-only recording surface is here. A source that re-scans at runtime has to retract a
/// problem it previously reported, and no loader in this layer re-scans.
/// </remarks>
public interface IConfigProblemSink
{
    /// <summary>Records a whole-file failure: the file is missing, unparseable, or structurally
    /// invalid.</summary>
    /// <param name="source">The subsystem that found it, such as "VaultConfig", "OpClassRegistry" or
    /// "GrantStore".</param>
    /// <param name="detail">What failed.</param>
    void RecordFatal(string source, string detail);

    /// <summary>Records a per-entry skip - one grants.json row, one vaults.json entry - that leaves
    /// the rest of the file usable.</summary>
    /// <param name="source">The subsystem that found it.</param>
    /// <param name="detail">Which entry was skipped and why.</param>
    void RecordDegraded(string source, string detail);
}
