using System.Text.Json;
using VaultShared.Health;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Auth;

/// <summary>
/// Holds the parsed contents of grants.json.
/// </summary>
/// <param name="TokenRows">The token-keyed rows a request can authenticate against.</param>
/// <param name="NamedGrants">The token-less named grants, such as <c>admin</c>. They carry no token
/// and cannot authenticate a request directly; they exist only as rebind targets for elevation and
/// as the default-binding target for the localhost selector token.</param>
public sealed record GrantFileContents(
    IReadOnlyList<(byte[] Hash, Grant Grant)> TokenRows,
    IReadOnlyDictionary<string, Grant> NamedGrants)
{
    /// <summary>An empty parse result, which is what a whole-file failure returns.</summary>
    public static GrantFileContents Empty { get; } =
        new([], new Dictionary<string, Grant>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Parses and validates grants.json into the <c>(token-hash, grant)</c> rows a
/// <see cref="GrantStore"/> authenticates against, plus the token-less named grants it uses as
/// rebind and default-binding targets. This runs once, when the host loads configuration.
/// </summary>
/// <remarks>
/// <para>
/// It is separate from <see cref="GrantStore"/> so the runtime store - authentication, minting,
/// expiry, elevation binding - carries neither the JSON parser nor its dependency on
/// <see cref="ScopePath"/> for canonicalizing scope roots. The two share no state and have entirely
/// different lifetimes.
/// </para>
/// <para>
/// A row is a named grant when it carries <c>"named": true</c>. Such a row has no token - its object
/// key is ignored and its <c>id</c> field names it - so it never joins the authenticatable rows.
/// Every other row is token-keyed and hashed. Grant ids are unique across both kinds, and either
/// kind may carry the optional <c>"canRequestElevation": true</c> opt-in, which defaults to false.
/// </para>
/// <para>
/// A row-level validation failure - a bad ttl, missing or bad scopeRoots, an unknown op class, an
/// unknown vault, a duplicate id - skips that row and records a Degraded problem naming the grant,
/// while every other row loads. Only a whole-file failure is Fatal, and even then the loader records
/// the problem and returns an empty result so the process still starts.
/// </para>
/// </remarks>
public static class GrantFileLoader
{
    /// <summary>Loads and validates grants.json.</summary>
    /// <param name="path">The grants.json path.</param>
    /// <param name="opClassRegistry">Validates and canonicalizes the op-class names a row
    /// declares.</param>
    /// <param name="vaultConfig">Validates the vault names a row declares.</param>
    /// <param name="sink">
    /// Receives problems instead of the loader throwing. A whole-file failure - a missing file,
    /// unparseable JSON, a non-object root, an empty grant table - is recorded as
    /// <see cref="ProblemSeverity.Fatal"/>, and a row-level failure as
    /// <see cref="ProblemSeverity.Degraded"/> naming the grant.
    /// </param>
    /// <param name="now">
    /// The instant a numeric <c>ttl</c> is measured from. <see cref="GrantStore.Load"/> supplies it
    /// from its <see cref="TimeProvider"/>, so a test can control it without waiting on wall-clock
    /// time.
    /// </param>
    /// <returns>The parsed contents, or <see cref="GrantFileContents.Empty"/> where the whole file
    /// failed.</returns>
    public static GrantFileContents Load(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, IConfigProblemSink sink, DateTimeOffset now)
    {
        try
        {
            return LoadCore(path, opClassRegistry, vaultConfig, sink, now);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            sink.RecordFatal("GrantStore", ex.Message);
            return GrantFileContents.Empty;
        }
    }

    private static GrantFileContents LoadCore(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, IConfigProblemSink sink, DateTimeOffset now)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Grants config not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: root must be an object keyed by bearer token.");

        var rows = new List<(byte[] Hash, Grant Grant)>();
        var namedGrants = new Dictionary<string, Grant>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalEntries = 0;
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            totalEntries++;
            // Skip and report at row level: any InvalidDataException from this row's validation
            // records a Degraded problem and moves on to the next row. Only the structural checks
            // above the loop are whole-file failures.
            try
            {
                var grant = LoadRow(entry, path, opClassRegistry, vaultConfig, seenIds, now);
                if (IsNamedGrant(entry.Value))
                    namedGrants[grant.Id] = grant;
                else
                    rows.Add((GrantStore.Hash(entry.Name), grant));
            }
            catch (InvalidDataException ex)
            {
                sink.RecordDegraded("GrantStore", $"Grant row skipped - {ex.Message}");
            }
        }

        // A file with no rows at all is a structurally empty grant table, which is fatal.
        if (totalEntries == 0)
            throw new InvalidDataException($"{path}: must contain at least one grant.");

        // A file whose every row was skipped leaves an equally unusable grant table, so it is fatal
        // too rather than leaving the server merely degraded with nothing pointing at the logs. The
        // per-row Degraded records above already say why each was skipped. Named grants count as
        // loaded content, because a file of only named grants is usable through the localhost
        // selector's default binding.
        if (rows.Count == 0 && namedGrants.Count == 0)
            throw new InvalidDataException($"{path}: every grant row was skipped as invalid - see the per-row Degraded sink recorded above.");

        return new GrantFileContents(rows, namedGrants);
    }

    /// <summary>Reports whether a row is a token-less named grant.</summary>
    /// <param name="grant">The row.</param>
    /// <returns>True when the row carries <c>"named": true</c>.</returns>
    private static bool IsNamedGrant(JsonElement grant) =>
        grant.TryGetProperty("named", out var named) && named.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Validates and builds one grant from its row. A token-keyed row's object key is its bearer
    /// token; a named grant's key is ignored and its <c>id</c> field identifies it. The caller
    /// decides which collection the result goes into.
    /// </summary>
    /// <param name="entry">The row.</param>
    /// <param name="path">The grants.json path, quoted in messages.</param>
    /// <param name="opClassRegistry">Validates and canonicalizes op-class names.</param>
    /// <param name="vaultConfig">Validates vault names.</param>
    /// <param name="seenIds">Ids already used, to reject a duplicate.</param>
    /// <param name="now">The instant a numeric ttl is measured from.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="InvalidDataException">The row is invalid and should be skipped.</exception>
    private static Grant LoadRow(JsonProperty entry, string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, HashSet<string> seenIds, DateTimeOffset now)
    {
        var isNamed = IsNamedGrant(entry.Value);
        if (!isNamed && string.IsNullOrWhiteSpace(entry.Name))
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
        // "*" expands against the full configured set, including vaults that were themselves
        // skipped (see VaultConfig.ConfiguredVaultNames), and an explicit vault name is valid as
        // long as it is configured, even where its root is currently broken. A skipped vault is the
        // vault's problem rather than the grant's, so grants stay stable while a vault root is
        // temporarily missing. A name absent from the configured set skips the row, which is what
        // catches a vault rename that would otherwise leave a grant silently inert.
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
        var canRequestElevation = ReadCanRequestElevation(entry.Value, path, id);

        return new Grant(id, opClasses, vaults, expiresAtUtc, scopeRoots) { CanRequestElevation = canRequestElevation };
    }

    /// <summary>Parses the optional <c>canRequestElevation</c> opt-in.</summary>
    /// <param name="grant">The row.</param>
    /// <param name="path">The grants.json path, quoted in messages.</param>
    /// <param name="id">The grant id, quoted in messages.</param>
    /// <returns>The declared value, or false when the field is absent.</returns>
    /// <exception cref="InvalidDataException">The value is not a boolean, which skips the row like
    /// any other bad field.</exception>
    private static bool ReadCanRequestElevation(JsonElement grant, string path, string id)
    {
        if (!grant.TryGetProperty("canRequestElevation", out var prop))
            return false;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{path}: grant \"{id}\" has a non-boolean \"canRequestElevation\" value."),
        };
    }

    /// <summary>Reports whether a grant row's array is the wildcard.</summary>
    /// <param name="values">The array's values.</param>
    /// <returns>True when the array is exactly <c>["*"]</c>, which the loader expands to the full
    /// current set of registry op classes or configured vaults rather than validating as a literal
    /// name.</returns>
    private static bool IsWildcard(IReadOnlyList<string> values) => values is [ "*" ];

    /// <summary>Parses a grant row's required <c>ttl</c>.</summary>
    /// <param name="grant">The row.</param>
    /// <param name="path">The grants.json path, quoted in messages.</param>
    /// <param name="id">The grant id, quoted in messages.</param>
    /// <param name="now">The instant the minutes are measured from.</param>
    /// <returns>The absolute expiry, or null for the infinite <c>"*"</c> ttl. A fractional number of
    /// minutes is rejected, because <see cref="JsonElement.TryGetInt32"/> succeeds only for a whole
    /// number.</returns>
    /// <exception cref="InvalidDataException">The field is missing, not <c>"*"</c> or an integer, or
    /// not positive.</exception>
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

    /// <summary>Reads a required array of non-empty strings from a grant row.</summary>
    /// <param name="grant">The row.</param>
    /// <param name="property">The field name.</param>
    /// <param name="path">The grants.json path, quoted in messages.</param>
    /// <param name="id">The grant id, quoted in messages.</param>
    /// <returns>The values, in declared order.</returns>
    /// <exception cref="InvalidDataException">The field is missing, is not an array, or holds an
    /// empty entry.</exception>
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
    /// Parses a grant row's required <c>scopeRoots</c>, an object keyed by vault name such as
    /// <c>{ "primary": ["Area"] }</c>.
    /// </summary>
    /// <param name="grant">The row.</param>
    /// <param name="path">The grants.json path, quoted in messages.</param>
    /// <param name="id">The grant id, quoted in messages.</param>
    /// <param name="vaults">The vaults the grant reaches, every one of which must have an
    /// entry.</param>
    /// <returns>The per-vault scope.</returns>
    /// <exception cref="InvalidDataException">The field is missing or malformed, a key names a vault
    /// the grant does not reach, an entry is not a well-formed folder-prefix root, or an entry is
    /// missing for a vault.</exception>
    /// <remarks>
    /// <para>
    /// The field is required and must cover every vault in <paramref name="vaults"/>, the same
    /// treatment <c>ttl</c> gets. Scope has to be explicit, so a vault can never end up with full
    /// access because someone forgot to write an entry for it.
    /// </para>
    /// <para>
    /// A vault's value of exactly <c>["*"]</c> means that vault is unscoped. An empty list is an
    /// error rather than a second spelling of it: the broadest scope must not be the value a mistake
    /// produces.
    /// </para>
    /// <para>
    /// The whole-grant wildcard <c>{ "*": ["*"] }</c> means every vault in
    /// <paramref name="vaults"/>, fully open, and is expanded here into an explicit unscoped entry
    /// per vault, the same way <c>vaults: ["*"]</c> is expanded before this method runs. It must be
    /// the row's only <c>scopeRoots</c> key, and its value must be exactly <c>["*"]</c>.
    /// </para>
    /// <para>
    /// A key that is not one of the grant's own vaults skips the row. Scoping a vault the grant
    /// cannot access is almost always a typo or stale configuration, and skipping is louder than
    /// doing nothing.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, VaultScope> ReadScopeRoots(JsonElement grant, string path, string id, IReadOnlySet<string> vaults)
    {
        if (!grant.TryGetProperty("scopeRoots", out var prop))
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"scopeRoots\" object.");
        if (prop.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: grant \"{id}\" has a \"scopeRoots\" that is not an object keyed by vault name.");

        // The whole-grant wildcard { "*": ["*"] } expands to an explicit unscoped entry for every
        // vault the grant has, the scopeRoots equivalent of vaults: ["*"].
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
                VaultScope (_) => VaultScope.Unscoped,
                StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase);
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

            // Reject an empty list rather than reading it as whole-vault access. It is the value a
            // dropped entry, a list filtered to nothing, or a half-finished edit produces, so
            // reading it as the broadest possible scope would make the widest grant what an accident
            // defaults to. Whole-vault access is ["*"] and nothing else, so it cannot be arrived at
            // by losing something.
            if (raw.Count == 0)
                throw new InvalidDataException($"{path}: grant \"{id}\" has an empty \"scopeRoots\" list for vault \"{vaultName}\" - write [\"*\"] for whole-vault access, which is the only spelling of it.");

            if (IsWildcard(raw))
            {
                result[vaultName] = VaultScope.Unscoped;
                continue;
            }

            var canonicalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in raw)
            {
                try
                {
                    canonicalized.Add(string.Join('/', ScopePath.Normalize(entry)));
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException($"{path}: grant \"{id}\" has an invalid \"scopeRoots\" entry \"{entry}\" for vault \"{vaultName}\": {ex.Message}");
                }
            }
            result[vaultName] = VaultScope.Of(canonicalized);
        }

        var missingVaults = vaults.Where(v => !result.ContainsKey(v)).ToList();
        if (missingVaults.Count > 0)
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"scopeRoots\" entry for vault{(missingVaults.Count > 1 ? "s" : "")} {string.Join(", ", missingVaults.Select(v => $"\"{v}\""))}.");

        return result;
    }
}
