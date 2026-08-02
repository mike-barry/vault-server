using System.Text.Json;
using ModelContextProtocol.Protocol;
using VaultShared.Auth;
using VaultShared.Health;
using VaultShared.Configuration;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>
/// Native tool (<see cref="INativeTool"/>) that reports the server's current configuration
/// health: whether it is fatally degraded, the configured/skipped vault sets, and every
/// recorded <see cref="HealthProblem"/> in detail. Zero arguments.
///
/// Diagnostics are a right of connection, not a privilege gated by op class: any
/// authenticated grant can call this tool regardless of what op classes it holds, and
/// regardless of what <see cref="OpClass"/> happens to report here (it is never consulted for
/// this tool - see <see cref="ScriptToolDispatcher.CallToolAsync"/>'s <c>health_check</c>
/// special case). The tool is read-only, so bypassing the usual <c>grant.Allows(...)</c>
/// check carries no write/side-effect risk.
///
/// Settled user decision: absolute filesystem paths that appear in a problem's
/// <see cref="HealthProblem.Detail"/> (e.g. a vault root or tool script path) flow verbatim
/// into this tool's JSON output - there is no separate, sanitized rendering path.
/// </summary>
public sealed class HealthCheckTool(ServerHealthState healthState, VaultConfig vaultConfig) : INativeTool
{
    /// <summary>The fixed tool name <see cref="ScriptToolDispatcher"/> matches on to bypass op-class enforcement.</summary>
    public const string ToolName = "health_check";

    public string Name => ToolName;

    public string Description =>
        "Reports the server's current configuration health: whether it is fatally degraded " +
        "(every request rejected with 503), the configured/skipped vault sets, and every " +
        "recorded configuration problem in detail. Callable by any authenticated grant " +
        "regardless of op class - takes no arguments.";

    // Never consulted: ScriptToolDispatcher.CallToolAsync special-cases health_check past the
    // grant.Allows(tool.OpClass) check entirely, since diagnostics are a right of connection
    // rather than a privilege. Left blank rather than a real op-class name so nothing could
    // ever mistake this for an enforced value if the special case were ever bypassed by
    // accident - an empty string never matches Grant.Allows's canonicalized op-class set.
    public string OpClass => "";

    // No arguments, so there is no record to derive a schema from and none is invented: the shared
    // no-arguments schema is the whole contract. See NativeToolArguments.None.
    public JsonElement InputSchema => NativeToolArguments.None;

    public Task<CallToolResult> InvokeAsync(IReadOnlyDictionary<string, JsonElement> arguments, Grant grant, CancellationToken cancellationToken)
    {
        var problems = healthState.Problems
            .Select(p => new
            {
                severity = p.Severity.ToString(),
                source = p.Source,
                detail = p.Detail,
                recordedAtUtc = p.RecordedAtUtc,
            })
            .ToList();

        var responseJson = JsonSerializer.Serialize(new
        {
            isFatallyDegraded = healthState.IsFatallyDegraded,
            vaults = new
            {
                configured = vaultConfig.Vaults.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                skipped = vaultConfig.SkippedVaults,
                defaultVault = vaultConfig.DefaultVault,
            },
            problems,
        });
        return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = responseJson }] });
    }
}
