namespace VaultShared.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> pinned to a settable instant, used to control the "now" a
/// grant's numeric <c>ttl</c> is measured from at <c>GrantStore.Load</c> time, and - since
/// <c>GrantStore</c> keeps the same provider for its later <c>Authenticate</c> expiry checks -
/// to simulate time passing after load without depending on real wall-clock time. Tests that
/// only need a fixed load-time instant can construct-and-forget; tests that need to observe a
/// grant transition to expired can mutate <see cref="Now"/> afterward.
/// </summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
