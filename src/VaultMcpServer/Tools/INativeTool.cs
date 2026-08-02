using System.Text.Json;
using ModelContextProtocol.Protocol;
using VaultMcpServer.Auth;

namespace VaultMcpServer.Tools;

/// <summary>
/// A compiled MCP tool with direct access to host services (e.g. <see cref="Auth.GrantStore"/>),
/// as opposed to a <see cref="ScriptTool"/> discovered from a PowerShell script under
/// <c>tools/</c>. Implementations register as <c>IEnumerable&lt;INativeTool&gt;</c> singletons
/// in Program.cs (<c>services.AddSingleton&lt;INativeTool, MintTokenTool&gt;()</c>);
/// <see cref="NativeToolRegistry"/> just wraps that collection - no file-watching, since
/// native tools are compiled, not hot-reloaded.
///
/// Mirrors <see cref="ScriptTool"/>'s Name/Description/OpClass/InputSchema shape closely
/// enough that <see cref="ScriptToolDispatcher"/> can list and dispatch both uniformly.
/// The gating is <em>not</em> uniform, and a new implementation inherits less than it looks:
/// <see cref="ScriptToolDispatcher.CallToolAsync"/> exempts <c>health_check</c> and
/// <c>request_elevation</c> from the <c>grant.Allows(tool.OpClass)</c> check by name, and its
/// vault-membership and unadvertised-argument checks run on the script branch only, which a
/// native tool returns before reaching. A native tool that targets a vault gates itself.
/// </summary>
public interface INativeTool
{
    string Name { get; }
    string Description { get; }

    /// <summary>Op-class name this tool requires. Unlike a <see cref="ScriptTool"/>'s, it is not
    /// canonicalized against <see cref="VaultMcpServer.Vault.OpClassRegistry"/> at load - nothing
    /// reads it but <c>grant.Allows</c>, so a name the registry does not define fails no check and
    /// simply never matches.</summary>
    string OpClass { get; }

    /// <summary>The tool's JSON Schema `inputSchema`, as required by the MCP `tools/list` response.</summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Executes the tool for an already-authenticated caller, op-class-checked unless this tool is
    /// one the dispatcher exempts by name (see the remarks above).
    /// <paramref name="grant"/> is the same <see cref="Grant"/> <see cref="ScriptToolDispatcher.CallToolAsync"/>
    /// read off <c>HttpContext.Items</c> and checked <c>Allows(OpClass)</c> against before
    /// invoking - passed through so an implementation can consult its own fields (e.g. the
    /// grant id, for auditing) without re-resolving it itself.
    /// </summary>
    Task<CallToolResult> InvokeAsync(IReadOnlyDictionary<string, JsonElement> arguments, Grant grant, CancellationToken cancellationToken);
}
