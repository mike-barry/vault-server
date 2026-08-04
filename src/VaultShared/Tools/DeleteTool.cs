using VaultShared.Access;
using VaultShared.Links;

namespace VaultShared.Tools;

/// <summary>
/// Represents the result of a delete operation or its preview.
/// </summary>
/// <param name="Applied">Whether this call removed anything. A preview returns false and otherwise
/// the same shape, computed under the same lock and the same validation, so a caller can compare a
/// preview against the result of the call it then authorized.</param>
/// <param name="Removed">The full set the removal destroys, in the order it is driven: each folder's
/// contents before the folder, deepest first. This is what the operation touches rather than what a
/// caller may name. A caller cannot name a dot path, and a recursive removal still destroys every dot
/// entry under the folder it was given.</param>
/// <param name="Backlinks">
/// Every surviving wikilink into the removed set, one entry per occurrence, because a caller fixing
/// them visits each one. Never null: the census always runs, so an empty list means it found nothing
/// rather than that it did not run.
/// </param>
/// <param name="AssertedAuthority">The op classes the outcome required beyond the tool's floor, empty
/// where it required none.</param>
public sealed record DeleteResult(
    bool Applied,
    IReadOnlyList<string> Removed,
    IReadOnlyList<LinkOccurrence> Backlinks,
    IReadOnlyList<string> AssertedAuthority);

/// <summary>
/// Removes one note or one folder, reporting everything the removal destroys. This is the only way to
/// remove either.
/// </summary>
/// <remarks>
/// <para>
/// The plan is built and reported before anything is removed. It comes from a census of every file
/// whatever its extension, dot entries included, because a hidden or non-markdown file is still on
/// disk, still destroyed, and may still need classifying before it goes. Building the plan from note
/// enumeration instead would fail three ways at once and silently: a folder holding only
/// non-markdown files would read as empty, the reported set would under-report, and authority
/// classification would run over an incomplete set.
/// </para>
/// <para>
/// Three guards apply, and none can be overridden. A <c>.git</c> directory anywhere in the set
/// refuses the whole removal. A folder holding anything is not removed by a call that did not ask to
/// recurse. And the whole plan is validated before anything is removed. No spelling of the call
/// proceeds past a refusal.
/// </para>
/// <para>
/// A removal reports orphaned wikilinks and never repairs them. Deleting one of two same-named notes
/// leaves bare wikilinks resolving to the survivor; each is reported and none is rewritten, because
/// what the caller intends for them cannot be known here. This is deliberately the opposite of what
/// creation does when it pins links.
/// </para>
/// <para>
/// The result makes no claim about what can be restored. Whether a removed file can be brought back
/// depends on the repository's state and the caller's own, which this tool cannot see.
/// </para>
/// </remarks>
public sealed class DeleteTool : ITool
{
    /// <summary>The tool's wire name.</summary>
    public const string ToolName = "delete";

    /// <summary>
    /// The tool's declaration. The floor is the base write class: a removal is authorized as a
    /// mutation like any other and takes no destructive op class of its own. What it requires beyond
    /// the floor is whatever the vault's path policy declares about the targets in its set.
    /// </summary>
    /// <remarks>
    /// Scope is checked against the folder the caller named, not against the files beneath it. A
    /// folder in scope has its whole subtree in scope, so a removal never meets a child it cannot
    /// reach.
    /// </remarks>
    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Removes one note, or one folder. Previews by default: the routine call reports the full " +
            "set the removal would destroy and every surviving wikilink into it, and a second call " +
            "with \"apply\" true removes it. A folder holding anything is refused unless \"recursive\" " +
            "is set. A \".git\" directory anywhere in the set refuses the whole removal. Nothing here " +
            "says what can be restored afterwards.",
        Arguments = typeof(DeleteArguments),
        OpClassFloor = new Floor.Class("content:write"),
        Mutates = true,
    };

    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<DeleteArguments>();
        var vault = call.RequireVault(ToolName);

        // Argument validation and vault resolution have already run in the shared execute path.

        // Path resolution, scope, and policy.
        var target = ToolPaths.Write(vault, arguments.Path, ToolPaths.KindOf(arguments.Path, arguments.Operand));

        // Plan.
        var plan = Plan(vault, arguments, target);

        RefuseOnGitDirectory(plan);
        RefuseIfNotEmpty(vault, arguments, target, plan);

        // The whole plan is validated before anything is removed, so a refusal cannot arrive with
        // part of the work already done. Removal routes through no frontmatter write, so this is
        // where the vault's path policy is consulted.
        var asserted = MutationSupport.AssertArtifactAuthority(
            vault,
            plan.Entries.Select(entry => new PathRequirement(entry.Value, vault.Vault.Policy.MutationRequires(entry.Value))));

        // The census covers every note in the vault, the same reach a move's rewrite has. A report
        // that stopped at a scope boundary would under-report exactly where the caller most needs
        // the truth.
        var census = WikilinkMaintenance.Census(vault.BypassingScope(), [.. plan.Entries]);
        var backlinks = census switch
        {
            LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.Completed completed => completed.Result,
            var failure => throw MutationSupport.PassFailure(failure),
        };

        var removed = plan.Entries.Select(entry => entry.Value).ToArray();

        // Apply.
        if (arguments.Apply)
            Remove(vault, plan, target);

        return new ToolResult.Data(new DeleteResult(arguments.Apply, removed, backlinks, asserted));
    }

    /// <summary>
    /// Builds the destruction plan: every file the removal destroys, then every folder it empties,
    /// each deepest first so nothing is asked to remove a folder before its contents.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="target">The admitted write path.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ToolRefusal">The census failed, or a note target does not exist.</exception>
    /// <remarks>
    /// A note's plan is the note itself. A folder's plan comes from the file census, filtered to what
    /// lies under it by whole-path prefix. Do not pass the folder's name to the search engine as a
    /// glob instead: a name already on disk is not held to the name grammar, so it can carry a
    /// character the glob syntax reads.
    ///
    /// The census cannot see a folder that holds no file at any depth. Such a folder is absent from
    /// the plan, and its parent's removal then meets it and refuses, which is loud rather than
    /// silent. The alternative would be a directory walk the access layer deliberately does not
    /// offer.
    /// </remarks>
    private static DestructionPlan Plan(VaultAccess vault, DeleteArguments arguments, WritePath target)
    {
        var listed = vault.EnumerateFiles(pattern: null, includeDotEntries: true);
        if (listed is not SearchOutcome<VaultPath>.Results census)
            throw ToolRefusalEnvelope.Translate(listed);

        if (arguments.Operand == OperandKind.Note)
        {
            // A missing sole target is a failure and never a skip. Skipping belongs to passes that
            // enumerate and then read; here it would turn a failed operation into a silent success,
            // and would let a preview predict a removal that cannot happen.
            if (!census.Items.Contains(target.Path))
                throw new ToolRefusal(
                    ToolCodes.PathNotFound,
                    $"No note is at \"{arguments.Path}\".");

            return new DestructionPlan([target.Path], []);
        }

        var prefix = target.Path.Value + '/';
        var files = census.Items
            .Where(path => path.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var folders = new Dictionary<string, VaultPath>(StringComparer.OrdinalIgnoreCase) { [target.Path.Value] = target.Path };
        foreach (var file in files)
            for (var depth = target.Path.Segments.Count + 1; depth < file.Segments.Count; depth++)
            {
                var value = string.Join('/', file.Segments.Take(depth));
                if (!folders.ContainsKey(value))
                    folders[value] = At(target.Path.Vault, value);
            }

        return new DestructionPlan(
            [.. files.OrderByDescending(p => p.Segments.Count).ThenBy(p => p.Value, StringComparer.Ordinal)],
            [.. folders.Values.OrderByDescending(p => p.Segments.Count).ThenBy(p => p.Value, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Refuses the whole removal when a <c>.git</c> directory lies anywhere in the set. A <c>.git</c>
    /// directory is a separate repository with its own history, and removing it from here would
    /// destroy work this vault never tracked.
    /// </summary>
    /// <param name="plan">The destruction plan.</param>
    /// <exception cref="ToolRefusal">A <c>.git</c> directory is in the set.</exception>
    /// <remarks>
    /// This runs over the plan built from a census that asked for dot entries, and before any policy
    /// filtering. Either one would hide a <c>.git</c> directory from the check that exists to find
    /// it.
    /// </remarks>
    private static void RefuseOnGitDirectory(DestructionPlan plan)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in plan.Entries)
            for (var i = 0; i < entry.Segments.Count; i++)
                if (string.Equals(entry.Segments[i], ".git", StringComparison.OrdinalIgnoreCase))
                    found.Add(string.Join('/', entry.Segments.Take(i + 1)));

        if (found.Count > 0)
            throw new ToolRefusal(
                ToolCodes.GitDirectoryPresent,
                $"A \".git\" directory is inside this removal: {string.Join(", ", found.Select(p => $"\"{p}\""))}. That is a separate repository with its own history, and no spelling of this call removes it.",
                new { paths = found.ToArray() });
    }

    /// <summary>
    /// Refuses a folder removal that did not ask to recurse when the folder holds anything. The
    /// refusal carries the full set a recursive removal would destroy, so the caller decides against
    /// the actual file list rather than against the folder name it first sent.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="target">The admitted write path.</param>
    /// <param name="plan">The destruction plan.</param>
    /// <exception cref="ToolRefusal">The folder is not empty and the call did not ask to
    /// recurse.</exception>
    /// <remarks>
    /// A folder holding nothing but the tooling's own <c>.gitkeep</c> counts as empty, and a
    /// non-recursive call removes both. The placeholder is not content a caller put there.
    /// </remarks>
    private static void RefuseIfNotEmpty(
        VaultAccess vault, DeleteArguments arguments, WritePath target, DestructionPlan plan)
    {
        if (arguments.Recursive || arguments.Operand == OperandKind.Note || plan.Files.Count == 0)
            return;

        if (plan.Files.Count == 1
            && plan.Folders.Count == 1
            && string.Equals(plan.Files[0].Value, $"{target.Path.Value}/{VaultFiles.GitKeepName}", StringComparison.OrdinalIgnoreCase))
            return;

        throw new ToolRefusal(
            ToolCodes.NotEmpty,
            $"\"{arguments.Path}\" is not empty. Send \"recursive\" to remove it with everything under it - which is the {plan.Files.Count + plan.Folders.Count} entries in this refusal.",
            new { plan = plan.Entries.Select(entry => entry.Value).ToArray() });
    }

    /// <summary>
    /// Removes the validated plan entry by entry, through the two removal primitives.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="plan">The validated destruction plan.</param>
    /// <param name="target">The path the caller named, which admits every entry beneath it.</param>
    /// <exception cref="ToolRefusal">An entry could not be removed, or the sole named target was
    /// already gone.</exception>
    /// <remarks>
    /// This tool holds the plan and drives it; the access layer never recurses. A layer that recursed
    /// would hold the only record of how far it got, leaving this code to report a stopping point it
    /// did not observe.
    ///
    /// An entry that vanished between the plan and the removal is already in the state the call asked
    /// for and counts as removed. The exception is the named target itself with nothing yet removed,
    /// where a miss is the operation's own failure rather than a race. An entry that is there and
    /// could not be removed stops the run.
    /// </remarks>
    private static void Remove(VaultAccess vault, DestructionPlan plan, WritePath target)
    {
        var applied = new List<string>();
        var entries = plan.Entries.ToArray();

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];

            // Every entry is the named folder or lies beneath it, so the folder's own admission
            // covers it. That is what lets the plan's dot entries be removed at all, given that a
            // caller can never name one.
            var write = vault.Beneath(target, entry);
            var isFolder = plan.Folders.Contains(entry);
            var outcome = isFolder ? VaultFiles.RemoveEmptyFolder(write) : VaultFiles.RemoveFile(write);

            switch (outcome)
            {
                case RemovalOutcome.Removed:
                case RemovalOutcome.NotFound when applied.Count > 0 || !entry.Equals(target.Path):
                    applied.Add(entry.Value);
                    continue;

                case RemovalOutcome.NotFound:
                    throw new ToolRefusal(
                        ToolCodes.PathNotFound,
                        $"Nothing is at \"{entry.Value}\". Nothing was removed.");

                default:
                    throw Stopped(entry, outcome, applied, entries.Skip(i), target);
            }
        }
    }

    /// <summary>
    /// Builds the refusal for a removal that could not finish.
    /// </summary>
    /// <param name="entry">The entry that could not be removed.</param>
    /// <param name="outcome">Why it could not be removed.</param>
    /// <param name="applied">The entries already removed.</param>
    /// <param name="remaining">The entries not removed, starting with this one.</param>
    /// <param name="target">The path the caller named, quoted in the recovery steps.</param>
    /// <returns>A clean refusal where nothing was removed, otherwise a partial-mutation refusal
    /// naming exactly what landed and what did not, with ordered recovery steps.</returns>
    private static ToolRefusal Stopped(
        VaultPath entry,
        RemovalOutcome outcome,
        IReadOnlyList<string> applied,
        IEnumerable<VaultPath> remaining,
        WritePath target)
    {
        var (code, why) = outcome switch
        {
            RemovalOutcome.NotEmpty => (
                ToolCodes.NotEmpty,
                $"\"{entry.Value}\" still holds something the destruction census could not see - a folder with no file anywhere under it."),
            RemovalOutcome.Failed failed => (ToolCodes.WriteFailed, $"\"{entry.Value}\" could not be removed: {failed.Reason}"),
            var other => (ToolCodes.WriteFailed, $"\"{entry.Value}\" could not be removed ({other.GetType().Name})."),
        };

        var notApplied = remaining.Select(path => path.Value).ToArray();
        if (applied.Count == 0)
            return new ToolRefusal(code, $"{why} Nothing was removed.", new { path = entry.Value });

        return MutationSupport.Partial(
            $"{why} {applied.Count} of {applied.Count + notApplied.Length} entries were removed first.",
            applied,
            notApplied,
            [
                $"Resolve what is holding \"{entry.Value}\".",
                $"Re-run the identical delete against \"{target.Path.Value}\" - the plan is built fresh, and what is already gone is simply not in it.",
            ]);
    }

    /// <summary>Composes a vault path from segments the access layer already produced.</summary>
    /// <param name="vault">The vault the path belongs to.</param>
    /// <param name="value">The vault-relative path text.</param>
    /// <returns>The path.</returns>
    /// <exception cref="InvalidOperationException">The composed text is not a valid vault path, which
    /// is a defect here rather than malformed caller input.</exception>
    private static VaultPath At(Vault vault, string value) =>
        VaultPath.TryCreate(vault, value, out var path, out var reason)
            ? path
            : throw new InvalidOperationException($"Composed plan entry \"{value}\" is not a vault path: {reason}");

    /// <summary>
    /// Holds what the removal destroys, split by which primitive removes each entry. A path does not
    /// say which of the two it names, and neither primitive may guess.
    /// </summary>
    /// <param name="Files">The files to remove, deepest first.</param>
    /// <param name="Folders">The folders to remove afterwards, deepest first.</param>
    private sealed record DestructionPlan(IReadOnlyList<VaultPath> Files, IReadOnlyList<VaultPath> Folders)
    {
        /// <summary>Every entry in removal order: the files, then the folders that held them,
        /// deepest first within each.</summary>
        public IEnumerable<VaultPath> Entries => Files.Concat(Folders);
    }
}
