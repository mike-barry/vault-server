using System.Security.Cryptography;
using System.Text;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Access;

/// <summary>
/// Represents one usable vault: the configuration's spelling of its name, its existing root
/// directory, and the path policy declared for it. It is the operand of every whole-vault operation
/// and the only thing that can produce a <see cref="VaultPath"/>.
/// </summary>
/// <remarks>
/// The root is represented by this type rather than by a path with no segments. Such a path would
/// be admitted under every scope, and avoiding it is why <see cref="ScopePath.Normalize"/> can
/// refuse to normalize the root without a carve-out here.
/// </remarks>
/// <param name="Name">The vault's name, as configuration spells it.</param>
/// <param name="Root">The absolute path of the vault's root directory, which exists.</param>
/// <param name="Policy">The path policy declared for this vault.</param>
public sealed record Vault(string Name, string Root, PathPolicy Policy)
{
    /// <summary>
    /// Resolves a vault name against configuration, returning a usable vault or one of two
    /// failures. It returns rather than throws, and the two failures are separate cases rather than
    /// one message, so a name nobody configured (a caller error) is told from a configured vault
    /// that is not usable right now (operational, and worth retrying).
    /// </summary>
    /// <param name="config">The loaded vault configuration.</param>
    /// <param name="name">
    /// The vault to resolve, or null or blank for <see cref="VaultConfig.DefaultVault"/>.
    /// </param>
    /// <returns>The resolution: resolved, unknown, or unavailable.</returns>
    public static VaultResolution Resolve(VaultConfig config, string? name)
    {
        var requested = string.IsNullOrWhiteSpace(name) ? config.DefaultVault : name;

        if (config.Vaults.TryGetValue(requested, out var entry))
            return new VaultResolution.Resolved(new Vault(Canonical(config.Vaults.Keys, requested), entry.Root, entry.PathPolicy));

        if (config.SkippedVaults.TryGetValue(requested, out var reason))
            return new VaultResolution.Unavailable(Canonical(config.SkippedVaults.Keys, requested), reason);

        // Nothing is configured under this name, so the caller's spelling is the only one that exists.
        return new VaultResolution.Unknown(requested);
    }

    /// <summary>
    /// Gets the name of the cross-process write mutex for this vault. It is derived from the root
    /// directory's location, so two configurations naming one directory contend for the same mutex.
    /// Nothing here acquires it.
    /// </summary>
    /// <remarks>
    /// Do not change this derivation. It is a cross-process contract: every process that takes the
    /// write lock over a vault has to arrive at the same name from the same root directory, or two
    /// of them hold different locks over the same files and neither sees the other. The
    /// <c>VaultWatcher</c> process is the second holder this exists for, and whoever implements it
    /// must produce this same name, whether by calling this property or by repeating the steps
    /// below in its own code.
    ///
    /// Each step is load-bearing. The location is hashed because a mutex name is capped at 260
    /// characters and cannot contain a backslash outside the reserved prefix. It is lowercased, or a
    /// case-only difference between two configurations produces two names. Windows: the
    /// <c>Global\</c> prefix is what makes the mutex span sessions, and a port needs whatever scopes
    /// a mutex that way on its own platform, or the watcher and the server take different locks over
    /// the same vault.
    /// </remarks>
    public string LockName
    {
        get
        {
            var canonical = Path.GetFullPath(Root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
            return $@"Global\VaultWrite-{hash}";
        }
    }

    /// <summary>
    /// Returns the configured spelling of a name the caller matched case-insensitively. Results are
    /// quoted back into later calls, so echoing the caller's spelling as though it were canonical
    /// would make that round trip lossy.
    /// </summary>
    /// <param name="configured">The names configuration holds.</param>
    /// <param name="requested">The name the caller spelled, which matches one of them.</param>
    /// <returns>The configured spelling.</returns>
    private static string Canonical(IEnumerable<string> configured, string requested) =>
        configured.First(k => k.Equals(requested, StringComparison.OrdinalIgnoreCase));
}
