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
    /// given. The set here uses the ordinal default, which is what a new producer would most
    /// easily introduce, and the gate must not turn on it.
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

    /// <summary>
    /// The vault gate compares without regard to case whatever comparer built the set, and agrees
    /// with <c>ScopeIn</c>, which reads its own map the same way. A grant that reaches a vault must
    /// not deny it under another spelling while holding a scope entry for it.
    /// </summary>
    [Theory]
    [InlineData("test")]
    [InlineData("Test")]
    [InlineData("TEST")]
    public void CanAccessVault_ComparesWithoutRegardToCaseAndAgreesWithScopeIn(string requested)
    {
        var grant = new Grant(
            "test-grant",
            new HashSet<string>(StringComparer.Ordinal) { "content:read" },
            new HashSet<string>(StringComparer.Ordinal) { "test" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["test"] = VaultScope.Unscoped });

        Assert.True(grant.CanAccessVault(requested));
        Assert.Equal(VaultScope.Unscoped, grant.ScopeIn(requested));
    }

    /// <summary>A vault the grant does not list is still denied, in any spelling.</summary>
    [Fact]
    public void CanAccessVault_DeniesAVaultTheGrantDoesNotList()
    {
        var grant = new Grant(
            "test-grant",
            new HashSet<string>(StringComparer.Ordinal) { "content:read" },
            new HashSet<string>(StringComparer.Ordinal) { "test" },
            null,
            new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { ["test"] = VaultScope.Unscoped });

        Assert.False(grant.CanAccessVault("scratch"));
        Assert.False(grant.CanAccessVault("SCRATCH"));
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
