using System.Text.Json;
using VaultShared.Health;

namespace VaultShared.Configuration;

/// <summary>
/// The vault name-to-root-path map, loaded from vaults.json at startup.
/// Every vault-targeting operation resolves its vault through <see cref="Resolve"/>,
/// falling back to <see cref="DefaultVault"/> when no vault is named.
///
/// A configured vault whose root directory does not exist at load time is skipped
/// per-entry rather than failing the whole file: it is excluded from the resolvable
/// <see cref="Vaults"/> set, listed in <see cref="SkippedVaults"/> (name to reason), and
/// recorded as a Degraded problem - the other vaults keep working. <see cref="Resolve"/>
/// against a skipped vault fails with a distinct "configured but currently unavailable"
/// error rather than the "unknown vault" error, so a broken root is tellable from a typo.
/// </summary>
public sealed class VaultConfig
{
    private static readonly IReadOnlyDictionary<string, VaultEntry> NoVaults =
        new Dictionary<string, VaultEntry>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> NoSkips =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One resolvable vault: its existing, fully-qualified root path and its
    /// <see cref="PathPolicy"/>. <c>vaults.json</c> is the one enumeration of vaults, so a
    /// vault's own facts live on its entry rather than in a second file keyed by vault name -
    /// two such files drift, and a vault renamed in one and not the other loses the policy
    /// someone believes it has.
    /// </summary>
    public sealed record VaultEntry(string Root, PathPolicy PathPolicy);

    /// <summary>The resolvable vaults: configured name to its entry.</summary>
    public required IReadOnlyDictionary<string, VaultEntry> Vaults { get; init; }

    public required string DefaultVault { get; init; }

    /// <summary>
    /// Configured vaults skipped at load time (name to human-readable reason) - today only
    /// "root directory does not exist". Never resolvable via <see cref="Resolve"/>, but still
    /// part of the <em>configured</em> set (<see cref="IsConfigured"/>,
    /// <see cref="ConfiguredVaultNames"/>): a grants.json row naming a skipped vault stays
    /// valid, because the vault being broken is the vault's problem, not the grant's.
    /// Exposed so grants validation and later diagnostics batches can consult it.
    /// </summary>
    public IReadOnlyDictionary<string, string> SkippedVaults { get; init; } = NoSkips;

    /// <summary>Every configured vault name - resolvable and skipped alike. The set grants
    /// validation (and <c>"*"</c> expansion) works against, so grants stay stable while a
    /// vault is temporarily broken.</summary>
    public IReadOnlyCollection<string> ConfiguredVaultNames => [.. Vaults.Keys, .. SkippedVaults.Keys];

    /// <summary>True when <paramref name="name"/> is a configured vault, whether resolvable or skipped (case-insensitive).</summary>
    public bool IsConfigured(string name) => Vaults.ContainsKey(name) || SkippedVaults.ContainsKey(name);

    /// <summary>An inert config with no vaults - nobody can resolve a vault against it.
    /// Returned by <see cref="Load"/> in place of throwing when vaults.json is missing,
    /// unparseable, or structurally invalid.</summary>
    private static VaultConfig Empty => new() { Vaults = NoVaults, DefaultVault = string.Empty };

    /// <param name="healthState">
    /// Whole-file failures (missing file, bad JSON, missing/empty "default_vault", empty or
    /// invalid "vaults") are recorded here as a <see cref="ProblemSeverity.Fatal"/> problem
    /// instead of being thrown - the caller always gets back a usable (if empty) instance.
    /// A configured vault entry whose root directory does not exist is recorded as a
    /// <see cref="ProblemSeverity.Degraded"/> per-entry skip instead (see
    /// <see cref="SkippedVaults"/>) and does not fail the file. Required, deliberately with
    /// no default: a defaulted throwaway instance would let a future production call site
    /// compile while silently discarding recorded failures - exactly the failure class this
    /// health-state redesign exists to eliminate. Callers that only exercise the success
    /// path (tests) pass a fresh local instance.
    /// </param>
    public static VaultConfig Load(string path, ServerHealthState healthState)
    {
        try
        {
            return LoadCore(path, healthState);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            healthState.RecordFatal("VaultConfig", ex.Message);
            return Empty;
        }
    }

    private static VaultConfig LoadCore(string path, ServerHealthState healthState)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Vault config not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (!root.TryGetProperty("default_vault", out var defaultProp) || defaultProp.GetString() is not { Length: > 0 } defaultVault)
            throw new InvalidDataException($"{path}: missing or empty \"default_vault\".");
        if (!root.TryGetProperty("vaults", out var vaultsProp) || vaultsProp.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: missing \"vaults\" object.");

        var vaults = new Dictionary<string, VaultEntry>(StringComparer.OrdinalIgnoreCase);
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
            {
                if (field.Name is "frontmatter_registry")
                    throw new InvalidDataException(
                        $"{path}: vault \"{entry.Name}\" uses \"frontmatter_registry\", which is specified but not implemented yet.");
                if (field.Name is not ("path" or "path_policy"))
                    throw new InvalidDataException($"{path}: vault \"{entry.Name}\" has an unrecognized key \"{field.Name}\".");
            }

            var fullRoot = Path.GetFullPath(vaultRoot);
            var resolvable = Directory.Exists(fullRoot);
            if (!resolvable)
            {
                // Per-entry skip, not a whole-file failure: the vault is excluded from the
                // resolvable set (operations targeting it fail as "unavailable") while every
                // other vault keeps working. Still a configured vault - see SkippedVaults.
                var reason = $"root directory does not exist: {fullRoot}";
                skipped[entry.Name] = reason;
                healthState.RecordDegraded("VaultConfig", $"{path}: vault \"{entry.Name}\" skipped - {reason}.");
                continue;
            }

            var policy = entry.Value.TryGetProperty("path_policy", out var policyProp)
                ? PathPolicy.Parse(policyProp, entry.Name, fullRoot, path, healthState)
                : PathPolicy.None;
            vaults[entry.Name] = new VaultEntry(fullRoot, policy);
        }

        if (vaults.Count == 0 && skipped.Count == 0)
            throw new InvalidDataException($"{path}: \"vaults\" must contain at least one entry.");
        if (!vaults.ContainsKey(defaultVault) && !skipped.ContainsKey(defaultVault))
            throw new InvalidDataException($"{path}: \"default_vault\" ({defaultVault}) is not a configured vault.");

        return new VaultConfig { Vaults = vaults, DefaultVault = defaultVault, SkippedVaults = skipped };
    }

    /// <summary>
    /// Resolves a vault name (or null/empty for the default) to its canonical configured
    /// name and root path. A vault skipped at load time (see <see cref="SkippedVaults"/>)
    /// fails with a "configured but currently unavailable" error naming the reason,
    /// distinct from the "unknown vault" error an unconfigured name gets.
    /// </summary>
    public (string Name, string Root) Resolve(string? vault)
    {
        var name = string.IsNullOrWhiteSpace(vault) ? DefaultVault : vault;
        if (!Vaults.TryGetValue(name, out var entry))
        {
            if (SkippedVaults.TryGetValue(name, out var reason))
                throw new ArgumentException($"Vault \"{name}\" is configured but currently unavailable: {reason}.");
            throw new ArgumentException($"Unknown vault: {name}. Configured vaults: {string.Join(", ", Vaults.Keys)}");
        }
        var canonical = Vaults.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return (canonical, entry.Root);
    }

    /// <summary>
    /// The <see cref="PathPolicy"/> declared for <paramref name="vault"/>, or
    /// <see cref="PathPolicy.None"/> when the vault declares none or is not resolvable. Never
    /// null, so a read caller can union the floor in unconditionally.
    /// </summary>
    public PathPolicy PathPolicyFor(string? vault)
    {
        var name = string.IsNullOrWhiteSpace(vault) ? DefaultVault : vault;
        return Vaults.TryGetValue(name, out var entry) ? entry.PathPolicy : PathPolicy.None;
    }
}
