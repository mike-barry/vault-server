using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class VaultAccessTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    [Theory]
    [MemberData(nameof(PathCases))]
    public void ResolvePath_SharedCases(string name, string path, bool valid)
    {
        if (valid)
        {
            var full = fixture.Vault.ResolvePath(null, path);
            Assert.True(Path.IsPathRooted(full), $"{name}: expected an absolute path, got {full}");
        }
        else
        {
            var ex = Record.Exception(() => fixture.Vault.ResolvePath(null, path));
            Assert.True(ex is ArgumentException, $"{name}: expected ArgumentException, got {ex?.GetType().Name ?? "no exception"}");
        }
    }

    [Theory]
    [MemberData(nameof(ScopeCases))]
    public void IsPathInScope_SharedCases(string name, string vault, string path, Dictionary<string, List<string>> scopeRoots, string outcome)
    {
        // The case files spell whole-vault access as an empty root list, which is the config
        // format rather than the model: it maps to the explicit unscoped case here.
        var scopeRootsByVault = scopeRoots.ToDictionary(
            kv => kv.Key,
            VaultScope (kv) => kv.Value.Count == 0
                ? VaultScope.Unscoped
                : VaultScope.Of(kv.Value.ToHashSet(StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        var resolved = VaultAccess.ResolveVaultScopeRoots(vault, scopeRootsByVault);

        switch (outcome)
        {
            case "inScope":
                Assert.True(VaultAccess.IsPathInScope(path, resolved), $"{name}: expected in scope");
                break;
            case "outOfScope":
                Assert.False(VaultAccess.IsPathInScope(path, resolved), $"{name}: expected out of scope");
                break;
            case "invalid":
                Assert.Throws<ArgumentException>(() => VaultAccess.IsPathInScope(path, resolved));
                break;
            default:
                Assert.Fail($"{name}: unknown outcome {outcome}");
                break;
        }
    }

    private static IReadOnlyDictionary<string, VaultScope> ScopeFor(string vault, params string[] roots) =>
        new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase)
        {
            [vault] = VaultScope.Of(roots.ToHashSet(StringComparer.OrdinalIgnoreCase)),
        };

    [Fact]
    public void VaultScope_EmptyRootSetIsNotConstructible()
    {
        // The point of the two-case shape: whole-vault authority has exactly one spelling, and
        // it is not "a root set that happens to be empty". A caller that filters a root list down
        // to nothing gets an error, not the widest possible grant.
        Assert.Throws<ArgumentException>(() => VaultScope.Of([]));
        Assert.Throws<ArgumentException>(() => VaultScope.Of(new HashSet<string>()));
    }

    [Fact]
    public void VaultScope_RootedScopesCompareByTheirRoots()
    {
        // Guards the record's compiler-generated equality, which would compare the underlying
        // set by reference and report two identical scopes as different.
        Assert.Equal(VaultScope.Of(["Projects", "Areas"]), VaultScope.Of(["Areas", "Projects"]));
        Assert.NotEqual(VaultScope.Of(["Projects"]), VaultScope.Of(["Areas"]));
        Assert.NotEqual(VaultScope.Unscoped, VaultScope.Of(["Projects"]));
    }

    [Fact]
    public void ResolveVaultScopeRoots_NonNullMapMissingVaultKeyThrows()
    {
        // Every real Grant.ScopeRoots is guaranteed to have an entry for every vault it can
        // reach (enforced by GrantStore.Load and GrantStore.Mint), so a non-null map missing
        // the queried vault's key means that invariant was violated upstream - it must fail
        // loudly rather than silently falling back to full access.
        var scopeRoots = ScopeFor("primary", "Projects");
        Assert.Throws<InvalidOperationException>(() => VaultAccess.ResolveVaultScopeRoots("test", scopeRoots));
    }

    public static TheoryData<string, string, bool> PathCases => SharedCases.Paths();
    public static TheoryData<string, string, string, Dictionary<string, List<string>>, string> ScopeCases => SharedCases.Scope();
}
