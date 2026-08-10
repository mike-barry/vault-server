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
/// Authenticates bearer tokens against the <c>(token-hash, grant)</c> rows it was built from, mints
/// in-memory runtime grants, and enforces expiry.
/// </summary>
/// <remarks>
/// Tokens are stored as SHA-256 hashes and never retained or logged in the clear. Authentication
/// compares hashes with <see cref="CryptographicOperations.FixedTimeEquals"/> against every row and
/// does not exit early on a match, so lookup time does not reveal which row matched, or whether one
/// did. Do not add an early return.
///
/// Parsing and validating grants.json into those rows is a startup-only concern that lives in
/// <see cref="GrantFileLoader"/>; this class holds no JSON parsing and shares no state with it.
/// <see cref="Load"/> runs the loader and wraps the result, and a caller that already holds rows can
/// use the constructor directly.
/// </remarks>
public sealed class GrantStore
{
    private readonly IReadOnlyList<(byte[] Hash, Grant Grant)> _rows;
    private readonly TimeProvider _timeProvider;
    private readonly LocalhostAuthOptions _localhostOptions;

    /// <summary>
    /// Token-less named grants parsed from grants.json, such as <c>admin</c>, keyed by grant id.
    /// These are the rebind and default-binding targets. They carry no token and can never
    /// authenticate a request directly, which is what separates them from the token rows. Grants
    /// minted for an elevation rebind register here too, so a live binding can resolve their id, but
    /// those are never valid targets for a new named-grant elevation request; use
    /// <see cref="TryGetNamedGrant"/> for that.
    /// </summary>
    private readonly ConcurrentDictionary<string, Grant> _grantsById;

    /// <summary>The ids that came from grants.json as named grants, rather than being minted for an
    /// elevation at runtime. These are the only ids a new elevation request may name as a
    /// target.</summary>
    private readonly HashSet<string> _namedGrantIds;

    /// <summary>
    /// Grants minted at runtime by <see cref="Mint"/>, held separately from the file-loaded rows and
    /// never written back to grants.json. The collection is thread-safe and append-only. Nothing
    /// sweeps expired entries, and <see cref="FindMatch"/> treats one past its TTL as a non-match,
    /// the same as an unknown token. They are gone on process restart.
    /// </summary>
    private readonly ConcurrentBag<(byte[] Hash, Grant Grant)> _mintedRows = new();

    /// <summary>
    /// The elevation binding table: a hex token-hash key (see <see cref="HashKey"/>) mapped to a
    /// grant id and an expiry. Only <see cref="RebindToNamedGrant"/> and
    /// <see cref="RebindToMintedGrant"/> write to it, and only <see cref="Authenticate"/> reads it,
    /// so a binding can never become a second authorization path. A binding is a pointer to a grant
    /// id, so expiry reverts the pointer to the principal's base grant rather than modifying any
    /// grant; grants stay immutable. The selector's default binding is not stored here.
    /// <see cref="ResolveLocalhostGrant"/> resolves that from <see cref="LocalhostAuthOptions"/> on
    /// each request.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string GrantId, DateTimeOffset ExpiresAt)> _bindings = new();

    /// <summary>
    /// The built-in minimum-access floor an unrecognized, blank, or expired localhost token degrades
    /// to. It holds no op classes and no vaults, so it reaches no vault content, but the tools that
    /// are not gated by op class still work: <c>health_check</c>, because diagnostics come with the
    /// connection, and <c>request_elevation</c>. It opts into elevation so that a session on the
    /// floor can still ask to be elevated with approval, which is what keeps a fail-closed floor
    /// recoverable.
    /// </summary>
    public static Grant MinimumGrant { get; } = new(
        "minimum-access",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ExpiresAtUtc: null,
        new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase))
    { CanRequestElevation = true };

    /// <summary>
    /// Builds a store over an already-parsed set of <c>(token-hash, grant)</c> rows, as
    /// <see cref="GrantFileLoader.Load"/> produces.
    /// </summary>
    /// <param name="rows">The token rows.</param>
    /// <param name="timeProvider">The clock grant expiry and minted TTLs are measured against.
    /// Defaults to <see cref="TimeProvider.System"/>; override it so a test can control "now".</param>
    /// <param name="namedGrants">The token-less rebind and default-binding targets, or null where
    /// there are none.</param>
    /// <param name="localhostOptions">Governs the localhost selector and floor fallback used by
    /// <see cref="ResolveLocalhostGrant"/>. Defaults to <see cref="LocalhostAuthOptions.Default"/>.</param>
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

    /// <summary>Creates an inert store holding no grants, so nothing can authenticate against
    /// it.</summary>
    /// <param name="timeProvider">The clock, or null for <see cref="TimeProvider.System"/>.</param>
    /// <returns>The empty store.</returns>
    /// <remarks>
    /// This is what a failed <see cref="Load"/> returns, and what the host uses to skip parsing
    /// grants.json when an upstream <see cref="OpClassRegistry"/> or <see cref="VaultConfig"/> load
    /// has already failed fatally. Validating real grant rows against an empty upstream registry
    /// would fail every row and bury the real cause under a stack of unknown-opClass and
    /// unknown-vault messages.
    /// </remarks>
    public static GrantStore Empty(TimeProvider? timeProvider = null) => new([], timeProvider);

    /// <summary>
    /// Parses and validates grants.json through <see cref="GrantFileLoader.Load"/> and wraps the
    /// resulting rows in a store. A caller that already holds parsed rows can use the constructor
    /// instead.
    /// </summary>
    /// <param name="path">The grants.json path.</param>
    /// <param name="opClassRegistry">Validates the op-class names each row declares.</param>
    /// <param name="vaultConfig">Validates the vault names each row declares.</param>
    /// <param name="sink">Receives the problems the load records.</param>
    /// <param name="timeProvider">The clock, or null for <see cref="TimeProvider.System"/>.</param>
    /// <param name="localhostOptions">The localhost selector and floor options.</param>
    /// <returns>The store, or an empty one where the whole file failed to load. A whole-file failure
    /// is recorded on <paramref name="sink"/> rather than thrown; see <see cref="GrantFileLoader"/>
    /// for which failures are fatal and which degrade one row.</returns>
    public static GrantStore Load(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, IConfigProblemSink sink, TimeProvider? timeProvider = null, LocalhostAuthOptions? localhostOptions = null)
    {
        var resolvedTimeProvider = timeProvider ?? TimeProvider.System;
        var contents = GrantFileLoader.Load(path, opClassRegistry, vaultConfig, sink, resolvedTimeProvider.GetUtcNow());
        return new GrantStore(contents.TokenRows, resolvedTimeProvider, contents.NamedGrants, localhostOptions);
    }

    /// <summary>
    /// Authenticates a presented bearer token. This is the only authentication point; every
    /// authorization decision - op class, vault membership, scope containment - is made downstream
    /// against the grant this returns.
    /// </summary>
    /// <param name="presentedToken">The bearer token as presented.</param>
    /// <returns>An active elevation binding's grant where one exists, otherwise the token's own
    /// grant from a file-loaded or minted row, or null where the token is unknown, blank, or its
    /// grant has expired. An expired grant is a non-match, the same as an unknown token.</returns>
    /// <remarks>
    /// An elevation binding is checked first and wins over the token's own grant until it lapses; a
    /// lapsed binding is reverted here. A caller gating a localhost request uses
    /// <see cref="ResolveLocalhostGrant"/>, which layers the selector and minimum floor on top of
    /// this and never returns null. Once this returns null,
    /// <see cref="GetAuthFailureReason"/> distinguishes unknown from expired for diagnostics.
    /// </remarks>
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
    /// Resolves the grant a localhost request runs as. This never errors, because the server is
    /// localhost-only.
    /// </summary>
    /// <param name="presentedToken">The bearer token as presented.</param>
    /// <returns>Whatever <see cref="Authenticate"/> returns, otherwise the configured default named
    /// grant where the token equals the effective selector, otherwise
    /// <see cref="MinimumGrant"/>. Always a grant.</returns>
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
    /// Looks up a token-less named grant by id. These are the only valid targets for a new
    /// named-grant elevation request.
    /// </summary>
    /// <param name="id">The grant id.</param>
    /// <param name="grant">Receives the grant on success.</param>
    /// <returns>True where a named grant with that id exists and has not expired. An
    /// elevation-minted grant that shares the by-id registry does not qualify.</returns>
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

    /// <summary>Computes the hex token-hash key the elevation binding table is keyed by.</summary>
    /// <param name="token">The bearer token.</param>
    /// <returns>The key. The middleware computes it from the presented token and stashes it for the
    /// elevation tool, so the raw token never has to reach a tool.</returns>
    public static string HashKey(string token) => Convert.ToHexString(Hash(token));

    /// <summary>
    /// Rebinds a principal to a named grant until an expiry. The caller - the elevation tool, after
    /// approval - has already validated the target through <see cref="TryGetNamedGrant"/>. On expiry
    /// the binding is reverted inside <see cref="Authenticate"/>, restoring the token's own grant.
    /// </summary>
    /// <param name="tokenHashKey">The principal's key from <see cref="HashKey"/>.</param>
    /// <param name="grantId">The named grant to bind to.</param>
    /// <param name="expiresAt">When the binding lapses.</param>
    /// <exception cref="ArgumentException">No named grant has that id.</exception>
    public void RebindToNamedGrant(string tokenHashKey, string grantId, DateTimeOffset expiresAt)
    {
        if (!_namedGrantIds.Contains(grantId))
            throw new ArgumentException($"Unknown named grant \"{grantId}\".", nameof(grantId));
        _bindings[tokenHashKey] = (grantId, expiresAt);
    }

    /// <summary>
    /// Registers a freshly minted scoped grant as a runtime elevation target and rebinds a principal
    /// to it until an expiry.
    /// </summary>
    /// <param name="tokenHashKey">The principal's key from <see cref="HashKey"/>.</param>
    /// <param name="id">The id to register the new grant under.</param>
    /// <param name="opClasses">The op classes the grant holds, already validated by the caller.</param>
    /// <param name="vaults">The vaults the grant reaches, already validated by the caller.</param>
    /// <param name="scopeRoots">The per-vault scope, already validated by the caller.</param>
    /// <param name="expiresAt">When the grant and the binding lapse.</param>
    /// <returns>The new grant.</returns>
    /// <remarks>
    /// Unlike <see cref="Mint"/>, this issues no new bearer token. The caller's existing token
    /// resolves to the new authority for the binding's life and then reverts. The grant is not a
    /// valid target for a later named-grant elevation request. As with <see cref="Mint"/>, the op
    /// classes, vaults and scope roots are treated as already-validated concrete sets.
    /// </remarks>
    public Grant RebindToMintedGrant(string tokenHashKey, string id, IReadOnlySet<string> opClasses, IReadOnlySet<string> vaults, IReadOnlyDictionary<string, VaultScope> scopeRoots, DateTimeOffset expiresAt)
    {
        var grant = new Grant(id, opClasses, vaults, expiresAt, scopeRoots);
        _grantsById[id] = grant;
        _bindings[tokenHashKey] = (id, expiresAt);
        return grant;
    }

    /// <summary>
    /// Consults the elevation binding table for a token. This stays private and is called only from
    /// <see cref="Authenticate"/>, so a binding is never a second, competing authorization path.
    /// </summary>
    /// <param name="presentedToken">The bearer token as presented.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The bound grant where a binding is active, otherwise null. A lapsed binding is
    /// removed here.</returns>
    private Grant? ResolveActiveBinding(string presentedToken, DateTimeOffset now)
    {
        var key = HashKey(presentedToken);
        if (!_bindings.TryGetValue(key, out var binding))
            return null;

        if (now >= binding.ExpiresAt)
        {
            // Drop the entry so later requests fall through to the token's own grant, or to the
            // localhost floor.
            _bindings.TryRemove(key, out _);
            return null;
        }

        if (_grantsById.TryGetValue(binding.GrantId, out var grant) && !grant.IsExpired(now))
            return grant;

        // The target grant vanished or expired underneath the binding; treat that as no binding.
        return null;
    }

    /// <summary>
    /// Reports why <see cref="Authenticate"/> returned null, for diagnostics.
    /// </summary>
    /// <param name="presentedToken">The bearer token as presented.</param>
    /// <returns>Whether the token was unrecognized or matched an expired grant. This returns a
    /// reason and never a grant, so it cannot be used to authorize a call.</returns>
    public AuthFailureReason GetAuthFailureReason(string presentedToken) =>
        FindMatch(presentedToken) is { } match && match.IsExpired(_timeProvider.GetUtcNow())
            ? AuthFailureReason.Expired
            : AuthFailureReason.Unknown;

    /// <summary>
    /// Mints a new grant in memory and returns its bearer token. This is the only point at which the
    /// plaintext token exists; only its SHA-256 hash is kept, the same as for a file-loaded row.
    /// </summary>
    /// <param name="id">The id for the new grant.</param>
    /// <param name="opClasses">The op classes the grant holds, as an already-validated concrete
    /// set.</param>
    /// <param name="vaults">The vaults the grant reaches, as an already-validated concrete set.</param>
    /// <param name="scopeRoots">The per-vault scope stored on <see cref="Grant.ScopeRoots"/>. It must
    /// carry an entry for every vault in <paramref name="vaults"/>;
    /// <see cref="VaultScope.Unscoped"/> means full access within that vault. This method checks the
    /// coverage rather than trusting it, so a caller that forgets an entry fails loudly instead of
    /// producing a grant with a silent full-access gap.</param>
    /// <param name="ttlMinutes">How long the grant lasts, in minutes.</param>
    /// <param name="maxExpiresAtUtc">
    /// The minting grant's own <see cref="Grant.ExpiresAtUtc"/>. Where it is finite and the expiry
    /// computed from <paramref name="ttlMinutes"/> would exceed it, the new grant's expiry is clamped
    /// down to it rather than the mint being refused, and the returned <c>TtlCapped</c> flag says so
    /// that the caller can report the effective expiry. Null means the minter has an infinite TTL and
    /// imposes no cap.
    /// </param>
    /// <returns>The plaintext token, the effective expiry, and whether the TTL was capped.</returns>
    /// <exception cref="ArgumentException">The id is blank, the op classes or vaults are empty, or
    /// <paramref name="scopeRoots"/> is missing a vault's entry.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ttlMinutes"/> is not
    /// positive.</exception>
    /// <remarks>
    /// A minted grant lives only in memory and is never written to grants.json. It disappears on
    /// process restart, or once its TTL elapses, whichever comes first;
    /// <see cref="Authenticate"/> and <see cref="GetAuthFailureReason"/> check expiry alongside the
    /// file-loaded rows.
    ///
    /// The caller validates the op classes, vaults and scope roots against the registry and
    /// configuration, rejects the <c>"*"</c> wildcard, groups flat <c>scope_roots</c> entries into
    /// the per-vault shape, and checks that each is a subset of its own currently-bound grant. This
    /// method enforces only the TTL side of attenuation, because that is the one dimension that needs
    /// this store's clock to stay consistent with the rest of expiry handling.
    /// </remarks>
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

    /// <summary>Finds the grant a token matches, across the file-loaded and minted rows.</summary>
    /// <param name="presentedToken">The bearer token as presented.</param>
    /// <returns>The matching grant, expired or not, or null where none matched.</returns>
    /// <remarks>Both loops run to completion rather than returning on the first match, so the time
    /// taken does not reveal which row matched, or whether one did.</remarks>
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

    /// <summary>Hashes a bearer token.</summary>
    /// <param name="token">The bearer token.</param>
    /// <returns>SHA-256 of the token's UTF-8 bytes. This is the stored form for both file-loaded rows
    /// and minted rows, so authentication compares a presented token against either in one
    /// constant-time pass.</returns>
    internal static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
