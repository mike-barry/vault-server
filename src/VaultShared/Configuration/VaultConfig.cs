using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// The vault name-to-root-path map, built by <see cref="VaultConfigLoader"/> from vaults.json.
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
    /// Exposed so grants validation and diagnostics can consult it.
    /// </summary>
    public IReadOnlyDictionary<string, string> SkippedVaults { get; init; } = NoSkips;

    /// <summary>Every configured vault name - resolvable and skipped alike. The set grants
    /// validation (and <c>"*"</c> expansion) works against, so grants stay stable while a
    /// vault is temporarily broken.</summary>
    public IReadOnlyCollection<string> ConfiguredVaultNames => [.. Vaults.Keys, .. SkippedVaults.Keys];

    /// <summary>True when <paramref name="name"/> is a configured vault, whether resolvable or skipped (case-insensitive).</summary>
    public bool IsConfigured(string name) => Vaults.ContainsKey(name) || SkippedVaults.ContainsKey(name);

    /// <summary>An inert config with no vaults - nobody can resolve a vault against it.
    /// Returned by <see cref="VaultConfigLoader.Load"/> in place of throwing when vaults.json
    /// is missing, unparseable, or structurally invalid.</summary>
    internal static VaultConfig Empty => new() { Vaults = NoVaults, DefaultVault = string.Empty };

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
