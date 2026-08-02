using System.Text.Json;
using VaultShared.Health;

namespace VaultShared.Configuration;

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

    /// <summary>An inert registry with no op-classes - every <see cref="IsValid"/> check
    /// fails and <see cref="Canonicalize"/> never matches. op-classes.json stays
    /// all-or-nothing: there is no per-row skip for this file, so a whole-file failure
    /// always produces this empty set rather than a partial one.</summary>
    private static OpClassRegistry Empty => new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <param name="healthState">
    /// Whole-file failures (missing file, bad JSON, non-array root, a row missing "name",
    /// a duplicate name, or an empty array) are recorded here as a
    /// <see cref="ProblemSeverity.Fatal"/> problem instead of being thrown - the caller
    /// always gets back a usable (if empty) instance. Required, deliberately with no
    /// default: a defaulted throwaway instance would let a future production call site
    /// compile while silently discarding recorded failures - exactly the failure class this
    /// health-state redesign exists to eliminate. Callers that only exercise the success
    /// path (tests) pass a fresh local instance.
    /// </param>
    public static OpClassRegistry Load(string path, ServerHealthState healthState)
    {
        try
        {
            return LoadCore(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            healthState.RecordFatal("OpClassRegistry", ex.Message);
            return Empty;
        }
    }

    private static OpClassRegistry LoadCore(string path)
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
