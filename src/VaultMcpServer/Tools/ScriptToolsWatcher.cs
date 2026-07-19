namespace VaultMcpServer.Tools;

/// <summary>
/// Watches `tools/` for `.ps1` create/change/rename/delete events and re-scans the folder
/// via <see cref="ScriptToolRegistry.Refresh"/> so added, edited, or removed script tools
/// take effect without a server restart. Rapid-fire events (an editor's save can fire
/// several in a row) are debounced behind a single timer that keeps getting pushed back,
/// so one save triggers one refresh.
///
/// After a refresh, broadcasts `notifications/tools/list_changed` via
/// <see cref="ScriptToolDispatcher.NotifyToolsListChangedAsync"/> - best-effort, per the
/// caveats documented there.
///
/// A missing `tools/` directory is not watched, matching <see cref="ScriptToolLoader.Load"/>
/// treating a missing folder as zero tools rather than a startup failure.
/// </summary>
public sealed class ScriptToolsWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly ScriptToolRegistry _registry;
    private readonly ScriptToolDispatcher _dispatcher;
    private readonly ILogger<ScriptToolsWatcher> _logger;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounceTimer;

    public ScriptToolsWatcher(string toolsDir, ScriptToolRegistry registry, ScriptToolDispatcher dispatcher, ILogger<ScriptToolsWatcher> logger)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _logger = logger;
        _debounceTimer = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);

        if (!Directory.Exists(toolsDir))
        {
            _logger.LogInformation("Tools directory {ToolsDir} does not exist; not watching for changes.", toolsDir);
            return;
        }

        _watcher = new FileSystemWatcher(toolsDir, "*.ps1")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("Watching {ToolsDir} for .ps1 changes.", toolsDir);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        _debounceTimer.Change(DebounceWindow, Timeout.InfiniteTimeSpan);

    private void OnError(object sender, ErrorEventArgs e) =>
        _logger.LogWarning(e.GetException(), "Tools directory watcher error.");

    private void OnDebounceElapsed()
    {
        try
        {
            _registry.Refresh();
            _logger.LogInformation("Tools directory changed; refreshed {Count} tool(s).", _registry.Tools.Count);
        }
        catch (Exception ex)
        {
            // Per-script validation problems no longer throw - ScriptToolLoader skips the
            // script and records the problem into ServerHealthState's replaceable
            // ScriptTools set (which also logs, via the attached logger). This catch is a
            // safety net for anything unexpected (e.g. an IO failure enumerating the
            // folder itself): log and keep the previously-loaded tool set rather than
            // crashing the watcher.
            _logger.LogWarning(ex, "Failed to refresh script tools after a tools directory change; keeping the previous tool set.");
            return;
        }

        _ = NotifyAsync();
    }

    private async Task NotifyAsync()
    {
        try
        {
            await _dispatcher.NotifyToolsListChangedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast a tools list_changed notification.");
        }
    }

    public void Dispose()
    {
        _debounceTimer.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileEvent;
            _watcher.Changed -= OnFileEvent;
            _watcher.Deleted -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }
    }
}
