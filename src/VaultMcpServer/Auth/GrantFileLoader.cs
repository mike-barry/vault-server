using System.Text.Json;
using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Auth;

/// <summary>
/// The parsed contents of grants.json: the token-keyed authenticatable rows plus the token-less
/// named grants. Named grants (e.g. <c>admin</c>) carry no token and cannot authenticate a
/// request directly - they exist only as rebind targets for elevation and as the default-binding
/// target for the localhost selector token (see <see cref="GrantStore"/> and
/// <see cref="LocalhostAuthOptions"/>).
/// </summary>
public sealed record GrantFileContents(
    IReadOnlyList<(byte[] Hash, Grant Grant)> TokenRows,
    IReadOnlyDictionary<string, Grant> NamedGrants)
{
    /// <summary>An empty parse result - the whole-file-failure return, mirroring the empty row
    /// list the loader returned before named grants existed.</summary>
    public static GrantFileContents Empty { get; } =
        new([], new Dictionary<string, Grant>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Parses and validates grants.json into the <c>(token-hash, grant)</c> rows a
/// <see cref="GrantStore"/> authenticates against, plus the token-less named grants it uses as
/// rebind and default-binding targets. Everything here is startup-only: it runs once when the
/// host loads config and never again. Split out of <see cref="GrantStore"/> so the runtime store
/// (authentication, minting, expiry, elevation binding) stays free of the JSON parser and its
/// dependency on <see cref="Vault.VaultAccess"/> for scope-root canonicalization - the two have
/// no shared state and entirely different lifetimes.
///
/// A row is a <em>named grant</em> when it carries <c>"named": true</c> - it has no token (its
/// object key is ignored as a token; the <c>id</c> field names it), so it never joins the
/// authenticatable rows. Every other row is a token-keyed grant, hashed and authenticatable as
/// before. Grant ids are unique across both kinds. Both may carry an optional
/// <c>"canRequestElevation": true</c> opt-in (default false).
///
/// Row-level validation failures (bad ttl, bad/missing scopeRoots, unknown opClass, unknown
/// vault, duplicate id, ...) skip just that row - recorded as a Degraded problem naming the
/// grant - while every other row loads normally. Only whole-file failures (missing file,
/// unparseable JSON, wrong root shape, an entirely empty grant table) are Fatal; on one, this
/// records the Fatal problem and returns an empty result so the process still starts.
/// </summary>
public static class GrantFileLoader
{
    /// <param name="healthState">
    /// Whole-file failures (missing file, unparseable JSON, a non-object root, an empty grant
    /// table) are recorded here as a <see cref="ProblemSeverity.Fatal"/> problem instead of being
    /// thrown; the caller always gets back a usable (if empty) row list. A row-level validation
    /// failure records a <see cref="ProblemSeverity.Degraded"/> problem naming the grant and skips
    /// just that row - every other row still loads.
    /// </param>
    /// <param name="now">
    /// The "now" a numeric <c>ttl</c> is measured from at load time - the caller
    /// (<see cref="GrantStore.Load"/>) supplies it from its <see cref="TimeProvider"/> so tests can
    /// control it without depending on real elapsed wall-clock time.
    /// </param>
    public static GrantFileContents Load(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, ServerHealthState healthState, DateTimeOffset now)
    {
        try
        {
            return LoadCore(path, opClassRegistry, vaultConfig, healthState, now);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            healthState.RecordFatal("GrantStore", ex.Message);
            return GrantFileContents.Empty;
        }
    }

    private static GrantFileContents LoadCore(string path, OpClassRegistry opClassRegistry, VaultConfig vaultConfig, ServerHealthState healthState, DateTimeOffset now)
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
            // Row-level skip-and-report: any InvalidDataException from this row's validation
            // (the same checks that used to fail the whole file) records a Degraded problem
            // and moves on to the next row. Only the pre-loop structural checks above stay
            // whole-file Fatal.
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
                healthState.RecordDegraded("GrantStore", $"Grant row skipped - {ex.Message}");
            }
        }

        // Zero configured rows is a structurally-empty grant table and stays Fatal.
        if (totalEntries == 0)
            throw new InvalidDataException($"{path}: must contain at least one grant.");

        // Every row present but every one of them individually skipped (see the per-row
        // Degraded records already emitted above) leaves an unusable, effectively-empty
        // grant table too - functionally indistinguishable from the zero-entries case above
        // for BearerGrantMiddleware's purposes (nothing loaded at all), so it gets the same
        // Fatal treatment rather than leaving the server merely Degraded with no "check the
        // logs" signal. Named grants count as loaded content: a file of only named grants is
        // usable via the localhost selector's default binding.
        if (rows.Count == 0 && namedGrants.Count == 0)
            throw new InvalidDataException($"{path}: every grant row was skipped as invalid - see the per-row Degraded problems recorded above.");

        return new GrantFileContents(rows, namedGrants);
    }

    /// <summary>A row is a token-less named grant when it carries <c>"named": true</c>.</summary>
    private static bool IsNamedGrant(JsonElement grant) =>
        grant.TryGetProperty("named", out var named) && named.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Validates and builds one grant from its row. A token-keyed row's object key
    /// (<paramref name="entry"/>.Name) is its bearer token; a named grant's key is ignored (its
    /// <c>id</c> field identifies it). The returned <see cref="Grant"/> is hashed and stored by
    /// the caller according to which kind it is.
    /// </summary>
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
        var canRequestElevation = ReadCanRequestElevation(entry.Value, path, id);

        return new Grant(id, opClasses, vaults, expiresAtUtc, scopeRoots) { CanRequestElevation = canRequestElevation };
    }

    /// <summary>Parses the optional <c>canRequestElevation</c> opt-in - a boolean, defaulting to
    /// false when absent. Any non-boolean value skips the row, the same rigor as the other
    /// fields.</summary>
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
    /// exactly <c>["*"]</c> means that vault is explicitly unscoped; an <em>empty</em> list is a
    /// row-level error rather than a second spelling of it, for the reason given at the check
    /// itself - the broadest scope must not be the value a mistake produces.
    ///
    /// The whole-grant wildcard <c>{ "*": ["*"] }</c> - mirroring the <c>"*"</c> convention
    /// already used by <c>opClasses</c>/<c>vaults</c>/<c>ttl</c> - means "every vault in
    /// <paramref name="vaults"/>, fully open" and is expanded here at load time into an explicit
    /// unscoped entry per vault, the same way <c>vaults: ["*"]</c> is expanded to the current
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
    private static IReadOnlyDictionary<string, VaultScope> ReadScopeRoots(JsonElement grant, string path, string id, IReadOnlySet<string> vaults)
    {
        if (!grant.TryGetProperty("scopeRoots", out var prop))
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"scopeRoots\" object.");
        if (prop.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: grant \"{id}\" has a \"scopeRoots\" that is not an object keyed by vault name.");

        // Whole-grant wildcard: { "*": ["*"] } expands to an explicit unscoped entry for every
        // vault the grant has - the scopeRoots equivalent of vaults: ["*"].
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

            // An empty list is rejected, not read as whole-vault access. It is the one spelling a
            // mistake produces on its own - a dropped entry, a list filtered to nothing, a
            // half-finished edit - and reading it as the broadest possible scope makes the widest
            // grant the accident's default. Whole-vault access is ["*"] and nothing else: it
            // cannot be arrived at by losing something.
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
                    canonicalized.Add(string.Join('/', VaultAccess.NormalizeScopeSegments(entry)));
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
