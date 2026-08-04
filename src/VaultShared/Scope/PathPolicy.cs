namespace VaultShared.Scope;

/// <summary>
/// Represents one vault's path policy: modifiers attached to targets within that vault, declared in
/// the vault's <c>vaults.json</c> entry and parsed by <c>PathPolicyParser</c>. A target is either a
/// vault-relative path or a named structural rule.
/// </summary>
/// <remarks>
/// <para>
/// There are two modifiers. <c>always_readable</c> is a read-scope floor: its target unions into a
/// grant's effective read scope for this vault, so an agent under any grant reaches the material it
/// is instructed to obey without that grant widening anywhere else.
/// <see cref="EffectiveReadScope"/> applies that union. <c>mutation_requires</c> names the op class
/// mutating a target requires, and contributions union, so no entry can clear one another entry
/// contributed.
/// </para>
/// <para>
/// <c>always_readable</c> applies to path targets only. It unions into scope roots, which are folder
/// prefixes, so a rule like "every folder note is readable" cannot be expressed as one. A
/// <see cref="StructuralRule"/> target therefore carries <c>mutation_requires</c> and nothing else.
/// </para>
/// <para>
/// This type declares the policy and enforces none of it. The frontmatter write path consults
/// <see cref="MutationRules"/> beside the authority assertion, including on a body-only edit, and the
/// delete and move tools consult it in their own validation, because neither routes through a
/// frontmatter write.
/// </para>
/// <para>
/// The read floor is a modifier here rather than a separate piece of configuration because a path
/// that instructs agents is special in both directions for the same reason. Stating it once per path
/// stops the two halves drifting into a path everyone can read that nobody protected.
/// </para>
/// </remarks>
public sealed class PathPolicy
{
    /// <summary>A vault that declares nothing. Every grant's read scope in it is exactly its own,
    /// and mutating any path in it requires nothing beyond the base write class.</summary>
    public static PathPolicy None { get; } = new([], []);

    /// <summary>Canonicalized, de-duplicated <c>always_readable</c> targets: vault-relative,
    /// forward-slash, in declared order.</summary>
    public IReadOnlyList<string> AlwaysReadableRoots { get; }

    /// <summary>The <c>mutation_requires</c> entries, in declared order. A path matches at most one
    /// of any given target, but several entries may match one path and every one of them
    /// contributes.</summary>
    public IReadOnlyList<MutationRule> MutationRules { get; }

    private PathPolicy(IReadOnlyList<string> alwaysReadableRoots, IReadOnlyList<MutationRule> mutationRules)
    {
        AlwaysReadableRoots = alwaysReadableRoots;
        MutationRules = mutationRules;
    }

    /// <summary>
    /// Builds a policy over already-canonicalized targets.
    /// </summary>
    /// <param name="alwaysReadableRoots">Canonicalized, de-duplicated always-readable targets.</param>
    /// <param name="mutationRules">The mutation rules, in declared order.</param>
    /// <returns>The policy, or <see cref="None"/> where both are empty.</returns>
    /// <remarks>
    /// Internal because only the parser may construct one. The targets have to be normalized and
    /// de-duplicated before they mean anything as scope roots, and a second producer would be a
    /// second place that can skip that.
    /// </remarks>
    internal static PathPolicy Of(IReadOnlyList<string> alwaysReadableRoots, IReadOnlyList<MutationRule> mutationRules) =>
        alwaysReadableRoots.Count == 0 && mutationRules.Count == 0
            ? None
            : new PathPolicy(alwaysReadableRoots, mutationRules);

    /// <summary>
    /// Returns every op class mutating a path requires, as a union. Several entries may match one
    /// path and every one of them contributes, so no entry can clear one another entry contributed.
    /// </summary>
    /// <param name="relativePath">The vault-relative path being mutated.</param>
    /// <param name="among">
    /// Which entries to consult. <see cref="PolicyTargets.All"/> is what a write asks and what "the
    /// cost of mutating this path" means. The two narrower answers exist for a move alone, where the
    /// two target kinds contribute under different rules: a path entry contributes whenever a
    /// declared file or folder is renamed or moved, while a structural rule contributes only where a
    /// target's artifact status changes.
    /// </param>
    /// <returns>The required op classes, empty where the policy says nothing about the path. Empty is
    /// the absence of an addition rather than evidence that the path is unprotected.</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not a well-formed
    /// vault-relative path. A path the policy cannot read is not one it may silently admit.</exception>
    /// <remarks>
    /// Matching is anchored at the vault root, compared by whole segment, and case-insensitive. Each
    /// of those closes an evasion: a batch rooted below the vault would hand the matcher relative
    /// paths that cannot match, a raw string prefix would catch a similarly named sibling directory,
    /// and a caller could otherwise reach a protected file by spelling it differently. A path entry
    /// matches that file and everything beneath it.
    ///
    /// This is the only matcher. Every enforcer calls it, so a second implementation would be a
    /// second answer.
    /// </remarks>
    public IReadOnlyCollection<string> MutationRequires(string relativePath, PolicyTargets among = PolicyTargets.All)
    {
        if (MutationRules.Count == 0)
            return [];

        var segments = ScopePath.Normalize(relativePath);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in MutationRules)
            if (Consulted(rule.Target, among) && Matches(rule.Target, segments))
                required.Add(rule.RequiredOpClass);

        return required;
    }

    private static bool Consulted(PolicyTarget target, PolicyTargets among) => among switch
    {
        PolicyTargets.Paths => target is PolicyTarget.Path,
        PolicyTargets.Structural => target is PolicyTarget.Structural,
        _ => true,
    };

    private static bool Matches(PolicyTarget target, string[] segments) => target switch
    {
        PolicyTarget.Path path => IsUnder(ScopePath.Normalize(path.Value), segments),
        PolicyTarget.Structural structural => structural.Rule switch
        {
            // A note whose basename matches its parent folder. This is a shape a path has rather
            // than a place it is, which a prefix target cannot express.
            StructuralRule.FolderNote =>
                segments.Length >= 2
                && System.IO.Path.GetFileNameWithoutExtension(segments[^1])
                    .Equals(segments[^2], StringComparison.OrdinalIgnoreCase)
                && segments[^1].EndsWith(".md", StringComparison.OrdinalIgnoreCase),
            _ => false,
        },
        _ => false,
    };

    private static bool IsUnder(string[] target, string[] segments) =>
        target.Length <= segments.Length
        && !target.Where((s, i) => !string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).Any();

    /// <summary>
    /// Represents one <c>mutation_requires</c> entry.
    /// </summary>
    /// <param name="Target">What the entry applies to.</param>
    /// <param name="RequiredOpClass">The op class mutating that target requires, named in the same
    /// vocabulary the grant model uses, so there is no second enumeration of protection levels to
    /// keep in step.</param>
    public sealed record MutationRule(PolicyTarget Target, string RequiredOpClass);

    /// <summary>
    /// Represents what a policy entry attaches its modifiers to. The two cases are closed because
    /// they are matched by different machinery: a path is a whole-segment prefix comparison, and a
    /// structural rule is code that inspects the path's shape.
    /// </summary>
    public abstract record PolicyTarget
    {
        private PolicyTarget() { }

        /// <summary>Represents a path target.</summary>
        /// <param name="Value">A vault-relative, canonicalized, forward-slash path, matched as a
        /// whole-segment prefix. A file entry matches that file, and a directory entry matches
        /// everything beneath it.</param>
        public sealed record Path(string Value) : PolicyTarget;

        /// <summary>Represents a structural-rule target.</summary>
        /// <param name="Rule">The rule from the closed set. The rule is code; the declaration decides
        /// whether it applies and what it costs.</param>
        public sealed record Structural(StructuralRule Rule) : PolicyTarget;
    }

    /// <summary>
    /// Returns the effective read scope for a grant: the union of the grant's own scope in this vault
    /// and the always-readable roots.
    /// </summary>
    /// <param name="grantScope">The grant's own scope in this vault.</param>
    /// <returns>The union, or the grant's own scope unchanged where the grant is unscoped or the
    /// policy declares no always-readable roots. With no policy this is the identity, so a read
    /// caller can call it unconditionally instead of testing whether a floor exists.</returns>
    /// <remarks>
    /// Call this only from read-class paths. Every write-class path, and every authority decision
    /// including the attenuation check when a token is minted, stays on the grant's own scope. A
    /// grant that can read the always-readable roots must not thereby be able to write them or to
    /// mint a token rooted at them.
    ///
    /// Pass the policy belonging to the vault the path is in, the same discipline
    /// <see cref="VaultShared.Auth.Grant.ScopeIn"/> enforces for scope roots.
    ///
    /// The union is applied here, at check time, rather than copied into a grant's roots when the
    /// grant is built. That is what makes it a floor: a copy would be attenuable, and a floor a
    /// minted token can drop is not a floor.
    ///
    /// It returns a <see cref="VaultScope"/> rather than answering a containment question, so
    /// enumeration runs over the same scope a containment check uses and needs no special case.
    /// </remarks>
    public VaultScope EffectiveReadScope(VaultScope grantScope)
    {
        if (AlwaysReadableRoots.Count == 0 || grantScope is not VaultScope.RootedScope rooted)
            return grantScope;

        var union = new HashSet<string>(rooted.Roots, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(AlwaysReadableRoots);
        return VaultScope.Of(union);
    }
}

/// <summary>
/// Selects which kinds of policy target a question consults. The two kinds are matched by different
/// machinery and, during a move, contribute under different rules, so a caller that needs them apart
/// says which it is asking about rather than re-deriving the split from
/// <see cref="PathPolicy.MutationRules"/>.
/// </summary>
public enum PolicyTargets
{
    /// <summary>Every entry, whatever its target. What a write asks.</summary>
    All,

    /// <summary>Path entries alone.</summary>
    Paths,

    /// <summary>Structural rules alone.</summary>
    Structural,
}

/// <summary>
/// The named structural rules a path policy entry may target, as a closed set. Each describes a shape
/// a path has rather than a place it is, which a prefix target cannot express.
/// </summary>
public enum StructuralRule
{
    /// <summary>A note whose basename matches its parent folder.</summary>
    FolderNote,
}

/// <summary>Maps between structural rules and the names <c>vaults.json</c> spells them with.</summary>
public static class StructuralRules
{
    /// <summary>Parses a rule name from a declaration.</summary>
    /// <param name="name">The name as written in <c>vaults.json</c>.</param>
    /// <returns>The rule, or null when the name is not one of them. The parser turns a null into a
    /// dropped entry rather than a rule nothing enforces.</returns>
    public static StructuralRule? Parse(string name) => name switch
    {
        "folder_note" => StructuralRule.FolderNote,
        _ => null,
    };

    /// <summary>Every name a declaration may write, so a refusal can say what it could have
    /// said.</summary>
    public static IReadOnlyList<string> Names { get; } = ["folder_note"];
}
