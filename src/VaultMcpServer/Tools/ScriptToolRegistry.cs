using VaultShared.Health;
using VaultShared.Configuration;

namespace VaultMcpServer.Tools;

/// <summary>
/// Holds the current set of script tools, loaded eagerly at construction (host startup).
/// <see cref="Refresh"/> re-scans the folder - the hook the tools/ file watcher calls to
/// pick up added, changed, or removed scripts without a restart. Both the initial load and
/// every refresh route per-script validation problems into <paramref name="healthState"/>'s
/// replaceable <see cref="ScriptToolLoader.HealthSource"/> set (see
/// <see cref="ScriptToolLoader.Load"/>), so the recorded problems always describe the
/// folder's current state.
/// </summary>
public sealed class ScriptToolRegistry(string toolsDir, OpClassRegistry opClassRegistry, ServerHealthState healthState)
{
    public IReadOnlyList<ScriptTool> Tools { get; private set; } = ScriptToolLoader.Load(toolsDir, opClassRegistry, healthState);

    /// <summary>Number of times <see cref="Refresh"/> has run, startup load excluded - lets tests confirm the <c>tools/</c> watcher's debounce coalesces a burst of filesystem events into one re-scan.</summary>
    public int RefreshCount { get; private set; }

    public void Refresh()
    {
        Tools = ScriptToolLoader.Load(toolsDir, opClassRegistry, healthState);
        RefreshCount++;
    }
}
