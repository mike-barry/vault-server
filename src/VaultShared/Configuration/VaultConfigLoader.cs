using System.Text.Json;
using VaultShared.Health;
using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// Builds a <see cref="VaultConfig"/> from vaults.json. This is its only producer, apart from
/// explicit construction in a test.
/// </summary>
public static class VaultConfigLoader
{
    /// <summary>Loads and validates vaults.json.</summary>
    /// <param name="path">The vaults.json path.</param>
    /// <param name="opClasses">
    /// The loaded op-class configuration, which a vault's path policy validates its
    /// <c>mutation_requires</c> classes against. It is loaded before this file for the same reason
    /// grants are loaded after it: a name checked against a file that has not been read yet is a
    /// check that always passes.
    /// </param>
    /// <param name="sink">
    /// Receives problems instead of the loader throwing. A missing file, bad JSON, a missing or empty
    /// <c>default_vault</c>, or an empty or invalid <c>vaults</c> object is recorded as
    /// <see cref="ProblemSeverity.Fatal"/>. A configured vault whose root directory does not exist is
    /// recorded as <see cref="ProblemSeverity.Degraded"/> and skipped, which does not fail the file;
    /// see <see cref="VaultConfig.SkippedVaults"/>. It has no default value on purpose: a defaulted
    /// throwaway sink would let a production call site compile while silently discarding recorded
    /// failures.
    /// </param>
    /// <returns>The configuration, or <see cref="VaultConfig.Empty"/> where the whole file
    /// failed.</returns>
    public static VaultConfig Load(string path, OpClassRegistry opClasses, IConfigProblemSink sink)
    {
        try
        {
            return LoadCore(path, opClasses, sink);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            sink.RecordFatal("VaultConfig", ex.Message);
            return VaultConfig.Empty;
        }
    }

    private static VaultConfig LoadCore(string path, OpClassRegistry opClasses, IConfigProblemSink sink)
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
            // A vault entry must be an object. A bare path string is a whole-file failure rather
            // than a second accepted spelling, because two shapes for one entry means two parse
            // paths to keep in step, and this file is small enough to edit.
            if (entry.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    $"{path}: vault \"{entry.Name}\" must be an object - write {{ \"path\": \"...\" }} rather than a bare path string.");
            if (!entry.Value.TryGetProperty("path", out var pathProp) || pathProp.GetString() is not { Length: > 0 } vaultRoot)
                throw new InvalidDataException($"{path}: vault \"{entry.Name}\" is missing a non-empty \"path\".");
            foreach (var field in entry.Value.EnumerateObject())
                if (field.Name is not ("path" or "path_policy" or "frontmatter_registry"))
                    throw new InvalidDataException($"{path}: vault \"{entry.Name}\" has an unrecognized key \"{field.Name}\".");

            // Carried as the entry spells it, and resolved later against this file's own directory.
            // A vault naming no registry has no vocabulary, so every frontmatter write in it
            // refuses. That degrades the host rather than failing the load, because nothing else in
            // the vault depends on it.
            string? frontmatterRegistry = null;
            if (entry.Value.TryGetProperty("frontmatter_registry", out var registryProp))
                frontmatterRegistry = registryProp.GetString() is { Length: > 0 } declared
                    ? declared
                    : throw new InvalidDataException($"{path}: vault \"{entry.Name}\" has an empty \"frontmatter_registry\".");

            var fullRoot = Path.GetFullPath(vaultRoot);
            var resolvable = Directory.Exists(fullRoot);
            if (!resolvable)
            {
                // Skip this entry rather than failing the file: the vault is left out of the
                // resolvable set, so operations targeting it fail as unavailable, while every other
                // vault keeps working. It is still a configured vault; see SkippedVaults.
                var reason = $"root directory does not exist: {fullRoot}";
                skipped[entry.Name] = reason;
                sink.RecordDegraded("VaultConfig", $"{path}: vault \"{entry.Name}\" skipped - {reason}.");
                continue;
            }

            var policy = entry.Value.TryGetProperty("path_policy", out var policyProp)
                ? PathPolicyParser.Parse(policyProp, entry.Name, fullRoot, path, opClasses, sink)
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
