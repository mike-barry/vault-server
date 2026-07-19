using System.Linq;
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
/// derived from it, while the remainder of each entry (the part after the leading vault
/// segment) is grouped per vault into <see cref="Grant.ScopeRoots"/>'s per-vault dictionary
/// shape for the folder-prefix containment check. A whole-vault entry (no subtree) produces an
/// <em>empty</em> set for that vault key, not an empty-string element - an empty/blank
/// scope-root string is malformed input on that branch, not a "matches everything" sentinel.
/// That containment check is wired up end to end for minted grants: <see cref="ScriptToolDispatcher.CallToolAsync"/>
/// passes <see cref="Grant.ScopeRoots"/> into <c>ScriptToolRunner</c>, which injects it into the
/// tool runspace as the <c>VaultMcpScopeRoots</c> global, and the PowerShell
/// <c>VaultAccess</c> chokepoint functions enforce it against every path they touch.
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

        Dictionary<string, IReadOnlySet<string>> scopeRootsByVault;
        try
        {
            scopeRoots = ReadScopeRoots(arguments);
            vaults = DeriveVaults(scopeRoots);
            scopeRootsByVault = GroupScopeRootsByVault(scopeRoots);
            opClasses = ReadOpClasses(arguments);
            ttlMinutes = ReadTtl(arguments);
        }
        catch (MintArgumentException ex)
        {
            return Task.FromResult(ErrorResult(ex.Message));
        }

        var id = $"minted-{Guid.NewGuid():N}";
        // GroupScopeRootsByVault derives scopeRootsByVault's keys from the same scope_roots
        // entries DeriveVaults derives vaults from, so it always has one entry per vault here -
        // no null/missing-key fallback needed; GrantStore.Mint also checks this defensively.
        var token = grantStore.Mint(id, opClasses, vaults, scopeRootsByVault, ttlMinutes);

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
            // A vault skipped at config load (root missing on disk) is configured but not
            // mintable-against: minting is an operation targeting the vault now, unlike a
            // grants.json row, which stays valid while the vault is broken.
            if (vaultConfig.SkippedVaults.ContainsKey(vaultName))
                throw new MintArgumentException($"\"scope_roots\" entry \"{root}\" names vault \"{vaultName}\", which is configured but currently unavailable: {vaultConfig.SkippedVaults[vaultName]}.");
            if (!vaultConfig.Vaults.ContainsKey(vaultName))
                throw new MintArgumentException($"\"scope_roots\" entry \"{root}\" names an unknown vault \"{vaultName}\".");
            vaults.Add(vaultName);
        }
        return vaults;
    }

    /// <summary>
    /// Splits each vault-prefixed <c>scope_roots</c> entry on its leading path segment (the
    /// vault name) and groups the remainders into a per-vault set, matching
    /// <see cref="Grant.ScopeRoots"/>'s <c>IReadOnlyDictionary&lt;string, IReadOnlySet&lt;string&gt;&gt;</c>
    /// shape. A whole-vault entry with no subtree (e.g. <c>"primary"</c>, no <c>/</c>) means "no
    /// restriction within this vault", which is represented by an <em>empty</em> set for that
    /// vault key - never by an empty-string element. Each subtree remainder is canonicalized via
    /// <see cref="VaultAccess.NormalizeScopeSegments"/> (the same validation
    /// <see cref="Auth.GrantStore.Load"/> applies to a file-loaded grant's scopeRoots), so a
    /// rooted, escaping, or empty remainder is rejected here as a mint argument error rather than
    /// producing a malformed scope root that fails closed later on an unrelated call. If the same
    /// mint call names both a whole-vault entry and a subtree
    /// entry for the same vault (e.g. <c>"primary"</c> and <c>"primary/Projects/Foo"</c>), the
    /// whole-vault entry wins: it already covers the subtree, so the subtree entry is redundant
    /// and dropped, leaving that vault's set empty. Entries for different vaults never leak into
    /// each other's set - e.g. <c>"primary/Projects/Foo"</c> and <c>"test/Areas/Bar"</c> produce
    /// two separate single-entry sets, one per vault key.
    /// </summary>
    private static Dictionary<string, IReadOnlySet<string>> GroupScopeRootsByVault(List<string> scopeRoots)
    {
        var byVault = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var wholeVault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scopeRoots)
        {
            var parts = root.Split('/', 2);
            var vaultName = parts[0];

            if (!byVault.TryGetValue(vaultName, out var subtrees))
            {
                subtrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byVault[vaultName] = subtrees;
            }

            // A subtree remainder must be canonicalized the same way GrantStore.Load canonicalizes
            // a file-loaded grant's scopeRoots (VaultAccess.NormalizeScopeSegments), so a minted
            // grant can never carry a rooted ("primary//x"), escaping ("primary/../x"), or empty
            // ("primary/") scope root - which would otherwise fail closed downstream with a
            // confusing error on some later, unrelated tool call. An entry with no subtree, or one
            // that normalizes to the vault root (e.g. a trailing slash), is a whole-vault grant.
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                string canonical;
                try
                {
                    canonical = string.Join('/', VaultAccess.NormalizeScopeSegments(parts[1]));
                }
                catch (ArgumentException ex)
                {
                    throw new MintArgumentException($"\"scope_roots\" entry \"{root}\" has an invalid subtree \"{parts[1]}\": {ex.Message}");
                }
                subtrees.Add(canonical);
            }
            else
                wholeVault.Add(vaultName);
        }

        foreach (var vaultName in wholeVault)
            byVault[vaultName].Clear();

        return byVault.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
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
