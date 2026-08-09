using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the failure cases of the test-isolation guard,
/// <see cref="ProgramHostFixtureBase.AssertUnderTempDir"/>. The guard must throw when a resolved
/// config path points outside the fixture's temp directory, which is what would otherwise let a
/// mutating test reach the committed vault config.
/// </summary>
public sealed class ProgramHostFixtureGuardTests
{
    // A minimal concrete fixture, so the tests can reach the protected helper from inside the class
    // hierarchy. Constructing it only creates and deletes a temp directory; the ASP.NET Core host is
    // never built, because Services is never touched.
    private sealed class ProbeFixture : ProgramHostFixtureBase
    {
        public ProbeFixture() : base("vault-mcp-guard-probe-tests-")
        {
        }

        public string TempDirForTest => TempDir;

        public void Probe(string? vaultsConfigPath, string? vaultMcpVaultsJsonPath)
        {
            AssertUnderTempDir(vaultsConfigPath, "host VaultsConfigPath", TempDir);
            AssertUnderTempDir(vaultMcpVaultsJsonPath, "$env:VAULT_MCP_VAULTS_JSON", TempDir);
        }
    }

    [Fact]
    public void AssertUnderTempDir_BothPathsUnderTempDir_DoesNotThrow()
    {
        using var fixture = new ProbeFixture();
        var goodPath = Path.Combine(fixture.TempDirForTest, "vaults.json");

        // No exception expected: this is what a correctly configured fixture looks like.
        fixture.Probe(goodPath, goodPath);
    }

    [Fact]
    public void AssertUnderTempDir_HostConfigPathOutsideTempDir_Throws()
    {
        using var fixture = new ProbeFixture();
        // The case the guard exists to catch: the path resolving to the repo's committed
        // vaults.json instead of the fixture's temp copy.
        var realVaultsJson = Path.Combine(TestVaultFixture.RepoRoot, "vaults.json");
        var goodPath = Path.Combine(fixture.TempDirForTest, "vaults.json");

        var ex = Assert.Throws<InvalidOperationException>(() => fixture.Probe(realVaultsJson, goodPath));
        Assert.Contains("Test-isolation guard failed", ex.Message);
        Assert.Contains("host VaultsConfigPath", ex.Message);
    }

    [Fact]
    public void AssertUnderTempDir_NullPath_Throws()
    {
        using var fixture = new ProbeFixture();
        var goodPath = Path.Combine(fixture.TempDirForTest, "vaults.json");

        var ex = Assert.Throws<InvalidOperationException>(() => fixture.Probe(null, goodPath));
        Assert.Contains("Test-isolation guard failed", ex.Message);
    }
}
