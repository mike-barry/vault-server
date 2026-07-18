using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Auth;

/// <summary>
/// Loads grants.json - the bearer-token grant table - and authenticates presented tokens
/// against it. Tokens are hashed (SHA-256) at load and never retained or logged in the
/// clear; authentication compares hashes with <see cref="CryptographicOperations.FixedTimeEquals"/>
/// against every row, with no early exit, so lookup time does not leak which row (if any)
/// matched.
/// </summary>
public sealed class GrantStore
{
    private readonly IReadOnlyList<(byte[] Hash, Grant Grant)> _rows;

    private GrantStore(IReadOnlyList<(byte[] Hash, Grant Grant)> rows) => _rows = rows;

    public static GrantStore Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Grants config not found: {path}", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path}: root must be an object keyed by bearer token.");

        var rows = new List<(byte[] Hash, Grant Grant)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var token = entry.Name;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException($"{path}: empty token.");

            if (!entry.Value.TryGetProperty("id", out var idProp) || idProp.GetString() is not { Length: > 0 } id)
                throw new InvalidDataException($"{path}: a grant is missing a non-empty \"id\".");
            if (!seenIds.Add(id))
                throw new InvalidDataException($"{path}: duplicate grant id \"{id}\".");

            var opClasses = ReadStringSet(entry.Value, "opClasses", path, id)
                .Select(s => Enum.TryParse<OpClass>(s, ignoreCase: true, out var op)
                    ? op
                    : throw new InvalidDataException($"{path}: grant \"{id}\" has unknown opClass \"{s}\"."))
                .ToHashSet();
            if (opClasses.Count == 0)
                throw new InvalidDataException($"{path}: grant \"{id}\" has no opClasses.");

            var vaults = ReadStringSet(entry.Value, "vaults", path, id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (vaults.Count == 0)
                throw new InvalidDataException($"{path}: grant \"{id}\" has no vaults.");

            rows.Add((Hash(token), new Grant(id, opClasses, vaults)));
        }

        if (rows.Count == 0)
            throw new InvalidDataException($"{path}: must contain at least one grant.");

        return new GrantStore(rows);
    }

    /// <summary>Authenticates a presented bearer token, returning the matching grant or null.</summary>
    public Grant? Authenticate(string presentedToken)
    {
        var presentedHash = Hash(presentedToken);
        Grant? match = null;
        foreach (var (hash, grant) in _rows)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, presentedHash))
                match = grant;
        }
        return match;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static IEnumerable<string> ReadStringSet(JsonElement grant, string property, string path, string id)
    {
        if (!grant.TryGetProperty(property, out var prop) || prop.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: grant \"{id}\" is missing a \"{property}\" array.");
        foreach (var item in prop.EnumerateArray())
        {
            if (item.GetString() is not { Length: > 0 } value)
                throw new InvalidDataException($"{path}: grant \"{id}\" has an empty entry in \"{property}\".");
            yield return value;
        }
    }
}
