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
/// not see such a change under the platform's default case handling, and this build performs no
/// repository operations, so it is reported for something else to stage.</param>
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
/// Do not reorder the repair and the move: the link repair runs first. The plan names every note by
/// the path it has now, so applying the repair after the move would address notes that are no longer
/// where the plan found them. Neither order survives an interruption cleanly, since links naming a
/// destination that does not exist yet and links naming a source that no longer does are equally
/// broken. This order is chosen because it is the one that cannot silently half-work.
/// </para>
/// <para>
/// A folder move is a single directory rename. The names inside it do not change, so the repair is
/// one prefix substitution, and the folder note is the only thing renamed: it takes the new folder's
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

        // The bytes a note carries afterwards are known before the move: a move relocates them rather
        // than replacing them, and where the repair pass rewrites the moved note itself, the plan
        // already holds the text it will write. Computing it here, once, is what makes a preview's
        // hash and the applied call's hash the same value.
        var hash = kind == OperandKind.Note ? HashAfter(source, arguments.Path, plan) : null;
        var caseOnly = source.Path.Equals(destination.Path) && !source.Path.HasSameSpellingAs(destination.Path);

        var moved = kind == OperandKind.Note
            ? (IReadOnlyList<string>)[destination.Path.Value]
            : [.. plan.Relocated.Select(pair => pair.To.Value).Order(StringComparer.Ordinal)];

        // Apply.
        if (arguments.Apply)
            Perform(vault, pass, kind, arguments, source, destination, plan);

        return new ToolResult.Data(new MoveResult(
            arguments.Apply,
            destination.Path.Value,
            hash,
            caseOnly,
            moved,
            plan.Repairs,
            plan.Pins,
            plan.FolderNote?.Value,
            asserted));
    }

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
    /// interleaving but does not make this atomic, so a failure partway names exactly what landed and
    /// what did not.</exception>
    /// <remarks>
    /// The move itself runs second, before the folder note's rename, because that is where the
    /// refusals are. An occupied destination and a missing parent are both decided by the folder
    /// rename, and meeting them before the folder note has been touched leaves one fewer partial
    /// state to report.
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
            throw MutationSupport.Partial(
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
            throw Refused(outcome, arguments, plan);

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
    /// Builds the refusal for a move that did not happen.
    /// </summary>
    /// <param name="outcome">Why the move did not happen.</param>
    /// <param name="arguments">The validated arguments, quoted in the message.</param>
    /// <param name="plan">The planned wikilink rewrites, which say whether anything was already
    /// written.</param>
    /// <returns>A clean refusal where no link had been rewritten yet, keeping
    /// <c>already_exists</c> distinguishable because a caller's next step after an occupied
    /// destination differs from every other refusal. Where the repair pass had already written, a
    /// partial-mutation refusal, because the graph now names a destination nothing arrived
    /// at.</returns>
    private static ToolRefusal Refused(MoveOutcome outcome, MoveArguments arguments, WikilinkPlan plan)
    {
        var clean = outcome switch
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

        if (plan.IsEmpty)
            return clean;

        return MutationSupport.Partial(
            $"{clean.Message} The wikilink repair this move planned had already been written.",
            [.. plan.Rewrites.Select(Render)],
            [$"move {arguments.Path} -> {arguments.Destination}"],
            [
                $"Nothing was moved: \"{arguments.Path}\" is still where it was.",
                $"{plan.Rewrites.Count} link targets across the vault now name \"{arguments.Destination}\", which is not there.",
                "Resolve what the refusal above names, then re-run the identical move - the pass is planned fresh and repairs only what still names the source.",
            ]);
    }

    /// <summary>
    /// Returns the note's content hash after the move.
    /// </summary>
    /// <param name="source">The admitted source path.</param>
    /// <param name="named">The path as the caller spelled it, used in refusal messages.</param>
    /// <param name="plan">The planned wikilink rewrites.</param>
    /// <returns>The source's own stored hash, because a move relocates bytes rather than replacing
    /// them - or, where the repair pass rewrites the moved note itself, the hash of the text the plan
    /// will write.</returns>
    /// <exception cref="ToolRefusal">The note is not there, or could not be read.</exception>
    /// <remarks>
    /// Reading here is also what keeps a preview honest about a source that is not there. A preview
    /// that predicted a move of nothing would predict something its own applied call cannot do.
    /// </remarks>
    private static string HashAfter(WritePath source, string named, WikilinkPlan plan)
    {
        var stored = VaultFiles.Read(source.AsRead()) switch
        {
            ReadOutcome.Content content => content,
            ReadOutcome.NotFound => throw new ToolRefusal(ToolCodes.PathNotFound, $"No note is at \"{named}\"."),
            ReadOutcome.Failed failed => throw new ToolRefusal(
                ToolCodes.ReadFailed,
                $"\"{named}\" is there and could not be read: {failed.Reason}",
                new { path = failed.Path }),
            var other => throw new InvalidOperationException($"Unhandled read outcome {other.GetType().Name}."),
        };

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
