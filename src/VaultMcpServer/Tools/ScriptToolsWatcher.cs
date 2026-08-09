namespace VaultMcpServer.Tools;

/// <summary>
/// Watches the tools directory for <c>.ps1</c> create, change, rename, and delete events and
/// re-scans it through <see cref="ScriptToolRegistry.Refresh"/>, so added, edited, or removed
/// script tools take effect without a server restart.
/// </summary>
/// <remarks>
/// An editor's save can fire several events in a row, so events are debounced behind one timer
/// that is pushed back on each event and one save produces one refresh.
///
/// A refresh is not announced to connected clients. A client sees a changed tool set the next
/// time it issues <c>tools/list</c>.
///
/// A missing tools directory is not watched, matching <see cref="ScriptToolLoader.Load"/>, which
/// treats a missing folder as zero tools rather than a startup failure.
/// </remarks>
public sealed class ScriptToolsWatcher : IDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly ScriptToolRegistry _registry;
    private readonly ILogger<ScriptToolsWatcher> _logger;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounceTimer;

    /// <summary>Starts watching the tools directory, if it exists.</summary>
    /// <param name="toolsDir">The directory to watch for <c>.ps1</c> changes.</param>
    /// <param name="registry">The registry re-scanned after a debounced change.</param>
    /// <param name="logger">Receives watcher status and failure messages.</param>
    public ScriptToolsWatcher(string toolsDir, ScriptToolRegistry registry, ILogger<ScriptToolsWatcher> logger)
    {
        _registry = registry;
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
            // Per-script validation problems do not throw: ScriptToolLoader skips the script
            // and records the problem into ServerHealthState's replaceable ScriptTools set,
            // which also logs it. This catch covers anything unexpected, such as an IO failure
            // enumerating the folder, and keeps the previously loaded tool set rather than
            // crashing the watcher.
            _logger.LogWarning(ex, "Failed to refresh script tools after a tools directory change; keeping the previous tool set.");
        }
    }

    /// <summary>Stops watching and releases the timer and file-system watcher.</summary>
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
