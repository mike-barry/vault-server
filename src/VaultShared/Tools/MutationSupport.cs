using VaultShared.Access;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>
/// Provides the pieces shared by the mutating tools that do not route through the frontmatter write
/// path: the artifact-level authority assertion, the partial-failure refusal, the name-grammar
/// refusal, and the empty vocabulary a write that interprets no frontmatter runs against.
/// </summary>
internal static class MutationSupport
{
    /// <summary>
    /// The empty vocabulary a note write that interprets no frontmatter runs against.
    /// </summary>
    /// <remarks>
    /// This is not a fallback vocabulary. It is reachable only where the write carries no operation
    /// and no supplied block, so no key is ever looked up in it and it can neither recognize nor
    /// refuse one. A write that does interpret frontmatter with no declaration loaded is refused by
    /// <see cref="RegistryState.Require"/> instead.
    /// </remarks>
    internal static readonly FrontmatterRegistry NoVocabulary = new(
        new Dictionary<string, KeyDeclaration>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, EntrySchema>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Asserts the artifact-level authority a set of targets requires, and returns the op classes
    /// that were asserted.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="targets">Each target path and the op classes the vault's path policy requires to
    /// mutate it.</param>
    /// <returns>The distinct op classes asserted, in ordinal order.</returns>
    /// <exception cref="ToolRefusal">The grant holds none of a required class.</exception>
    /// <remarks>
    /// The delete and move tools do not route through the frontmatter write path, so each calls this
    /// during its own validation, before anything is mutated. Requirements union across the set, so
    /// no target can clear a class another target contributed.
    ///
    /// The refusal names the paths in the caller's own target set that carry the class that was
    /// denied. A caller told only that something inside a folder needed authority it lacks cannot
    /// act on that. Those paths come from the vault's path policy applied to the caller's own
    /// operands and say nothing about the grant.
    /// </remarks>
    public static IReadOnlyList<string> AssertArtifactAuthority(
        VaultAccess vault, IEnumerable<PathRequirement> targets)
    {
        var byClass = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (path, required) in targets)
            foreach (var opClass in required)
            {
                if (!byClass.TryGetValue(opClass, out var paths))
                    byClass[opClass] = paths = new SortedSet<string>(StringComparer.Ordinal);
                paths.Add(path);
            }

        foreach (var (opClass, paths) in byClass)
        {
            try
            {
                vault.AssertKey(opClass);
            }
            catch (AuthorityRefusal)
            {
                throw new ToolRefusal(
                    ToolCodes.AuthorityRequired,
                    $"This vault's path policy requires {opClass} to mutate {Describe(paths)}.",
                    new { paths = paths.ToArray() });
            }
        }

        return [.. byClass.Keys];
    }

    /// <summary>
    /// Builds the refusal a mutation over several targets raises when it fails partway.
    /// </summary>
    /// <param name="message">The caller-visible message.</param>
    /// <param name="applied">The targets the mutation completed.</param>
    /// <param name="notApplied">The targets it did not.</param>
    /// <param name="recovery">Ordered steps the caller can take to reach a consistent state.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// The write lock means no other tool interleaved. It does not mean the change is rolled back,
    /// so the payload names exactly what landed and what did not, and the recovery steps are the
    /// caller's route out of that state.
    ///
    /// Recovery steps must be non-destructive and in order. Never tell a caller to remove something
    /// to make room. Neither a move nor a delete ever leaves content in two places, because a move
    /// relocates bytes rather than copying them and a delete only unlinks.
    /// </remarks>
    public static ToolRefusal Partial(
        string message,
        IReadOnlyList<string> applied,
        IReadOnlyList<string> notApplied,
        IReadOnlyList<string> recovery) =>
        new(ToolCodes.PartialMutation, message, new { applied, notApplied, recovery });

    /// <summary>
    /// Builds the refusal for a name outside the name grammar.
    /// </summary>
    /// <param name="name">The segment the caller sent.</param>
    /// <returns>The refusal. Where a conforming spelling can be derived from the caller's text, the
    /// payload carries exactly one, so the miss costs one retry rather than a reading of the
    /// grammar. None is offered where the text carries no conforming characters at all.</returns>
    public static ToolRefusal InvalidName(string name)
    {
        const string Grammar =
            "groups of ASCII letters, digits, \"+\" and \"&\" joined by single dashes, starting with an upper-case letter or a digit";

        return VaultName.Suggest(name) is { } suggested
            ? new ToolRefusal(
                ToolCodes.InvalidName,
                $"\"{name}\" is outside the name grammar - {Grammar}. \"{suggested}\" would be accepted.",
                new { suggested })
            : new ToolRefusal(ToolCodes.InvalidName, $"\"{name}\" is outside the name grammar - {Grammar}.");
    }

    /// <summary>Translates the failure of a pass over every note in the vault into this vocabulary.
    /// A pass that could not enumerate or could not read returns no answer at all, because a partial
    /// answer would under-report exactly the links a caller needs.</summary>
    /// <typeparam name="TResult">The pass's result type.</typeparam>
    /// <param name="outcome">A pass outcome the caller has already matched the completed case
    /// out of.</param>
    /// <returns>The refusal.</returns>
    public static ToolRefusal PassFailure<TResult>(Links.LinkPassOutcome<TResult> outcome) => outcome switch
    {
        Links.LinkPassOutcome<TResult>.EnumerationFailed enumeration =>
            ToolRefusalEnvelope.Translate(enumeration.Outcome),
        Links.LinkPassOutcome<TResult>.ReadFailed read => new ToolRefusal(
            ToolCodes.ReadFailed,
            $"\"{read.Path}\" is there and could not be read, so the vault-wide wikilink pass has no answer: {read.Reason}",
            new { path = read.Path }),
        var other => new ToolRefusal(
            ToolCodes.SearchFailed,
            $"The vault-wide wikilink pass did not complete ({other.GetType().Name})."),
    };

    private static string Describe(SortedSet<string> paths) =>
        paths.Count == 1 ? $"\"{paths.Min}\"" : $"{paths.Count} paths in this operation's target set";
}

/// <summary>Pairs one target of an operation with what the vault's path policy requires to mutate
/// it.</summary>
/// <param name="Path">The vault-relative target path.</param>
/// <param name="Required">The op classes the path policy requires for that path.</param>
internal readonly record struct PathRequirement(string Path, IReadOnlyCollection<string> Required);
