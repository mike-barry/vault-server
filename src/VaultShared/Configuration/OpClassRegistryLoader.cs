using System.Text.Json;
using VaultShared.Health;

namespace VaultShared.Configuration;

/// <summary>
/// Builds an <see cref="OpClassRegistry"/> from op-classes.json. The only producer of one.
/// </summary>
public static class OpClassRegistryLoader
{
    /// <param name="sink">
    /// Whole-file failures (missing file, bad JSON, non-array root, a row missing "name",
    /// a duplicate name, or an empty array) are recorded here as a
    /// <see cref="ProblemSeverity.Fatal"/> problem instead of being thrown - the caller
    /// always gets back a usable (if empty) instance. Required, deliberately with no
    /// default: a defaulted throwaway instance would let a production call site compile
    /// while silently discarding recorded failures. Callers that only exercise the success
    /// path (tests) pass a fresh local instance.
    /// </param>
    public static OpClassRegistry Load(string path, IConfigProblemSink sink)
    {
        try
        {
            return LoadCore(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException)
        {
            sink.RecordFatal("OpClassRegistry", ex.Message);
            return OpClassRegistry.Empty;
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

        return OpClassRegistry.Of(names);
    }
}
