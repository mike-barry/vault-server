using VaultMcpServer.Health;
using VaultShared.Configuration;

namespace VaultMcpServer.Tools;

/// <summary>
/// Holds the current set of script tools, loaded from the tools directory at construction.
/// <see cref="Refresh"/> re-scans the folder, and is what the tools directory watcher calls to
/// pick up added, changed, or removed scripts without a restart. The initial load and every
/// refresh record per-script validation problems into the replaceable
/// <see cref="ScriptToolLoader.HealthSource"/> set, so the recorded problems always describe the
/// folder's current state.
/// </summary>
/// <param name="toolsDir">The directory scanned for <c>.ps1</c> tool scripts.</param>
/// <param name="opClassRegistry">Validates the op class each script declares.</param>
/// <param name="healthState">Receives this scan's per-script validation problems.</param>
public sealed class ScriptToolRegistry(string toolsDir, OpClassRegistry opClassRegistry, ServerHealthState healthState)
{
    /// <summary>Gets the tools loaded by the most recent scan.</summary>
    public IReadOnlyList<ScriptTool> Tools { get; private set; } = ScriptToolLoader.Load(toolsDir, opClassRegistry, healthState);

    /// <summary>
    /// Gets the number of times <see cref="Refresh"/> has run, not counting the load at
    /// construction. Tests read it to confirm the watcher's debounce coalesces a burst of
    /// filesystem events into one re-scan.
    /// </summary>
    public int RefreshCount { get; private set; }

    /// <summary>Re-scans the tools directory and replaces <see cref="Tools"/> with the result.</summary>
    public void Refresh()
    {
        Tools = ScriptToolLoader.Load(toolsDir, opClassRegistry, healthState);
        RefreshCount++;
    }
}
