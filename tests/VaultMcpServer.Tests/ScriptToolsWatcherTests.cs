using Microsoft.Extensions.Logging.Abstractions;
using VaultMcpServer.Health;
using VaultShared.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the <c>tools/</c> file watcher: that create, change and delete events re-scan the folder
/// through <see cref="ScriptToolRegistry.Refresh"/>, that a burst of rapid events debounces to one
/// refresh, that a broken script becomes a health problem and a fixed one clears it, and that a
/// missing directory is tolerated.
/// </summary>
public sealed class ScriptToolsWatcherTests : IDisposable
{
    private const string ValidScript = """
        <#
        .SYNOPSIS
        Greets someone.
        .PARAMETER Name
        Who to greet.
        .NOTES
        OpClass: content:read
        #>
        param([string]$Name)
        Write-Output "hi $Name"
        """;

    private static readonly OpClassRegistry OpClasses = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-watcher-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Builds a registry over a tools directory, alongside the health state it records
    /// script-tool problems to.</summary>
    /// <param name="toolsDir">The directory the registry scans. It need not exist.</param>
    /// <returns>The registry and the health state it records to.</returns>
    private static (ScriptToolRegistry Registry, ServerHealthState HealthState) MakeRegistry(string toolsDir)
    {
        var healthState = new ServerHealthState();
        return (new ScriptToolRegistry(toolsDir, OpClasses, healthState), healthState);
    }

    /// <summary>Polls until a condition holds, failing the test if it never does.</summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">How long to keep polling before failing.</param>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Condition not met within {timeout}.");
    }

    [Fact]
    public async Task AddingScript_RefreshesRegistry()
    {
        var (registry, _) = MakeRegistry(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Empty(registry.Tools);

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "greet.ps1"), ValidScript);

        await WaitUntilAsync(() => registry.Tools.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal("greet", registry.Tools[0].Name);
    }

    [Fact]
    public async Task EditingScript_RefreshesRegistryWithNewContent()
    {
        var scriptPath = Path.Combine(_tempDir, "greet.ps1");
        await File.WriteAllTextAsync(scriptPath, ValidScript);
        var (registry, _) = MakeRegistry(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Equal("Greets someone.", registry.Tools[0].Description);

        await File.WriteAllTextAsync(scriptPath, ValidScript.Replace("Greets someone.", "Greets someone warmly."));

        await WaitUntilAsync(() => registry.Tools is [{ Description: "Greets someone warmly." }], TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BreakingThenFixingScript_ReplacesTheRecordedProblemSetAtRuntime()
    {
        // Through the watcher: a script broken by an edit shows up as a Degraded ScriptTools
        // problem after the re-scan it triggers, while the rest of the folder keeps its tools, and
        // fixing it clears the problem on the next re-scan. This is what health_check reports on.
        var scriptPath = Path.Combine(_tempDir, "greet.ps1");
        await File.WriteAllTextAsync(scriptPath, ValidScript);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "other.ps1"), ValidScript);
        var (registry, healthState) = MakeRegistry(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Equal(2, registry.Tools.Count);
        Assert.Empty(healthState.Problems);

        await File.WriteAllTextAsync(scriptPath, "param([string]$X)\nWrite-Output $X\n");

        await WaitUntilAsync(() => healthState.Problems.Count == 1, TimeSpan.FromSeconds(5));
        Assert.False(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal(ScriptToolLoader.HealthSource, problem.Source);
        Assert.Contains("greet.ps1", problem.Detail);
        Assert.Equal("other", Assert.Single(registry.Tools).Name);

        await File.WriteAllTextAsync(scriptPath, ValidScript);

        await WaitUntilAsync(() => healthState.Problems.Count == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(2, registry.Tools.Count);
    }

    [Fact]
    public async Task DeletingScript_RefreshesRegistryToEmpty()
    {
        var scriptPath = Path.Combine(_tempDir, "greet.ps1");
        await File.WriteAllTextAsync(scriptPath, ValidScript);
        var (registry, _) = MakeRegistry(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Single(registry.Tools);

        File.Delete(scriptPath);

        await WaitUntilAsync(() => registry.Tools.Count == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RapidSuccessiveWrites_DebounceToASingleRefresh()
    {
        var (registry, _) = MakeRegistry(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, NullLogger<ScriptToolsWatcher>.Instance);
        var scriptPath = Path.Combine(_tempDir, "greet.ps1");

        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(scriptPath, ValidScript.Replace("Greets someone.", $"Greets someone. Take {i}."));
            await Task.Delay(20);
        }

        await WaitUntilAsync(() => registry.Tools is [{ Description: "Greets someone. Take 4." }], TimeSpan.FromSeconds(5));
        // Give any further debounce firings a chance to land before asserting the count.
        await Task.Delay(500);
        Assert.Equal(1, registry.RefreshCount);
    }

    [Fact]
    public void MissingToolsDirectory_DoesNotThrowAndStaysEmpty()
    {
        var missingDir = Path.Combine(_tempDir, "no-such-dir");
        var (registry, _) = MakeRegistry(missingDir);

        using var watcher = new ScriptToolsWatcher(missingDir, registry, NullLogger<ScriptToolsWatcher>.Instance);

        Assert.Empty(registry.Tools);
    }

    [Fact]
    public async Task Dispose_StopsWatchingFurtherChanges()
    {
        var (registry, _) = MakeRegistry(_tempDir);
        var watcher = new ScriptToolsWatcher(_tempDir, registry, NullLogger<ScriptToolsWatcher>.Instance);
        watcher.Dispose();

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "greet.ps1"), ValidScript);
        await Task.Delay(500);

        Assert.Empty(registry.Tools);
        Assert.Equal(0, registry.RefreshCount);
    }
}
