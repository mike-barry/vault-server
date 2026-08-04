using VaultShared.Health;
using VaultShared.Configuration;

namespace VaultShared.Tools;

/// <summary>
/// Reports the calling process's configuration health: whether it is fatally degraded, the
/// configured and skipped vault sets, and every recorded <see cref="HealthProblem"/>.
/// </summary>
/// <remarks>
/// <para>
/// The report describes the process that ran the tool, read through <see cref="IHealthState"/> rather
/// than a server-specific type. The CLI therefore reports the configuration load of the invocation
/// the caller is making, and the server reports the one its host started with. A front-end that
/// refuses to start on a fatal problem never reports one here, because any process that can answer
/// got past its own configuration load.
/// </para>
/// <para>
/// The declaration is <see cref="Floor.SelfGated"/>, so any grant may call this whatever op classes
/// it holds. Diagnostics come with the connection rather than with an op class, and the tool is
/// read-only.
/// </para>
/// <para>
/// Absolute filesystem paths that appear in a problem's <see cref="HealthProblem.Detail"/>, such as a
/// vault root or a tool script path, are reported verbatim. There is deliberately no sanitized
/// rendering path.
/// </para>
/// </remarks>
/// <param name="healthState">The running process's recorded configuration health.</param>
/// <param name="vaultConfig">The configured and skipped vaults to report.</param>
public sealed class HealthCheckTool(IHealthState healthState, VaultConfig vaultConfig) : ITool
{
    public const string ToolName = "health_check";

    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Reports this process's current configuration health: whether it is fatally degraded " +
            "(the server rejects every request with 503; the CLI fails before any tool runs), the " +
            "configured/skipped vault sets, and every recorded configuration problem in detail. " +
            "Callable by any grant regardless of op class - takes no arguments.",
        Arguments = typeof(NoArguments),
        OpClassFloor = new Floor.SelfGated(),
    };

    /// <summary>
    /// Represents one recorded configuration problem. A record rather than an anonymous type, so its
    /// wire names come from the shared naming policy exactly as an argument's do.
    /// </summary>
    /// <param name="Severity">The problem's severity.</param>
    /// <param name="Source">What recorded the problem.</param>
    /// <param name="Detail">The problem's description, including any absolute paths.</param>
    /// <param name="RecordedAtUtc">When the problem was recorded.</param>
    public sealed record ProblemReport(string Severity, string Source, string Detail, DateTimeOffset RecordedAtUtc);

    /// <summary>Represents the vault portion of the report.</summary>
    /// <param name="Configured">The names of the vaults that loaded, ordered case-insensitively.</param>
    /// <param name="Skipped">Each vault that was skipped, mapped to why.</param>
    /// <param name="DefaultVault">The vault used when a call names none.</param>
    public sealed record VaultReport(IReadOnlyList<string> Configured, IReadOnlyDictionary<string, string> Skipped, string DefaultVault);

    /// <summary>Represents the whole health report.</summary>
    /// <param name="IsFatallyDegraded">Whether the process is fatally degraded.</param>
    /// <param name="Vaults">The configured and skipped vaults.</param>
    /// <param name="Problems">Every recorded problem.</param>
    public sealed record HealthReport(bool IsFatallyDegraded, VaultReport Vaults, IReadOnlyList<ProblemReport> Problems);

    /// <inheritdoc/>
    public ToolResult Invoke(ToolCall call) => new ToolResult.Data(new HealthReport(
        healthState.IsFatallyDegraded,
        new VaultReport(
            [.. vaultConfig.Vaults.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)],
            vaultConfig.SkippedVaults,
            vaultConfig.DefaultVault),
        [.. healthState.Problems.Select(p => new ProblemReport(p.Severity.ToString(), p.Source, p.Detail, p.RecordedAtUtc))]));
}
