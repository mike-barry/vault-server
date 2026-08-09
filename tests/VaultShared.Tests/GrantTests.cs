using VaultShared.Auth;
using VaultShared.Scope;

namespace VaultShared.Tests;

public sealed class GrantTests
{
    [Fact]
    public void ScopeIn_VaultWithNoEntryThrows()
    {
        // Every real grant has an entry for every vault it can reach (enforced by
        // GrantStore.Load and GrantStore.Mint), so a grant missing the queried vault's key means
        // that invariant was violated upstream - it must fail loudly rather than silently falling
        // back to full access.
        var grant = new Grant(
            "test-grant",
            new HashSet<string>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["test"] = VaultScope.Of(["Projects"]) });

        Assert.Throws<InvalidOperationException>(() => grant.ScopeIn("scratch"));
    }

    /// <summary>
    /// The op-class gate compares without regard to case whatever comparer built the set it was
    /// given. Every producer chooses a case-insensitive one today, so a set built with the ordinal
    /// default is what a new producer would most easily introduce, and the gate must not turn on it.
    /// </summary>
    [Theory]
    [InlineData("content:write")]
    [InlineData("Content:Write")]
    [InlineData("CONTENT:WRITE")]
    public void Allows_ComparesWithoutRegardToCaseOverAnOrdinalSet(string requested)
    {
        var grant = new Grant(
            "test-grant",
            new HashSet<string>(StringComparer.Ordinal) { "content:write" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["test"] = VaultScope.Unscoped });

        Assert.True(grant.Allows(requested));
    }

    /// <summary>An op class the grant does not hold is still denied, in any spelling.</summary>
    [Fact]
    public void Allows_DeniesAnOpClassTheGrantDoesNotHold()
    {
        var grant = new Grant(
            "test-grant",
            new HashSet<string>(StringComparer.Ordinal) { "content:read" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["test"] = VaultScope.Unscoped });

        Assert.False(grant.Allows("content:write"));
        Assert.False(grant.Allows("CONTENT:WRITE"));
    }
}
