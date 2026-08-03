using System.ComponentModel;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>
/// <see cref="MintTokenTool"/>'s arguments: both the deserialization target and the source of the
/// tool's advertised <c>inputSchema</c> (see <see cref="NativeToolArguments"/>). The wire names are
/// the <c>snake_case</c> renderings of these members under
/// <see cref="NativeToolArguments.Options"/>' pinned naming policy.
///
/// <c>ScopeRoots</c> and <c>OpClass</c> carry no default, which is what makes them <c>required</c>
/// in the derived schema; they are still nullable because binding leaves an omitted member null
/// rather than refusing, so the tool can raise its own named refusal for the omission.
///
/// <see cref="Validate"/> is empty here, and deliberately so: every rule this tool enforces needs
/// either the op-class registry, the vault configuration, or the caller's own grant, and none of
/// those is reachable from a pure check. The refusals it raises instead are coded
/// <c>unknown_vault</c>, <c>out_of_scope</c> and <c>authority_required</c> as often as
/// <c>invalid_argument</c>, which an argument fault could not express either.
/// </summary>
public sealed record MintTokenArguments(
    [property: Description("Folder-prefix scope roots for the minted grant, e.g. \"primary\" (a whole vault) or \"primary/Projects/Foo\" (a subtree). At least one required; \"*\" is not accepted.")]
    List<string>? ScopeRoots,
    [property: Description("Explicit op classes the minted grant allows, e.g. [\"content:read\"]. Required, at least one; \"*\" is not accepted.")]
    List<string>? OpClass,
    [property: Description("Minutes until the minted grant expires. Optional, defaults to 60. \"*\" (infinite) is not accepted.")]
    int? Ttl = null) : IToolArguments
{
    public ArgumentFault? Validate() => null;
}

/// <summary>
/// Mints a new, in-memory, scoped, time-limited bearer grant via <see cref="GrantStore.Mint"/> -
/// gated by the <c>grants:manage</c> op class, which its declaration names as its floor and the
/// shared execute path checks before this tool runs. Target caller: an orchestrator spawning a
/// restricted child session without hand-editing grants.json.
///
/// It declares <see cref="ToolDeclaration.RequiresClientSession"/> because what it mints lands in
/// per-process authority state keyed to a live caller's token, which a process that exits after one
/// command has nothing to do with.
///
/// A minted grant's op_class and scope roots are always a concrete, explicit set - the
/// <c>"*"</c> wildcard is a grants.json convenience for hand-edited rows only and is rejected
/// here for every parameter, including <c>ttl</c> (always an integer number of minutes,
/// defaulting to 60 when omitted).
///
/// Attenuation-only minting: every requested dimension must be a subset of the *caller's own
/// currently-bound* grant (the <see cref="Grant"/> on the <see cref="ToolCall"/>, including any
/// active elevation binding). Op classes must be a subset of the caller's own; every vault implied
/// by <c>scope_roots</c> must be one the caller can access; each requested scope root must fall
/// within one of the caller's own scope roots for that vault (a caller unscoped in that vault
/// permits anything there); and the minted grant's absolute expiry is capped at the caller's own
/// remaining lifetime (an infinite caller imposes no cap) - see <see cref="GrantStore.Mint"/>'s
/// <c>maxExpiresAtUtc</c> parameter. This uniformly covers <c>grants:manage</c> too: a minted grant
/// may carry it iff the caller does, no special case. A violation raises a coded
/// <see cref="ToolRefusal"/> - <c>authority_required</c> for the op-class dimension,
/// <c>out_of_scope</c> for the vault and scope-root dimensions - whose <c>message</c> names the
/// dimension and the caller's actual bound authority, matching the repo's informative-denial
/// convention.
///
/// <c>data</c> follows the no-oracle rule this tool needs most, being the grant-administration
/// surface: it echoes only values the caller itself supplied (the argument name, the offending
/// <c>scope_roots</c> entry, vault, or op class) and never a set computed by filtering the request
/// against the grant's held authority. The prose - which does name that authority - is unchanged,
/// because attenuation is only reachable behind the <c>grants:manage</c> floor and telling a grant
/// administrator the bounds it may mint within is the point of the denial.
///
/// <c>scope_roots</c> entries are folder-prefix strings of the form <c>"&lt;vault&gt;"</c> (the
/// whole vault) or <c>"&lt;vault&gt;/&lt;folder-prefix&gt;"</c> (a subtree within it) - the
/// leading path segment names the vault, so the minted grant's <see cref="Grant.Vaults"/> set
/// (enforced by <see cref="Auth.BearerGrantMiddleware"/>'s vault-scope check) is derived from it,
/// while the remainder of each entry is grouped per vault into <see cref="Grant.ScopeRoots"/>'s
/// per-vault dictionary shape for the folder-prefix containment check. A whole-vault entry (no
/// subtree) produces <see cref="VaultScope.Unscoped"/> for that vault key, never an empty root set -
/// an empty/blank scope-root string is malformed input on that branch, and an empty set is
/// unconstructable by design (<see cref="VaultScope"/>), so neither can become a "matches
/// everything" sentinel.
/// </summary>
public sealed class MintTokenTool(GrantStore grantStore, OpClassRegistry opClassRegistry, VaultConfig vaultConfig) : ITool
{
    private const int DefaultTtlMinutes = 60;

    public const string ToolName = "mint_token";

    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Mints a new, scoped, time-limited bearer token in memory (never persisted to grants.json). " +
            "Requires the grants:manage op class. Returns the token once - it cannot be retrieved again.",
        Arguments = typeof(MintTokenArguments),
        OpClassFloor = new Floor.Class("grants:manage"),
        RequiresClientSession = true,
    };

    /// <summary>The minted grant, reported once. <c>Token</c> is the only member a caller cannot get
    /// back by another route.</summary>
    public sealed record MintReport(
        string Token, string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults,
        IReadOnlyList<string> ScopeRoots, int TtlMinutes, DateTimeOffset ExpiresAtUtc, bool TtlCapped, string MintedBy);

    public ToolResult Invoke(ToolCall call)
    {
        var args = call.ArgumentsAs<MintTokenArguments>();
        var grant = call.Grant;

        var scopeRoots = ReadScopeRoots(args);
        var vaults = DeriveVaults(scopeRoots);
        var scopeRootsByVault = GroupScopeRootsByVault(scopeRoots);
        var opClasses = ReadOpClasses(args);
        var ttlMinutes = ReadTtl(args);
        ValidateAttenuation(grant, opClasses, vaults, scopeRootsByVault);

        var id = $"minted-{Guid.NewGuid():N}";
        // GroupScopeRootsByVault derives scopeRootsByVault's keys from the same scope_roots
        // entries DeriveVaults derives vaults from, so it always has one entry per vault here -
        // no null/missing-key fallback needed; GrantStore.Mint also checks this defensively.
        var (token, expiresAtUtc, ttlCapped) = grantStore.Mint(id, opClasses, vaults, scopeRootsByVault, ttlMinutes, grant.ExpiresAtUtc);

        return new ToolResult.Data(new MintReport(
            token, id, opClasses, vaults, scopeRoots, ttlMinutes, expiresAtUtc, ttlCapped, grant.Id));
    }

    /// <summary>
    /// Attenuation-only minting: validates every requested dimension against <paramref name="grant"/>
    /// (the caller's own currently-bound grant), raising a refusal naming the first violated dimension
    /// and the caller's actual bound authority. TTL is not checked here - it is capped, not rejected,
    /// inside <see cref="GrantStore.Mint"/> (see its <c>maxExpiresAtUtc</c> parameter), since capping
    /// needs the store's own "now" to stay consistent with the rest of expiry handling.
    ///
    /// A request exceeding the caller's authority is refused rather than intersected down, so these are
    /// authority denials, not malformed arguments: <c>authority_required</c> for the op-class
    /// dimension - the same code the shared floor check raises, one condition at two layers - and
    /// <c>out_of_scope</c> for the vault and scope-root dimensions. Coding these
    /// <c>invalid_argument</c> would tell a caller to fix the request when the fix is elevation.
    ///
    /// Each <c>data</c> payload names the dimension and, where one exists, the caller's own operand -
    /// never the filtered subset that violated the check, because that subset is computed from the
    /// grant's held classes, vault set, or scope roots and would turn a replayable denial into a
    /// per-item enumeration of the caller's authority.
    /// </summary>
    private static void ValidateAttenuation(Grant grant, HashSet<string> opClasses, HashSet<string> vaults, Dictionary<string, VaultScope> scopeRootsByVault)
    {
        var missingOpClasses = opClasses.Where(o => !grant.OpClasses.Contains(o)).ToList();
        if (missingOpClasses.Count > 0)
            throw new ToolRefusal(
                ToolCodes.AuthorityRequired,
                $"\"op_class\" requests {FormatSet(missingOpClasses)}, which the caller's own grant \"{grant.Id}\" does not hold " +
                $"(caller's op classes: {FormatSet(grant.OpClasses)}).",
                new { argument = "op_class" });

        var missingVaults = vaults.Where(v => !grant.Vaults.Contains(v)).ToList();
        if (missingVaults.Count > 0)
            throw new ToolRefusal(
                ToolCodes.OutOfScope,
                $"\"scope_roots\" names vault{(missingVaults.Count > 1 ? "s" : "")} {FormatSet(missingVaults)} outside the caller's own grant " +
                $"\"{grant.Id}\" (caller's vaults: {FormatSet(grant.Vaults)}).",
                new { argument = "scope_roots" });

        foreach (var (vaultName, requestedScope) in scopeRootsByVault)
        {
            // A caller unscoped in this vault can delegate anything within it, so there is
            // nothing to attenuate against.
            if (grant.ScopeIn(vaultName) is not VaultScope.RootedScope callerScope)
                continue;

            var callerRoots = callerScope.Roots;

            if (requestedScope is not VaultScope.RootedScope requestedRooted)
                throw new ToolRefusal(
                    ToolCodes.OutOfScope,
                    $"\"scope_roots\" requests unscoped access to vault \"{vaultName}\", but the caller's own grant \"{grant.Id}\" " +
                    $"is scoped there to {FormatSet(callerRoots)}.",
                    new { argument = "scope_roots", vault = vaultName });

            var outOfScope = requestedRooted.Roots.Where(r => !callerScope.Admits(r)).ToList();
            if (outOfScope.Count > 0)
                throw new ToolRefusal(
                    ToolCodes.OutOfScope,
                    $"\"scope_roots\" entr{(outOfScope.Count > 1 ? "ies" : "y")} {FormatSet(outOfScope.Select(r => $"{vaultName}/{r}"))} fall outside " +
                    $"the caller's own grant \"{grant.Id}\" scope for vault \"{vaultName}\" ({FormatSet(callerRoots)}).",
                    new { argument = "scope_roots", vault = vaultName });
        }
    }

    private static string FormatSet(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => $"\"{v}\""));

    private static List<string> ReadScopeRoots(MintTokenArguments args)
    {
        var values = RequireNonEmptyEntries(args.ScopeRoots, "scope_roots");
        if (values.Count == 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"scope_roots\" is required and must contain at least one entry.", new { argument = "scope_roots" });
        if (values.Any(v => v == "*"))
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"scope_roots\" does not accept \"*\" - list explicit folder-prefix roots.", new { argument = "scope_roots" });
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
            // The two vault-resolution codes, classified by which config map holds the name
            // rather than by message text.
            if (vaultConfig.SkippedVaults.ContainsKey(vaultName))
                throw new ToolRefusal(
                    ToolCodes.VaultUnavailable,
                    $"\"scope_roots\" entry \"{root}\" names vault \"{vaultName}\", which is configured but currently unavailable: {vaultConfig.SkippedVaults[vaultName]}.",
                    new { argument = "scope_roots", scopeRoot = root, vault = vaultName });
            if (!vaultConfig.Vaults.ContainsKey(vaultName))
                throw new ToolRefusal(
                    ToolCodes.UnknownVault,
                    $"\"scope_roots\" entry \"{root}\" names an unknown vault \"{vaultName}\".",
                    new { argument = "scope_roots", scopeRoot = root, vault = vaultName });
            vaults.Add(vaultName);
        }
        return vaults;
    }

    /// <summary>
    /// Splits each vault-prefixed <c>scope_roots</c> entry on its leading path segment (the
    /// vault name) and groups the remainders into a per-vault set, matching
    /// <see cref="Grant.ScopeRoots"/>'s per-vault <see cref="VaultScope"/> shape. A whole-vault
    /// entry with no subtree (e.g. <c>"primary"</c>, no <c>/</c>) means "no restriction within this
    /// vault", which is <see cref="VaultScope.Unscoped"/> for that vault key - never an
    /// empty-string element and never an empty root set. Each subtree remainder is canonicalized via
    /// <see cref="ScopePath.Normalize"/> (the same validation
    /// <see cref="GrantStore.Load"/> applies to a file-loaded grant's scopeRoots), so a
    /// rooted, escaping, or empty remainder is rejected here as a mint argument error rather than
    /// producing a malformed scope root that fails closed later on an unrelated call. If the same
    /// mint call names both a whole-vault entry and a subtree
    /// entry for the same vault (e.g. <c>"primary"</c> and <c>"primary/Projects/Foo"</c>), the
    /// whole-vault entry wins: it already covers the subtree, so the subtree entry is redundant
    /// and dropped, leaving that vault unscoped. Entries for different vaults never leak into
    /// each other's scope - e.g. <c>"primary/Projects/Foo"</c> and <c>"test/Areas/Bar"</c> produce
    /// two separate single-root scopes, one per vault key.
    /// </summary>
    private static Dictionary<string, VaultScope> GroupScopeRootsByVault(List<string> scopeRoots)
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
            // a file-loaded grant's scopeRoots (ScopePath.Normalize), so a minted
            // grant can never carry a rooted ("primary//x"), escaping ("primary/../x"), or empty
            // ("primary/") scope root - which would otherwise fail closed downstream with a
            // confusing error on some later, unrelated tool call. An entry with no subtree, or one
            // that normalizes to the vault root (e.g. a trailing slash), is a whole-vault grant.
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                string canonical;
                try
                {
                    canonical = string.Join('/', ScopePath.Normalize(parts[1]));
                }
                catch (ArgumentException ex)
                {
                    throw new ToolRefusal(
                        ToolCodes.InvalidArgument,
                        $"\"scope_roots\" entry \"{root}\" has an invalid subtree \"{parts[1]}\": {ex.Message}",
                        new { argument = "scope_roots", scopeRoot = root });
                }
                subtrees.Add(canonical);
            }
            else
                wholeVault.Add(vaultName);
        }

        // A whole-vault entry subsumes any subtree entry for the same vault, so that vault
        // resolves to the unscoped case rather than to a set emptied of its subtrees.
        return byVault.ToDictionary(
            kv => kv.Key,
            kv => wholeVault.Contains(kv.Key) || kv.Value.Count == 0
                ? VaultScope.Unscoped
                : VaultScope.Of(kv.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> ReadOpClasses(MintTokenArguments args)
    {
        var values = RequireNonEmptyEntries(args.OpClass, "op_class");
        if (values.Count == 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"op_class\" is required and must contain at least one entry.", new { argument = "op_class" });
        if (values.Any(v => v == "*"))
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"op_class\" does not accept \"*\" - list explicit op classes.", new { argument = "op_class" });

        var opClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            // invalid_argument, not authority_required: the name is not in the registry at all,
            // so no elevation could ever satisfy it. Whether the caller *holds* a real class is the
            // separate attenuation check.
            var canonical = opClassRegistry.Canonicalize(value)
                ?? throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"op_class\" entry \"{value}\" is not a known op class.", new { argument = "op_class", opClass = value });
            opClasses.Add(canonical);
        }
        return opClasses;
    }

    /// <summary>
    /// Range check, not a type check: the schema says <c>ttl</c> is an integer, and binding already
    /// refused anything that is not one, but "a positive number of minutes" is a meaning JSON Schema
    /// has no way to carry into the tool's own vocabulary. An omitted <c>ttl</c> binds to null and
    /// takes the documented default.
    /// </summary>
    private static int ReadTtl(MintTokenArguments args)
    {
        if (args.Ttl is not { } minutes)
            return DefaultTtlMinutes;
        if (minutes <= 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"ttl\" must be a positive number of minutes, got {minutes}.", new { argument = "ttl" });
        return minutes;
    }

    /// <summary>
    /// The one shape rule binding leaves open: <c>items: {"type": "string"}</c> admits <c>""</c> and
    /// a JSON <c>null</c> element, both of which are malformed scope roots and op classes here.
    /// A missing argument binds to null and is reported as missing by the caller, not here.
    /// </summary>
    private static List<string> RequireNonEmptyEntries(List<string>? values, string name)
    {
        if (values is null)
            return [];
        if (values.Any(string.IsNullOrEmpty))
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"{name}\" must contain only non-empty strings.", new { argument = name });
        return values;
    }
}
