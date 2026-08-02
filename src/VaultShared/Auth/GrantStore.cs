using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VaultShared.Health;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Auth;

/// <summary>Why a presented bearer token failed <see cref="GrantStore.Authenticate"/>.</summary>
public enum AuthFailureReason
{
    /// <summary>No grant row matched the token.</summary>
    Unknown,

    /// <summary>A grant row matched, but its <c>ttl</c> has elapsed.</summary>
    Expired,
}

/// <summary>
/// The runtime bearer-token grant store: authenticates presented tokens against the
/// <c>(token-hash, grant)</c> rows it was built from, mints in-memory runtime grants, and
/// enforces expiry. Tokens are hashed (SHA-256) and never retained or logged in the clear;
/// authentication compares hashes with <see cref="CryptographicOperations.FixedTimeEquals"/>
/// against every row, with no early exit, so lookup time does not leak which row (if any)
/// matched.
///
/// Parsing and validating grants.json into those rows is a separate, startup-only concern that
/// lives in <see cref="GrantFileLoader"/> - this class holds no JSON-parsing logic and shares no
/// state with it. <see cref="Load"/> is a thin convenience that runs the loader and wraps the
/// result; callers that already have rows can use the constructor directly.
/// </summary>
public sealed class GrantStore
{
    private readonly IReadOnlyList<(byte[] Hash, Grant Grant)> _rows;
    private readonly TimeProvider _timeProvider;
    private readonly LocalhostAuthOptions _localhostOptions;

    /// <summary>
    /// Token-less named grants (e.g. <c>admin</c>) parsed from grants.json - the rebind and
    /// default-binding targets. Immutable and keyed by grant id; distinct from
    /// <see cref="_rows"/> since they carry no token and can never authenticate a request
    /// directly. Elevation grants freshly minted for a rebind (see
    /// <see cref="RebindToMintedGrant"/>) also register here so a live binding can resolve their
    /// id, but those are never valid targets for a <em>new</em> named-grant elevation request -
    /// use <see cref="TryGetNamedGrant"/> for that.
    /// </summary>
    private readonly ConcurrentDictionary<string, Grant> _grantsById;

    /// <summary>The subset of <see cref="_grantsById"/> ids that came from grants.json as named
    /// grants (not elevation-minted at runtime) - the only ids a new elevation request may name
    /// as a target.</summary>
    private readonly HashSet<string> _namedGrantIds;

    /// <summary>
    /// Grants minted at runtime via <see cref="Mint"/> - distinct from the file-loaded
    /// <see cref="_rows"/>, never written back to grants.json. A thread-safe append-only
    /// collection: entries are never removed on expiry (no background sweep), just treated as
    /// a non-match by <see cref="FindMatch"/> once past their TTL, same as an unknown token.
    /// Gone on process restart, same as any in-memory state.
    /// </summary>
    private readonly ConcurrentBag<(byte[] Hash, Grant Grant)> _mintedRows = new();

    /// <summary>
    /// The token-to-grant elevation binding table (the locked "binding shape"): a token-hash key
    /// (hex, see <see cref="HashKey"/>) to a <c>(grant id, expiresAt)</c> binding, mutated only by
    /// elevation (<see cref="RebindToNamedGrant"/>/<see cref="RebindToMintedGrant"/>) and consulted
    /// - and lazily reverted on expiry - inside <see cref="Authenticate"/>, the single
    /// authorization decision point. A binding is a pointer to an id, so expiry is a pointer revert
    /// to the principal's base binding, not un-mutation of any grant; grants stay immutable. The
    /// selector's default binding is <em>not</em> stored here - it is resolved fresh per request in
    /// <see cref="ResolveLocalhostGrant"/> from <see cref="LocalhostAuthOptions"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string GrantId, DateTimeOffset ExpiresAt)> _bindings = new();

    /// <summary>
    /// The built-in minimum-access floor: what an unrecognized, blank, or expired localhost token
    /// degrades to (see <see cref="ResolveLocalhostGrant"/>). No op classes and no vaults - so no
    /// content:read/content:write/grants:manage and no vault content - but callable tools that bypass the op-class
    /// check by name still work: <c>health_check</c> (diagnostics as a right of connection) and
    /// <c>request_elevation</c>. It opts into elevation so a floored session can ask to be
    /// elevated with approval - the whole point of a fail-closed floor that stays recoverable.
    /// </summary>
    public static Grant MinimumGrant { get; } = new(
        "minimum-access",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ExpiresAtUtc: null,
        new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase))
    { CanRequestElevation = true };

    /// <summary>
    /// Builds a store over an already-parsed set of <c>(token-hash, grant)</c> rows - the output
    /// of <see cref="GrantFileLoader.Load"/>. <paramref name="timeProvider"/> defaults to
    /// <see cref="TimeProvider.System"/>; override it so tests can control the "now" that grant
    /// expiry (and a minted grant's TTL) is measured against. <paramref name="namedGrants"/> are
    /// the token-less rebind/default-binding targets (empty when none); <paramref name="localhostOptions"/>
    /// governs the localhost selector/floor fallback (see <see cref="ResolveLocalhostGrant"/>),
    /// defaulting to the permissive-minimum posture.
    /// </summary>
    public GrantStore(
        IReadOnlyList<(byte[] Hash, Grant Grant)> rows,
        TimeProvider? timeProvider = null,
        IReadOnlyDictionary<string, Grant>? namedGrants = null,
        LocalhostAuthOptions? localhostOptions = null)
    {
        _rows = rows;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localhostOptions = localhostOptions ?? LocalhostAuthOptions.Default;
        _grantsById = new ConcurrentDictionary<string, Grant>(
            namedGrants ?? new Dictionary<string, Grant>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        _namedGrantIds = new HashSet<string>(_grantsById.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An inert store with no grants - nobody can authenticate against it, the
    /// same "deny by default" posture the MCP front-end's bearer middleware already
    /// applies to an unrecognized token. Used both as the return value of a failed
    /// <see cref="Load"/> and directly by <c>Program.cs</c> to skip a real grants.json
    /// parse entirely when an upstream <see cref="OpClassRegistry"/>/<see cref="VaultConfig"/>
    /// load has already gone fatal - validating real grant rows (vaults/opClasses) against
    /// an empty upstream registry would otherwise fail every row and pile a stack of
    /// confusing "unknown opClass"/"unknown vault" skips on top of the real root cause.
    /// </summary>
    public static GrantStore Empty(TimeProvider? timeProvider = null) => new([], timeProvider);

    /// <summary>
    /// Convenience factory: parses and validates grants.json via <see cref="GrantFileLoader.Load"/>
    /// and wraps the resulting rows in a store. Whole-file failures are recorded on
    /// <paramref name="healthState"/> and yield an empty (inert) store rather than throwing - see
    /// <see cref="GrantFileLoader"/> for the whole-file-Fatal / per-row-Degraded split. Callers that
    /// already hold parsed rows can bypass this and use the constructor.
    /// </summary>
    public static GrantStore Load(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, ServerHealthState healthState, TimeProvider? timeProvider = null, LocalhostAuthOptions? localhostOptions = null)
    {
        var resolvedTimeProvider = timeProvider ?? TimeProvider.System;
        var contents = GrantFileLoader.Load(path, opClassRegistry, vaultConfig, healthState, resolvedTimeProvider.GetUtcNow());
        return new GrantStore(contents.TokenRows, resolvedTimeProvider, contents.NamedGrants, localhostOptions);
    }

    /// <summary>
    /// Authenticates a presented bearer token, returning the matching grant or null. This is the
    /// sole authentication point - every authorization decision (op class, vault membership, scope
    /// containment) is made downstream against the grant this returns. It resolves authority in order:
    /// <list type="number">
    /// <item>an active elevation binding for this token (the binding table) - consulted here and
    /// lazily reverted on expiry, so a rebind wins over the token's base grant until it lapses;</item>
    /// <item>the token's base grant - a file-loaded row or a runtime-minted grant, excluding an
    /// expired one (treated as a non-match, same as an unknown token).</item>
    /// </list>
    /// Returns null when neither applies - an unknown, blank, or expired token. Callers gating a
    /// localhost request use <see cref="ResolveLocalhostGrant"/> instead, which layers the
    /// selector/minimum-floor fallback on top of this and never returns null. Use
    /// <see cref="GetAuthFailureReason"/> to distinguish "unknown" from "expired" for diagnostics
    /// once this returns null.
    /// </summary>
    public Grant? Authenticate(string presentedToken)
    {
        var now = _timeProvider.GetUtcNow();

        var bound = ResolveActiveBinding(presentedToken, now);
        if (bound is not null)
            return bound;

        var match = FindMatch(presentedToken);
        return match is null || match.IsExpired(now) ? null : match;
    }

    /// <summary>
    /// Resolves the grant a localhost request runs as - the fail-closed fallthrough that never
    /// errors, since the server is localhost-only. First tries <see cref="Authenticate"/> (an
    /// active binding or the token's real base grant); if that finds nothing, applies the localhost
    /// policy: a token equal to the effective selector (the strict expected token when configured,
    /// else the well-known selector) binds to the configured default named grant, and everything
    /// else - blank, unrecognized, expired, or the selector when no dev default is configured -
    /// degrades to the built-in <see cref="MinimumGrant"/>. Always returns a grant.
    /// </summary>
    public Grant ResolveLocalhostGrant(string presentedToken)
    {
        var authenticated = Authenticate(presentedToken);
        if (authenticated is not null)
            return authenticated;

        var selector = _localhostOptions.EffectiveSelectorToken;
        if (!string.IsNullOrEmpty(presentedToken)
            && !string.IsNullOrEmpty(selector)
            && CryptographicOperations.FixedTimeEquals(Hash(presentedToken), Hash(selector))
            && _localhostOptions.DefaultGrantId is { Length: > 0 } defaultId
            && _grantsById.TryGetValue(defaultId, out var defaultGrant)
            && !defaultGrant.IsExpired(_timeProvider.GetUtcNow()))
        {
            return defaultGrant;
        }

        return MinimumGrant;
    }

    /// <summary>
    /// Looks up a token-less named grant by id (the only valid targets for a new named-grant
    /// elevation request). Excludes runtime elevation-minted grants that also live in the by-id
    /// registry, and an expired named grant.
    /// </summary>
    public bool TryGetNamedGrant(string id, out Grant grant)
    {
        if (_namedGrantIds.Contains(id)
            && _grantsById.TryGetValue(id, out var found)
            && !found.IsExpired(_timeProvider.GetUtcNow()))
        {
            grant = found;
            return true;
        }
        grant = null!;
        return false;
    }

    /// <summary>The hex token-hash key the elevation binding table is keyed by. The middleware
    /// computes it from the presented bearer token and stashes it for the elevation tool, so the
    /// raw token never needs to reach a tool.</summary>
    public static string HashKey(string token) => Convert.ToHexString(Hash(token));

    /// <summary>
    /// Rebinds the principal identified by <paramref name="tokenHashKey"/> (see
    /// <see cref="HashKey"/>) to the named grant <paramref name="grantId"/> until
    /// <paramref name="expiresAt"/>. The caller (the elevation tool, post-approval) has already
    /// validated the target via <see cref="TryGetNamedGrant"/>. On expiry the binding is lazily
    /// reverted inside <see cref="Authenticate"/>, restoring the token's base grant.
    /// </summary>
    public void RebindToNamedGrant(string tokenHashKey, string grantId, DateTimeOffset expiresAt)
    {
        if (!_namedGrantIds.Contains(grantId))
            throw new ArgumentException($"Unknown named grant \"{grantId}\".", nameof(grantId));
        _bindings[tokenHashKey] = (grantId, expiresAt);
    }

    /// <summary>
    /// Registers a freshly-minted scoped grant as a runtime elevation target and rebinds the
    /// principal identified by <paramref name="tokenHashKey"/> to it until
    /// <paramref name="expiresAt"/>. Unlike <see cref="Mint"/> this issues no new bearer token -
    /// the caller's existing token resolves to the new authority for the binding's life, then
    /// reverts. The grant is <em>not</em> a valid target for a later named-grant elevation
    /// request. Op classes/vaults/scope roots are treated as already-validated concrete sets, same
    /// contract as <see cref="Mint"/>.
    /// </summary>
    public Grant RebindToMintedGrant(string tokenHashKey, string id, IReadOnlySet<string> opClasses, IReadOnlySet<string> vaults, IReadOnlyDictionary<string, VaultScope> scopeRoots, DateTimeOffset expiresAt)
    {
        var grant = new Grant(id, opClasses, vaults, expiresAt, scopeRoots);
        _grantsById[id] = grant;
        _bindings[tokenHashKey] = (id, expiresAt);
        return grant;
    }

    /// <summary>
    /// Consults the elevation binding table for <paramref name="presentedToken"/>, returning the
    /// bound grant when a binding is active, or null when there is none (or it has lapsed - reverted
    /// lazily here). Kept private and called only from <see cref="Authenticate"/> so the binding is
    /// never a second, competing authorization path.
    /// </summary>
    private Grant? ResolveActiveBinding(string presentedToken, DateTimeOffset now)
    {
        var key = HashKey(presentedToken);
        if (!_bindings.TryGetValue(key, out var binding))
            return null;

        if (now >= binding.ExpiresAt)
        {
            // Pointer revert to the base binding: drop the entry so future requests fall through
            // to the token's real grant (or the localhost floor).
            _bindings.TryRemove(key, out _);
            return null;
        }

        if (_grantsById.TryGetValue(binding.GrantId, out var grant) && !grant.IsExpired(now))
            return grant;

        // The target grant vanished or expired underneath the binding - treat as no binding.
        return null;
    }

    /// <summary>
    /// Diagnostic lookup for why <see cref="Authenticate"/> returned null: whether the token was
    /// unrecognized or matched an expired grant. Returns a reason, never a grant, so it cannot be
    /// used to authorize a call.
    /// </summary>
    public AuthFailureReason GetAuthFailureReason(string presentedToken) =>
        FindMatch(presentedToken) is { } match && match.IsExpired(_timeProvider.GetUtcNow())
            ? AuthFailureReason.Expired
            : AuthFailureReason.Unknown;

    /// <summary>
    /// Mints a new grant in memory and returns its bearer token - the only time the plaintext
    /// token is available; only its SHA-256 hash is retained, same as a file-loaded row. Held
    /// only in the in-memory <see cref="_mintedRows"/> collection, never persisted to
    /// grants.json - gone on process restart or once <paramref name="ttlMinutes"/> elapses
    /// (checked lazily by <see cref="Authenticate"/>/<see cref="GetAuthFailureReason"/>
    /// alongside the file-loaded rows), whichever comes first. Callers (see
    /// <c>MintTokenTool</c>) are responsible for validating <paramref name="opClasses"/>,
    /// <paramref name="vaults"/>, and <paramref name="scopeRoots"/> against the registry/config
    /// and for rejecting the <c>"*"</c> wildcard - this method treats them as already-validated,
    /// concrete sets, and it is also where the attenuation-only minting decision's TTL cap
    /// (see <c>MintTokenTool</c>) is applied. <paramref name="scopeRoots"/> is the per-vault shape
    /// stored directly on <see cref="Grant.ScopeRoots"/> - required, and required to have an entry
    /// for every vault in <paramref name="vaults"/> (<see cref="VaultScope.Unscoped"/> for a
    /// vault means full access within it - the same "explicit, never implicit" convention
    /// <c>GrantFileLoader</c> enforces for file-loaded grants). Checked defensively below rather
    /// than merely trusted, so a future caller that forgets to populate every vault's entry fails
    /// loudly instead of producing a grant with a silent full-access gap. Callers (see
    /// <c>MintTokenTool</c>) are responsible for grouping flat <c>scope_roots</c> entries into this
    /// shape and for validating attenuation (op classes/vaults/scope roots as subsets of the
    /// caller's own currently-bound grant) - this method only enforces the TTL side of
    /// attenuation, since it is the one dimension that needs this store's <see cref="_timeProvider"/>
    /// "now" to compute consistently with the rest of expiry handling.
    /// </summary>
    /// <param name="maxExpiresAtUtc">
    /// The caller's own grant's <see cref="Grant.ExpiresAtUtc"/> - attenuation-only minting caps
    /// the minted grant's absolute expiry at this value when it is finite (never null means the
    /// minter is infinite-TTL and imposes no cap). When the nominal expiry computed from
    /// <paramref name="ttlMinutes"/> would exceed it, the minted grant's actual expiry is clamped
    /// down to it instead of rejecting the mint outright - the returned <c>TtlCapped</c> flag tells
    /// the caller (<c>MintTokenTool</c>) this happened so it can report the effective expiry.
    /// </param>
    public (string Token, DateTimeOffset ExpiresAtUtc, bool TtlCapped) Mint(string id, IReadOnlySet<string> opClasses, IReadOnlySet<string> vaults, IReadOnlyDictionary<string, VaultScope> scopeRoots, int ttlMinutes, DateTimeOffset? maxExpiresAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Minted grant id must not be empty.", nameof(id));
        if (opClasses.Count == 0)
            throw new ArgumentException("Minted grant must have at least one op class.", nameof(opClasses));
        if (vaults.Count == 0)
            throw new ArgumentException("Minted grant must have at least one vault.", nameof(vaults));
        var missingScopeRootsVaults = vaults.Where(v => !scopeRoots.ContainsKey(v)).ToList();
        if (missingScopeRootsVaults.Count > 0)
            throw new ArgumentException(
                $"scopeRoots is missing an entry for vault{(missingScopeRootsVaults.Count > 1 ? "s" : "")} {string.Join(", ", missingScopeRootsVaults.Select(v => $"\"{v}\""))} - every vault the grant can access must have an explicit entry (VaultScope.Unscoped for full access).",
                nameof(scopeRoots));
        if (ttlMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(ttlMinutes), ttlMinutes, "ttl must be a positive number of minutes.");

        var token = GenerateToken();
        var nominalExpiresAtUtc = _timeProvider.GetUtcNow().AddMinutes(ttlMinutes);
        var ttlCapped = maxExpiresAtUtc is { } cap && nominalExpiresAtUtc > cap;
        var expiresAtUtc = ttlCapped ? maxExpiresAtUtc!.Value : nominalExpiresAtUtc;
        var grant = new Grant(id, opClasses, vaults, expiresAtUtc, scopeRoots);
        _mintedRows.Add((Hash(token), grant));
        return (token, expiresAtUtc, ttlCapped);
    }

    private static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private Grant? FindMatch(string presentedToken)
    {
        var presentedHash = Hash(presentedToken);
        Grant? match = null;
        foreach (var (hash, grant) in _rows)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, presentedHash))
                match = grant;
        }
        foreach (var (hash, grant) in _mintedRows)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, presentedHash))
                match = grant;
        }
        return match;
    }

    /// <summary>SHA-256 of a bearer token's UTF-8 bytes - the stored form for both file-loaded
    /// rows (produced by <see cref="GrantFileLoader"/>) and minted rows, so authentication can
    /// compare a presented token against either in one constant-time pass.</summary>
    internal static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
