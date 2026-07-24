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
        var scopeRootsByVault = scopeRoots.ToDictionary(
            kv => kv.Key,
            IReadOnlySet<string> (kv) => kv.Value.ToHashSet(StringComparer.OrdinalIgnoreCase),
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

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> ScopeFor(string vault, params string[] roots) =>
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [vault] = roots.ToHashSet(StringComparer.OrdinalIgnoreCase),
        };

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
