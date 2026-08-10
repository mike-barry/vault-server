using System.Text.Json.Serialization;
using VaultShared.Access;
using VaultShared.Links;
using VaultShared.Scope;

namespace VaultShared.Tools;

/// <summary>
/// Represents the result of a move operation or its preview. Every path-valued member reports a
/// post-move path, on a preview exactly as on a result, so no caller has to translate between two
/// path vocabularies.
/// </summary>
/// <param name="Applied">Whether this call moved anything. A preview returns false and otherwise the
/// same shape, computed under the same lock and the same validation.</param>
/// <param name="Path">Where the operand lands.</param>
/// <param name="ContentHash">The moved note's content hash afterwards, the same value a read of it
/// returns, so a move chains straight into an edit. Absent for a folder move, which leaves no single
/// note to describe.</param>
/// <param name="CaseOnly">Whether the two paths name one entry under different spellings. Git does
/// not see such a change under its default case handling, and nothing here performs repository
/// operations, so it is reported for something else to stage.</param>
/// <param name="Moved">Every note whose path this move changes, and where it lands. One entry for a
/// note; for a folder, every note the folder carries. The folder's other content moves with it under
/// names that do not change.</param>
/// <param name="RepairedLinks">Links that named something this move relocates, re-emitted at its
/// destination. They are canonical rather than minimal: the short form where the name is unique after
/// the move, the full vault path where it is not.</param>
/// <param name="PinnedLinks">Links in other notes frozen to what they open today, where the move
/// would otherwise have changed the answer. Pinning freezes and never redirects; no link is ever
/// re-aimed at something it did not already name.</param>
/// <param name="FolderNote">Where a relocated folder's folder note lands, renamed to match its new
/// folder. Absent where the folder has none, since none is created, and absent when a note
/// moves.</param>
/// <param name="AssertedAuthority">The op classes the outcome required beyond the tool's floor, empty
/// where it required none.</param>
public sealed record MoveResult(
    bool Applied,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContentHash,
    bool CaseOnly,
    IReadOnlyList<string> Moved,
    IReadOnlyList<LinkRewrite> RepairedLinks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LinkRewrite>? PinnedLinks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FolderNote,
    IReadOnlyList<string> AssertedAuthority);

/// <summary>
/// Relocates or renames one note or folder, keeping the wikilink graph intact. This is the only way
/// to move either.
/// </summary>
/// <remarks>
/// <para>
/// Do not reorder the repair and the move. The link repair runs first. The plan names every note by
/// the path it has now, so applying the repair after the move would address notes that are no longer
/// where the plan found them. Neither order survives an interruption cleanly, since links naming a
/// destination that does not exist yet and links naming a source that no longer does are equally
/// broken. This order is chosen because it is the one that cannot silently half-work.
/// </para>
/// <para>
/// A folder move is a single directory rename. The names inside it do not change, so the repair is
/// one prefix substitution, and the folder note is the only thing renamed. It takes the new folder's
/// name, and a folder that has no folder note does not get one created.
/// </para>
/// <para>
/// A call previews by default and a second call with apply set performs the move. The preview runs
/// the identical path - the same lock, the same validation, every refusal a real call would raise -
/// and differs from its own result only in <see cref="MoveResult.Applied"/>.
/// </para>
/// </remarks>
public sealed class MoveTool : ITool
{
    /// <summary>The tool's wire name.</summary>
    public const string ToolName = "move";

    /// <summary>
    /// The tool's declaration. The floor is the base write class. What a particular move requires
    /// beyond it is whatever the vault's path policy declares about the targets in its set.
    /// </summary>
    /// <remarks>
    /// The wikilink pass contributes that same base class and nothing more. It replaces the target
    /// inside links that already exist and reads no key, and the notes it rewrites contribute nothing
    /// of their own, because those rewrites are maintenance rather than authoring. The pass is not
    /// scope-checked, which <see cref="VaultAccess.BypassingScope"/> makes explicit at the call site.
    /// </remarks>
    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Relocates or renames one note or folder, repairing every wikilink that named it and " +
            "pinning any bystander link the move would otherwise have re-aimed. Previews by default: " +
            "the routine call reports where everything lands and which links change, and a second " +
            "call with \"apply\" true performs it. An occupied destination is refused, and the " +
            "destination's parent must already exist.",
        Arguments = typeof(MoveArguments),
        OpClassFloor = new Floor.Class("content:write"),
        Mutates = true,
    };

    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<MoveArguments>();
        var vault = call.RequireVault(ToolName);

        // Argument validation and vault resolution have already run in the shared execute path.

        // Path resolution, scope, and policy. The kind comes from the operand's own spelling, and an
        // assertion the caller made only checks it. The destination is held to the same rule, so a
        // note never lands under a folder-shaped name and a folder never acquires an extension.
        var kind = ToolPaths.KindOf(arguments.Path, arguments.Kind);
        var source = ToolPaths.Write(vault, arguments.Path, kind);
        var destination = ToolPaths.Write(vault, arguments.Destination, kind);

        if (destination.Path.HasSameSpellingAs(source.Path))
            throw new ToolRefusal(
                ToolCodes.InvalidArgument,
                "\"destination\" is the path already given - a move relocates or renames, and a case-only rename is the one spelling of this call that names the same entry twice.",
                new { argument = "destination" });

        if (kind == OperandKind.Folder && Encloses(source.Path, destination.Path))
            throw new ToolRefusal(
                ToolCodes.InvalidArgument,
                $"\"{arguments.Destination}\" is inside \"{arguments.Path}\", and a folder cannot be moved into itself. Move it to a folder outside the one being moved.",
                new { argument = "destination" });

        // Every refusal the move itself would raise is decided here, before the wikilink pass plans
        // or writes anything. A preview that skipped these would report success for a move that
        // cannot happen, and an applied call would discover the refusal with the whole vault's links
        // already rewritten for a move that never lands.
        //
        // This does not replace the check inside the move. The move is still what decides, because
        // another writer can occupy the destination between this call and the move. Reading it here
        // narrows that window and keeps the work in between off a doomed move; reading it again in
        // the move is what makes the answer true at the moment the entry is relocated.
        //
        // The operand's own absence is answered before anything about the destination. Both can be
        // true of one call, and a caller whose source is not there has to fix that first; an answer
        // about the destination sends it to fix something else. A note's source is established by
        // reading it, which the hash needs in any case and which tells a note that is not there from
        // one that is there and cannot be read - a presence probe cannot tell those apart, so a
        // folder, which has no such read, gets the probe and a note does not.
        var stored = kind == OperandKind.Note ? SourceContent(source, arguments.Path) : null;

        var refusing = kind == OperandKind.Note
            ? VaultFiles.PreflightMoveFile(source, destination)
            : VaultFiles.PreflightMoveFolder(source, destination);

        if (refusing is not null)
            throw Refused(refusing, arguments);

        // Plan.
        var pass = vault.BypassingScope();
        var planned = kind == OperandKind.Note
            ? WikilinkMaintenance.PlanNoteRelocation(pass, source.Path, destination.Path)
            : WikilinkMaintenance.PlanFolderRelocation(pass, source.Path, destination.Path);

        var plan = planned switch
        {
            LinkPassOutcome<WikilinkPlan>.Completed completed => completed.Result,
            var failure => throw MutationSupport.PassFailure(failure),
        };

        var asserted = MutationSupport.AssertArtifactAuthority(vault, Requirements(vault.Vault.Policy, kind, source.Path, destination.Path, plan));

        // The bytes a note carries afterwards are known before the move. A move relocates them rather
        // than replacing them, and where the repair pass rewrites the moved note itself, the plan
        // already holds the text it will write. Computing it here, once, is what makes a preview's
        // hash and the applied call's hash the same value.
        var hash = stored is null ? null : HashAfter(stored, source, plan);
        var caseOnly = source.Path.Equals(destination.Path) && !source.Path.HasSameSpellingAs(destination.Path);

        var moved = kind == OperandKind.Note
            ? (IReadOnlyList<string>)[destination.Path.Value]
            : [.. plan.Relocated.Select(pair => pair.To.Value).Order(StringComparer.Ordinal)];

        // Apply.
        if (arguments.Apply)
            Perform(vault, pass, kind, arguments, source, destination, plan);

        // The plan names each note by the path it has now, because that is where it has to write it.
        // A rewrite inside a note this move relocates is therefore reported where the plan found it,
        // and the caller is owed the path it can read afterwards.
        var landing = plan.Relocated.ToDictionary(
            pair => pair.From.Value, pair => pair.To.Value, StringComparer.OrdinalIgnoreCase);

        return new ToolResult.Data(new MoveResult(
            arguments.Apply,
            destination.Path.Value,
            hash,
            caseOnly,
            moved,
            AtPostMovePaths(plan.Repairs, landing),
            AtPostMovePaths(plan.Pins, landing),
            plan.FolderNote?.Value,
            asserted));
    }

    /// <summary>
    /// Reports each rewrite at the path its note has after the move.
    /// </summary>
    /// <param name="rewrites">The rewrites as the plan named them, by the path each note has
    /// now.</param>
    /// <param name="landing">Where each note the move relocates lands, keyed by the path it has
    /// now.</param>
    /// <returns>The rewrites, with a relocated note's path replaced by where it lands. A rewrite in
    /// a note the move leaves alone is already at its final path and passes through.</returns>
    private static IReadOnlyList<LinkRewrite> AtPostMovePaths(
        IReadOnlyList<LinkRewrite> rewrites, Dictionary<string, string> landing) =>
        [.. rewrites.Select(rewrite =>
            landing.TryGetValue(rewrite.Path, out var lands) ? rewrite with { Path = lands } : rewrite)];

    /// <summary>
    /// Determines whether one folder path contains another, at any depth.
    /// </summary>
    /// <param name="folder">The containing folder.</param>
    /// <param name="candidate">The path that may sit under it.</param>
    /// <returns>True when the candidate is strictly below the folder. A path equal to the folder is
    /// not below it, so a case-only rename is not caught here.</returns>
    /// <remarks>
    /// The comparison is case-insensitive, matching how two paths are read as naming one entry.
    /// </remarks>
    private static bool Encloses(VaultPath folder, VaultPath candidate) =>
        candidate.Segments.Count > folder.Segments.Count
        && candidate.Value.StartsWith(folder.Value + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Repairs the wikilink graph, performs the move, and renames a moved folder's folder note.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="pass">The vault-wide pass the repair runs over.</param>
    /// <param name="kind">Whether a note or a folder is moving.</param>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="source">The admitted source path.</param>
    /// <param name="destination">The admitted destination path.</param>
    /// <param name="plan">The planned wikilink rewrites.</param>
    /// <exception cref="ToolRefusal">Any step refused. The write lock stops other tools from
    /// interleaving but does not make this atomic, so a failure that landed part of the work names
    /// exactly what landed and what did not. One that landed none of it is an ordinary
    /// refusal.</exception>
    /// <remarks>
    /// Every refusal that can be decided from the arguments and the vault as it stands has already
    /// been raised before this runs, so a failure here is one only the attempt could report - a note
    /// that could not be written, or an entry another writer took hold of. The move itself runs
    /// before the folder note's rename, so a move that cannot land leaves the folder note untouched
    /// and one fewer partial state to report.
    /// </remarks>
    private static void Perform(
        VaultAccess vault,
        VaultWidePass pass,
        OperandKind kind,
        MoveArguments arguments,
        WritePath source,
        WritePath destination,
        WikilinkPlan plan)
    {
        if (!plan.IsEmpty && WikilinkMaintenance.Apply(pass, plan) is LinkApplyOutcome.Failed stopped)
            throw stopped.Written.Count == 0
                // The pass stopped on the first note it tried, so nothing was written and the vault
                // is as it was. Reporting a partial mutation here would send the caller to repair
                // state that does not exist, and a caller cannot tell the two apart from anything
                // but the code.
                ? new ToolRefusal(
                    ToolCodes.WriteFailed,
                    $"\"{stopped.Path}\" could not be rewritten, so \"{arguments.Path}\" was not moved and nothing in the vault changed.",
                    new { path = stopped.Path })
                : MutationSupport.Partial(
                    $"\"{stopped.Path}\" could not be rewritten, so the repair this move needs is only partly in place and \"{arguments.Path}\" was not moved.",
                    [.. stopped.Written.Select(Render)],
                    [.. stopped.Unwritten.Select(Render), $"move {arguments.Path} -> {arguments.Destination}"],
                    [
                        $"Nothing was moved: \"{arguments.Path}\" is still where it was, and the notes already rewritten name \"{arguments.Destination}\", which is not there yet.",
                        $"Resolve what is holding \"{stopped.Path}\".",
                        "Re-run the identical move - the pass is planned fresh against the vault as it is, and repairs only what still names the source.",
                    ]);

        var outcome = kind == OperandKind.Note
            ? VaultFiles.MoveFile(source, destination)
            : VaultFiles.MoveFolder(source, destination);

        if (outcome is not MoveOutcome.Moved)
            throw AfterRepair(Refused(outcome, arguments), arguments, plan);

        RenameFolderNote(vault, arguments, source, destination, plan);
    }

    /// <summary>
    /// Renames a moved folder's folder note to match its new folder. The directory rename has already
    /// carried the note across under its old name; this gives it the new one.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="source">The admitted source path.</param>
    /// <param name="destination">The admitted destination path.</param>
    /// <param name="plan">The plan, which says where the folder note belongs.</param>
    /// <exception cref="ToolRefusal">The folder moved and its folder note could not be
    /// renamed.</exception>
    /// <remarks>
    /// A folder with no folder note does not get one created, and a move that keeps the folder's name
    /// has nothing to rename. Both fall out of comparing where the note now is against where the plan
    /// says it belongs.
    /// </remarks>
    private static void RenameFolderNote(
        VaultAccess vault, MoveArguments arguments, WritePath source, WritePath destination, WikilinkPlan plan)
    {
        if (plan.FolderNote is not { } belongs)
            return;

        // Where the directory rename left it: the folder's old basename, under the new folder.
        var carried = Compose(vault, destination, $"{destination.Path.Value}/{source.Path.Segments[^1]}{VaultName.NoteExtension}");
        if (carried.Path.HasSameSpellingAs(belongs))
            return;

        if (VaultFiles.MoveFile(carried, Compose(vault, destination, belongs.Value)) is { } outcome and not MoveOutcome.Moved)
            throw MutationSupport.Partial(
                $"\"{arguments.Path}\" was relocated and its folder note could not be renamed to match ({outcome.GetType().Name}).",
                [$"move {arguments.Path} -> {arguments.Destination}", .. plan.Rewrites.Select(Render)],
                [$"rename {carried.Path.Value} -> {belongs.Value}"],
                [
                    $"\"{arguments.Destination}\" holds everything the move carried, and its folder note is still called \"{carried.Path.Value}\".",
                    $"Resolve what is holding \"{carried.Path.Value}\".",
                    $"Rename it with a note move from \"{carried.Path.Value}\" to \"{belongs.Value}\" - the wikilink repair already names the second.",
                ]);
    }

    /// <summary>Composes an admitted write path beneath the destination, which the grant already
    /// admits for writing.</summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="destination">The admitted destination path.</param>
    /// <param name="value">The vault-relative path text, composed from segments the access layer
    /// produced.</param>
    /// <returns>The admitted write path.</returns>
    /// <exception cref="InvalidOperationException">The composed text is not a valid vault path, which
    /// is a defect here rather than malformed caller input.</exception>
    private static WritePath Compose(VaultAccess vault, WritePath destination, string value) =>
        VaultPath.TryCreate(destination.Path.Vault, value, out var path, out var reason)
            ? vault.Beneath(destination, path)
            : throw new InvalidOperationException($"Composed folder-note path \"{value}\" is not a vault path: {reason}");

    /// <summary>
    /// Builds the refusal for a move that did not happen, with nothing written.
    /// </summary>
    /// <param name="outcome">Why the move did not happen, or would not.</param>
    /// <param name="arguments">The validated arguments, quoted in the message.</param>
    /// <returns>The refusal. <c>already_exists</c> stays distinguishable from every other one,
    /// because a caller's next step after an occupied destination differs from all of them.</returns>
    private static ToolRefusal Refused(MoveOutcome outcome, MoveArguments arguments) =>
        outcome switch
        {
            MoveOutcome.SourceMissing => new ToolRefusal(
                ToolCodes.PathNotFound,
                $"Nothing is at \"{arguments.Path}\"."),

            MoveOutcome.DestinationOccupied => new ToolRefusal(
                ToolCodes.AlreadyExists,
                $"\"{arguments.Destination}\" is already there. A move never overwrites and never merges."),

            MoveOutcome.MissingParent missing => new ToolRefusal(
                ToolCodes.ParentMissing,
                $"\"{arguments.Destination}\" cannot be reached because \"{missing.Parent}\" is not there. A move brings no folder into existence - create it first.",
                new { parent = missing.Parent }),

            MoveOutcome.InvalidName invalid => MutationSupport.InvalidName(invalid.Name),

            MoveOutcome.Failed failed => new ToolRefusal(
                ToolCodes.WriteFailed,
                $"\"{arguments.Path}\" could not be moved: {failed.Reason}",
                new { path = failed.Path }),

            var other => new ToolRefusal(
                ToolCodes.WriteFailed, $"\"{arguments.Path}\" could not be moved ({other.GetType().Name})."),
        };

    /// <summary>
    /// Restates a refusal raised by the move itself as a partial mutation where the repair pass had
    /// already written.
    /// </summary>
    /// <param name="clean">The refusal for the move that did not happen.</param>
    /// <param name="arguments">The validated arguments, quoted in the message.</param>
    /// <param name="plan">The planned wikilink rewrites, which say whether anything was
    /// written.</param>
    /// <returns>The refusal unchanged where the plan wrote nothing, and a partial-mutation refusal
    /// where it wrote, because the link graph then names a destination nothing arrived at.</returns>
    /// <remarks>
    /// Every refusal decidable before the move is raised before the repair pass runs, so reaching
    /// this with a non-empty plan means the move failed for a reason only the attempt could report.
    /// </remarks>
    private static ToolRefusal AfterRepair(ToolRefusal clean, MoveArguments arguments, WikilinkPlan plan) =>
        plan.IsEmpty
            ? clean
            : MutationSupport.Partial(
                $"{clean.Message} The wikilink repair this move planned had already been written.",
                [.. plan.Rewrites.Select(Render)],
                [$"move {arguments.Path} -> {arguments.Destination}"],
                [
                    $"Nothing was moved: \"{arguments.Path}\" is still where it was.",
                    $"{plan.Rewrites.Count} link targets across the vault now name \"{arguments.Destination}\", which is not there.",
                    "Resolve what the refusal above names, then re-run the identical move - the pass is planned fresh and repairs only what still names the source.",
                ]);

    /// <summary>
    /// Reads the note being moved, and refuses where it is not there or cannot be read.
    /// </summary>
    /// <param name="source">The admitted source path.</param>
    /// <param name="named">The path as the caller spelled it, used in refusal messages.</param>
    /// <returns>The note's stored content and its hash.</returns>
    /// <exception cref="ToolRefusal">The note is not there, or could not be read.</exception>
    /// <remarks>
    /// This is what establishes that a note move has an operand at all, on a preview as on a real
    /// call. A preview that predicted a move of nothing would predict something its own applied call
    /// cannot do. It is a read rather than a presence probe because the two failures are different
    /// answers to the caller - one has nothing to move, the other has something it cannot get at -
    /// and only reading tells them apart.
    /// </remarks>
    private static ReadOutcome.Content SourceContent(WritePath source, string named) =>
        VaultFiles.Read(source.AsRead()) switch
        {
            ReadOutcome.Content content => content,
            ReadOutcome.NotFound => throw new ToolRefusal(ToolCodes.PathNotFound, $"No note is at \"{named}\"."),
            ReadOutcome.Failed failed => throw new ToolRefusal(
                ToolCodes.ReadFailed,
                $"\"{named}\" is there and could not be read: {failed.Reason}",
                new { path = failed.Path }),
            var other => throw new InvalidOperationException($"Unhandled read outcome {other.GetType().Name}."),
        };

    /// <summary>
    /// Returns the note's content hash after the move.
    /// </summary>
    /// <param name="stored">The note's content as it is now.</param>
    /// <param name="source">The admitted source path.</param>
    /// <param name="plan">The planned wikilink rewrites.</param>
    /// <returns>The note's own stored hash, because a move relocates bytes rather than replacing
    /// them - or, where the repair pass rewrites the moved note itself, the hash of the text the plan
    /// will write.</returns>
    private static string HashAfter(ReadOutcome.Content stored, WritePath source, WikilinkPlan plan)
    {
        var rewritten = plan.Notes.FirstOrDefault(note => note.Note.Equals(source.Path));
        return rewritten is null ? stored.ContentHash : VaultFiles.DigestOf(rewritten.Content);
    }

    /// <summary>
    /// Returns what the vault's path policy requires of this move's target set: the operand itself
    /// and, for a folder, every note the folder carries. Artifact-level authority applies to what an
    /// operation names and to everything else in its target set.
    /// </summary>
    /// <param name="policy">The vault's path policy.</param>
    /// <param name="kind">Whether a note or a folder is moving.</param>
    /// <param name="from">The operand's current path.</param>
    /// <param name="to">The operand's destination path.</param>
    /// <param name="plan">The plan, which lists the notes a folder carries.</param>
    /// <returns>One requirement per target.</returns>
    private static IEnumerable<PathRequirement> Requirements(
        PathPolicy policy, OperandKind kind, VaultPath from, VaultPath to, WikilinkPlan plan)
    {
        yield return Relocating(policy, from, to);

        if (kind == OperandKind.Folder)
            foreach (var pair in plan.Relocated)
                yield return Relocating(policy, pair.From, pair.To);
    }

    /// <summary>
    /// Returns one target's contribution to the required op classes, under the two rules a move
    /// splits by.
    /// </summary>
    /// <param name="policy">The vault's path policy.</param>
    /// <param name="from">The target's current path.</param>
    /// <param name="to">The target's destination path.</param>
    /// <returns>The destination path and every op class the move of this target requires.</returns>
    /// <remarks>
    /// A path-based policy entry always contributes, at both ends. A declared file or folder cannot
    /// be renamed or moved without its op class, whether it stays under the declared path or leaves
    /// it; otherwise the protection is avoided by moving the file aside, editing it, and moving it
    /// back. The destination is asked too, because arriving under a declared path mutates that target
    /// as well.
    ///
    /// A structural policy entry contributes only where the target's artifact status changes. A note
    /// renamed to match its folder takes on that shape and requires the class; a folder note carried
    /// along by its own folder's rename is still a folder note and requires nothing. Both directions
    /// count, so renaming a folder note out of that shape requires the class as well, which is the
    /// same evasion read backwards.
    /// </remarks>
    private static PathRequirement Relocating(PathPolicy policy, VaultPath from, VaultPath to)
    {
        var required = new HashSet<string>(policy.MutationRequires(from.Value, PolicyTargets.Paths), StringComparer.Ordinal);
        required.UnionWith(policy.MutationRequires(to.Value, PolicyTargets.Paths));

        var was = policy.MutationRequires(from.Value, PolicyTargets.Structural);
        var becomes = policy.MutationRequires(to.Value, PolicyTargets.Structural);
        required.UnionWith(was.Except(becomes));
        required.UnionWith(becomes.Except(was));

        // Reported at the post-move path, like every path-valued member on this tool.
        return new PathRequirement(to.Value, required);
    }

    private static string Render(LinkRewrite rewrite) => $"{rewrite.Path}:{rewrite.Line}";
}
