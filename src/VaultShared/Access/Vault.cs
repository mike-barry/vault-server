using System.Security.Cryptography;
using System.Text;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Access;

/// <summary>
/// One usable vault: the configuration's spelling of its name, its existing root directory, and
/// the path policy declared for it. The operand of every whole-vault operation - enumeration, the
/// census, search, and the mutex lock's identity - and the only thing that can produce a
/// <see cref="VaultPath"/>.
///
/// The root is a vault rather than a path, so nothing denotes it as a path with no segments. Such
/// a value would be admitted under every scope, which is an exception inside the one property the
/// layer depends on, and it is why the retained grammar's refusal to normalize the root
/// (<see cref="ScopePath.Normalize"/>) needs no carve-out.
/// </summary>
public sealed record Vault(string Name, string Root, PathPolicy Policy)
{
    /// <summary>
    /// Resolves <paramref name="name"/> - or <see cref="VaultConfig.DefaultVault"/> when it is
    /// null or blank - against <paramref name="config"/>. Total: it answers rather than throwing,
    /// and its three cases come from configuration state rather than from message text, so a vault
    /// nobody configured (a caller error) and a vault configured but unusable (operational, and
    /// worth retrying) are told apart structurally.
    ///
    /// Resolution lives here rather than on <see cref="VaultConfig"/> because its product is this
    /// layer's type: configuration holds which vaults exist, and the layer decides what a usable
    /// one is.
    /// </summary>
    public static VaultResolution Resolve(VaultConfig config, string? name)
    {
        var requested = string.IsNullOrWhiteSpace(name) ? config.DefaultVault : name;

        if (config.Vaults.TryGetValue(requested, out var entry))
            return new VaultResolution.Resolved(new Vault(Canonical(config.Vaults.Keys, requested), entry.Root, entry.PathPolicy));

        if (config.SkippedVaults.TryGetValue(requested, out var reason))
            return new VaultResolution.Unavailable(Canonical(config.SkippedVaults.Keys, requested), reason);

        // Nothing configured under this name, so the only spelling that exists is the caller's.
        return new VaultResolution.Unknown(requested);
    }

    /// <summary>
    /// The cross-process mutex name for this vault (<c>Spec-Vault-Access-Design.md:The Mutex Lock
    /// Name</c>), derived from the root's location so two configurations naming one directory
    /// contend. Nothing here acquires it.
    ///
    /// The derivation is a contract with <c>VaultWatcher</c>, which takes it as code: any change
    /// breaks both processes at once. Hashed because a mutex name is capped at 260 characters and
    /// cannot contain a backslash outside the reserved prefix; lowercased on every platform, or a
    /// case-only difference in configuration gives two processes different locks over the same
    /// files; <c>Global\</c>, which is Windows-only, so the mutex spans sessions.
    /// </summary>
    public string LockName
    {
        get
        {
            var canonical = Path.GetFullPath(Root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
            return OperatingSystem.IsWindows() ? $@"Global\VaultWrite-{hash}" : $"VaultWrite-{hash}";
        }
    }

    /// <summary>
    /// The configured spelling of a name matched case-insensitively. Results are quoted back into
    /// later calls, so echoing the caller's spelling as though it were canonical would make the
    /// round trip lossy in a way nothing detects.
    /// </summary>
    private static string Canonical(IEnumerable<string> configured, string requested) =>
        configured.First(k => k.Equals(requested, StringComparison.OrdinalIgnoreCase));
}
