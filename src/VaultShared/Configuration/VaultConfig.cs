using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// The vault name-to-entry map, built by <see cref="VaultConfigLoader"/> from vaults.json. It holds
/// which vaults exist; the access layer decides what a usable one is and is the only thing that
/// resolves a name against this (<c>VaultShared.Access.Vault.Resolve</c>).
///
/// A configured vault whose root directory does not exist at load time is skipped
/// per-entry rather than failing the whole file: it is excluded from the resolvable
/// <see cref="Vaults"/> set, listed in <see cref="SkippedVaults"/> (name to reason), and
/// recorded as a Degraded problem - the other vaults keep working. The two sets are what makes
/// a broken vault tellable from a typo without reading an error message.
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
    /// <param name="FrontmatterRegistry">
    /// This vault's declaration file, as the entry names it, or null when it names none. Resolved
    /// against the directory holding <c>vaults.json</c> rather than against the vault root: the
    /// declaration is the source of every key-level protection and lives beside the grant and
    /// op-class configuration at the same trust tier, so it is never discovered by walking vault
    /// content. Two vaults may name one file and share a vocabulary, or name their own.
    /// </param>
    public sealed record VaultEntry(string Root, PathPolicy PathPolicy, string? FrontmatterRegistry = null);

    /// <summary>The resolvable vaults: configured name to its entry.</summary>
    public required IReadOnlyDictionary<string, VaultEntry> Vaults { get; init; }

    public required string DefaultVault { get; init; }

    /// <summary>
    /// Configured vaults skipped at load time (name to human-readable reason) - today only
    /// "root directory does not exist". Never resolvable, but still
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

}
