namespace VaultShared.Auth;

/// <summary>
/// Represents the context an <see cref="IElevationApprover"/> reviews: who is asking, what they hold
/// now, what they want, for how long, and any session context worth showing a human approver. It
/// carries grant ids and never tokens, so it is safe to log and to show in an approval prompt.
/// </summary>
/// <param name="PrincipalGrantId">The id of the grant the requesting session currently resolves
/// to (its base or currently-bound authority).</param>
/// <param name="CurrentGrant">The grant the requesting session currently holds.</param>
/// <param name="RequestedGrantId">The id of the grant being requested (a named grant, or the
/// generated id of a freshly-minted scoped grant).</param>
/// <param name="RequestedGrant">The grant being requested.</param>
/// <param name="TtlMinutes">How long the rebind would last if approved.</param>
/// <param name="SessionContext">Optional free-text context the caller supplied about the
/// requesting session, for a human approver to judge - never trusted for authorization.</param>
public sealed record ElevationRequest(
    string PrincipalGrantId,
    Grant CurrentGrant,
    string RequestedGrantId,
    Grant RequestedGrant,
    int TtlMinutes,
    string? SessionContext);

/// <summary>Represents an approver's verdict.</summary>
/// <param name="Approved">Whether the elevation was approved.</param>
/// <param name="Reason">Explains a denial, which the tool error surfaces, and may annotate an
/// approval.</param>
public sealed record ElevationDecision(bool Approved, string? Reason)
{
    /// <summary>Creates an approval.</summary>
    /// <param name="reason">Optional annotation.</param>
    /// <returns>The decision.</returns>
    public static ElevationDecision Approve(string? reason = null) => new(true, reason);

    /// <summary>Creates a denial.</summary>
    /// <param name="reason">Why the request was denied.</param>
    /// <returns>The decision.</returns>
    public static ElevationDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Reviews elevation rebind requests raised by the <c>request_elevation</c> tool.
/// </summary>
/// <remarks>
/// This interface exists so a real approval provider - an out-of-band prompt showing the scope, the
/// TTL and the requesting session, so approval is a decision rather than a reflex - can be added
/// later without changing the rebind mechanism. The shipped implementation is a stub selected by
/// configuration. It only matters once an environment defaults to minimum access; on a development
/// machine the default binding is already admin, so elevation is off the daily path.
/// </remarks>
public interface IElevationApprover
{
    /// <summary>Reviews an elevation request.</summary>
    /// <param name="request">What is being asked for, and by whom.</param>
    /// <param name="cancellationToken">Cancels the review.</param>
    /// <returns>The approve-or-deny decision. The method is asynchronous so a real provider can await
    /// an out-of-band human response.</returns>
    Task<ElevationDecision> ReviewAsync(ElevationRequest request, CancellationToken cancellationToken);
}
