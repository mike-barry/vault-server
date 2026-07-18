using System.Text.Json;

namespace VaultMcpServer.Vault;

/// <summary>
/// The set of valid op-class names, loaded from op-classes.json at startup - a committed,
/// non-secret data file rather than a hardcoded enum, so adding a class is a config edit.
/// Loaded before <c>GrantStore</c> and <c>ScriptToolLoader</c>, both of which validate
/// op-class names read from their own config/scripts against this registry.
/// </summary>
public sealed class OpClassRegistry
{
    private readonly IReadOnlySet<string> _names;

    private OpClassRegistry(IReadOnlySet<string> names) => _names = names;

    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_names;

    public static OpClassRegistry Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Op-class registry not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: root must be an array of op-class rows.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name)
                throw new InvalidDataException($"{path}: an op-class row is missing a non-empty \"name\".");
            if (!names.Add(name))
                throw new InvalidDataException($"{path}: duplicate op-class \"{name}\".");
        }

        if (names.Count == 0)
            throw new InvalidDataException($"{path}: must contain at least one op-class.");

        return new OpClassRegistry(names);
    }

    /// <summary>Returns the registry's canonical-cased name for a case-insensitive match, or null.</summary>
    public string? Canonicalize(string name) => _names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool IsValid(string name) => _names.Contains(name);
}
