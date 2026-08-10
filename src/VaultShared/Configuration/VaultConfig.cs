using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// Holds the vault name-to-entry map, built by <see cref="VaultConfigLoader"/> from vaults.json. It
/// records which vaults exist; the access layer decides which are usable and is the only thing that
/// resolves a name against it, through <c>VaultShared.Access.Vault.Resolve</c>.
/// </summary>
/// <remarks>
/// A configured vault whose root directory does not exist at load time is skipped rather than failing
/// the whole file. It is left out of <see cref="Vaults"/>, listed in <see cref="SkippedVaults"/> with
/// the reason, and recorded as a Degraded problem, while the other vaults keep working. Keeping the
/// two sets apart is what lets a caller tell a broken vault from a typo without reading an error
/// message.
/// </remarks>
public sealed class VaultConfig
{
    private static readonly IReadOnlyDictionary<string, VaultEntry> NoVaults =
        new Dictionary<string, VaultEntry>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> NoSkips =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Represents one resolvable vault.
    /// </summary>
    /// <param name="Root">The vault's existing, fully-qualified root path.</param>
    /// <param name="PathPolicy">The vault's path policy.</param>
    /// <param name="FrontmatterRegistry">
    /// This vault's declaration file as the entry names it, or null where it names none. It is
    /// resolved against the directory holding <c>vaults.json</c> rather than against the vault root,
    /// because the declaration is the source of every key-level protection and sits beside the grant
    /// and op-class configuration at the same trust tier. It is never discovered by walking vault
    /// content. Two vaults may name the same file and share a vocabulary, or each name their own.
    /// </param>
    /// <remarks>
    /// <c>vaults.json</c> is the only enumeration of vaults, so a vault's own facts belong on its
    /// entry rather than in a second file keyed by vault name.
    /// </remarks>
    public sealed record VaultEntry(string Root, PathPolicy PathPolicy, string? FrontmatterRegistry = null);

    /// <summary>The resolvable vaults, keyed by configured name.</summary>
    public required IReadOnlyDictionary<string, VaultEntry> Vaults { get; init; }

    /// <summary>The vault used when a call names none.</summary>
    public required string DefaultVault { get; init; }

    /// <summary>
    /// Configured vaults that were skipped at load time, mapped to a human-readable reason. The only
    /// reason today is that the root directory does not exist.
    /// </summary>
    /// <remarks>
    /// A skipped vault is never resolvable but remains part of the configured set, so a grants.json
    /// row naming it stays valid. It is exposed here so grants validation and diagnostics can
    /// consult it.
    /// </remarks>
    public IReadOnlyDictionary<string, string> SkippedVaults { get; init; } = NoSkips;

    /// <summary>Every configured vault name, resolvable and skipped alike. This is the set grants
    /// validation and <c>"*"</c> expansion work against, so grants stay stable while a vault is
    /// temporarily broken.</summary>
    public IReadOnlyCollection<string> ConfiguredVaultNames => [.. Vaults.Keys, .. SkippedVaults.Keys];

    /// <summary>Reports whether a name is a configured vault, resolvable or skipped.</summary>
    /// <param name="name">The vault name, compared case-insensitively.</param>
    /// <returns>True where the vault is configured.</returns>
    public bool IsConfigured(string name) => Vaults.ContainsKey(name) || SkippedVaults.ContainsKey(name);

    /// <summary>An inert configuration holding no vaults, so no name resolves against it.
    /// <see cref="VaultConfigLoader.Load"/> returns this instead of throwing when vaults.json is
    /// missing, unparseable, or structurally invalid.</summary>
    internal static VaultConfig Empty => new() { Vaults = NoVaults, DefaultVault = string.Empty };

}
