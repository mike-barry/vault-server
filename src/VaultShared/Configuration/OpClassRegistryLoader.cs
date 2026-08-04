using System.Text.Json;
using VaultShared.Health;

namespace VaultShared.Configuration;

/// <summary>
/// Builds an <see cref="OpClassRegistry"/> from op-classes.json. This is its only producer.
/// </summary>
public static class OpClassRegistryLoader
{
    /// <summary>Loads and validates op-classes.json.</summary>
    /// <param name="path">The op-classes.json path.</param>
    /// <param name="sink">
    /// Receives a <see cref="ProblemSeverity.Fatal"/> problem instead of the loader throwing, for a
    /// missing file, bad JSON, a non-array root, a row missing <c>name</c>, a duplicate name, or an
    /// empty array. It has no default value on purpose: a defaulted throwaway sink would let a
    /// production call site compile while silently discarding recorded failures. A test that only
    /// exercises the success path passes a fresh local instance.
    /// </param>
    /// <returns>The registry, or <see cref="OpClassRegistry.Empty"/> where the file failed.</returns>
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
