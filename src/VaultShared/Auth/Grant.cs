using VaultShared.Scope;

namespace VaultShared.Auth;

/// <summary>
/// A grants.json row: an identity plus the op classes and vaults it may operate against.
/// Op-class names are validated against <see cref="VaultShared.Configuration.OpClassRegistry"/>
/// at load time (see <see cref="GrantStore"/>), not constrained by the C# type here.
/// </summary>
/// <param name="ExpiresAtUtc">
/// Absolute expiry instant computed at load time from the row's <c>ttl</c> (minutes from the
/// load-time "now"), or <c>null</c> for a <c>"*"</c> (infinite) ttl.
/// </param>
/// <param name="ScopeRoots">
/// Per-vault path scope, distinct from <paramref name="Vaults"/> - path scope restricts *within*
/// a vault the grant already has <see cref="Vaults"/> access to. Keyed by vault name
/// (case-insensitive); each value is a <see cref="VaultScope"/>, which is either unscoped or a
/// non-empty set of folder-prefix roots (vault-relative, forward-slash, canonicalized at load
/// time). Also the shape used for grants minted at runtime by <see cref="GrantStore.Mint"/>.
/// Read it through <see cref="ScopeIn"/> rather than by key, which is where the coverage
/// invariant below is enforced.
///
/// Required and always fully populated, for both a file-loaded grant (see
/// <see cref="GrantStore.Load"/>) and one minted at runtime (see <see cref="GrantStore.Mint"/>):
/// every vault in <see cref="Vaults"/> must have a corresponding key here, so this map is never
/// <c>null</c> and never missing a vault's entry - a <c>grants.json</c> row that omits
/// <c>scopeRoots</c> entirely, or that lists a vault in <c>vaults</c> without a matching
/// <c>scopeRoots</c> key, fails to load rather than silently granting that vault full access,
/// and <see cref="GrantStore.Mint"/> enforces the same coverage at runtime. There is no
/// "missing key" or "missing map" state to interpret.
///
/// Whole-vault access is <see cref="VaultScope.Unscoped"/> and nothing else: the configuration
/// spells it <c>["*"]</c>, and the whole-grant wildcard <c>{ "*": ["*"] }</c> expands to one such
/// entry per vault, mirroring how <c>vaults: ["*"]</c> itself expands. An empty root set is
/// neither representable in the model nor accepted by the loader, so no caller can produce
/// whole-vault authority by accident at either layer.
///
/// An empty/blank scope-root string is not a valid element of a root set; it is treated as
/// malformed input, not as a "matches everything" sentinel - see
/// <see cref="ScopePath.Normalize"/>.
/// </param>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc, IReadOnlyDictionary<string, VaultScope> ScopeRoots)
{
    /// <summary>
    /// Whether a session bound to this grant may request an elevation rebind (see
    /// <see cref="GrantStore"/>'s binding table and the <c>request_elevation</c> tool).
    /// A deliberate per-grant opt-in, defaulting to <c>false</c> - self-escalation is denied
    /// unless a grant explicitly allows it. Runtime-minted grants (<see cref="GrantStore.Mint"/>)
    /// never set it, so an automated subagent can never self-escalate; a file-loaded token row or
    /// token-less named grant opts in via a <c>"canRequestElevation": true</c> field, and the
    /// built-in minimum-access floor (<see cref="GrantStore.MinimumGrant"/>) sets it so a floored
    /// session can still ask to be elevated with approval. Init-only so the many existing positional
    /// <c>new Grant(...)</c> sites stay unchanged and only opt-in construction sets it.
    /// </summary>
    public bool CanRequestElevation { get; init; }

    public bool Allows(string op) => OpClasses.Contains(op);

    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>True once <paramref name="now"/> has reached the grant's <see cref="ExpiresAtUtc"/>; always false for an infinite (<c>"*"</c>) ttl.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;

    /// <summary>
    /// This grant's path scope in <paramref name="vault"/> - the already-vault-scoped shape every
    /// containment check takes. This is where "a scope root configured for one vault must not leak
    /// into another vault the same grant can access" is enforced: only the entry keyed to
    /// <paramref name="vault"/> is ever consulted.
    ///
    /// A vault with no entry throws. Every grant carries an entry for every vault it can access
    /// (see <see cref="ScopeRoots"/>), so a missing key means that invariant was violated
    /// upstream. There is no absent-scope case to answer, and nothing here denotes "no
    /// restriction" - a grant that means the whole vault holds <see cref="VaultScope.Unscoped"/>
    /// for it.
    /// </summary>
    public VaultScope ScopeIn(string vault)
    {
        foreach (var (key, scope) in ScopeRoots)
        {
            if (string.Equals(key, vault, StringComparison.OrdinalIgnoreCase))
                return scope;
        }

        throw new InvalidOperationException(
            $"Grant configuration invariant violated: vault \"{vault}\" has no scope entry in grant \"{Id}\". " +
            "Every vault a grant can access must have an explicit entry - this should be impossible for any grant built through GrantStore.Load or GrantStore.Mint.");
    }
}
