using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Loads grants.json - the bearer-token grant table - and authenticates presented tokens
/// against it. Tokens are hashed (SHA-256) at load and never retained or logged in the
/// clear; authentication compares hashes with <see cref="CryptographicOperations.FixedTimeEquals"/>
/// against every row, with no early exit, so lookup time does not leak which row (if any)
/// matched.
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

    private GrantStore(IReadOnlyList<(byte[] Hash, Grant Grant)> rows, TimeProvider timeProvider)
    {
        _rows = rows;
        _timeProvider = timeProvider;
    }

    /// <param name="timeProvider">
    /// Defaults to <see cref="TimeProvider.System"/>. Overridable so tests can control the
    /// "now" a numeric <c>ttl</c> is measured from at load time, without depending on real
    /// elapsed wall-clock time.
    /// </param>
    public static GrantStore Load(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, TimeProvider? timeProvider = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Grants config not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: root must be an object keyed by bearer token.");

        var resolvedTimeProvider = timeProvider ?? TimeProvider.System;
        var now = resolvedTimeProvider.GetUtcNow();
        var rows = new List<(byte[] Hash, Grant Grant)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var token = entry.Name;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException($"{path}: empty token.");

            if (!entry.Value.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
                throw new InvalidDataException($"{path}: a grant is missing a non-empty \"id\".");
            if (!seenIds.Add(id))
                throw new InvalidDataException($"{path}: duplicate grant id \"{id}\".");

            var opClassesRaw = ReadStringSet(entry.Value, "opClasses", path, id).ToList();
            var opClasses = IsWildcard(opClassesRaw)
                ? opClassRegistry.Names.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : opClassesRaw
                    .Select(s => opClassRegistry.Canonicalize(s)
                        ?? throw new InvalidDataException($"{path}: grant \"{id}\" has unknown opClass \"{s}\"."))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (opClasses.Count == 0)
                throw new InvalidDataException($"{path}: grant \"{id}\" has no opClasses.");

            var vaultsRaw = ReadStringSet(entry.Value, "vaults", path, id).ToList();
            var vaults = IsWildcard(vaultsRaw)
                ? vaultConfig.Vaults.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : vaultsRaw.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (vaults.Count == 0)
                throw new InvalidDataException($"{path}: grant \"{id}\" has no vaults.");

            var expiresAtUtc = ReadTtl(entry.Value, path, id, now);

            rows.Add((Hash(token), new Grant(id, opClasses, vaults, expiresAtUtc)));
        }

        if (rows.Count == 0)
            throw new InvalidDataException($"{path}: must contain at least one grant.");

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
    /// <see cref="Grant.ScopeRoots"/> - <c>null</c> for unscoped-everywhere, or a dictionary
    /// whose absent keys mean unscoped for that vault; callers (see <c>MintTokenTool</c>) are
    /// responsible for grouping flat <c>scope_roots</c> entries into this shape.
    /// </summary>
    public string Mint(string id, IReadOnlySet<string> opClasses, IReadOnlySet<string> vaults, IReadOnlyDictionary<string, IReadOnlySet<string>>? scopeRoots, int ttlMinutes)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Minted grant id must not be empty.", nameof(id));
        if (opClasses.Count == 0)
            throw new ArgumentException("Minted grant must have at least one op class.", nameof(opClasses));
        if (vaults.Count == 0)
            throw new ArgumentException("Minted grant must have at least one vault.", nameof(vaults));
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

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>True when a grant row's array is exactly <c>["*"]</c> - expanded at load time
    /// to the full current set of registry op-classes / configured vaults, rather than being
    /// validated as a literal name.</summary>
    private static bool IsWildcard(IReadOnlyList<string> values) => values is [ "*" ];

    /// <summary>
    /// Parses a grant row's required <c>ttl</c>: <c>"*"</c> for infinite (returns null), or an
    /// integer number of minutes from <paramref name="now"/> (fractional values are rejected -
    /// <see cref="JsonElement.TryGetInt32"/> only succeeds for a whole number).
    /// </summary>
    private static DateTimeOffset? ReadTtl(JsonElement grant, string path, string id, DateTimeOffset now)
    {
        if (!grant.TryGetProperty("ttl", out var prop))
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"ttl\" value.");

        if (prop.ValueKind == JsonValueKind.String)
        {
            if (prop.GetString() != "*")
                throw new InvalidDataException($"{path}: grant \"{id}\" has an invalid \"ttl\" - expected \"*\" or an integer number of minutes.");
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var minutes))
        {
            if (minutes <= 0)
                throw new InvalidDataException($"{path}: grant \"{id}\" has a non-positive \"ttl\" of {minutes} minutes.");
            return now.AddMinutes(minutes);
        }

        throw new InvalidDataException($"{path}: grant \"{id}\" has an invalid \"ttl\" - expected \"*\" or an integer number of minutes.");
    }

    private static IEnumerable<string> ReadStringSet(JsonElement grant, string property, string path, string id)
    {
        if (!grant.TryGetProperty(property, out var prop) || prop.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"{property}\" array.");
        foreach (var item in prop.EnumerateArray())
        {
            if (item.GetString() is not { Length: > 0 } value)
                throw new InvalidDataException($"{path}: grant \"{id}\" has an empty entry in \"{property}\".");
            yield return value;
        }
    }
}
