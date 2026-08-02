using System.Text.Json;
using VaultShared.Health;
using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// One vault's path policy: modifiers attached to targets within that vault, declared in its
/// <c>vaults.json</c> entry. A target is a vault-relative path or a named structural rule; a
/// modifier says something the tooling must do about that target.
///
/// Two modifiers are specified. Only the first is implemented here:
/// <list type="bullet">
/// <item><b><c>always_readable</c></b> - the read-class scope floor. Its target unions into a
/// grant's effective read scope for this vault, so an agent under any grant reaches the material
/// it is instructed to obey without that grant widening anywhere else. See
/// <see cref="EffectiveReadScope"/>, which is where that union is applied.</item>
/// <item><b><c>mutation_requires</c></b> - an op class that mutating the target requires. Not
/// implemented here, and <b>refused at load</b> rather than parsed and ignored: config naming an
/// authority that nothing enforces is a lie that reads as working, which is worse than a refusal
/// telling the operator so.</item>
/// </list>
///
/// A <c>rule</c> target is refused for the same reason. <c>always_readable</c> is path-only -
/// it is unioned into scope roots and those are folder prefixes, so "every folder note is
/// readable" is not expressible as one - which leaves <c>mutation_requires</c> as the only
/// modifier a rule could carry, so a rule target carries nothing this class can act on.
///
/// Why the floor is a modifier here rather than its own configuration: a path that instructs
/// agents is special in both directions for one reason, and stating that once per path keeps the
/// two halves from drifting into a path everyone reads that nobody protected.
/// </summary>
public sealed class PathPolicy
{
    /// <summary>A vault that declares nothing. Every grant's read scope in it is exactly its own.</summary>
    public static PathPolicy None { get; } = new([]);

    /// <summary>Canonicalized, de-duplicated <c>always_readable</c> targets: vault-relative,
    /// forward-slash, in declared order.</summary>
    public IReadOnlyList<string> AlwaysReadableRoots { get; }

    private PathPolicy(IReadOnlyList<string> alwaysReadableRoots) => AlwaysReadableRoots = alwaysReadableRoots;

    /// <summary>
    /// Parses one vault's <c>path_policy</c> array.
    ///
    /// A malformed entry is dropped and recorded Degraded rather than failing the vault: the rest
    /// of the policy still applies, and dropping an <c>always_readable</c> entry narrows the floor
    /// rather than widening anything, so the safe direction is also the quiet one. Unrecognized
    /// keys are refused rather than ignored - a positive allowlist, the same posture the
    /// frontmatter registry takes, so a typo'd modifier cannot read as a policy someone declared.
    /// </summary>
    /// <param name="vaultRoot">
    /// The vault's resolved root, or <c>null</c> when the vault's own root did not resolve. A
    /// declared target absent from disk is recorded Degraded and kept - it is this vault's claim
    /// about its own layout, worth saying once, and a root matching nothing widens nothing. With
    /// no resolvable root there is nowhere to look, and the vault's own skip already reports that.
    /// </param>
    public static PathPolicy Parse(JsonElement policy, string vaultName, string vaultRoot, string configPath, IConfigProblemSink sink)
    {
        if (policy.ValueKind != JsonValueKind.Array)
        {
            sink.RecordDegraded("VaultConfig", $"{configPath}: vault \"{vaultName}\" has a \"path_policy\" that is not an array - the whole policy is ignored.");
            return None;
        }

        var alwaysReadable = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in policy.EnumerateArray())
        {
            try
            {
                if (ParseEntry(entry, vaultName, configPath) is { } root && seen.Add(root))
                {
                    alwaysReadable.Add(root);
                    if (vaultRoot.Length > 0)
                    {
                        var full = Path.GetFullPath(Path.Combine(vaultRoot, root));
                        if (!Directory.Exists(full) && !File.Exists(full))
                            sink.RecordDegraded(
                                "VaultConfig",
                                $"{configPath}: vault \"{vaultName}\" declares \"{root}\" always_readable, but it does not exist ({full}) - it stays in effect and matches nothing.");
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                sink.RecordDegraded("VaultConfig", $"Path policy entry dropped - {ex.Message}");
            }
        }

        return alwaysReadable.Count == 0 ? None : new PathPolicy(alwaysReadable);
    }

    /// <summary>
    /// Validates one entry and returns its <c>always_readable</c> target, or null when the entry
    /// is well-formed but carries no modifier this class implements. Throws
    /// <see cref="InvalidDataException"/> on anything malformed.
    /// </summary>
    private static string? ParseEntry(JsonElement entry, string vaultName, string configPath)
    {
        var where = $"{configPath}: vault \"{vaultName}\"";
        if (entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where} has a \"path_policy\" entry that is not an object.");

        string? path = null;
        var alwaysReadable = false;

        foreach (var field in entry.EnumerateObject())
        {
            switch (field.Name)
            {
                case "path":
                    path = field.Value.GetString() is { Length: > 0 } value
                        ? value
                        : throw new InvalidDataException($"{where} has a \"path_policy\" entry with an empty \"path\".");
                    break;

                case "always_readable":
                    alwaysReadable = field.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new InvalidDataException($"{where} has a non-boolean \"always_readable\"."),
                    };
                    break;

                // Specified but not implemented here. Refused rather than ignored - see the class remarks.
                case "rule":
                case "mutation_requires":
                    throw new InvalidDataException(
                        $"{where} uses \"{field.Name}\", which is specified but not implemented yet - remove it rather than relying on it, since nothing enforces it.");

                default:
                    throw new InvalidDataException($"{where} has an unrecognized \"path_policy\" key \"{field.Name}\".");
            }
        }

        if (path is null)
            throw new InvalidDataException($"{where} has a \"path_policy\" entry naming no target - every entry needs a \"path\".");
        if (!alwaysReadable)
            throw new InvalidDataException($"{where} has a \"path_policy\" entry for \"{path}\" carrying no modifier - an entry that says nothing is a policy someone believes they declared.");

        try
        {
            return string.Join('/', VaultAccess.NormalizeScopeSegments(path));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"{where} has an invalid \"path_policy\" target \"{path}\": {ex.Message}");
        }
    }

    /// <summary>
    /// The effective read scope for a grant whose own scope in this vault is
    /// <paramref name="grantScope"/>: the union of that scope and the always-readable roots.
    ///
    /// An unscoped grant is returned unchanged - it already reaches everything. With no policy
    /// this is the identity, so a read caller can call it unconditionally rather than branching on
    /// whether a floor exists. It returns a <see cref="VaultScope"/> rather than answering a
    /// containment question, so enumeration runs over the same scope a containment check uses and
    /// needs no special case of its own.
    ///
    /// Applied here, at check time, rather than copied into a grant's roots when the grant is
    /// built - which is what makes it a floor. A copy would be attenuable, and a floor a minted
    /// token can drop is not one.
    /// </summary>
    public VaultScope EffectiveReadScope(VaultScope grantScope)
    {
        if (AlwaysReadableRoots.Count == 0 || grantScope is not VaultScope.RootedScope rooted)
            return grantScope;

        var union = new HashSet<string>(rooted.Roots, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(AlwaysReadableRoots);
        return VaultScope.Of(union);
    }
}
