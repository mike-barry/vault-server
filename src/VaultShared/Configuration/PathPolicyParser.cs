using System.Text.Json;
using VaultShared.Health;
using VaultShared.Scope;

namespace VaultShared.Configuration;

/// <summary>
/// Parses one vault's <c>path_policy</c> array out of <c>vaults.json</c> into a
/// <see cref="PathPolicy"/>. The only producer of one.
/// </summary>
public static class PathPolicyParser
{
    /// <summary>
    /// A malformed entry is dropped and recorded Degraded rather than failing the vault: the rest
    /// of the policy still applies, and dropping an <c>always_readable</c> entry narrows the floor
    /// rather than widening anything, so the safe direction is also the quiet one. Unrecognized
    /// keys are refused rather than ignored - a positive allowlist, the same posture the
    /// frontmatter registry takes, so a typo'd modifier cannot read as a policy someone declared.
    /// </summary>
    /// <param name="vaultRoot">
    /// The vault's resolved root. A declared target absent from disk is recorded Degraded and
    /// kept - it is this vault's claim about its own layout, worth saying once, and a root
    /// matching nothing widens nothing. An unresolvable vault is skipped before this is reached,
    /// so there is no case here for a root that does not exist.
    /// </param>
    public static PathPolicy Parse(JsonElement policy, string vaultName, string vaultRoot, string configPath, IConfigProblemSink sink)
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
                var parsed = ParseEntry(entry, vaultName, configPath);

                if (parsed.AlwaysReadable && parsed.Path is { } root && seenReadable.Add(root))
                {
                    alwaysReadable.Add(root);
                    var full = Path.GetFullPath(Path.Combine(vaultRoot, root));
                    if (!Directory.Exists(full) && !File.Exists(full))
                        sink.RecordDegraded(
                            "VaultConfig",
                            $"{configPath}: vault \"{vaultName}\" declares \"{root}\" always_readable, but it does not exist ({full}) - it stays in effect and matches nothing.");
                }

                // De-duplicated by target rather than by entry: a target declared twice with two
                // classes would union to both anyway, and one declared twice with one class is a
                // repetition. Keeping the first keeps the list the order it was declared in.
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
    /// One well-formed entry: its target, as exactly one of a canonicalized vault-relative
    /// <paramref name="Path"/> or a named <paramref name="Rule"/>, and the modifiers it carries.
    /// </summary>
    private readonly record struct PolicyEntry(string? Path, StructuralRule? Rule, bool AlwaysReadable, string? MutationRequires);

    /// <summary>
    /// Validates one entry. Throws <see cref="InvalidDataException"/> on anything malformed, which
    /// the caller turns into a dropped entry rather than a failed vault.
    /// </summary>
    private static PolicyEntry ParseEntry(JsonElement entry, string vaultName, string configPath)
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

                // A target that is a shape rather than a place, from a closed set. The rule is
                // code; the declaration decides whether it applies and at what price, so an
                // unrecognized name is a rule nothing implements rather than one to pass through.
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
                    mutationRequires = field.Value.GetString() is { Length: > 0 } opClass
                        ? opClass
                        : throw new InvalidDataException($"{where} has a \"path_policy\" entry with an empty \"mutation_requires\".");
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

        // always_readable is unioned into scope roots, and those are folder prefixes, so a
        // structural rule has nothing to contribute to one.
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
