using VaultMcpServer.Health;

namespace VaultMcpServer.Vault;

/// <summary>
/// The read-class scope floor: a configured set of vault-relative paths unioned into every
/// grant's effective scope for read-class operations, in every configured vault.
///
/// It exists so an agent under any grant can read the material it is instructed to obey - the
/// vault's own guide docs - without that grant reaching anything else it was not given. A grant
/// scoped to one project cannot read <c>Resources/Vault/Guides</c>, yet every agent is told to
/// consult it; widening the grant to fix that widens everything.
///
/// Four properties, each deliberate:
/// <list type="bullet">
/// <item><b>Read-class only.</b> Nothing here widens a write. Writing a guide still requires a
/// grant genuinely scoped to it, because a floor that widened writes would let any grant author
/// the material every agent obeys. This type is consulted by
/// <see cref="VaultAccess.IsPathInReadScope"/> and by nothing on the write path.</item>
/// <item><b>Path reach, not authority.</b> A grant without the read op class still reads
/// nothing - the floor is unioned into the scope check, which runs after op-class
/// enforcement, never instead of it.</item>
/// <item><b>A floor, so attenuation cannot remove it.</b> It is applied at check time rather
/// than baked into a grant's own roots, so a deliberately narrowed minted token still reaches
/// the guides. That is the property the floor exists to have; a copy written into each grant
/// at mint time would be attenuable, which is the same thing as not being a floor.</item>
/// <item><b>Union, not exemption.</b> <see cref="EffectiveReadScope"/> returns a
/// <see cref="VaultScope"/>, so "no entry point opts out of the scope check" stays literally
/// true and enumeration needs no special case - a wikilink between two guides resolves through
/// the same discovery every other reference uses.</item>
/// </list>
///
/// Entries are paths, file or folder, matched exactly the way scope roots already are, and apply
/// to every configured vault: the guides folder is a layout convention rather than one vault's
/// fact.
/// </summary>
public sealed class SharedReadRoots
{
    /// <summary>No floor at all - every grant's read scope is exactly its own. The value an
    /// absent or entirely-invalid configuration produces, and the one a bare
    /// <see cref="VaultAccess"/> caller with no server config passes.</summary>
    public static SharedReadRoots None { get; } = new([]);

    /// <summary>The canonicalized, de-duplicated roots: vault-relative, forward-slash, in
    /// configured order. Empty for <see cref="None"/>.</summary>
    public IReadOnlyList<string> Roots { get; }

    private SharedReadRoots(IReadOnlyList<string> roots) => Roots = roots;

    /// <summary>
    /// Validates and canonicalizes the configured entries. Validation happens here, at load,
    /// rather than at first use: a malformed entry is a configuration defect and should surface
    /// on startup, not on the first read that happens to touch it.
    ///
    /// Per-entry treatment, matching how vaults.json entries and grants.json rows are handled:
    /// <list type="bullet">
    /// <item>An entry that cannot canonicalize as a vault-relative folder-prefix root - rooted,
    /// escaping the vault, blank - is <b>dropped</b> and recorded Degraded. The rest still
    /// apply; one bad entry does not cost the floor entirely.</item>
    /// <item>An entry naming a path that does not exist in a resolvable vault is recorded
    /// Degraded but <b>kept</b>. It is a real signal (the floor is almost certainly pointing at
    /// a folder someone moved) but not a malformed value, and a root that matches nothing is
    /// harmless - it widens no reach it should not.</item>
    /// <item>A vault skipped by <see cref="VaultConfig"/> (its root does not resolve) is not
    /// checked and records nothing. There is no directory to look in, and the vault's own
    /// Degraded record already says so - a second problem per shared root would report the
    /// same broken root once per entry.</item>
    /// </list>
    /// </summary>
    /// <param name="healthState">
    /// Where per-entry problems are recorded. Required, with no default, for the reason
    /// <see cref="VaultConfig.Load"/> gives: a defaulted throwaway would let a call site compile
    /// while silently discarding what it found.
    /// </param>
    public static SharedReadRoots Load(IEnumerable<string?>? configured, VaultConfig vaultConfig, ServerHealthState healthState)
    {
        if (configured is null)
            return None;

        var canonical = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configured)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                healthState.RecordDegraded("SharedReadRoots", "Shared read root dropped - the entry is empty.");
                continue;
            }

            string root;
            try
            {
                root = string.Join('/', VaultAccess.NormalizeScopeSegments(entry));
            }
            catch (ArgumentException ex)
            {
                healthState.RecordDegraded("SharedReadRoots", $"Shared read root \"{entry}\" dropped - {ex.Message}");
                continue;
            }

            if (!seen.Add(root))
                continue;
            canonical.Add(root);

            foreach (var (vault, vaultRoot) in vaultConfig.Vaults)
            {
                var full = Path.GetFullPath(Path.Combine(vaultRoot, root));
                if (!Directory.Exists(full) && !File.Exists(full))
                    healthState.RecordDegraded(
                        "SharedReadRoots",
                        $"Shared read root \"{root}\" does not exist in vault \"{vault}\" ({full}) - it stays in effect and matches nothing there.");
            }
        }

        return canonical.Count == 0 ? None : new SharedReadRoots(canonical);
    }

    /// <summary>
    /// The effective read scope for a grant whose own scope in this vault is
    /// <paramref name="grantScope"/>: the union of that scope and the shared roots.
    ///
    /// An unscoped grant is returned unchanged - it already reaches everything, and widening it
    /// would be meaningless. A rooted grant gains the shared roots alongside its own. With no
    /// shared roots configured this is the identity, so the read path can call it
    /// unconditionally rather than branching on whether a floor exists.
    /// </summary>
    public VaultScope EffectiveReadScope(VaultScope grantScope)
    {
        if (Roots.Count == 0 || grantScope is not VaultScope.RootedScope rooted)
            return grantScope;

        var union = new HashSet<string>(rooted.Roots, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(Roots);
        return VaultScope.Of(union);
    }
}
