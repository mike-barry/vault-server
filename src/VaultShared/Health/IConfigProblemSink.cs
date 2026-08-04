namespace VaultShared.Health;

/// <summary>
/// Where a configuration loader reports what it could not use. Write-only and deliberately
/// narrow: <c>vaults.json</c>, <c>op-classes.json</c> and <c>grants.json</c> record whole-file
/// failures and per-entry skips here instead of throwing, and return an inert or partial result,
/// so a loader never decides what its host should do about a bad file.
///
/// That decision differs by host, which is why this is an interface rather than a concrete type.
/// A long-running server records problems, starts anyway, and refuses requests while any fatal
/// one stands. A single-invocation front-end has nothing to stay up for and fails instead, because
/// configuration that will not load must never admit an invocation unrestricted. Both use the same
/// loaders.
///
/// Only the append-only recording surface is here. A source that re-scans at runtime needs to
/// retract a problem it previously reported, which no loader in this layer does.
/// </summary>
public interface IConfigProblemSink
{
    /// <summary>Records a whole-file failure: the file is missing, unparseable, or structurally
    /// invalid. <paramref name="source"/> identifies the subsystem (e.g. "VaultConfig",
    /// "OpClassRegistry", "GrantStore"); <paramref name="detail"/> says what failed.</summary>
    void RecordFatal(string source, string detail);

    /// <summary>Records a per-entry skip - one grants.json row, one vaults.json entry - that
    /// leaves the rest of the file usable.</summary>
    void RecordDegraded(string source, string detail);
}
