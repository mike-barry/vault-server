using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the <c>tools/</c> file watcher: that create/change events re-scan the folder via
/// <see cref="ScriptToolRegistry.Refresh"/>, that a burst of rapid-fire events debounces down
/// to one refresh, that a refresh is followed by a call into
/// <see cref="ScriptToolDispatcher.NotifyToolsListChangedAsync"/>, and that a missing
/// directory is tolerated. Uses <see cref="ScriptToolRegistry.RefreshCount"/> and
/// <see cref="ScriptToolDispatcher.NotifyCount"/> as test-only counters rather than
/// asserting actual notification delivery: under the host's stateless HTTP transport a
/// session's <c>McpServer</c> is gone by the time an async filesystem event fires, so there
/// is nothing at the unit level to receive a push - see the caveats documented on
/// <see cref="ScriptToolDispatcher"/>.
/// </summary>
public sealed class ScriptToolsWatcherTests : IDisposable, IClassFixture<ScriptToolsWatcherTests.RunnerFixture>
{
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
        OpClass: Read
        #>
        param([string]$Name)
        Write-Output "hi $Name"
        """;

    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"));

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-watcher-tests-").FullName;
    private readonly ScriptToolRunner _runner;

    public ScriptToolsWatcherTests(RunnerFixture fixture) => _runner = fixture.Runner;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private (ScriptToolRegistry Registry, ScriptToolDispatcher Dispatcher) MakeRegistryAndDispatcher(string toolsDir)
    {
        var registry = new ScriptToolRegistry(toolsDir, OpClasses);
        var dispatcher = new ScriptToolDispatcher(registry, new NativeToolRegistry([]), _runner, new HttpContextAccessor());
        dispatcher.AttachLogger(NullLogger<ScriptToolDispatcher>.Instance);
        return (registry, dispatcher);
    }

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
        var (registry, dispatcher) = MakeRegistryAndDispatcher(_tempDir);
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
        var (registry, dispatcher) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Equal("Greets someone.", registry.Tools[0].Description);

        await File.WriteAllTextAsync(scriptPath, ValidScript.Replace("Greets someone.", "Greets someone warmly."));

        await WaitUntilAsync(() => registry.Tools is [{ Description: "Greets someone warmly." }], TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeletingScript_RefreshesRegistryToEmpty()
    {
        var scriptPath = Path.Combine(_tempDir, "greet.ps1");
        await File.WriteAllTextAsync(scriptPath, ValidScript);
        var (registry, dispatcher) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
        Assert.Single(registry.Tools);

        File.Delete(scriptPath);

        await WaitUntilAsync(() => registry.Tools.Count == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RapidSuccessiveWrites_DebounceToASingleRefresh()
    {
        var (registry, dispatcher) = MakeRegistryAndDispatcher(_tempDir);
        using var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
        var scriptPath = Path.Combine(_tempDir, "greet.ps1");

        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(scriptPath, ValidScript.Replace("Greets someone.", $"Greets someone. Take {i}."));
            await Task.Delay(20);
        }

        await WaitUntilAsync(() => registry.Tools is [{ Description: "Greets someone. Take 4." }], TimeSpan.FromSeconds(5));
        // Give any further (unexpected) debounce firings a chance to land before asserting the count.
        await Task.Delay(500);
        Assert.Equal(1, registry.RefreshCount);
        Assert.Equal(1, dispatcher.NotifyCount);
    }

    [Fact]
    public void MissingToolsDirectory_DoesNotThrowAndStaysEmpty()
    {
        var missingDir = Path.Combine(_tempDir, "no-such-dir");
        var (registry, dispatcher) = MakeRegistryAndDispatcher(missingDir);

        using var watcher = new ScriptToolsWatcher(missingDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);

        Assert.Empty(registry.Tools);
    }

    [Fact]
    public async Task Dispose_StopsWatchingFurtherChanges()
    {
        var (registry, dispatcher) = MakeRegistryAndDispatcher(_tempDir);
        var watcher = new ScriptToolsWatcher(_tempDir, registry, dispatcher, NullLogger<ScriptToolsWatcher>.Instance);
        watcher.Dispose();

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "greet.ps1"), ValidScript);
        await Task.Delay(500);

        Assert.Empty(registry.Tools);
        Assert.Equal(0, registry.RefreshCount);
    }
}
