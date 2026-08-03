using VaultShared.Access;
using VaultShared.Links;

namespace VaultShared.Tools;

/// <summary>
/// What a removal turned out to be, or would be.
/// </summary>
/// <param name="Applied">Whether this call removed anything. False is the preview: the same shape,
/// computed under the same lock and the same validation, so a caller can compare a plan against the
/// result of the call it authorized.</param>
/// <param name="Removed">The full set the removal destroys, in the order it is driven - each
/// folder's contents before the folder, deepest first. It is the operation's footprint rather than
/// the caller's perimeter: a caller cannot name a dot path, and a recursive removal still destroys
/// every dot entry under the folder it was given.</param>
/// <param name="Backlinks">
/// Every surviving wikilink into the removed set, one entry per occurrence, because a caller fixing
/// them visits each one. <b>Not nullable</b>: the census always runs, and a census that ran and
/// found nothing is a finding rather than an absence.
/// </param>
/// <param name="AssertedAuthority">What the outcome required beyond the tool's floor, empty where it
/// required nothing.</param>
public sealed record DeleteResult(
    bool Applied,
    IReadOnlyList<string> Removed,
    IReadOnlyList<LinkOccurrence> Backlinks,
    IReadOnlyList<string> AssertedAuthority);

/// <summary>
/// The only way to remove a note or a folder, reporting the full blast radius.
///
/// <b>Nothing is removed before its plan is reported.</b> The plan is built from the file census -
/// every file whatever its extension, dot entries admitted - because a non-markdown or hidden file
/// is still on disk, still destroyed, and still possibly something that must be classified before it
/// goes. Asking note enumeration instead would fail three ways at once, all silent: a folder holding
/// only non-markdown files would read as empty, the blast radius would under-report, and authority
/// classification would run over an incomplete set.
///
/// <b>Three guards, and none has an override.</b> A <c>.git</c> directory anywhere in the set
/// refuses the whole removal; a folder with anything in it is not removed by a plain call; and the
/// whole plan is validated before anything is removed. There is no force, no purge and no spelling
/// of the call that proceeds past a refusal.
///
/// <b>Removal reports orphaning rather than repairing it.</b> Deleting one of two same-named notes
/// leaves bare wikilinks resolving to the survivor; each is reported and none is rewritten, because
/// the caller's intent for them is not knowable here. The asymmetry with creation's pinning is
/// deliberate.
///
/// <b>It makes no claim about what survives.</b> Whether a removed file can be brought back turns on
/// the repository's state and the caller's own, and a verdict computed here would be a safety claim
/// this tool cannot back.
/// </summary>
public sealed class DeleteTool : ITool
{
    public const string ToolName = "delete";

    /// <summary>
    /// The floor is the base write class. <b>Removal takes no destructive class of its own</b> - it
    /// is authorized as a mutation like any other, and what it requires beyond the floor is whatever
    /// the vault's path policy declares about the targets in its set.
    ///
    /// Scope decides the folder that was named, not the files beneath it: a folder in scope has its
    /// whole subtree in scope, so a removal never meets a child it cannot reach.
    /// </summary>
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

        // ---- 3. Path resolution, scope, and policy ----

        var target = ToolPaths.Write(vault, arguments.Path, ToolPaths.KindOf(arguments.Path, arguments.Operand));

        // ---- 4. Plan ----

        var plan = Plan(vault, arguments, target);

        RefuseOnGitDirectory(plan);
        RefuseIfNotEmpty(vault, arguments, target, plan);

        // The whole plan is validated before anything is removed, so a refusal cannot arrive with
        // part of the work already done. Removal routes through no frontmatter write, so this is
        // where the vault's path policy is consulted.
        var asserted = MutationSupport.AssertArtifactAuthority(
            vault,
            plan.Entries.Select(entry => new PathRequirement(entry.Value, vault.Vault.Policy.MutationRequires(entry.Value))));

        // The scan covers every note in the vault, the same reach as relocation's rewrite: a report
        // that stopped at a scope boundary would under-report exactly where the caller most needs
        // the truth.
        var census = WikilinkMaintenance.Census(vault.BypassingScope(), [.. plan.Entries]);
        var backlinks = census switch
        {
            LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.Completed completed => completed.Result,
            var failure => throw MutationSupport.PassFailure(failure),
        };

        var removed = plan.Entries.Select(entry => entry.Value).ToArray();

        // ---- 5. Apply ----

        if (arguments.Apply)
            Remove(vault, plan, target);

        return new ToolResult.Data(new DeleteResult(arguments.Apply, removed, backlinks, asserted));
    }

    /// <summary>
    /// The destruction plan: every file the removal destroys, then every folder it empties, each
    /// deepest first so nothing is asked to remove a folder before its contents.
    ///
    /// A note's plan is itself. A folder's is built from the file census, filtered to what lies under
    /// it by whole-path prefix rather than by handing the folder's name to the engine as a glob -
    /// names already on disk are not held to the name grammar, so one could carry a character the
    /// glob syntax reads.
    ///
    /// <b>What the census cannot see is a folder holding no file at any depth.</b> Such a folder is
    /// absent from the plan and its parent's removal then meets it and refuses; the outcome is loud
    /// rather than silent, which is the property that matters, and the alternative is a directory
    /// walk the layer deliberately does not offer.
    /// </summary>
    private static DestructionPlan Plan(VaultAccess vault, DeleteArguments arguments, WritePath target)
    {
        var listed = vault.EnumerateFiles(pattern: null, includeDotEntries: true);
        if (listed is not SearchOutcome<VaultPath>.Results census)
            throw ToolRefusalEnvelope.Translate(listed);

        if (arguments.Operand == OperandKind.Note)
        {
            // A missing sole target is a failure, never a skip: skip-the-unreadable belongs to
            // enumerate-then-read passes, and applying it here would turn a failed operation into a
            // silent success - and would let a preview predict a removal that cannot happen.
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
    /// The first guard. A <c>.git</c> directory is a separate repository with its own history, and
    /// removing it from here destroys work this vault never tracked.
    ///
    /// It runs over the plan the tool has already built - from a census that asked for dot entries,
    /// and before any policy filtering - because either one would otherwise hide a <c>.git</c>
    /// directory from the check that exists to find it.
    /// </summary>
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
    /// The second guard. A folder with anything in it is not removed by a plain call: the call fails
    /// and the refusal carries the full set a recursive removal would destroy, so the caller retries
    /// against the actual file list rather than against the folder name it first sent.
    ///
    /// A folder holding nothing but the tooling's own <c>.gitkeep</c> counts as empty, and a plain
    /// call removes both - the placeholder is not content a caller put there.
    /// </summary>
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
    /// Drives the validated plan entry by entry, through the two removal primitives. <b>The caller
    /// holds the plan and the layer never walks</b>: a layer that recursed would hold the only
    /// record of how far it got, leaving this to report a boundary it did not observe.
    ///
    /// The two failure answers are classified rather than guessed. An entry that vanished between
    /// the plan and the removal is gone, which is the state the call asked for, and it is counted as
    /// removed; the one exception is the named operand itself with nothing yet removed, where a miss
    /// is the operation's own failure rather than a race. An entry that is there and could not be
    /// removed stops the run.
    /// </summary>
    private static void Remove(VaultAccess vault, DestructionPlan plan, WritePath target)
    {
        var applied = new List<string>();
        var entries = plan.Entries.ToArray();

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];

            // Every entry is the named folder or beneath it, so the folder's own admission carries
            // it - which is what lets the plan's dot entries be removed at all, the perimeter
            // refusing a caller to name one.
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
    /// A removal that could not finish. With nothing yet removed it is a clean refusal; with entries
    /// already gone it is a partial mutation, naming exactly what landed and what did not and
    /// carrying the ordered way out.
    /// </summary>
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

    /// <summary>A path composed from segments the layer already produced, so text that is not
    /// well-formed is a defect here rather than a caller's malformed input.</summary>
    private static VaultPath At(Vault vault, string value) =>
        VaultPath.TryCreate(vault, value, out var path, out var reason)
            ? path
            : throw new InvalidOperationException($"Composed plan entry \"{value}\" is not a vault path: {reason}");

    /// <summary>
    /// What the removal destroys, split by which primitive drives each entry - a path does not say
    /// which of the two it names, and neither primitive may guess.
    /// </summary>
    private sealed record DestructionPlan(IReadOnlyList<VaultPath> Files, IReadOnlyList<VaultPath> Folders)
    {
        /// <summary>Every entry in removal order: the files, then the folders that held them,
        /// deepest first within each.</summary>
        public IEnumerable<VaultPath> Entries => Files.Concat(Folders);
    }
}
