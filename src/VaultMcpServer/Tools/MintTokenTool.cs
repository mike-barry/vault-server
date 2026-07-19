using System.Text.Json;
using ModelContextProtocol.Protocol;
using VaultMcpServer.Auth;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tools;

/// <summary>
/// Native tool (<see cref="INativeTool"/>) that mints a new, in-memory, scoped, time-limited
/// bearer grant via <see cref="GrantStore.Mint"/> - gated by the <c>ManageGrants</c> op class,
/// enforced by <see cref="ScriptToolDispatcher.CallToolAsync"/> the same way any tool's op
/// class is (<c>grant.Allows(tool.OpClass)</c> against the caller's own grant). Target caller:
/// an orchestrator spawning a restricted child session without hand-editing grants.json.
///
/// A minted grant's op_class and scope roots are always a concrete, explicit set - the
/// <c>"*"</c> wildcard is a grants.json convenience for hand-edited rows only and is rejected
/// here for every parameter, including <c>ttl</c> (always an integer number of minutes,
/// defaulting to 60 when omitted).
///
/// <c>scope_roots</c> entries are folder-prefix strings of the form <c>"&lt;vault&gt;"</c> (the
/// whole vault) or <c>"&lt;vault&gt;/&lt;folder-prefix&gt;"</c> (a subtree within it) - the
/// leading path segment names the vault, so the minted grant's <see cref="Grant.Vaults"/> set
/// (still enforced today by <see cref="Auth.BearerGrantMiddleware"/>'s vault-scope check) is
/// derived from it, while the full entries are also carried on <see cref="Grant.ScopeRoots"/>
/// for the folder-prefix containment check landing on a parallel branch. That branch's
/// enforcement code isn't wired up yet - <c>scope_roots</c> is accepted and stored, not yet
/// enforced beyond the existing whole-vault check.
/// </summary>
public sealed class MintTokenTool(GrantStore grantStore, OpClassRegistry opClassRegistry, VaultConfig vaultConfig) : INativeTool
{
    private const int DefaultTtlMinutes = 60;

    public string Name => "mint_token";

    public string Description =>
        "Mints a new, scoped, time-limited bearer token in memory (never persisted to grants.json). " +
        "Requires the ManageGrants op class. Returns the token once - it cannot be retrieved again.";

    public string OpClass => "ManageGrants";

    public JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["scope_roots"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                ["description"] = "Folder-prefix scope roots for the minted grant, e.g. \"primary\" (a whole vault) or \"primary/Projects/Foo\" (a subtree). At least one required; \"*\" is not accepted.",
            },
            ["op_class"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object> { ["type"] = "string" },
                ["description"] = "Explicit op classes the minted grant allows, e.g. [\"Read\"]. Required, at least one; \"*\" is not accepted.",
            },
            ["ttl"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Minutes until the minted grant expires. Optional, defaults to 60. \"*\" (infinite) is not accepted.",
            },
        },
        ["required"] = new[] { "scope_roots", "op_class" },
    });

    public Task<CallToolResult> InvokeAsync(IReadOnlyDictionary<string, JsonElement> arguments, Grant grant, CancellationToken cancellationToken)
    {
        List<string> scopeRoots;
        HashSet<string> opClasses;
        HashSet<string> vaults;
        int ttlMinutes;

        try
        {
            scopeRoots = ReadScopeRoots(arguments);
            vaults = DeriveVaults(scopeRoots);
            opClasses = ReadOpClasses(arguments);
            ttlMinutes = ReadTtl(arguments);
        }
        catch (MintArgumentException ex)
        {
            return Task.FromResult(ErrorResult(ex.Message));
        }

        var id = $"minted-{Guid.NewGuid():N}";
        var token = grantStore.Mint(id, opClasses, vaults, scopeRoots, ttlMinutes);

        var responseJson = JsonSerializer.Serialize(new
        {
            token,
            id,
            opClasses,
            vaults,
            scopeRoots,
            ttlMinutes,
            mintedBy = grant.Id,
        });
        return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = responseJson }] });
    }

    private static List<string> ReadScopeRoots(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var values = ReadStringArray(arguments, "scope_roots");
        if (values.Count == 0)
            throw new MintArgumentException("\"scope_roots\" is required and must contain at least one entry.");
        if (values.Any(v => v == "*"))
            throw new MintArgumentException("\"scope_roots\" does not accept \"*\" - list explicit folder-prefix roots.");
        return values;
    }

    private HashSet<string> DeriveVaults(List<string> scopeRoots)
    {
        var vaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scopeRoots)
        {
            var vaultName = root.Split('/', 2)[0];
            if (!vaultConfig.Vaults.Keys.Any(v => v.Equals(vaultName, StringComparison.OrdinalIgnoreCase)))
                throw new MintArgumentException($"\"scope_roots\" entry \"{root}\" names an unknown vault \"{vaultName}\".");
            vaults.Add(vaultName);
        }
        return vaults;
    }

    private HashSet<string> ReadOpClasses(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var values = ReadStringArray(arguments, "op_class");
        if (values.Count == 0)
            throw new MintArgumentException("\"op_class\" is required and must contain at least one entry.");
        if (values.Any(v => v == "*"))
            throw new MintArgumentException("\"op_class\" does not accept \"*\" - list explicit op classes.");

        var opClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var canonical = opClassRegistry.Canonicalize(value)
                ?? throw new MintArgumentException($"\"op_class\" entry \"{value}\" is not a known op class.");
            opClasses.Add(canonical);
        }
        return opClasses;
    }

    private static int ReadTtl(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (!arguments.TryGetValue("ttl", out var prop))
            return DefaultTtlMinutes;

        if (prop.ValueKind == JsonValueKind.String)
            throw new MintArgumentException("\"ttl\" does not accept \"*\" - pass an integer number of minutes, or omit it for the default (60).");

        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out var minutes))
            throw new MintArgumentException("\"ttl\" must be an integer number of minutes.");

        if (minutes <= 0)
            throw new MintArgumentException($"\"ttl\" must be a positive number of minutes, got {minutes}.");

        return minutes;
    }

    private static List<string> ReadStringArray(IReadOnlyDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var prop))
            return [];
        if (prop.ValueKind != JsonValueKind.Array)
            throw new MintArgumentException($"\"{name}\" must be an array of strings.");

        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } value)
                throw new MintArgumentException($"\"{name}\" must contain only non-empty strings.");
            result.Add(value);
        }
        return result;
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    /// <summary>Internal control-flow exception for a bad argument - caught in <see cref="InvokeAsync"/> and turned into a normal MCP tool error, never propagated.</summary>
    private sealed class MintArgumentException(string message) : Exception(message);
}
