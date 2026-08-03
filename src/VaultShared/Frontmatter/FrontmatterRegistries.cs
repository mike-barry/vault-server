using VaultShared.Configuration;
using VaultShared.Health;

namespace VaultShared.Frontmatter;

/// <summary>
/// Every vault's declaration state, loaded once at startup. Both front-ends load the same files
/// through this: if one loaded at startup and the other re-read per invocation, they would disagree
/// about the vocabulary the moment a file was edited mid-session. There is no hot reload, because a
/// batch of operations spanning one would be validated against two registries.
///
/// <b>The file is the identity.</b> Two vaults naming one declaration share one loaded instance, so
/// a vocabulary declared once is one object however many vaults take it.
/// </summary>
public sealed class FrontmatterRegistries
{
    private readonly IReadOnlyDictionary<string, RegistryState> _byVault;

    private FrontmatterRegistries(IReadOnlyDictionary<string, RegistryState> byVault) => _byVault = byVault;

    /// <summary>An inert set in which every vault is <see cref="RegistryState.Absent"/> - every
    /// frontmatter write refuses and nothing else is touched. What a host gets when the cascade
    /// above this one already failed, so a second load does not pile a confusing "unknown op class"
    /// on top of the real root cause.</summary>
    public static FrontmatterRegistries None { get; } =
        new(new Dictionary<string, RegistryState>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// One vault's state, for a caller holding a declaration rather than a file. The write path
    /// takes its vocabulary as an argument so it can be exercised against a synthetic one; this is
    /// the same seam a step above, for the tools that hold the per-vault set rather than a single
    /// registry. Nothing in a host produces one this way - a host loads files.
    /// </summary>
    public static FrontmatterRegistries Of(string vaultName, RegistryState state) =>
        new(new Dictionary<string, RegistryState>(StringComparer.OrdinalIgnoreCase) { [vaultName] = state });

    /// <summary>
    /// This vault's declaration state. A vault the configuration never named answers
    /// <see cref="RegistryState.Absent"/> with an empty path rather than throwing or returning
    /// null: a caller asking about an unknown vault is asking a fail-closed question, and every
    /// answer here is one of the three states.
    /// </summary>
    public RegistryState For(string vaultName) =>
        _byVault.TryGetValue(vaultName, out var state) ? state : new RegistryState.Absent(string.Empty);

    /// <summary>
    /// Loads every configured vault's declaration, joining the load cascade after op-classes -
    /// which the cross-file check consults - and after the vault configuration that names the files.
    /// </summary>
    /// <param name="configDirectory">What a vault's configured declaration name is resolved
    /// against: the directory holding <c>vaults.json</c>. The declaration lives beside the grant
    /// and op-class configuration, at the same trust tier, and is never discovered by walking vault
    /// content - resolving against the vault root would put a file the whole protection model rests
    /// on inside the thing it protects.</param>
    /// <param name="sink">Where a failure is recorded, per vault, as
    /// <see cref="ProblemSeverity.Degraded"/>. Fatal would take down a host whose reads, searches,
    /// moves and removals are all still correct. The condition is visible in health state rather
    /// than inferred from refusals.</param>
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
