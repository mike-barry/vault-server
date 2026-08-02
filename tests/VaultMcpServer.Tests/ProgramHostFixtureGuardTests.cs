using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Negative proof for the test-isolation prevention guard
/// (<see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/>, via the extracted
/// <see cref="ProgramHostFixtureBase.AssertUnderTempDir"/> helper): demonstrates the guard
/// actually throws when a resolved config path points outside the fixture's temp sandbox -
/// exactly the "two env vars" handoff misfire - rather than only ever exercising the
/// (trivially passing) happy path.
/// </summary>
public sealed class ProgramHostFixtureGuardTests
{
    // A minimal concrete fixture so the test can reach the protected helper from within the
    // class hierarchy - constructing it only creates/deletes a temp dir, it never builds the
    // ASP.NET Core host (Services is never touched).
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

        // No exception expected - this is the happy path the real fixtures hit on every
        // correctly-configured run.
        fixture.Probe(goodPath, goodPath);
    }

    [Fact]
    public void AssertUnderTempDir_HostConfigPathOutsideTempDir_Throws()
    {
        using var fixture = new ProbeFixture();
        // The exact misfire the guard exists to catch: the handoff resolving to the repo's
        // real, committed vaults.json instead of the fixture's temp copy.
        var realVaultsJson = Path.Combine(TestVaultFixture.RepoRoot, "vaults.json");
        var goodPath = Path.Combine(fixture.TempDirForTest, "vaults.json");

        var ex = Assert.Throws<InvalidOperationException>(() => fixture.Probe(realVaultsJson, goodPath));
        Assert.Contains("Test-isolation guard failed", ex.Message);
        Assert.Contains("host VaultsConfigPath", ex.Message);
    }

    [Fact]
    public void AssertUnderTempDir_ModuleEnvVarOutsideTempDir_Throws()
    {
        using var fixture = new ProbeFixture();
        var goodPath = Path.Combine(fixture.TempDirForTest, "vaults.json");
        var realVaultsJson = Path.Combine(TestVaultFixture.RepoRoot, "vaults.json");

        var ex = Assert.Throws<InvalidOperationException>(() => fixture.Probe(goodPath, realVaultsJson));
        Assert.Contains("Test-isolation guard failed", ex.Message);
        Assert.Contains("VAULT_MCP_VAULTS_JSON", ex.Message);
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
