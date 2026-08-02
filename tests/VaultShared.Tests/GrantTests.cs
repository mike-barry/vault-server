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
}
