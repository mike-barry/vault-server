using System.Text.Json.Serialization;
using VaultShared.Access;
using VaultShared.Links;
using VaultShared.Scope;

namespace VaultShared.Tools;

/// <summary>
/// What a relocation turned out to be, or would be. Every path-valued member names a post-mutation
/// path, on a plan exactly as on a result, so no caller translates between two path vocabularies.
/// </summary>
/// <param name="Applied">Whether this call moved anything. False is the preview: the same shape,
/// computed under the same lock and the same validation.</param>
/// <param name="Path">Where the operand lands.</param>
/// <param name="ContentHash">The moved note's hash afterwards - the same value a read of it returns,
/// so a move chains straight into an edit. Absent for a folder move: there is no single note left to
/// describe.</param>
/// <param name="CaseOnly">Whether the two paths name one entry under different spellings. Git does
/// not see such a change under the platform's default case handling and repository operations are
/// outside this build, so it is reported for a later pickup to stage.</param>
/// <param name="Moved">Every note whose path this relocation changes, where it lands. One entry for
/// a note; for a folder, every note the folder carries - the folder's other content moves with it
/// under names that do not change.</param>
/// <param name="RepairedLinks">Links that named something this move relocates, re-emitted at its
/// destination. Canonical rather than minimal - the short form where the name is unique after the
/// move, the full vault path where it is not.</param>
/// <param name="PinnedLinks">Bystander links frozen to what they open today, where the move would
/// otherwise have changed the answer. Pinning freezes; it never redirects, and no link is ever
/// re-aimed at something it did not already name.</param>
/// <param name="FolderNote">Where a relocated folder's folder note lands, renamed to match its new
/// folder. Absent where the folder has none - a folder that has none gets none created - and absent
/// on a note's relocation.</param>
/// <param name="AssertedAuthority">What the outcome required beyond the tool's floor, empty where it
/// required nothing.</param>
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
/// The only way to relocate or rename a note or a folder, keeping the wikilink graph intact.
///
/// <b>Repair runs before the move.</b> The plan names every note by the path it has now, so applying
/// it afterwards would address notes that are no longer where the plan found them. Neither order is
/// better on interruption - links naming a destination that does not exist yet and links naming a
/// source that no longer does are equally broken - so what decides it is that only this order cannot
/// silently half-work.
///
/// <b>A folder relocation is one directory rename.</b> Contained names do not change, so the repair
/// is a single prefix substitution and the folder note is the only name event: it is renamed to match
/// its new folder, and a folder that has none gets none created.
///
/// <b>It previews by default.</b> The routine call reports what the move would do and a second call
/// applies it. The plan runs the identical path - the same lock, the same validation, every refusal a
/// real call would raise - and differs from its own result in <see cref="MoveResult.Applied"/> alone.
/// </summary>
public sealed class MoveTool : ITool
{
    public const string ToolName = "move";

    /// <summary>
    /// The floor is the base write class. What a particular move requires beyond it is what the
    /// vault's path policy declares about the targets in its set.
    ///
    /// The wikilink pass contributes that same base class and nothing more: it replaces the target
    /// inside links that already exist, reads no key, and the notes it rewrites contribute none of
    /// their own - those rewrites are maintenance rather than authoring. What is special about the
    /// pass is that it is not scope-checked, which is the seam
    /// <see cref="VaultAccess.BypassingScope"/> makes deliberate.
    /// </summary>
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

        // ---- 3. Path resolution, scope, and policy ----

        // The kind comes from the operand's own spelling; the assertion, where the caller made one,
        // only checks it. The destination is held to the same rule, so a note never lands under a
        // folder-shaped name and a folder never acquires an extension.
        var kind = ToolPaths.KindOf(arguments.Path, arguments.Kind);
        var source = ToolPaths.Write(vault, arguments.Path, kind);
        var destination = ToolPaths.Write(vault, arguments.Destination, kind);

        if (destination.Path.HasSameSpellingAs(source.Path))
            throw new ToolRefusal(
                ToolCodes.InvalidArgument,
                "\"destination\" is the path already given - a move relocates or renames, and a case-only rename is the one spelling of this call that names the same entry twice.",
                new { argument = "destination" });

        // ---- 4. Plan ----

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

        // The bytes a note carries afterwards are known before the move: a relocation moves them
        // rather than replacing them, and where the repair pass rewrites the moved note itself, the
        // plan already holds the text it will write. Computing it once is what makes a plan's hash
        // and its own result's hash the same value rather than two derivations that can disagree.
        var hash = kind == OperandKind.Note ? HashAfter(source, arguments.Path, plan) : null;
        var caseOnly = source.Path.Equals(destination.Path) && !source.Path.HasSameSpellingAs(destination.Path);

        var moved = kind == OperandKind.Note
            ? (IReadOnlyList<string>)[destination.Path.Value]
            : [.. plan.Relocated.Select(pair => pair.To.Value).Order(StringComparer.Ordinal)];

        // ---- 5. Apply ----

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
    /// Repairs the graph, relocates, and renames a relocated folder's folder note - the three
    /// coupled effects one relocation carries. All of it is multi-target work under one lock, and the
    /// lock is not atomicity: a failure partway names exactly what landed and what did not.
    ///
    /// The order puts the relocation itself second, ahead of the folder note's rename, because that
    /// is where the refusals are: an occupied destination and a missing parent are decided by the
    /// rename of the folder, and meeting them before the folder note has been touched leaves one
    /// fewer partial state to report.
    /// </summary>
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
    /// The folder note's rename, the one name event a folder relocation carries. Contained names do
    /// not change, so the directory rename has already carried the note across under its old name;
    /// what is left is to give it the new folder's.
    ///
    /// A folder that has none gets none created, and a relocation that keeps the folder's name has
    /// nothing to rename. Both fall out of comparing where the note now is against where the plan
    /// says it belongs.
    /// </summary>
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

    /// <summary>An admitted path beneath the destination, which the grant already admits for
    /// writing. The value is composed from segments the layer produced, so text that is not
    /// well-formed is a defect here rather than a caller's malformed input.</summary>
    private static WritePath Compose(VaultAccess vault, WritePath destination, string value) =>
        VaultPath.TryCreate(destination.Path.Vault, value, out var path, out var reason)
            ? vault.Beneath(destination, path)
            : throw new InvalidOperationException($"Composed folder-note path \"{value}\" is not a vault path: {reason}");

    /// <summary>
    /// The relocation's own refusal. With no link yet rewritten it is a clean one, and
    /// <c>already_exists</c> in particular stays distinguishable because a caller's next move after
    /// an occupied destination is entirely different from every other refusal. Where the repair pass
    /// has already written, the same condition is a partial mutation, because it is one: the graph
    /// names a destination nothing arrived at.
    /// </summary>
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
    /// The note's hash after the relocation. A move relocates bytes rather than replacing them, so
    /// the answer is the source's own stored hash - unless the repair pass rewrites the moved note
    /// itself, in which case the plan already holds the text that will be written and the hash is
    /// taken from that.
    ///
    /// The read is also what makes a plan faithful about a source that is not there: a preview that
    /// predicted a relocation of nothing would be a prediction its own result cannot produce.
    /// </summary>
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
    /// What the vault's path policy requires of this relocation's target set: the operand, and - on a
    /// folder - every note the folder carries, since artifact authority applies to what an operation
    /// names and to everything in its target set.
    /// </summary>
    private static IEnumerable<PathRequirement> Requirements(
        PathPolicy policy, OperandKind kind, VaultPath from, VaultPath to, WikilinkPlan plan)
    {
        yield return Relocating(policy, from, to);

        if (kind == OperandKind.Folder)
            foreach (var pair in plan.Relocated)
                yield return Relocating(policy, pair.From, pair.To);
    }

    /// <summary>
    /// One target's contribution, under the two rules relocation splits by.
    ///
    /// <b>A path entry always contributes.</b> A declared file or folder cannot be renamed or moved
    /// without the class, whether it stays under the declared path or leaves it - otherwise the
    /// protection is trivially avoided by moving the file aside, editing it, and moving it back. Both
    /// ends are asked, because arriving under a declared path mutates that target too.
    ///
    /// <b>A structural rule contributes only where artifact status changes.</b> A note renamed to
    /// match its folder acquires the shape and requires the class; a folder note carried along by its
    /// own folder's rename is still a folder note and requires nothing. Both directions count, so
    /// renaming a folder note out of its shape requires the class as well - that is the same evasion
    /// read backwards.
    /// </summary>
    private static PathRequirement Relocating(PathPolicy policy, VaultPath from, VaultPath to)
    {
        var required = new HashSet<string>(policy.MutationRequires(from.Value, PolicyTargets.Paths), StringComparer.Ordinal);
        required.UnionWith(policy.MutationRequires(to.Value, PolicyTargets.Paths));

        var was = policy.MutationRequires(from.Value, PolicyTargets.Structural);
        var becomes = policy.MutationRequires(to.Value, PolicyTargets.Structural);
        required.UnionWith(was.Except(becomes));
        required.UnionWith(becomes.Except(was));

        // Named at the post-mutation path, as every path-valued member on this tool is.
        return new PathRequirement(to.Value, required);
    }

    private static string Render(LinkRewrite rewrite) => $"{rewrite.Path}:{rewrite.Line}";
}
