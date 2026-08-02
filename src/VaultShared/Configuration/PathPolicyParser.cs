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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in policy.EnumerateArray())
        {
            try
            {
                if (ParseEntry(entry, vaultName, configPath) is { } root && seen.Add(root))
                {
                    alwaysReadable.Add(root);
                    var full = Path.GetFullPath(Path.Combine(vaultRoot, root));
                    if (!Directory.Exists(full) && !File.Exists(full))
                        sink.RecordDegraded(
                            "VaultConfig",
                            $"{configPath}: vault \"{vaultName}\" declares \"{root}\" always_readable, but it does not exist ({full}) - it stays in effect and matches nothing.");
                }
            }
            catch (InvalidDataException ex)
            {
                sink.RecordDegraded("VaultConfig", $"Path policy entry dropped - {ex.Message}");
            }
        }

        return PathPolicy.Of(alwaysReadable);
    }

    /// <summary>
    /// Validates one entry and returns its <c>always_readable</c> target, or null when the entry
    /// is well-formed but carries no modifier <see cref="PathPolicy"/> implements. Throws
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

                // Specified, and nothing enforces either one. Refused rather than parsed and
                // ignored: config naming an authority nothing checks is a lie that reads as
                // working, which is worse than a refusal telling the operator so.
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
            return string.Join('/', ScopePath.Normalize(path));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"{where} has an invalid \"path_policy\" target \"{path}\": {ex.Message}");
        }
    }
}
