namespace VaultMcpServer.Tools;

/// <summary>
/// Holds the current set of script tools, loaded eagerly at construction (host startup).
/// <see cref="Refresh"/> re-scans the folder - the hook the tools/ file watcher calls to
/// pick up added, changed, or removed scripts without a restart.
/// </summary>
public sealed class ScriptToolRegistry(string toolsDir)
{
    public IReadOnlyList<ScriptTool> Tools { get; private set; } = ScriptToolLoader.Load(toolsDir);

    public void Refresh() => Tools = ScriptToolLoader.Load(toolsDir);
}
