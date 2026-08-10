using System.ComponentModel;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultMcpServer.Tools;

/// <summary>
/// Represents <see cref="MintTokenTool"/>'s arguments. This record is both the deserialization
/// target and the source of the tool's advertised <c>inputSchema</c> (see
/// <see cref="NativeToolArguments"/>); the wire names are the snake_case renderings of these
/// members under <see cref="NativeToolArguments.Options"/>' naming policy.
/// </summary>
/// <remarks>
/// <c>ScopeRoots</c> and <c>OpClass</c> carry no default, which is what makes them
/// <c>required</c> in the derived schema. They are still nullable, because binding leaves an
/// omitted member null rather than refusing, which lets the tool raise its own named refusal.
///
/// <see cref="Validate"/> returns null because every rule this tool enforces needs the op-class
/// registry, the vault configuration, or the caller's own grant, none of which a pure check can
/// reach. The refusals it raises instead are coded <c>unknown_vault</c>, <c>out_of_scope</c>, and
/// <c>authority_required</c> as often as <c>invalid_argument</c>, which an argument fault cannot
/// express.
/// </remarks>
/// <param name="ScopeRoots">The vault-prefixed folder scope roots the minted grant is limited to.</param>
/// <param name="OpClass">The op classes the minted grant allows.</param>
/// <param name="Ttl">The minted grant's lifetime in minutes, or null to use the default.</param>
public sealed record MintTokenArguments(
    [property: Description("Folder-prefix scope roots for the minted grant, e.g. \"primary\" (a whole vault) or \"primary/Projects/Foo\" (a subtree). At least one required; \"*\" is not accepted.")]
    List<string>? ScopeRoots,
    [property: Description("Explicit op classes the minted grant allows, e.g. [\"content:read\"]. Required, at least one; \"*\" is not accepted.")]
    List<string>? OpClass,
    [property: Description("Minutes until the minted grant expires. Optional, defaults to 60. \"*\" (infinite) is not accepted.")]
    int? Ttl = null) : IToolArguments
{
    /// <summary>Performs no validation; see this record's remarks.</summary>
    /// <returns>Always null.</returns>
    public ArgumentFault? Validate() => null;
}

/// <summary>
/// Mints a new in-memory, scoped, time-limited bearer grant through
/// <see cref="GrantStore.Mint"/>. Its declaration names <c>grants:manage</c> as its floor, which
/// the shared execute path checks before this tool runs. It exists for an orchestrator spawning a
/// restricted child session without hand-editing grants.json.
/// </summary>
/// <remarks>
/// <para>
/// The declaration sets <see cref="ToolDeclaration.RequiresClientSession"/>, because what it
/// mints lands in per-process authority state keyed to a live caller's token.
/// </para>
/// <para>
/// A minted grant's op classes and scope roots are always an explicit set. The <c>"*"</c>
/// wildcard is a convenience for hand-edited grants.json rows only and is rejected here for every
/// argument, including <c>ttl</c>, which is always an integer number of minutes and defaults to
/// 60.
/// </para>
/// <para>
/// Minting can only attenuate, so every requested dimension must be within the caller's own
/// currently-bound grant, including any active elevation binding. Op classes must be a subset of
/// the caller's; every vault implied by <c>scope_roots</c> must be one the caller can access;
/// each requested scope root must fall within one of the caller's own scope roots for that vault,
/// where a caller unscoped in a vault permits anything there; and the minted grant's expiry is
/// capped at the caller's own remaining lifetime, with a caller that never expires imposing no
/// cap (see <see cref="GrantStore.Mint"/>'s <c>maxExpiresAtUtc</c> parameter). This covers
/// <c>grants:manage</c> itself with no special case, so a minted grant may carry it only if the
/// caller does. A violation raises a <see cref="ToolRefusal"/> coded
/// <c>authority_required</c> for the op-class dimension and <c>out_of_scope</c> for the vault and
/// scope-root dimensions, whose message names the dimension and the caller's actual bound
/// authority.
/// </para>
/// <para>
/// The <c>data</c> of a refusal echoes only values the caller itself supplied - the argument
/// name and the offending <c>scope_roots</c> entry, vault, or op class - and never a set computed
/// by filtering the request against the grant's held authority. The message does name that
/// authority, because attenuation is reachable only behind the <c>grants:manage</c> floor and
/// telling a grant administrator the bounds it may mint within is the point of the denial.
/// </para>
/// <para>
/// A <c>scope_roots</c> entry is either <c>"&lt;vault&gt;"</c> for a whole vault or
/// <c>"&lt;vault&gt;/&lt;folder-prefix&gt;"</c> for a subtree within it. The leading segment
/// names the vault, so the minted grant's <see cref="Grant.Vaults"/> set - which
/// <see cref="Auth.BearerGrantMiddleware"/>'s vault-scope check enforces - is derived from it,
/// while the remainder is grouped per vault into <see cref="Grant.ScopeRoots"/> for the
/// folder-prefix containment check. A whole-vault entry produces
/// <see cref="VaultScope.Unscoped"/> for that vault, never an empty root set. An empty or blank
/// scope-root string is malformed input, and <see cref="VaultScope"/> cannot construct an empty
/// set, so neither can become a "matches everything" sentinel.
/// </para>
/// </remarks>
/// <param name="grantStore">Mints the grant and applies the expiry cap.</param>
/// <param name="opClassRegistry">Validates and canonicalizes the requested op classes.</param>
/// <param name="vaultConfig">Resolves the vaults the requested scope roots name.</param>
public sealed class MintTokenTool(GrantStore grantStore, OpClassRegistry opClassRegistry, VaultConfig vaultConfig) : ITool
{
    private const int DefaultTtlMinutes = 60;

    /// <summary>The tool's name on the wire.</summary>
    public const string ToolName = "mint_token";

    /// <summary>The declaration the tool registry and the shared execute path work from.</summary>
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

    /// <summary>Represents the minted grant, reported once.</summary>
    /// <param name="Token">The bearer token. This is the only member a caller cannot obtain by
    /// another route.</param>
    /// <param name="Id">The minted grant's id.</param>
    /// <param name="OpClasses">The op classes the grant allows.</param>
    /// <param name="Vaults">The vaults the grant can access.</param>
    /// <param name="ScopeRoots">The vault-prefixed scope roots as the caller supplied them.</param>
    /// <param name="TtlMinutes">The requested lifetime in minutes.</param>
    /// <param name="ExpiresAtUtc">The grant's absolute expiry.</param>
    /// <param name="TtlCapped">Whether the expiry was capped at the caller's own remaining
    /// lifetime.</param>
    /// <param name="MintedBy">The id of the caller's grant.</param>
    public sealed record MintReport(
        string Token, string Id, IReadOnlySet<string> OpClasses, IReadOnlySet<string> Vaults,
        IReadOnlyList<string> ScopeRoots, int TtlMinutes, DateTimeOffset ExpiresAtUtc, bool TtlCapped, string MintedBy);

    /// <summary>Mints a grant attenuated from the caller's own and returns it once.</summary>
    /// <param name="call">The call, carrying the arguments and the caller's grant.</param>
    /// <returns>The minted grant's report, including its token.</returns>
    /// <exception cref="ToolRefusal">An argument is malformed, names an unknown or unavailable
    /// vault or op class, or requests authority beyond the caller's own grant.</exception>
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
        // GroupScopeRootsByVault and DeriveVaults derive their keys from the same scope_roots
        // entries, so there is always one scope entry per vault here and no missing-key fallback
        // is needed. GrantStore.Mint also checks this.
        var (token, expiresAtUtc, ttlCapped) = grantStore.Mint(id, opClasses, vaults, scopeRootsByVault, ttlMinutes, grant.ExpiresAtUtc);

        return new ToolResult.Data(new MintReport(
            token, id, opClasses, vaults, scopeRoots, ttlMinutes, expiresAtUtc, ttlCapped, grant.Id));
    }

    /// <summary>
    /// Validates every requested dimension against the caller's own currently-bound grant,
    /// raising a refusal that names the first violated dimension and the caller's actual bound
    /// authority. The TTL is not checked here. <see cref="GrantStore.Mint"/> caps it rather than
    /// rejecting it, because capping needs the store's own clock to stay consistent with the rest
    /// of expiry handling.
    /// </summary>
    /// <remarks>
    /// A request exceeding the caller's authority is refused rather than narrowed to fit, so
    /// these are authority denials rather than malformed arguments: <c>authority_required</c> for
    /// the op-class dimension, the same code the shared floor check raises, and
    /// <c>out_of_scope</c> for the vault and scope-root dimensions. Coding them
    /// <c>invalid_argument</c> would tell a caller to fix the request when the fix is elevation.
    ///
    /// Each refusal's <c>data</c> names the dimension and, where one exists, the caller's own
    /// operand, never the filtered subset that violated the check. That subset is computed from
    /// the grant's held classes, vault set, or scope roots, so returning it would let a caller
    /// enumerate its own authority one replayed denial at a time.
    /// </remarks>
    /// <param name="grant">The caller's currently-bound grant.</param>
    /// <param name="opClasses">The op classes requested for the minted grant.</param>
    /// <param name="vaults">The vaults implied by the requested scope roots.</param>
    /// <param name="scopeRootsByVault">The requested scope roots, grouped by vault.</param>
    /// <exception cref="ToolRefusal">A requested dimension exceeds the caller's grant.</exception>
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

    /// <summary>Renders a set of values as a quoted, comma-separated list for a message.</summary>
    /// <param name="values">The values to render.</param>
    /// <returns>The rendered list.</returns>
    private static string FormatSet(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => $"\"{v}\""));

    /// <summary>Reads and checks the shape of the <c>scope_roots</c> argument.</summary>
    /// <param name="args">The call's arguments.</param>
    /// <returns>The requested scope roots.</returns>
    /// <exception cref="ToolRefusal">The argument is missing, empty, contains an empty entry, or
    /// contains <c>"*"</c>.</exception>
    private static List<string> ReadScopeRoots(MintTokenArguments args)
    {
        var values = RequireNonEmptyEntries(args.ScopeRoots, "scope_roots");
        if (values.Count == 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"scope_roots\" is required and must contain at least one entry.", new { argument = "scope_roots" });
        if (values.Any(v => v == "*"))
            throw new ToolRefusal(ToolCodes.InvalidArgument, "\"scope_roots\" does not accept \"*\" - list explicit folder-prefix roots.", new { argument = "scope_roots" });
        return values;
    }

    /// <summary>Derives the set of vaults the requested scope roots name.</summary>
    /// <param name="scopeRoots">The requested scope roots.</param>
    /// <returns>The vault names, compared case-insensitively.</returns>
    /// <exception cref="ToolRefusal">An entry names an unknown or currently unavailable
    /// vault.</exception>
    private HashSet<string> DeriveVaults(List<string> scopeRoots)
    {
        var vaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scopeRoots)
        {
            var vaultName = root.Split('/', 2)[0];
            // A vault skipped at config load, because its root was missing on disk, is
            // configured but cannot be minted against, because minting targets the vault now,
            // unlike a grants.json row, which stays valid while the vault is broken. The two
            // vault-resolution codes are chosen by which config map holds the name, never by
            // message text.
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
    /// Splits each vault-prefixed <c>scope_roots</c> entry on its leading path segment and groups
    /// the remainders per vault, matching <see cref="Grant.ScopeRoots"/>'s shape.
    /// </summary>
    /// <remarks>
    /// An entry naming a whole vault, such as <c>"primary"</c> with no <c>/</c>, means no
    /// restriction within that vault, which is <see cref="VaultScope.Unscoped"/> for that key,
    /// never an empty-string element and never an empty root set.
    ///
    /// Each subtree remainder is canonicalized through <see cref="ScopePath.Normalize"/>, the
    /// same validation <see cref="GrantStore.Load"/> applies to a file-loaded grant's scope
    /// roots, so a rooted, escaping, or empty remainder is refused here as a mint argument error
    /// rather than producing a malformed scope root that fails closed later on an unrelated call.
    ///
    /// When one call names both a whole vault and a subtree of it, the whole-vault entry wins and
    /// the subtree entry is dropped as redundant, leaving that vault unscoped. Entries for
    /// different vaults never affect each other.
    /// </remarks>
    /// <param name="scopeRoots">The requested scope roots.</param>
    /// <returns>The scope for each named vault.</returns>
    /// <exception cref="ToolRefusal">An entry's subtree does not normalize.</exception>
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

            // An entry with no subtree, or one that normalizes to the vault root such as a
            // trailing slash, is a whole-vault grant. Every other remainder is canonicalized, so
            // a minted grant can never carry a rooted ("primary//x"), escaping ("primary/../x"),
            // or empty ("primary/") scope root.
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

    /// <summary>Reads the <c>op_class</c> argument and canonicalizes each entry.</summary>
    /// <param name="args">The call's arguments.</param>
    /// <returns>The canonical op-class names, compared case-insensitively.</returns>
    /// <exception cref="ToolRefusal">The argument is missing, empty, contains an empty entry,
    /// contains <c>"*"</c>, or names a class the registry does not know.</exception>
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
            // invalid_argument rather than authority_required, because the name is not in the
            // registry at all and no elevation could satisfy it. Whether the caller holds a real
            // class is the separate attenuation check.
            var canonical = opClassRegistry.Canonicalize(value)
                ?? throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"op_class\" entry \"{value}\" is not a known op class.", new { argument = "op_class", opClass = value });
            opClasses.Add(canonical);
        }
        return opClasses;
    }

    /// <summary>
    /// Reads the <c>ttl</c> argument. This is a range check rather than a type check. Binding
    /// already refused anything that is not an integer, and JSON Schema cannot say that the
    /// integer must be positive.
    /// </summary>
    /// <param name="args">The call's arguments.</param>
    /// <returns>The requested lifetime in minutes, or the default when <c>ttl</c> was
    /// omitted.</returns>
    /// <exception cref="ToolRefusal">The value is zero or negative.</exception>
    private static int ReadTtl(MintTokenArguments args)
    {
        if (args.Ttl is not { } minutes)
            return DefaultTtlMinutes;
        if (minutes <= 0)
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"ttl\" must be a positive number of minutes, got {minutes}.", new { argument = "ttl" });
        return minutes;
    }

    /// <summary>
    /// Checks the one shape rule binding leaves open: <c>items: {"type": "string"}</c> admits
    /// <c>""</c> and a JSON <c>null</c> element, neither of which is a usable scope root or op
    /// class.
    /// </summary>
    /// <param name="values">The argument's entries, or null when the argument was omitted.</param>
    /// <param name="name">The argument's wire name, used in the refusal.</param>
    /// <returns>The entries, or an empty list when the argument was omitted. The caller reports
    /// the omission itself.</returns>
    /// <exception cref="ToolRefusal">An entry is null or empty.</exception>
    private static List<string> RequireNonEmptyEntries(List<string>? values, string name)
    {
        if (values is null)
            return [];
        if (values.Any(string.IsNullOrEmpty))
            throw new ToolRefusal(ToolCodes.InvalidArgument, $"\"{name}\" must contain only non-empty strings.", new { argument = name });
        return values;
    }
}
