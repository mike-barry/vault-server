using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace VaultMcpServer.Tests;

/// <summary>
/// Shared base for every <see cref="WebApplicationFactory{Program}"/>-based fixture that boots
/// the real host (Program.cs) against per-fixture temp config. Program.cs reads
/// VaultsConfigPath/OpClassesConfigPath/GrantsConfigPath/ToolsDirectory/LogsDirectory from
/// IConfiguration before builder.Build(), so a ConfigureWebHost/ConfigureAppConfiguration
/// override lands too late - WebApplicationFactory doesn't guarantee it's applied before the
/// entry point's own top-level code runs. Environment variables are folded into
/// WebApplicationBuilder.Configuration synchronously inside CreateBuilder() itself, so setting
/// them here (before the host is lazily built on first client/server access) reliably overrides
/// both paths.
///
/// Each derived fixture creates its own temp directory (isolating it from every other fixture
/// and from the committed repo config), writes whatever vaults.json/grants.json/tools/ it needs
/// under that directory, then calls <see cref="ConfigureEnvironment"/> once to point Program.cs
/// at them. Disposal clears every env var this fixture set and deletes the temp directory.
/// </summary>
public abstract class ProgramHostFixtureBase : WebApplicationFactory<Program>
{
    private readonly List<string> _envVarsSet = [];

    protected string TempDir { get; }

    protected ProgramHostFixtureBase(string tempDirPrefix)
    {
        TempDir = Directory.CreateTempSubdirectory(tempDirPrefix).FullName;
    }

    /// <summary>
    /// Points Program.cs's config loaders at this fixture's temp files/directories.
    /// VaultsConfigPath, GrantsConfigPath, and LogsDirectory are always set - LogsDirectory is
    /// redirected into <c>TempDir/logs</c> so test runs never litter the repo-root
    /// <c>logs/</c> directory with timestamped files. ToolsDirectory, OpClassesConfigPath, and
    /// the PowerShell-side VAULT_MCP_VAULTS_JSON variable are optional: several fixtures point
    /// ToolsDirectory at the real repo <c>tools/</c> directory (so a script's own relative
    /// Import-Module resolves) and also need VAULT_MCP_VAULTS_JSON so that in-process script
    /// sees the same vaults.json the C# host does; others use a throwaway temp tools/ folder
    /// and need neither the real path nor the PowerShell-side variable. Leaving an optional
    /// parameter null leaves Program.cs's own default for that setting in place.
    /// </summary>
    protected void ConfigureEnvironment(
        string vaultsConfigPath,
        string grantsConfigPath,
        string? toolsDirectory = null,
        string? opClassesConfigPath = null,
        string? vaultMcpVaultsJsonPath = null)
    {
        SetEnvironmentVariable("VaultsConfigPath", vaultsConfigPath);
        SetEnvironmentVariable("GrantsConfigPath", grantsConfigPath);
        SetEnvironmentVariable("LogsDirectory", Path.Combine(TempDir, "logs"));
        if (toolsDirectory is not null)
            SetEnvironmentVariable("ToolsDirectory", toolsDirectory);
        if (opClassesConfigPath is not null)
            SetEnvironmentVariable("OpClassesConfigPath", opClassesConfigPath);
        if (vaultMcpVaultsJsonPath is not null)
            SetEnvironmentVariable("VAULT_MCP_VAULTS_JSON", vaultMcpVaultsJsonPath);
    }

    /// <summary>Sets a process-global env var and tracks it for disposal cleanup. Derived
    /// fixtures use it for extra settings beyond the config paths (e.g. the
    /// <c>LocalhostAuth__*</c> / <c>Elevation__*</c> sections), which ASP.NET Core folds into
    /// <c>WebApplicationBuilder.Configuration</c> synchronously - the same reason the config paths
    /// are set this way rather than via ConfigureAppConfiguration.</summary>
    protected void SetEnvironmentVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _envVarsSet.Add(name);
    }

    /// <summary>
    /// Prevention half of the test-isolation guard pair (detection half is
    /// <see cref="TestVaultCleanGuard"/>, run from <see cref="Dispose"/> below). Targets the
    /// exact observed failure mode from Vault-MCP-Server/Corpus/Testing.md's "two env vars"
    /// gotcha: a mutating dispatch test relies entirely on the host's resolved
    /// <c>VaultsConfigPath</c> and the PowerShell module's <c>$env:VAULT_MCP_VAULTS_JSON</c>
    /// both pointing under this fixture's temp directory, and when that handoff misfires, the
    /// tool operates on the repo's real, committed <c>vaults.json</c> instead. Call this
    /// immediately before invoking a mutating tool call, so a misfire fails fast with a clear
    /// message rather than quietly mutating the committed fixture.
    /// </summary>
    protected void AssertMutationSandboxed()
    {
        var config = Services.GetRequiredService<IConfiguration>();
        var env = Services.GetRequiredService<IWebHostEnvironment>();
        var hostVaultsConfigPath = Path.GetFullPath(Path.Combine(
            env.ContentRootPath, config["VaultsConfigPath"] ?? "../../vaults.json"));

        AssertUnderTempDir(hostVaultsConfigPath, "the host's resolved VaultsConfigPath", TempDir);
        AssertUnderTempDir(Environment.GetEnvironmentVariable("VAULT_MCP_VAULTS_JSON"), "$env:VAULT_MCP_VAULTS_JSON", TempDir);
    }

    /// <summary>
    /// Extracted as an internal static helper (rather than inlined in
    /// <see cref="AssertMutationSandboxed"/>) so it has a unit-testable negative case - see
    /// <c>ProgramHostFixtureGuardTests</c>, which proves the guard actually throws when a
    /// resolved path points outside the fixture's temp directory.
    /// </summary>
    protected internal static void AssertUnderTempDir(string? actualPath, string label, string tempDir)
    {
        if (string.IsNullOrEmpty(actualPath))
        {
            throw new InvalidOperationException(
                $"Test-isolation guard failed: {label} was not set. A mutating tool call would resolve vault " +
                "config through an undefined path instead of this fixture's temp sandbox - aborting before the call runs.");
        }

        var full = Path.GetFullPath(actualPath);
        var tempFull = Path.GetFullPath(tempDir);
        var isUnder = full.Equals(tempFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(tempFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (!isUnder)
        {
            throw new InvalidOperationException(
                $"Test-isolation guard failed: {label} resolved to '{full}', which is outside this fixture's " +
                $"temp directory '{tempFull}'. A mutating tool would risk escaping the sandbox and mutating the " +
                "committed test-vault/ on disk - aborting before the tool call runs.");
        }
    }

    protected static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var name in _envVarsSet)
                Environment.SetEnvironmentVariable(name, null);
            try
            {
                Directory.Delete(TempDir, recursive: true);
            }
            catch (IOException)
            {
            }

            // Detection half of the test-isolation guard pair - runs after this fixture's own
            // cleanup so a dirty test-vault/ is reported clearly rather than buried under other
            // teardown noise. See TestVaultCleanGuard's remarks.
            TestVaultCleanGuard.AssertCommittedFixtureUntouched();
        }
    }
}
