namespace VaultShared.Tests;

/// <summary>
/// Provides a <see cref="TimeProvider"/> pinned to a settable instant, so tests control the
/// current time instead of depending on the wall clock.
/// </summary>
/// <remarks>
/// <c>GrantStore</c> measures a grant's numeric <c>ttl</c> from this provider at load time and
/// keeps the same provider for its later <c>Authenticate</c> expiry checks. Advancing
/// <see cref="Now"/> after a load therefore lets a test watch a grant become expired.
/// </remarks>
/// <param name="now">The instant this provider reports until <see cref="Now"/> is changed.</param>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>Gets or sets the instant this provider reports.</summary>
    public DateTimeOffset Now { get; set; } = now;

    /// <summary>Returns the current value of <see cref="Now"/>.</summary>
    /// <returns>The instant this provider is pinned to.</returns>
    public override DateTimeOffset GetUtcNow() => Now;
}
