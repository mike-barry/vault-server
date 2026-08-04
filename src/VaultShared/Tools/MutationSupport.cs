using VaultShared.Access;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>
/// The pieces the three mutating tools that do not route through the frontmatter write path share:
/// the artifact-level assertion, the partial-failure refusal, and the vocabulary a write that
/// interprets no frontmatter runs against. They sit together because each is one rule for the whole
/// tool set, and a copy per tool is a copy that can drift.
/// </summary>
internal static class MutationSupport
{
    /// <summary>
    /// The vocabulary a note write that interprets no frontmatter runs against. <b>Not a fallback
    /// vocabulary</b>: it is reachable only where the write carries no operation and no supplied
    /// block, so no key is ever looked up in it and it can neither recognize nor refuse one. What
    /// refuses a write that does interpret frontmatter, with no declaration loaded, is
    /// <see cref="RegistryState.Require"/>.
    /// </summary>
    internal static readonly FrontmatterRegistry NoVocabulary = new(
        new Dictionary<string, KeyDeclaration>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, EntrySchema>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Asserts the artifact-level authority a set of targets requires, and answers what was
    /// asserted.
    ///
    /// <b>Neither <c>delete</c> nor <c>move</c> routes through the frontmatter write path</b>, so
    /// each consults the vault's path policy in its own validate phase, before the lock is used for
    /// anything and with zero mutation on denial. Contributions union across the set - no target can
    /// clear a class another contributed.
    ///
    /// The refusal names the paths in the operation's own set that carry the class it asked for.
    /// A caller told that something in a folder needed authority it lacks, without being told what,
    /// cannot act; and those paths are the declaration's answer about the caller's own operand set
    /// rather than anything about the grant.
    /// </summary>
    /// <exception cref="ToolRefusal">The grant holds none of a required class.</exception>
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
    /// The refusal a multi-target mutation raises when it fails partway. The mutex lock means no
    /// other tool interleaved; it does not mean rollback, so what landed and what did not are named
    /// exactly and the recovery steps are the caller's route out of that state.
    ///
    /// <b>The steps are non-destructive and in order.</b> Nothing here tells a caller to remove
    /// something to make room, because neither relocation nor removal ever leaves content in two
    /// places: a move relocates bytes rather than copying them, and a removal only ever unlinks.
    /// </summary>
    public static ToolRefusal Partial(
        string message,
        IReadOnlyList<string> applied,
        IReadOnlyList<string> notApplied,
        IReadOnlyList<string> recovery) =>
        new(ToolCodes.PartialMutation, message, new { applied, notApplied, recovery });

    /// <summary>
    /// The name grammar's refusal, carrying a conforming spelling where one can be derived from what
    /// the caller sent - so the miss costs one retry rather than a reading of the grammar. Choosing
    /// among conforming names stays the caller's, which is why exactly one is offered, and none is
    /// offered where the text carries no conforming characters at all.
    /// </summary>
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

    /// <summary>What a pass over every note in the vault answers when it did not complete, in this
    /// vocabulary. A pass that could not enumerate or could not read has no answer at all - a
    /// partial one would under-report exactly the links a caller needs.</summary>
    /// <exception cref="ToolRefusal">Always; the caller has already matched away the completed
    /// case.</exception>
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

/// <summary>One target of an operation and what the vault's path policy says mutating it
/// requires.</summary>
internal readonly record struct PathRequirement(string Path, IReadOnlyCollection<string> Required);
