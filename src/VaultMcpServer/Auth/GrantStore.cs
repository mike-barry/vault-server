using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Loads grants.json - the bearer-token grant table - and authenticates presented tokens
/// against it. Tokens are hashed (SHA-256) at load and never retained or logged in the
/// clear; authentication compares hashes with <see cref="CryptographicOperations.FixedTimeEquals"/>
/// against every row, with no early exit, so lookup time does not leak which row (if any)
/// matched.
///
/// Row-level validation failures (bad ttl, bad/missing scopeRoots, unknown opClass,
/// unknown vault, duplicate id, ...) skip just that row - recorded as a Degraded problem
/// naming the grant - while every other row loads normally. Only whole-file failures
/// (missing file, unparseable JSON, wrong root shape, an entirely empty grant table)
/// remain Fatal.
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

    /// <summary>An inert store with no grants - nobody can authenticate against it, the
    /// same "deny by default" posture <see cref="Auth.BearerGrantMiddleware"/> already
    /// applies to an unrecognized token. Used both as the return value of a failed
    /// <see cref="Load"/> and directly by <c>Program.cs</c> to skip a real grants.json
    /// parse entirely when an upstream <see cref="OpClassRegistry"/>/<see cref="VaultConfig"/>
    /// load has already gone fatal - validating real grant rows (vaults/opClasses) against
    /// an empty upstream registry would otherwise fail every row and pile a stack of
    /// confusing "unknown opClass"/"unknown vault" skips on top of the real root cause.
    /// </summary>
    public static GrantStore Empty(TimeProvider? timeProvider = null) => new([], timeProvider ?? TimeProvider.System);

    /// <param name="healthState">
    /// Whole-file failures (missing file, unparseable JSON, a non-object root, an empty
    /// grant table) are recorded here as a <see cref="ProblemSeverity.Fatal"/> problem
    /// instead of being thrown; the caller always gets back a usable (if empty, via
    /// <see cref="Empty"/>) instance. A row-level validation failure records a
    /// <see cref="ProblemSeverity.Degraded"/> problem naming the grant and skips just that
    /// row - every other row still loads. Required, deliberately with no default (and placed
    /// ahead of the optional <paramref name="timeProvider"/> so it can be): a defaulted
    /// throwaway instance would let a future production call site compile while silently
    /// discarding recorded failures - exactly the failure class this health-state redesign
    /// exists to eliminate. Callers that only exercise the success path (tests) pass a
    /// fresh local instance.
    /// </param>
    /// <param name="timeProvider">
    /// Defaults to <see cref="TimeProvider.System"/>. Overridable so tests can control the
    /// "now" a numeric <c>ttl</c> is measured from at load time, without depending on real
    /// elapsed wall-clock time.
    /// </param>
    public static GrantStore Load(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, ServerHealthState healthState, TimeProvider? timeProvider = null)
    {
        var resolvedTimeProvider = timeProvider ?? TimeProvider.System;
        try
        {
            return LoadCore(path, opClassRegistry, vaultConfig, healthState, resolvedTimeProvider);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            healthState.RecordFatal("GrantStore", ex.Message);
            return Empty(resolvedTimeProvider);
        }
    }

    private static GrantStore LoadCore(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, ServerHealthState healthState, TimeProvider resolvedTimeProvider)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Grants config not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: root must be an object keyed by bearer token.");

        var now = resolvedTimeProvider.GetUtcNow();
        var rows = new List<(byte[] Hash, Grant Grant)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalEntries = 0;
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            totalEntries++;
            // Row-level skip-and-report: any InvalidDataException from this row's validation
            // (the same checks that used to fail the whole file) records a Degraded problem
            // and moves on to the next row. Only the pre-loop structural checks above stay
            // whole-file Fatal.
            try
            {
                rows.Add(LoadRow(entry, path, opClassRegistry, vaultConfig, seenIds, now));
            }
            catch (InvalidDataException ex)
            {
                healthState.RecordDegraded("GrantStore", $"Grant row skipped - {ex.Message}");
            }
        }

        // Zero configured rows is a structurally-empty grant table and stays Fatal.
        if (totalEntries == 0)
            throw new InvalidDataException($"{path}: must contain at least one grant.");

        // Every row present but every one of them individually skipped (see the per-row
        // Degraded records already emitted above) leaves an unusable, effectively-empty
        // grant table too - functionally indistinguishable from the zero-entries case above
        // for BearerGrantMiddleware's purposes (nobody can authenticate against it), so it
        // gets the same Fatal treatment rather than leaving the server merely Degraded and
        // every caller silently 401ing with no "check the logs" signal.
        if (rows.Count == 0)
            throw new InvalidDataException($"{path}: every grant row was skipped as invalid - see the per-row Degraded problems recorded above.");

        return new GrantStore(rows, resolvedTimeProvider);
    }

    private static (byte[] Hash, Grant Grant) LoadRow(JsonProperty entry, string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, HashSet<string> seenIds, DateTimeOffset now)
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
        // "*" expands against the FULL configured set - resolvable and per-entry-skipped
        // vaults alike (see VaultConfig.ConfiguredVaultNames) - and an explicit vault name
        // is valid as long as it is configured, even if its root is currently broken: the
        // vault being skipped is the vault's problem, not the grant's, so grants stay
        // stable while a vault root is temporarily missing. A name absent from the
        // configured set entirely skips the row - this is what catches a vault rename
        // leaving a grant silently inert.
        var vaults = IsWildcard(vaultsRaw)
            ? vaultConfig.ConfiguredVaultNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : vaultsRaw.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!IsWildcard(vaultsRaw))
        {
            foreach (var vault in vaults)
            {
                if (!vaultConfig.IsConfigured(vault))
                    throw new InvalidDataException($"{path}: grant \"{id}\" names unknown vault \"{vault}\" - not in the configured vault set.");
            }
        }
        if (vaults.Count == 0)
            throw new InvalidDataException($"{path}: grant \"{id}\" has no vaults.");

        var expiresAtUtc = ReadTtl(entry.Value, path, id, now);
        var scopeRoots = ReadScopeRoots(entry.Value, path, id, vaults);

        return (Hash(token), new Grant(id, opClasses, vaults, expiresAtUtc, scopeRoots));
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
    /// access within it - the same "explicit, never implicit" convention <c>GrantStore.Load</c>
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
    /// Parses a grant row's required <c>scopeRoots</c>: an object keyed by vault name, e.g.
    /// <c>{ "primary": ["Area"], "test": [] }</c>. Required - and required to cover every vault
    /// in <paramref name="vaults"/> - the same treatment <c>ttl</c> gets from <see cref="ReadTtl"/>:
    /// scope must be explicit, so a vault can never silently end up with full access just
    /// because someone forgot to add a <c>scopeRoots</c> entry for it. A vault key's value being
    /// empty or exactly <c>["*"]</c> means that vault is explicitly unscoped (full access within
    /// it) - still a valid, common choice, just no longer the default for an absent key.
    ///
    /// The whole-grant wildcard <c>{ "*": ["*"] }</c> - mirroring the <c>"*"</c> convention
    /// already used by <c>opClasses</c>/<c>vaults</c>/<c>ttl</c> - means "every vault in
    /// <paramref name="vaults"/>, fully open" and is expanded here at load time into a concrete
    /// empty-set entry per vault, the same way <c>vaults: ["*"]</c> is expanded to the current
    /// configured vault set before this method ever runs. It must be the row's only
    /// <c>scopeRoots</c> key (mixing it with explicit per-vault entries is rejected as
    /// redundant/ambiguous) and its value must be exactly <c>["*"]</c>.
    ///
    /// A key that isn't one of the grant's own <paramref name="vaults"/> is a row-level error -
    /// scoping a vault the grant can't access at all is nonsensical and almost certainly a typo
    /// or stale config, so the row skips rather than silently doing nothing. A present,
    /// non-wildcard entry must canonicalize as a folder-prefix root (see
    /// <see cref="Vault.VaultAccess.NormalizeScopeSegments"/>) - rooted, escaping, or empty
    /// entries skip the row, the same rigor as an unknown opClass.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> ReadScopeRoots(JsonElement grant, string path, string id, IReadOnlySet<string> vaults)
    {
        if (!grant.TryGetProperty("scopeRoots", out var prop))
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"scopeRoots\" object.");
        if (prop.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: grant \"{id}\" has a \"scopeRoots\" that is not an object keyed by vault name.");

        // Whole-grant wildcard: { "*": ["*"] } expands to an explicit, empty (fully open) entry
        // for every vault the grant has - the scopeRoots equivalent of vaults: ["*"].
        if (prop.EnumerateObject().Any(p => p.Name == "*"))
        {
            if (prop.EnumerateObject().Count() != 1)
                throw new InvalidDataException($"{path}: grant \"{id}\" mixes the wildcard \"scopeRoots\" key \"*\" with explicit vault entries - use one or the other.");

            var wildcardValue = prop.GetProperty("*");
            var wildcardRaw = wildcardValue.ValueKind == JsonValueKind.Array
                ? wildcardValue.EnumerateArray().Select(item => item.GetString() ?? "").ToList()
                : throw new InvalidDataException($"{path}: grant \"{id}\" has a \"scopeRoots\" entry for vault \"*\" that is not an array.");
            if (!IsWildcard(wildcardRaw))
                throw new InvalidDataException($"{path}: grant \"{id}\" has a wildcard \"scopeRoots\" key \"*\" whose value must be exactly [\"*\"].");

            return vaults.ToDictionary(
                v => v,
                IReadOnlySet<string> (_) => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }

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

        var missingVaults = vaults.Where(v => !result.ContainsKey(v)).ToList();
        if (missingVaults.Count > 0)
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"scopeRoots\" entry for vault{(missingVaults.Count > 1 ? "s" : "")} {string.Join(", ", missingVaults.Select(v => $"\"{v}\""))}.");

        return result;
    }
}
