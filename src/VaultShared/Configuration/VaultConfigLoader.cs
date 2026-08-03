using System.Text.Json;
using VaultShared.Health;
using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// Builds a <see cref="VaultConfig"/> from vaults.json. The only producer of one outside an
/// explicit construction in a test.
/// </summary>
public static class VaultConfigLoader
{
    /// <param name="sink">
    /// Whole-file failures (missing file, bad JSON, missing/empty "default_vault", empty or
    /// invalid "vaults") are recorded here as a <see cref="ProblemSeverity.Fatal"/> problem
    /// instead of being thrown - the caller always gets back a usable (if empty) instance.
    /// A configured vault entry whose root directory does not exist is recorded as a
    /// <see cref="ProblemSeverity.Degraded"/> per-entry skip instead (see
    /// <see cref="VaultConfig.SkippedVaults"/>) and does not fail the file. Required,
    /// deliberately with no default: a defaulted throwaway instance would let a production call
    /// site compile while silently discarding recorded failures. Callers that only exercise the
    /// success path (tests) pass a fresh local instance.
    /// </param>
    public static VaultConfig Load(string path, IConfigProblemSink sink)
    {
        try
        {
            return LoadCore(path, sink);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            sink.RecordFatal("VaultConfig", ex.Message);
            return VaultConfig.Empty;
        }
    }

    private static VaultConfig LoadCore(string path, IConfigProblemSink sink)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Vault config not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (!root.TryGetProperty("default_vault", out var defaultProp) || defaultProp.GetString() is not { Length: > 0 } defaultVault)
            throw new InvalidDataException($"{path}: missing or empty \"default_vault\".");
        if (!root.TryGetProperty("vaults", out var vaultsProp) || vaultsProp.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: missing \"vaults\" object.");

        var vaults = new Dictionary<string, VaultConfig.VaultEntry>(StringComparer.OrdinalIgnoreCase);
        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in vaultsProp.EnumerateObject())
        {
            // A vault entry is an object and nothing else. The bare-string form it replaced is a
            // whole-file failure rather than a second accepted spelling: two shapes for one entry
            // is two parse paths to keep in step, and this file is small enough to migrate.
            if (entry.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    $"{path}: vault \"{entry.Name}\" must be an object - write {{ \"path\": \"...\" }} rather than a bare path string.");
            if (!entry.Value.TryGetProperty("path", out var pathProp) || pathProp.GetString() is not { Length: > 0 } vaultRoot)
                throw new InvalidDataException($"{path}: vault \"{entry.Name}\" is missing a non-empty \"path\".");
            foreach (var field in entry.Value.EnumerateObject())
                if (field.Name is not ("path" or "path_policy" or "frontmatter_registry"))
                    throw new InvalidDataException($"{path}: vault \"{entry.Name}\" has an unrecognized key \"{field.Name}\".");

            // Carried as the entry names it and resolved by the frontmatter registry cascade
            // against this file's own directory. A vault naming none has no vocabulary, so every
            // frontmatter write in it refuses - which is a Degraded host rather than a failed load,
            // since nothing else in the vault depends on it.
            string? frontmatterRegistry = null;
            if (entry.Value.TryGetProperty("frontmatter_registry", out var registryProp))
                frontmatterRegistry = registryProp.GetString() is { Length: > 0 } declared
                    ? declared
                    : throw new InvalidDataException($"{path}: vault \"{entry.Name}\" has an empty \"frontmatter_registry\".");

            var fullRoot = Path.GetFullPath(vaultRoot);
            var resolvable = Directory.Exists(fullRoot);
            if (!resolvable)
            {
                // Per-entry skip, not a whole-file failure: the vault is excluded from the
                // resolvable set (operations targeting it fail as "unavailable") while every
                // other vault keeps working. Still a configured vault - see SkippedVaults.
                var reason = $"root directory does not exist: {fullRoot}";
                skipped[entry.Name] = reason;
                sink.RecordDegraded("VaultConfig", $"{path}: vault \"{entry.Name}\" skipped - {reason}.");
                continue;
            }

            var policy = entry.Value.TryGetProperty("path_policy", out var policyProp)
                ? PathPolicyParser.Parse(policyProp, entry.Name, fullRoot, path, sink)
                : PathPolicy.None;
            vaults[entry.Name] = new VaultConfig.VaultEntry(fullRoot, policy, frontmatterRegistry);
        }

        if (vaults.Count == 0 && skipped.Count == 0)
            throw new InvalidDataException($"{path}: \"vaults\" must contain at least one entry.");
        if (!vaults.ContainsKey(defaultVault) && !skipped.ContainsKey(defaultVault))
            throw new InvalidDataException($"{path}: \"default_vault\" ({defaultVault}) is not a configured vault.");

        return new VaultConfig { Vaults = vaults, DefaultVault = defaultVault, SkippedVaults = skipped };
    }
}
