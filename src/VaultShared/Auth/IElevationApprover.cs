namespace VaultShared.Auth;

/// <summary>
/// The context an <see cref="IElevationApprover"/> reviews: who is asking, what they hold now,
/// what they want, for how long, and any session context worth showing a human approver. Carries
/// grant ids (never tokens) so it is safe to log and to surface in an approval prompt.
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

/// <summary>An approver's verdict. <paramref name="Reason"/> explains a denial (surfaced in the
/// tool error) and may annotate an approval.</summary>
public sealed record ElevationDecision(bool Approved, string? Reason)
{
    public static ElevationDecision Approve(string? reason = null) => new(true, reason);
    public static ElevationDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The pluggable approval seam for an elevation rebind (see the <c>request_elevation</c> tool).
/// A real provider (out-of-band push with scope + TTL + requesting session shown, so approval is
/// not reflexive theater) slots in behind this later without touching the rebind mechanism; the
/// shipped implementation is a config-selected stub. Only matters once a default-minimal
/// environment exists - on a dev machine the default binding is already admin, so elevation is
/// off the daily path.
/// </summary>
public interface IElevationApprover
{
    /// <summary>Reviews an elevation request and returns an approve/deny decision. Async so a
    /// real provider can await an out-of-band human response.</summary>
    Task<ElevationDecision> ReviewAsync(ElevationRequest request, CancellationToken cancellationToken);
}
