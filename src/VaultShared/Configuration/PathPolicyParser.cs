using System.Text.Json;
using VaultShared.Health;
using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// Parses one vault's <c>path_policy</c> array out of <c>vaults.json</c> into a
/// <see cref="PathPolicy"/>. This is its only producer.
/// </summary>
public static class PathPolicyParser
{
    /// <summary>Parses a vault's <c>path_policy</c> array.</summary>
    /// <param name="policy">The <c>path_policy</c> element.</param>
    /// <param name="vaultName">The vault's name, quoted in messages.</param>
    /// <param name="vaultRoot">
    /// The vault's resolved root, used to check whether a declared target exists. A target absent
    /// from disk is recorded Degraded and kept, because a root matching nothing widens nothing. An
    /// unresolvable vault is skipped before this runs, so there is no case here for a root that does
    /// not exist.
    /// </param>
    /// <param name="configPath">The vaults.json path, quoted in messages.</param>
    /// <param name="opClasses">
    /// The loaded op-class configuration, which each <c>mutation_requires</c> class is checked
    /// against. This is the same cross-file check the frontmatter registry and grants run. A policy
    /// naming an authority nothing defines is a protection nothing can enforce.
    /// </param>
    /// <param name="sink">Receives a Degraded problem for each entry dropped.</param>
    /// <returns>The policy, or <see cref="PathPolicy.None"/> where nothing usable was
    /// declared.</returns>
    /// <remarks>
    /// A malformed entry is dropped and recorded Degraded rather than failing the vault. The rest of
    /// the policy still applies, and dropping an <c>always_readable</c> entry narrows the read floor
    /// rather than widening anything. An unrecognized key is refused rather than ignored, so a
    /// mistyped modifier cannot read as a policy someone declared.
    /// </remarks>
    public static PathPolicy Parse(
        JsonElement policy, string vaultName, string vaultRoot, string configPath, OpClassRegistry opClasses, IConfigProblemSink sink)
    {
        if (policy.ValueKind != JsonValueKind.Array)
        {
            sink.RecordDegraded("VaultConfig", $"{configPath}: vault \"{vaultName}\" has a \"path_policy\" that is not an array - the whole policy is ignored.");
            return PathPolicy.None;
        }

        var alwaysReadable = new List<string>();
        var mutationRules = new List<PathPolicy.MutationRule>();
        var seenReadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMutation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in policy.EnumerateArray())
        {
            try
            {
                var parsed = ParseEntry(entry, vaultName, configPath, opClasses);

                if (parsed.AlwaysReadable && parsed.Path is { } root && seenReadable.Add(root))
                {
                    alwaysReadable.Add(root);
                    var full = Path.GetFullPath(Path.Combine(vaultRoot, root));
                    if (!Directory.Exists(full) && !File.Exists(full))
                        sink.RecordDegraded(
                            "VaultConfig",
                            $"{configPath}: vault \"{vaultName}\" declares \"{root}\" always_readable, but it does not exist ({full}) - it stays in effect and matches nothing.");
                }

                // De-duplicate by target and class rather than by entry. A target declared twice
                // with two classes unions to both anyway, and one declared twice with the same
                // class is a repetition. Keeping the first occurrence keeps the declared order.
                if (parsed.MutationRequires is { } opClass && seenMutation.Add($"{parsed.Path}|{parsed.Rule}|{opClass}"))
                    mutationRules.Add(new PathPolicy.MutationRule(
                        parsed.Rule is { } rule
                            ? new PathPolicy.PolicyTarget.Structural(rule)
                            : new PathPolicy.PolicyTarget.Path(parsed.Path!),
                        opClass));
            }
            catch (InvalidDataException ex)
            {
                sink.RecordDegraded("VaultConfig", $"Path policy entry dropped - {ex.Message}");
            }
        }

        return PathPolicy.Of(alwaysReadable, mutationRules);
    }

    /// <summary>
    /// Represents one well-formed policy entry: its target, which is exactly one of a path or a rule,
    /// and the modifiers it carries.
    /// </summary>
    /// <param name="Path">The canonicalized vault-relative path, or null where the entry names a
    /// rule.</param>
    /// <param name="Rule">The named structural rule, or null where the entry names a path.</param>
    /// <param name="AlwaysReadable">Whether the entry declares its target always readable.</param>
    /// <param name="MutationRequires">The op class mutating the target requires, or null.</param>
    private readonly record struct PolicyEntry(string? Path, StructuralRule? Rule, bool AlwaysReadable, string? MutationRequires);

    /// <summary>Validates one policy entry.</summary>
    /// <param name="entry">The entry element.</param>
    /// <param name="vaultName">The vault's name, quoted in messages.</param>
    /// <param name="configPath">The vaults.json path, quoted in messages.</param>
    /// <param name="opClasses">Validates the declared <c>mutation_requires</c> class.</param>
    /// <returns>The parsed entry.</returns>
    /// <exception cref="InvalidDataException">The entry is malformed, which the caller turns into a
    /// dropped entry rather than a failed vault.</exception>
    private static PolicyEntry ParseEntry(JsonElement entry, string vaultName, string configPath, OpClassRegistry opClasses)
    {
        var where = $"{configPath}: vault \"{vaultName}\"";
        if (entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where} has a \"path_policy\" entry that is not an object.");

        string? path = null;
        StructuralRule? rule = null;
        var alwaysReadable = false;
        string? mutationRequires = null;

        foreach (var field in entry.EnumerateObject())
        {
            switch (field.Name)
            {
                case "path":
                    path = field.Value.GetString() is { Length: > 0 } value
                        ? value
                        : throw new InvalidDataException($"{where} has a \"path_policy\" entry with an empty \"path\".");
                    break;

                // An unrecognized name is a rule nothing implements, rather than one to pass
                // through.
                case "rule":
                    rule = field.Value.GetString() is { Length: > 0 } name
                        ? StructuralRules.Parse(name)
                            ?? throw new InvalidDataException(
                                $"{where} has a \"path_policy\" entry naming an unknown rule \"{name}\" - the rules are {string.Join(", ", StructuralRules.Names)}.")
                        : throw new InvalidDataException($"{where} has a \"path_policy\" entry with an empty \"rule\".");
                    break;

                case "always_readable":
                    alwaysReadable = field.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new InvalidDataException($"{where} has a non-boolean \"always_readable\"."),
                    };
                    break;

                case "mutation_requires":
                    var declared = field.Value.GetString() is { Length: > 0 } opClass
                        ? opClass
                        : throw new InvalidDataException($"{where} has a \"path_policy\" entry with an empty \"mutation_requires\".");
                    if (!opClasses.IsValid(declared))
                        throw new InvalidDataException(
                            $"{where} declares \"mutation_requires\": \"{declared}\", which the op-class configuration does not define.");
                    mutationRequires = opClasses.Canonicalize(declared) ?? declared;
                    break;

                default:
                    throw new InvalidDataException($"{where} has an unrecognized \"path_policy\" key \"{field.Name}\".");
            }
        }

        if (path is null && rule is null)
            throw new InvalidDataException($"{where} has a \"path_policy\" entry naming no target - every entry needs a \"path\" or a \"rule\".");
        if (path is not null && rule is not null)
            throw new InvalidDataException($"{where} has a \"path_policy\" entry naming both a \"path\" and a \"rule\" - a target is one or the other.");

        var target = path ?? $"rule {rule}";
        if (!alwaysReadable && mutationRequires is null)
            throw new InvalidDataException($"{where} has a \"path_policy\" entry for \"{target}\" carrying no modifier - an entry that says nothing is a policy someone believes they declared.");

        // always_readable is unioned into scope roots, which are folder prefixes, so a structural
        // rule has nothing it could contribute to one.
        if (alwaysReadable && rule is not null)
            throw new InvalidDataException($"{where} declares \"{target}\" always_readable - always_readable takes a \"path\", since a read scope is a set of path prefixes.");

        if (path is null)
            return new PolicyEntry(null, rule, alwaysReadable, mutationRequires);

        try
        {
            return new PolicyEntry(string.Join('/', ScopePath.Normalize(path)), null, alwaysReadable, mutationRequires);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"{where} has an invalid \"path_policy\" target \"{path}\": {ex.Message}");
        }
    }
}
