using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VaultShared.Auth;

namespace VaultMcpServer.Auth;

/// <summary>
/// Denies every elevation request and logs it. This is the default
/// <see cref="IElevationApprover"/>, so a server with no out-of-band approval provider configured
/// fails closed. The log line records the grant ids and TTL of the attempt, never a token.
/// </summary>
/// <remarks>
/// This type is constructed before <c>builder.Build()</c>, when no <see cref="ILogger"/> can be
/// resolved yet, so it starts with a no-op logger and <c>Program.cs</c> calls
/// <see cref="AttachLogger"/> once the service provider exists.
/// </remarks>
public sealed class AutoDenyElevationApprover : IElevationApprover
{
    private ILogger _logger = NullLogger.Instance;

    /// <summary>Sets the logger used to record denied elevation attempts.</summary>
    /// <param name="logger">The logger to write denial warnings to.</param>
    public void AttachLogger(ILogger logger) => _logger = logger;

    /// <summary>Logs the request and denies it.</summary>
    /// <param name="request">The elevation request to review.</param>
    /// <param name="cancellationToken">Not used; the decision is made without waiting.</param>
    /// <returns>A completed task holding a denial decision.</returns>
    public Task<ElevationDecision> ReviewAsync(ElevationRequest request, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Elevation auto-denied: principal={PrincipalGrantId} current={CurrentGrantId} requested={RequestedGrantId} ttlMinutes={TtlMinutes}",
            request.PrincipalGrantId, request.CurrentGrant.Id, request.RequestedGrantId, request.TtlMinutes);
        return Task.FromResult(ElevationDecision.Deny(
            "No approval provider is configured; elevation is auto-denied. Set up an approver to allow rebinding."));
    }
}
