using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VaultMcpServer.Auth;

/// <summary>
/// The honest default <see cref="IElevationApprover"/>: denies every request and logs it. With no
/// real out-of-band approval provider wired up, auto-deny is the safe posture - an unattended or
/// floored session cannot escalate just because nothing was configured to say no. The log line
/// records that an elevation was requested (grant ids and TTL, never a token) so the attempt is
/// visible.
///
/// Constructed before <c>builder.Build()</c> alongside the native tools, so no <see cref="ILogger"/>
/// is resolvable yet - defaults to a no-op logger and <c>Program.cs</c> calls
/// <see cref="AttachLogger"/> once the service provider exists, mirroring
/// <c>ScriptToolDispatcher</c>.
/// </summary>
public sealed class AutoDenyElevationApprover : IElevationApprover
{
    private ILogger _logger = NullLogger.Instance;

    public void AttachLogger(ILogger logger) => _logger = logger;

    public Task<ElevationDecision> ReviewAsync(ElevationRequest request, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Elevation auto-denied: principal={PrincipalGrantId} current={CurrentGrantId} requested={RequestedGrantId} ttlMinutes={TtlMinutes}",
            request.PrincipalGrantId, request.CurrentGrant.Id, request.RequestedGrantId, request.TtlMinutes);
        return Task.FromResult(ElevationDecision.Deny(
            "No approval provider is configured; elevation is auto-denied. Set up an approver to allow rebinding."));
    }
}
