using VaultShared.Configuration;
using VaultShared.Health;

namespace VaultShared.Frontmatter;

/// <summary>
/// Holds every vault's frontmatter declaration state, loaded once at startup. Both front-ends load
/// the same files through this type, so they cannot disagree about the vocabulary mid-session.
/// There is no hot reload, because a batch of operations spanning one would be validated against
/// two different registries.
///
/// Declaration files are shared by path: two vaults naming one file get one loaded instance.
/// </summary>
public sealed class FrontmatterRegistries
{
    private readonly IReadOnlyDictionary<string, RegistryState> _byVault;

    /// <summary>Initializes the set from per-vault states.</summary>
    /// <param name="byVault">Each vault's state, keyed by vault name.</param>
    private FrontmatterRegistries(IReadOnlyDictionary<string, RegistryState> byVault) => _byVault = byVault;

    /// <summary>Gets an inert set in which every vault is <see cref="RegistryState.Absent"/>, so
    /// every frontmatter write refuses and nothing else is affected. A host uses this when an
    /// earlier stage of the load cascade already failed, so a second load does not report a
    /// confusing follow-on error on top of the real cause.</summary>
    public static FrontmatterRegistries None { get; } =
        new(new Dictionary<string, RegistryState>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Returns a set holding one vault's state, for a caller that already has a declaration rather
    /// than a file. Tests use this to exercise the tools against a synthetic vocabulary; a host
    /// always loads files instead.
    /// </summary>
    /// <param name="vaultName">The vault the state belongs to.</param>
    /// <param name="state">That vault's declaration state.</param>
    /// <returns>A set holding just that vault.</returns>
    public static FrontmatterRegistries Of(string vaultName, RegistryState state) =>
        new(new Dictionary<string, RegistryState>(StringComparer.OrdinalIgnoreCase) { [vaultName] = state });

    /// <summary>Returns this vault's declaration state.</summary>
    /// <param name="vaultName">The vault to look up, matched case-insensitively.</param>
    /// <returns>The vault's state. A vault the configuration never named answers
    /// <see cref="RegistryState.Absent"/> with an empty path rather than throwing or returning
    /// null, so every answer is one of the three states.</returns>
    public RegistryState For(string vaultName) =>
        _byVault.TryGetValue(vaultName, out var state) ? state : new RegistryState.Absent(string.Empty);

    /// <summary>
    /// Loads every configured vault's declaration. This runs in the load cascade after the
    /// op-class configuration, which the cross-file check consults, and after the vault
    /// configuration that names the declaration files.
    /// </summary>
    /// <param name="config">The vault configuration naming each vault's declaration file.</param>
    /// <param name="configDirectory">The directory holding <c>vaults.json</c>, which a vault's
    /// configured declaration name is resolved against. The declaration lives beside the grant and
    /// op-class configuration at the same trust tier and is never discovered by walking vault
    /// content, because resolving against the vault root would put a file the protection model
    /// rests on inside the thing it protects.</param>
    /// <param name="opClasses">The loaded op-class configuration, for the cross-file check that a
    /// declared authority is defined.</param>
    /// <param name="sink">Where a per-vault failure is recorded, as
    /// <see cref="ProblemSeverity.Degraded"/>. Fatal would take down a host whose reads, searches,
    /// moves and removals are all still correct, and recording it here makes the condition visible
    /// in health state rather than something to infer from refusals.</param>
    /// <returns>The per-vault declaration states.</returns>
    public static FrontmatterRegistries Load(
        VaultConfig config, string configDirectory, OpClassRegistry opClasses, IConfigProblemSink sink)
    {
        var byVault = new Dictionary<string, RegistryState>(StringComparer.OrdinalIgnoreCase);
        var byFile = new Dictionary<string, RegistryState>(StringComparer.OrdinalIgnoreCase);

        foreach (var (vaultName, entry) in config.Vaults)
        {
            if (entry.FrontmatterRegistry is not { Length: > 0 } declared)
            {
                byVault[vaultName] = new RegistryState.Absent(string.Empty);
                sink.RecordDegraded(
                    "FrontmatterRegistry",
                    $"Vault \"{vaultName}\" names no \"frontmatter_registry\" - every frontmatter write in it refuses, and everything else is unaffected.");
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(configDirectory, declared));
            if (!byFile.TryGetValue(path, out var state))
            {
                state = FrontmatterRegistryLoader.Load(path, opClasses);
                byFile[path] = state;
            }

            byVault[vaultName] = state;
            switch (state)
            {
                case RegistryState.Absent absent:
                    sink.RecordDegraded(
                        "FrontmatterRegistry",
                        $"Vault \"{vaultName}\" names a frontmatter registry that is not there ({absent.Path}) - every frontmatter write in it refuses, and everything else is unaffected.");
                    break;

                case RegistryState.Unloadable unloadable:
                    sink.RecordDegraded(
                        "FrontmatterRegistry",
                        $"Vault \"{vaultName}\" has an unusable frontmatter registry ({unloadable.Path}): {unloadable.Why} Every frontmatter write in it refuses, and everything else is unaffected.");
                    break;
            }
        }

        return new FrontmatterRegistries(byVault);
    }
}
