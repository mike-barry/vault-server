using VaultMcpServer.Health;
using VaultShared.Health;
using VaultShared.Configuration;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>
/// Reports the server's current configuration health: whether it is fatally degraded, the
/// configured/skipped vault sets, and every recorded <see cref="HealthProblem"/> in detail. Zero
/// arguments.
///
/// Diagnostics are a right of connection, not a privilege gated by op class, so the declaration is
/// <see cref="Floor.SelfGated"/>: any authenticated grant can call this regardless of what op classes
/// it holds. The tool is read-only, so that carries no write or side-effect risk.
///
/// Settled user decision: absolute filesystem paths that appear in a problem's
/// <see cref="HealthProblem.Detail"/> (e.g. a vault root or tool script path) flow verbatim into this
/// tool's output - there is no separate, sanitized rendering path.
/// </summary>
public sealed class HealthCheckTool(ServerHealthState healthState, VaultConfig vaultConfig) : ITool
{
    public const string ToolName = "health_check";

    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Reports the server's current configuration health: whether it is fatally degraded " +
            "(every request rejected with 503), the configured/skipped vault sets, and every " +
            "recorded configuration problem in detail. Callable by any authenticated grant " +
            "regardless of op class - takes no arguments.",
        Arguments = typeof(NoArguments),
        OpClassFloor = new Floor.SelfGated(),
    };

    /// <summary>
    /// The reported problem, as a record rather than an anonymous type so the wire names come from
    /// the shared naming policy exactly as an argument's do.
    /// </summary>
    public sealed record ProblemReport(string Severity, string Source, string Detail, DateTimeOffset RecordedAtUtc);

    public sealed record VaultReport(IReadOnlyList<string> Configured, IReadOnlyDictionary<string, string> Skipped, string DefaultVault);

    public sealed record HealthReport(bool IsFatallyDegraded, VaultReport Vaults, IReadOnlyList<ProblemReport> Problems);

    public ToolResult Invoke(ToolCall call) => new ToolResult.Data(new HealthReport(
        healthState.IsFatallyDegraded,
        new VaultReport(
            [.. vaultConfig.Vaults.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)],
            vaultConfig.SkippedVaults,
            vaultConfig.DefaultVault),
        [.. healthState.Problems.Select(p => new ProblemReport(p.Severity.ToString(), p.Source, p.Detail, p.RecordedAtUtc))]));
}
