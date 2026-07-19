using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Auth;

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

    /// <summary>
    /// Grants minted at runtime via <see cref="Mint"/> - distinct from the file-loaded
    /// <see cref="_rows"/>, never written back to grants.json. A thread-safe append-only
    /// collection: entries are never removed on expiry (no background sweep), just treated as
    /// a non-match by <see cref="FindMatch"/> once past their TTL, same as an unknown token.
    /// Gone on process restart, same as any in-memory state.
    /// </summary>
    private readonly ConcurrentBag<(byte[] Hash, Grant Grant)> _mintedRows = new();

    /// <summary>
    /// Builds a store over an already-parsed set of <c>(token-hash, grant)</c> rows - the output
    /// of <see cref="GrantFileLoader.Load"/>. <paramref name="timeProvider"/> defaults to
    /// <see cref="TimeProvider.System"/>; override it so tests can control the "now" that grant
    /// expiry (and a minted grant's TTL) is measured against.
    /// </summary>
    public GrantStore(IReadOnlyList<(byte[] Hash, Grant Grant)> rows, TimeProvider? timeProvider = null)
    {
        _rows = rows;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>An inert store with no grants - nobody can authenticate against it, the
    /// same "deny by default" posture <see cref="Auth.BearerGrantMiddleware"/> already
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
    public static GrantStore Load(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, ServerHealthState healthState, TimeProvider? timeProvider = null)
    {
        var resolvedTimeProvider = timeProvider ?? TimeProvider.System;
        var rows = GrantFileLoader.Load(path, opClassRegistry, vaultConfig, healthState, resolvedTimeProvider.GetUtcNow());
        return new GrantStore(rows, resolvedTimeProvider);
    }

    /// <summary>
    /// Authenticates a presented bearer token, returning the matching grant or null. An expired
    /// grant (see <see cref="Grant.IsExpired"/>) is treated as a non-match, same as an unknown
    /// token - this is the sole authorization decision point. Use
    /// <see cref="GetAuthFailureReason"/> to distinguish "unknown" from "expired" for
    /// diagnostics once this returns null.
    /// </summary>
    public Grant? Authenticate(string presentedToken)
    {
        var match = FindMatch(presentedToken);
        return match is null || match.IsExpired(_timeProvider.GetUtcNow()) ? null : match;
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
    /// concrete sets. <paramref name="scopeRoots"/> is the per-vault shape stored directly on
    /// <see cref="Grant.ScopeRoots"/> - required, and required to have an entry for every vault
    /// in <paramref name="vaults"/> (an empty set for a vault means that vault is unscoped, full
    /// access within it - the same "explicit, never implicit" convention <c>GrantFileLoader</c>
    /// enforces for file-loaded grants). Checked defensively below rather than merely trusted,
    /// so a future caller that forgets to populate every vault's entry fails loudly instead of
    /// producing a grant with a silent full-access gap. Callers (see <c>MintTokenTool</c>) are
    /// responsible for grouping flat <c>scope_roots</c> entries into this shape.
    /// </summary>
    public string Mint(string id, IReadOnlySet<string> opClasses, IReadOnlySet<string> vaults, IReadOnlyDictionary<string, IReadOnlySet<string>> scopeRoots, int ttlMinutes)
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
                $"scopeRoots is missing an entry for vault{(missingScopeRootsVaults.Count > 1 ? "s" : "")} {string.Join(", ", missingScopeRootsVaults.Select(v => $"\"{v}\""))} - every vault the grant can access must have an explicit entry (empty set for full access).",
                nameof(scopeRoots));
        if (ttlMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(ttlMinutes), ttlMinutes, "ttl must be a positive number of minutes.");

        var token = GenerateToken();
        var expiresAtUtc = _timeProvider.GetUtcNow().AddMinutes(ttlMinutes);
        var grant = new Grant(id, opClasses, vaults, expiresAtUtc, scopeRoots);
        _mintedRows.Add((Hash(token), grant));
        return token;
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
