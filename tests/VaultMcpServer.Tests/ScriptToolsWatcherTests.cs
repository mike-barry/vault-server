using VaultShared.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VaultMcpServer.Health;
using VaultShared.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the <c>tools/</c> file watcher: that create and change events re-scan the folder through
/// <see cref="ScriptToolRegistry.Refresh"/>, that a burst of rapid events debounces to one refresh,
/// that a refresh is followed by a call into
/// <see cref="ScriptToolDispatcher.NotifyToolsListChangedAsync"/>, and that a missing directory is
/// tolerated.
/// </summary>
/// <remarks>
/// These tests read <see cref="ScriptToolRegistry.RefreshCount"/> and
/// <see cref="ScriptToolDispatcher.NotifyCount"/> rather than asserting that a notification was
/// delivered. Under the host's stateless HTTP transport a session's <c>McpServer</c> is gone by the
/// time an asynchronous filesystem event fires, so there is nothing here to receive a push. See the
/// remarks on <see cref="ScriptToolDispatcher"/>.
/// </remarks>
public sealed class ScriptToolsWatcherTests : IDisposable, IClassFixture<ScriptToolsWatcherTests.RunnerFixture>
{
    /// <summary>Holds one runner shared by every test in the class, and disposes it at the end.</summary>
    public sealed class RunnerFixture : IDisposable
    {
        public ScriptToolRunner Runner { get; } = new();
        public void Dispose() => Runner.Dispose();
    }

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
    private readonly ScriptToolRunner _runner;

    public ScriptToolsWatcherTests(RunnerFixture fixture) => _runner = fixture.Runner;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Builds a registry and dispatcher over a tools directory, sharing one health
    /// state.</summary>
    /// <param name="toolsDir">The directory the registry scans. It need not exist.</param>
    /// <returns>The registry, the dispatcher over it, and the health state they both record to.</returns>
    private (ScriptToolRegistry Registry, ScriptToolDispatcher Dispatcher, ServerHealthState HealthState) MakeRegistryAndDispatcher(string toolsDir)
    {
        var healthState = new ServerHealthState();
        var registry = new ScriptToolRegistry(toolsDir, OpClasses, healthState);
        var vaultConfig = new VaultConfig { Vaults = new Dictionary<string, VaultConfig.VaultEntry>(), DefaultVault = "" };
        var dispatcher = new ScriptToolDispatcher(registry, new ToolRegistry(), new ToolExecution(vaultConfig), _runner, new HttpContextAccessor(), healthState, vaultConfig);
        dispatcher.AttachLogger(NullLogger<ScriptToolDispatcher>.Instance);
        return (registry, dispatcher, healthState);
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
    public async Task AddingScript_RefreshesRegistryAndNotifies()
    {
        var (registry, dispatcher, _) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Empty(registry.Tools);

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "greet.ps1"), ValidScript);

        await WaitUntilAsync(() => registry.Tools.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal("greet", registry.Tools[0].Name);
        await WaitUntilAsync(() => dispatcher.NotifyCount >= 1, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EditingScript_RefreshesRegistryWithNewContent()
    {
        var scriptPath = Path.Combine(_tempDir, "greet.ps1");
        await File.WriteAllTextAsync(scriptPath, ValidScript);
        var (registry, dispatcher, _) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
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
        var (registry, dispatcher, healthState) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
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
        var (registry, dispatcher, _) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Single(registry.Tools);

        File.Delete(scriptPath);

        await WaitUntilAsync(() => registry.Tools.Count == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RapidSuccessiveWrites_DebounceToASingleRefresh()
    {
        var (registry, dispatcher, _) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
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
        Assert.Equal(1, dispatcher.NotifyCount);
    }

    [Fact]
    public void MissingToolsDirectory_DoesNotThrowAndStaysEmpty()
    {
        var missingDir = Path.Combine(_tempDir, "no-such-dir");
        var (registry, dispatcher, _) = MakeRegistryAndDispatcher(missingDir);

        using var watcher = new ScriptToolsWatcher(missingDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);

        Assert.Empty(registry.Tools);
    }

    [Fact]
    public async Task Dispose_StopsWatchingFurtherChanges()
    {
        var (registry, dispatcher, _) = MakeRegistryAndDispatcher(_tempDir);
        var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
        watcher.Dispose();

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "greet.ps1"), ValidScript);
        await Task.Delay(500);

        Assert.Empty(registry.Tools);
        Assert.Equal(0, registry.RefreshCount);
    }
}
