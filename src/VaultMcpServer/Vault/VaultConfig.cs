using System.Text.Json;

namespace VaultMcpServer.Vault;

/// <summary>
/// The vault name-to-root-path map, loaded from vaults.json at startup.
/// Every vault-targeting operation resolves its vault through <see cref="Resolve"/>,
/// falling back to <see cref="DefaultVault"/> when no vault is named.
/// </summary>
public sealed class VaultConfig
{
    public required IReadOnlyDictionary<string, string> Vaults { get; init; }
    public required string DefaultVault { get; init; }

    public static VaultConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Vault config not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (!root.TryGetProperty("default_vault", out var defaultProp) || defaultProp.GetString() is not { Length: > 0 } defaultVault)
            throw new InvalidDataException($"{path}: missing or empty \"default_vault\".");
        if (!root.TryGetProperty("vaults", out var vaultsProp) || vaultsProp.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: missing \"vaults\" object.");

        var vaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in vaultsProp.EnumerateObject())
        {
            if (entry.Value.GetString() is not { Length: > 0 } vaultRoot)
                throw new InvalidDataException($"{path}: vault \"{entry.Name}\" has an empty root path.");
            var fullRoot = Path.GetFullPath(vaultRoot);
            if (!Directory.Exists(fullRoot))
                throw new DirectoryNotFoundException($"{path}: vault \"{entry.Name}\" root does not exist: {fullRoot}");
            vaults[entry.Name] = fullRoot;
        }

        if (vaults.Count == 0)
            throw new InvalidDataException($"{path}: \"vaults\" must contain at least one entry.");
        if (!vaults.ContainsKey(defaultVault))
            throw new InvalidDataException($"{path}: \"default_vault\" ({defaultVault}) is not a configured vault.");

        return new VaultConfig { Vaults = vaults, DefaultVault = defaultVault };
    }

    /// <summary>Resolves a vault name (or null/empty for the default) to its canonical configured name and root path.</summary>
    public (string Name, string Root) Resolve(string? vault)
    {
        var name = string.IsNullOrWhiteSpace(vault) ? DefaultVault : vault;
        if (!Vaults.TryGetValue(name, out var root))
            throw new ArgumentException($"Unknown vault: {name}. Configured vaults: {string.Join(", ", Vaults.Keys)}");
        var canonical = Vaults.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return (canonical, root);
    }
}
