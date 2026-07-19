namespace VaultMcpServer.Tools;

/// <summary>
/// Wraps the DI-registered <see cref="INativeTool"/> collection - mirrors
/// <see cref="ScriptToolRegistry"/>'s shape (an eagerly-materialized <c>Tools</c> list) without
/// its file-watching/<c>Refresh()</c>, since native tools are compiled into the assembly, not
/// discovered from disk. A second native tool is just another <see cref="INativeTool"/>
/// implementation plus a DI registration - no change needed here for that.
/// </summary>
public sealed class NativeToolRegistry(IEnumerable<INativeTool> tools)
{
    public IReadOnlyList<INativeTool> Tools { get; } = tools.ToList();
}
