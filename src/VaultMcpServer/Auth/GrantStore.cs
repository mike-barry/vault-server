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
            var scopeRoots = ReadScopeRoots(entry.Value, path, id, vaults);

            rows.Add((Hash(token), new Grant(id, opClasses, vaults, expiresAtUtc, scopeRoots)));
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

    private Grant? FindMatch(string presentedToken)
    {
        var presentedHash = Hash(presentedToken);
        Grant? match = null;
        foreach (var (hash, grant) in _rows)
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

    /// <summary>
    /// Parses a grant row's optional <c>scopeRoots</c>: an object keyed by vault name, e.g.
    /// <c>{ "primary": ["Area"], "test": [] }</c>. Absent entirely returns <c>null</c> -
    /// unscoped for every vault the grant has. A vault key's value being empty or exactly
    /// <c>["*"]</c> means that vault is unscoped, the same "no explicit scope means unscoped"
    /// convention as before, now applied per-vault rather than to the whole grant. Unlike
    /// <c>opClasses</c>/<c>vaults</c>/<c>ttl</c>, this field is optional rather than required:
    /// most rows (including every phase-1 row) have no reason to be scoped at all.
    ///
    /// A key that isn't one of the grant's own <paramref name="vaults"/> is a load-time error -
    /// scoping a vault the grant can't access at all is nonsensical and almost certainly a typo
    /// or stale config, so it fails fast rather than silently doing nothing. A present,
    /// non-wildcard entry must canonicalize as a folder-prefix root (see
    /// <see cref="Vault.VaultAccess.NormalizeScopeSegments"/>) - rooted, escaping, or empty
    /// entries fail the grant load fast, the same rigor as an unknown opClass.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>>? ReadScopeRoots(JsonElement grant, string path, string id, IReadOnlySet<string> vaults)
    {
        if (!grant.TryGetProperty("scopeRoots", out var prop))
            return null;
        if (prop.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: grant \"{id}\" has a \"scopeRoots\" that is not an object keyed by vault name.");

        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var vaultEntry in prop.EnumerateObject())
        {
            var vaultName = vaultEntry.Name;
            if (!vaults.Contains(vaultName))
                throw new InvalidDataException($"{path}: grant \"{id}\" has \"scopeRoots\" for vault \"{vaultName}\", which is not in its \"vaults\".");

            if (vaultEntry.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{path}: grant \"{id}\" has a \"scopeRoots\" entry for vault \"{vaultName}\" that is not an array.");

            var raw = new List<string>();
            foreach (var item in vaultEntry.Value.EnumerateArray())
            {
                if (item.GetString() is not { Length: > 0 } value)
                    throw new InvalidDataException($"{path}: grant \"{id}\" has an empty entry in \"scopeRoots\" for vault \"{vaultName}\".");
                raw.Add(value);
            }

            if (IsWildcard(raw))
            {
                result[vaultName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var canonicalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in raw)
            {
                try
                {
                    canonicalized.Add(string.Join('/', Vault.VaultAccess.NormalizeScopeSegments(entry)));
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException($"{path}: grant \"{id}\" has an invalid \"scopeRoots\" entry \"{entry}\" for vault \"{vaultName}\": {ex.Message}");
                }
            }
            result[vaultName] = canonicalized;
        }
        return result;
    }
}
