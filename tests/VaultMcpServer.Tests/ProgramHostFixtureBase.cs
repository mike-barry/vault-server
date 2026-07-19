using Microsoft.AspNetCore.Mvc.Testing;

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

    private void SetEnvironmentVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _envVarsSet.Add(name);
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
        }
    }
}
