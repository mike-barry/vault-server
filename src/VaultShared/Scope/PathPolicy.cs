namespace VaultShared.Scope;

/// <summary>
/// One vault's path policy: modifiers attached to targets within that vault, declared in its
/// <c>vaults.json</c> entry and parsed by <c>PathPolicyParser</c>. A target is a vault-relative
/// path or a named structural rule; a modifier says something the tooling must do about that
/// target.
///
/// Two modifiers are carried. <c>always_readable</c> is the read-class scope floor: its target
/// unions into a grant's effective read scope for this vault, so an agent under any grant reaches
/// the material it is instructed to obey without that grant widening anywhere else.
/// <see cref="EffectiveReadScope"/> is where that union is applied. <c>mutation_requires</c> names
/// the op class mutating a target requires, and contributions union - no entry can clear one
/// another contributed.
///
/// <c>always_readable</c> is path-only: it is unioned into scope roots and those are folder
/// prefixes, so "every folder note is readable" is not expressible as one. A
/// <see cref="StructuralRule"/> target therefore carries <c>mutation_requires</c> and nothing else.
///
/// One declaration does not mean one enforcer, and none of them is here. The write path consults
/// <see cref="MutationRules"/> beside the authority assertion, including on a body-only edit, and
/// the tools that remove or relocate consult it at their own validate phases, since neither routes
/// through a frontmatter write.
///
/// Why the floor is a modifier here rather than its own configuration: a path that instructs
/// agents is special in both directions for one reason, and stating that once per path keeps the
/// two halves from drifting into a path everyone reads that nobody protected.
///
/// This type is a scope value rather than a configuration one. Its input is a vault's
/// configuration and its output is a <see cref="VaultScope"/>, and the read path is its only
/// consumer, so it sits with the scope model and the parser that builds it sits with the file it
/// parses.
/// </summary>
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
    /// Builds a policy over already-canonicalized targets. Internal because construction is the
    /// parser's alone: the targets have to be normalized and de-duplicated before they mean
    /// anything as scope roots, and a second producer is a second place that can skip it.
    /// </summary>
    internal static PathPolicy Of(IReadOnlyList<string> alwaysReadableRoots, IReadOnlyList<MutationRule> mutationRules) =>
        alwaysReadableRoots.Count == 0 && mutationRules.Count == 0
            ? None
            : new PathPolicy(alwaysReadableRoots, mutationRules);

    /// <summary>
    /// What mutating <paramref name="relativePath"/> requires, as a union: several entries may match
    /// one path and every one of them contributes, so no entry can clear one another contributed.
    /// Empty where the policy says nothing about it, which is not evidence that the path is
    /// unprotected - it is the absence of an addition.
    ///
    /// Matching is anchored at the vault root, by whole segment, and case-insensitive. Each closes
    /// an evasion: a batch rooted below the vault would hand the matcher relative paths that cannot
    /// match, a raw string prefix would catch a similarly named sibling directory, and a caller
    /// could otherwise reach a protected file by spelling it differently. A path entry matches that
    /// file and everything beneath it.
    ///
    /// One matcher for every enforcer. The frontmatter write path consults it beside the authority
    /// assertion, including on a body-only edit, and the tools that remove or relocate consult it at
    /// their own validate phases; a second implementation is a second answer.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not a well-formed
    /// vault-relative path. A path a policy cannot read is not one it may silently admit.</exception>
    public IReadOnlyCollection<string> MutationRequires(string relativePath)
    {
        if (MutationRules.Count == 0)
            return [];

        var segments = ScopePath.Normalize(relativePath);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in MutationRules)
            if (Matches(rule.Target, segments))
                required.Add(rule.RequiredOpClass);

        return required;
    }

    private static bool Matches(PolicyTarget target, string[] segments) => target switch
    {
        PolicyTarget.Path path => IsUnder(ScopePath.Normalize(path.Value), segments),
        PolicyTarget.Structural structural => structural.Rule switch
        {
            // A note whose basename matches its parent folder. A shape a path has rather than a
            // place it is, which is what a prefix target cannot express.
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
    /// One <c>mutation_requires</c> entry: what mutating <paramref name="Target"/> requires, named
    /// as an op class in the same vocabulary the grant model uses, so there is no second
    /// enumeration of protection levels to keep in step.
    /// </summary>
    public sealed record MutationRule(PolicyTarget Target, string RequiredOpClass);

    /// <summary>
    /// What a policy entry attaches its modifiers to. A closed pair, because the two are matched by
    /// different machinery: a path is a whole-segment prefix comparison and a structural rule is
    /// code that inspects the path's shape.
    /// </summary>
    public abstract record PolicyTarget
    {
        private PolicyTarget() { }

        /// <summary>A vault-relative, canonicalized, forward-slash path, matched as a whole-segment
        /// prefix - a file entry matches that file and a directory entry matches everything
        /// beneath it.</summary>
        public sealed record Path(string Value) : PolicyTarget;

        /// <summary>A named rule from the closed set. The rule is code; the declaration decides
        /// whether it applies and at what price.</summary>
        public sealed record Structural(StructuralRule Rule) : PolicyTarget;
    }

    /// <summary>
    /// The effective read scope for a grant whose own scope in this vault is
    /// <paramref name="grantScope"/>: the union of that scope and the always-readable roots.
    ///
    /// An unscoped grant is returned unchanged - it already reaches everything. With no policy
    /// this is the identity, so a read caller can call it unconditionally rather than branching on
    /// whether a floor exists. It returns a <see cref="VaultScope"/> rather than answering a
    /// containment question, so enumeration runs over the same scope a containment check uses and
    /// needs no special case of its own.
    ///
    /// Applied here, at check time, rather than copied into a grant's roots when the grant is
    /// built - which is what makes it a floor. A copy would be attenuable, and a floor a minted
    /// token can drop is not one.
    ///
    /// <b>Read-class callers only.</b> Every write-class path - and every authority decision,
    /// including mint's attenuation check - stays on the grant's own scope. A grant that can read
    /// the always-readable roots must not thereby be able to write them, nor to mint a token
    /// rooted at them.
    ///
    /// The policy must be the one belonging to the vault the path is in - the same discipline
    /// <see cref="VaultShared.Auth.Grant.ScopeIn"/> enforces for scope roots.
    /// </summary>
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
/// The named structural rules a path policy entry may target, as a closed set. Each is a shape a
/// path has rather than a place it is, which is what a prefix target cannot express.
/// </summary>
public enum StructuralRule
{
    /// <summary>A note whose basename matches its parent folder.</summary>
    FolderNote,
}

/// <summary>The rule names as <c>vaults.json</c> spells them.</summary>
public static class StructuralRules
{
    /// <summary>The rule a declaration names, or null when the name is not one of them - which the
    /// parser turns into a dropped entry rather than a rule nothing enforces.</summary>
    public static StructuralRule? Parse(string name) => name switch
    {
        "folder_note" => StructuralRule.FolderNote,
        _ => null,
    };

    /// <summary>Every name a declaration may write, for a refusal that says what it could have said.</summary>
    public static IReadOnlyList<string> Names { get; } = ["folder_note"];
}
