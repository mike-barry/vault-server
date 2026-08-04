using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Provides the shared base for every <see cref="WebApplicationFactory{Program}"/> fixture that
/// boots the real host (Program.cs) against per-fixture temporary config.
/// </summary>
/// <remarks>
/// Config paths must be supplied as environment variables. Program.cs reads
/// VaultsConfigPath, OpClassesConfigPath, GrantsConfigPath, ToolsDirectory and LogsDirectory from
/// IConfiguration before builder.Build(), and a ConfigureWebHost or ConfigureAppConfiguration
/// override is not guaranteed to be applied before the entry point's top-level code runs.
/// Environment variables are folded into WebApplicationBuilder.Configuration synchronously inside
/// CreateBuilder(), so setting them here - before the host is lazily built on first client or
/// server access - reliably takes effect.
///
/// Each derived fixture creates its own temp directory, writes whatever vaults.json, grants.json
/// or tools/ it needs under that directory, then calls <see cref="ConfigureEnvironment"/> once to
/// point Program.cs at them. That keeps a fixture isolated from every other fixture and from the
/// committed repo config. Disposal clears every environment variable this fixture set and deletes
/// the temp directory.
/// </remarks>
public abstract class ProgramHostFixtureBase : WebApplicationFactory<Program>
{
    private readonly List<string> _envVarsSet = [];

    protected string TempDir { get; }

    /// <summary>
    /// The request header a test sets to simulate a specific client remote address. When the
    /// header is absent the request is presented as coming from loopback.
    /// </summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    protected ProgramHostFixtureBase(string tempDirPrefix)
    {
        TempDir = Directory.CreateTempSubdirectory(tempDirPrefix).FullName;
    }

    /// <summary>
    /// Registers the startup filter that gives every test request a concrete remote IP address.
    /// </summary>
    /// <param name="builder">The web host builder supplied by <see cref="WebApplicationFactory{TEntryPoint}"/>.</param>
    /// <remarks>
    /// TestServer leaves <c>HttpContext.Connection.RemoteIpAddress</c> null unless something sets
    /// it, and <see cref="Auth.BearerGrantMiddleware"/>'s loopback gate fails closed on null, so
    /// without this filter every hosted request would be treated as non-loopback and rejected with
    /// 401. The filter stamps loopback by default and honours the <see cref="RemoteIpHeader"/>
    /// header when a test needs to drive the non-loopback path.
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
    }

    /// <summary>
    /// Wraps the whole pipeline and sets <c>RemoteIpAddress</c> before any app middleware runs:
    /// loopback by default, or the <see cref="RemoteIpHeader"/> value when a test supplies one.
    /// </summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress =
                    context.Request.Headers.TryGetValue(RemoteIpHeader, out var supplied)
                    && IPAddress.TryParse(supplied.ToString(), out var parsed)
                        ? parsed
                        : IPAddress.Loopback;
                await nextMiddleware();
            });
            next(app);
        };
    }

    /// <summary>
    /// Points Program.cs's config loaders at this fixture's temporary files and directories.
    /// </summary>
    /// <param name="vaultsConfigPath">Path written to the <c>VaultsConfigPath</c> setting.</param>
    /// <param name="grantsConfigPath">Path written to the <c>GrantsConfigPath</c> setting.</param>
    /// <param name="toolsDirectory">Path written to the <c>ToolsDirectory</c> setting, or null to
    /// leave Program.cs's default in place.</param>
    /// <param name="opClassesConfigPath">Path written to the <c>OpClassesConfigPath</c> setting,
    /// or null to leave Program.cs's default in place.</param>
    /// <param name="vaultMcpVaultsJsonPath">Path written to the <c>VAULT_MCP_VAULTS_JSON</c>
    /// variable that PowerShell tool scripts read, or null to leave it unset.</param>
    /// <remarks>
    /// LogsDirectory is always redirected into <c>TempDir/logs</c> so test runs never write
    /// timestamped files into the repo-root <c>logs/</c> directory.
    ///
    /// A fixture that points <paramref name="toolsDirectory"/> at the real repo <c>tools/</c>
    /// directory - so a script's relative Import-Module resolves - must also pass
    /// <paramref name="vaultMcpVaultsJsonPath"/>, because the script reads its vault list from
    /// that variable rather than from the host's configuration. A fixture using a throwaway temp
    /// tools folder needs neither.
    /// </remarks>
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

    /// <summary>
    /// Sets a process-global environment variable and tracks it so disposal clears it. Derived
    /// fixtures use this for settings beyond the config paths, such as the <c>LocalhostAuth__*</c>
    /// and <c>Elevation__*</c> sections.
    /// </summary>
    /// <param name="name">Name of the environment variable to set.</param>
    /// <param name="value">Value to set it to.</param>
    protected void SetEnvironmentVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _envVarsSet.Add(name);
    }

    /// <summary>
    /// Throws unless both the host's resolved <c>VaultsConfigPath</c> and the script runspace's
    /// <c>VAULT_MCP_VAULTS_JSON</c> point under this fixture's temp directory.
    /// </summary>
    /// <exception cref="InvalidOperationException">Either path is unset or resolves outside the
    /// temp directory.</exception>
    /// <remarks>
    /// Call this immediately before a mutating tool call. A mutating test depends on both
    /// variables, and if either one is missed the tool operates on the repo's real committed
    /// <c>vaults.json</c>. Failing here reports that clearly instead of letting the mutation
    /// happen. The matching check after the fact is
    /// <see cref="TestVaultCleanGuard"/>, run from <see cref="Dispose"/>.
    /// </remarks>
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
    /// Throws unless <paramref name="actualPath"/> is set and resolves to
    /// <paramref name="tempDir"/> or somewhere beneath it.
    /// </summary>
    /// <param name="actualPath">The path to check. Null or empty is a failure.</param>
    /// <param name="label">Name of the setting the path came from, used in the failure message.</param>
    /// <param name="tempDir">The fixture temp directory the path must be under.</param>
    /// <exception cref="InvalidOperationException">The path is unset or falls outside
    /// <paramref name="tempDir"/>.</exception>
    /// <remarks>
    /// Separate from <see cref="AssertMutationSandboxed"/> so its failure case can be tested
    /// directly; see <c>ProgramHostFixtureGuardTests</c>.
    /// </remarks>
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

            // Runs after this fixture's own cleanup so a dirty test-vault/ is reported clearly
            // rather than buried under other teardown noise.
            TestVaultCleanGuard.AssertCommittedFixtureUntouched();
        }
    }
}
