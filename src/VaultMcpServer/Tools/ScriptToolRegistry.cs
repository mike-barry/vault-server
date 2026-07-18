namespace VaultMcpServer.Tools;

/// <summary>
/// Holds the current set of script tools, loaded eagerly at construction (host startup).
/// <see cref="Refresh"/> re-scans the folder - the hook the tools/ file watcher calls to
/// pick up added, changed, or removed scripts without a restart.
/// </summary>
public sealed class ScriptToolRegistry(string toolsDir)
{
    public IReadOnlyList<ScriptTool> Tools { get; private set; } = ScriptToolLoader.Load(toolsDir);

    /// <summary>Number of times <see cref="Refresh"/> has run, startup load excluded - lets tests confirm the <c>tools/</c> watcher's debounce coalesces a burst of filesystem events into one re-scan.</summary>
    public int RefreshCount { get; private set; }

    public void Refresh()
    {
        Tools = ScriptToolLoader.Load(toolsDir);
        RefreshCount++;
    }
}
