using VaultShared.Scope;

namespace VaultShared.Auth;

/// <summary>
/// Represents one grants.json row: an identity plus the op classes and vaults it may operate
/// against. Op-class names are validated against
/// <see cref="VaultShared.Configuration.OpClassRegistry"/> at load time by <see cref="GrantStore"/>
/// rather than being constrained by the types here.
/// </summary>
/// <param name="Id">The grant's identity.</param>
/// <param name="OpClasses">The op classes the grant allows.</param>
/// <param name="Vaults">The vaults the grant may operate in.</param>
/// <param name="ExpiresAtUtc">
/// The absolute expiry instant, computed at load time from the row's <c>ttl</c> in minutes, or null
/// for an infinite <c>"*"</c> ttl.
/// </param>
/// <param name="ScopeRoots">
/// Per-vault path scope, keyed by vault name and compared case-insensitively. This restricts what
/// the grant reaches within a vault it already has access to, which
/// <paramref name="Vaults"/> decides. Read it through <see cref="ScopeIn"/> rather than by key.
/// </param>
/// <remarks>
/// <para>
/// <paramref name="ScopeRoots"/> is always fully populated, whether the grant was loaded from a file
/// or minted at runtime: every vault in <paramref name="Vaults"/> has a key here. A
/// <c>grants.json</c> row that omits <c>scopeRoots</c>, or that lists a vault without a matching
/// <c>scopeRoots</c> key, fails to load rather than silently granting that vault full access, and
/// <see cref="GrantStore.Mint"/> enforces the same coverage. There is no missing-key or missing-map
/// state for a caller to interpret.
/// </para>
/// <para>
/// Whole-vault access is <see cref="VaultScope.Unscoped"/> and nothing else. The configuration spells
/// it <c>["*"]</c>, and the whole-grant wildcard <c>{ "*": ["*"] }</c> expands to one such entry per
/// vault, the same way <c>vaults: ["*"]</c> expands. An empty root set is neither representable in
/// the model nor accepted by the loader, so whole-vault authority cannot be produced by accident at
/// either layer.
/// </para>
/// <para>
/// An empty or blank scope-root string is malformed input rather than a matches-everything sentinel;
/// see <see cref="ScopePath.Normalize"/>.
/// </para>
/// </remarks>
public sealed record Grant(string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults, DateTimeOffset? ExpiresAtUtc, IReadOnlyDictionary<string, VaultScope> ScopeRoots)
{
    /// <summary>
    /// Whether a session bound to this grant may request an elevation rebind, through the
    /// <c>request_elevation</c> tool and <see cref="GrantStore"/>'s binding table.
    /// </summary>
    /// <remarks>
    /// This is a per-grant opt-in and defaults to false, so self-escalation is denied unless a grant
    /// explicitly allows it. <see cref="GrantStore.Mint"/> never sets it, so an automated subagent
    /// can never self-escalate. A file-loaded token row or token-less named grant opts in with
    /// <c>"canRequestElevation": true</c>, and the built-in minimum-access floor
    /// (<see cref="GrantStore.MinimumGrant"/>) sets it so a floored session can still ask to be
    /// elevated with approval. It is init-only so that only opt-in construction sets it and the
    /// positional constructor sites stay unchanged.
    /// </remarks>
    public bool CanRequestElevation { get; init; }

    /// <summary>Reports whether the grant allows an op class.</summary>
    /// <param name="op">The op class name.</param>
    /// <returns>True where the grant holds it.</returns>
    public bool Allows(string op) => OpClasses.Contains(op);

    /// <summary>Reports whether the grant may operate in a vault.</summary>
    /// <param name="vault">The vault name.</param>
    /// <returns>True where the grant reaches it.</returns>
    public bool CanAccessVault(string vault) => Vaults.Contains(vault);

    /// <summary>Reports whether the grant has expired.</summary>
    /// <param name="now">The instant to compare against.</param>
    /// <returns>True once <paramref name="now"/> has reached <see cref="ExpiresAtUtc"/>. Always false
    /// for an infinite <c>"*"</c> ttl.</returns>
    public bool IsExpired(DateTimeOffset now) => ExpiresAtUtc is { } expiresAt && now >= expiresAt;

    /// <summary>
    /// Returns this grant's path scope in one vault, which is the shape every containment check
    /// takes.
    /// </summary>
    /// <param name="vault">The vault to read the scope for.</param>
    /// <returns>The scope. A grant that reaches the whole vault holds
    /// <see cref="VaultScope.Unscoped"/> for it; nothing here means "no restriction".</returns>
    /// <exception cref="InvalidOperationException">The grant has no entry for that vault, which means
    /// the coverage invariant was violated upstream.</exception>
    /// <remarks>
    /// Only the entry keyed to <paramref name="vault"/> is ever consulted, which is what stops a
    /// scope root configured for one vault leaking into another vault the same grant can access.
    /// </remarks>
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
