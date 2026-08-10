using System.Text.Json.Serialization;
using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Links;

// Two namespaces each declare a WriteOutcome: the access layer's answer to storing bytes, and the
// frontmatter write path's answer to composing a note. This alias names the first; the second is
// only ever held in a var.
using WriteOutcome = VaultShared.Access.WriteOutcome;

namespace VaultShared.Tools;

/// <summary>
/// Represents the result of a create operation.
/// </summary>
/// <param name="Path">Where the note or folder now is, vault-relative and usable in a later call.
/// This is the resolved spelling rather than the caller's.</param>
/// <param name="ContentHash">The new note's content hash, so a create chains straight into an edit.
/// Absent for a folder, which leaves no single note to describe.</param>
/// <param name="PinnedLinks">
/// The links in other notes that were frozen to what they open today. Absent and empty mean different
/// things: absent means the basename was unique and no pass ran, and an empty list means the pass ran
/// and found nothing to protect. It is always absent for a folder, which brings no note into
/// existence.
/// </param>
/// <param name="AssertedAuthority">The op classes the outcome required beyond the tool's floor. It is
/// reported even when empty, so that a caller can tell an operation that asserted nothing from a tool
/// that never asserts.</param>
public sealed record CreateResult(
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContentHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LinkRewrite>? PinnedLinks,
    IReadOnlyList<string> AssertedAuthority);

/// <summary>
/// Creates one note or one folder. This is the only way to bring either into existence, and the only
/// tool that creates directories.
/// </summary>
/// <remarks>
/// <para>
/// An occupied target is refused rather than overwritten or merged, with its own code. A note
/// creation reads the target first, so a call that would refuse does not rewrite other notes'
/// wikilinks on the way to finding out, but the answer that counts is the filesystem's own to an
/// atomic create. Nothing this tool reads beforehand can rule out something else creating the file
/// in between, and only the atomic create refuses at the moment the bytes would land.
/// </para>
/// <para>
/// Nothing is composed here. A note's content is finished text: no templating, no field-by-field
/// composition, no computed name, no injected provenance. What the caller sends is validated whole
/// through the single frontmatter write path, which classifies and asserts the authority the supplied
/// block turns out to require, and then stored.
/// </para>
/// <para>
/// There is no preview. A creation destroys nothing. It can still pin wikilinks in notes the caller
/// never named, but pinning only holds the resolution that already applied, so the result reports
/// what was pinned rather than asking permission first.
/// </para>
/// </remarks>
/// <param name="registries">Every vault's loaded frontmatter declaration. Held on the tool rather
/// than passed per call, because a declaration is per vault and loaded once at startup.</param>
public sealed class CreateTool(FrontmatterRegistries registries) : ITool
{
    /// <summary>The tool's wire name.</summary>
    public const string ToolName = "create";

    /// <summary>
    /// The tool's declaration. The floor is the base write class. What a particular creation requires
    /// beyond it depends on which keys the supplied block carries and on what the vault's path policy
    /// says about the target, and both are asserted against the outcome rather than against what the
    /// caller said it was doing.
    /// </summary>
    /// <remarks>
    /// The wikilink pass a duplicate basename triggers contributes that same base class, because it
    /// replaces the target inside links that already exist and reads no key. It is not
    /// scope-checked, which
    /// <see cref="VaultAccess.BypassingScope"/> makes explicit at the call site rather than through
    /// an op class of its own.
    /// </remarks>
    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Creates one note or one folder. The path is vault-relative; a note carries the \".md\" " +
            "extension and a folder carries none, and \"kind\" says which is meant. A note's " +
            "\"content\" is finished text - nothing is templated or composed here. An occupied " +
            "target is refused rather than overwritten. Where the new note's basename is already in " +
            "use, bare wikilinks that would have changed meaning are pinned first and reported.",
        Arguments = typeof(CreateArguments),
        OpClassFloor = new Floor.Class("content:write"),
        Mutates = true,
    };

    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<CreateArguments>();
        var vault = call.RequireVault(ToolName);

        // Argument validation and vault resolution have already run in the shared execute path.

        // Path resolution, scope, and policy.
        var target = ToolPaths.Write(vault, arguments.Path, ToolPaths.KindOf(arguments.Path, arguments.Operand));

        return new ToolResult.Data(
            arguments.Operand == OperandKind.Note
                ? CreateNote(vault, arguments, target)
                : CreateFolder(vault, arguments, target));
    }

    /// <summary>
    /// Creates a note. Every refusal that can be decided before the write is raised first, so
    /// nothing changes behind one: the artifact-level authority assertion, the supplied note through
    /// the frontmatter write path, the target's name, parent and occupancy, and the basename census.
    /// Only then are the pins written and the note stored, in that order.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="target">The admitted write path.</param>
    /// <returns>The result, including the new note's hash and any links that were pinned.</returns>
    /// <exception cref="ToolRefusal">Any step refused.</exception>
    private CreateResult CreateNote(VaultAccess vault, CreateArguments arguments, WritePath target)
    {
        // Plan.
        var artifact = ArtifactRequirement(vault, target.Path);
        var asserted = new SortedSet<string>(MutationSupport.AssertArtifactAuthority(vault, [artifact]), StringComparer.Ordinal);

        var supplied = NoteSplit.Of(arguments.Content!);
        var registry = supplied.HasBlock ? registries.For(vault.Vault.Name).Require() : MutationSupport.NoVocabulary;

        // The single frontmatter write path, run against a note with no original. A supplied block is
        // parsed rather than spliced through as text, and is validated whole because the caller
        // supplied every key. The authority the outcome requires is classified over the composed
        // diff, which against no original counts every key present as changed. This tool catches none
        // of the write path's refusals; they reach the wire through ToolRefusalEnvelope.
        var composed = FrontmatterWrite.Apply(new WriteRequest(
            Content: string.Empty,
            RelativePath: target.Path.Value,
            Registry: registry,
            Policy: vault.Vault.Policy,
            Authority: vault,
            Operations: [],
            SuppliedBlock: supplied.HasBlock ? supplied.InnerText : null,
            NewBody: supplied.Body));

        asserted.UnionWith(composed.AssertedAuthority);

        // Every refusal the creation itself can decide from the filesystem is answered here, before
        // the pinning pass plans or writes anything. Without it an applied call would discover the
        // refusal with bystander notes across the vault already rewritten for a note that never
        // lands, and the caller would be told the creation did not happen and nothing about the
        // notes it never named that changed.
        //
        // This does not replace the check inside the write. The write is still what decides
        // occupancy, because another writer can take the target between this call and the write.
        // Reading it here narrows that window and keeps the pass off a doomed creation; reading it
        // again in the write is what makes the answer true at the moment the bytes land.
        if (VaultFiles.PreflightCreate(target) is { } refusing)
            throw Refused(arguments.Path, refusing);

        // The basename census, taken before anything is written. A basename nothing else carries
        // cannot change what any link resolves to, so no pass runs and no pins are reported.
        var pass = vault.BypassingScope();
        var planned = WikilinkMaintenance.PlanCreation(pass, target.Path);
        var plan = planned switch
        {
            LinkPassOutcome<WikilinkPlan>.Completed completed => completed.Result,
            LinkPassOutcome<WikilinkPlan>.NotNeeded => null,
            var failure => throw MutationSupport.PassFailure(failure),
        };

        // Apply.

        // Do not reorder. Pinning must run before the note is written. Interrupted after pinning, the
        // vault holds links naming a target that is still correct. Interrupted after writing, it
        // holds bare links that have silently changed meaning.
        if (plan is not null && !plan.IsEmpty && WikilinkMaintenance.Apply(pass, plan) is LinkApplyOutcome.Failed stopped)
            throw MutationSupport.Partial(
                $"\"{stopped.Path}\" could not be rewritten, so the pins this creation needs are only partly in place and \"{target.Path.Value}\" was not created.",
                [.. stopped.Written.Select(Render)],
                [.. stopped.Unwritten.Select(Render), $"create {target.Path.Value}"],
                [
                    $"Nothing was created: \"{target.Path.Value}\" is not there.",
                    $"Resolve what is holding \"{stopped.Path}\".",
                    "Re-run the identical create - the pass is planned fresh each time and re-pins only what still needs it.",
                ]);

        return new CreateResult(
            target.Path.Value,
            Store(target, arguments.Path, composed.Content),
            plan?.Pins,
            [.. asserted]);
    }

    /// <summary>
    /// Creates a folder. No note comes into existence, so no wikilink pass runs and no pins are
    /// reported.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="target">The admitted write path.</param>
    /// <returns>The result, with no content hash and no pinned links.</returns>
    /// <exception cref="ToolRefusal">The authority assertion or the write refused.</exception>
    /// <remarks>
    /// The caller can ask for the placeholder file but can never name one, which is why
    /// <see cref="VaultFiles.CreateGitKeep"/> takes the folder and composes the child path itself.
    /// The paths a caller may name are deliberately narrower than what an operation touches.
    /// </remarks>
    private static CreateResult CreateFolder(VaultAccess vault, CreateArguments arguments, WritePath target)
    {
        // Plan. Folder creation routes through no frontmatter write, so this tool consults the
        // vault's path policy itself, before anything is mutated.
        var asserted = MutationSupport.AssertArtifactAuthority(vault, [ArtifactRequirement(vault, target.Path)]);

        // Apply.
        switch (VaultFiles.CreateFolder(target))
        {
            case WriteOutcome.Written:
                break;
            case WriteOutcome.AlreadyExists:
                throw new ToolRefusal(
                    ToolCodes.AlreadyExists,
                    $"\"{arguments.Path}\" is already there. Creation never overwrites and never merges - use it, or create under a different name.");
            case WriteOutcome.InvalidName invalid:
                throw MutationSupport.InvalidName(invalid.Name);
            case WriteOutcome.MissingParent missing:
                throw ParentMissing(arguments.Path, missing.Parent);
            case WriteOutcome.Failed failed:
                throw Failed(arguments.Path, failed);
            case var other:
                throw new InvalidOperationException($"Unhandled write outcome {other.GetType().Name}.");
        }

        // An existing placeholder is already the state the caller asked for, so it is not a failure.
        if (arguments.GitKeep && VaultFiles.CreateGitKeep(target) is WriteOutcome.Failed seeding)
            throw Failed($"{arguments.Path}/{VaultFiles.GitKeepName}", seeding);

        return new CreateResult(target.Path.Value, ContentHash: null, PinnedLinks: null, asserted);
    }

    /// <summary>Returns what the vault's path policy requires to mutate this target. A creation names
    /// one path, so the set has one member.</summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="target">The target path.</param>
    /// <returns>The path and the op classes required for it.</returns>
    private static PathRequirement ArtifactRequirement(VaultAccess vault, VaultPath target) =>
        new(target.Value, vault.Vault.Policy.MutationRequires(target.Value));

    /// <summary>Stores the composed note and returns the hash of what landed.</summary>
    /// <param name="target">The admitted write path.</param>
    /// <param name="named">The path as the caller spelled it, used in refusal messages.</param>
    /// <param name="content">The composed note text.</param>
    /// <returns>The stored file's content hash.</returns>
    /// <exception cref="ToolRefusal">The target was occupied, the name was refused, the parent was
    /// missing, or the write failed. The occupied-target refusal comes from the atomic create, so
    /// nothing can create the file between a check and the write.</exception>
    private static string Store(WritePath target, string named, string content) =>
        VaultFiles.Create(target, content) switch
        {
            WriteOutcome.Written written => written.ContentHash!,
            var refusing => throw Refused(named, refusing),
        };

    /// <summary>Turns a note creation's refusing outcome into the refusal a caller sees.</summary>
    /// <param name="named">The path as the caller spelled it, quoted in the message.</param>
    /// <param name="outcome">The outcome to translate, which must not be a success.</param>
    /// <returns>The refusal.</returns>
    /// <exception cref="InvalidOperationException">The outcome is one this tool does not
    /// produce.</exception>
    private static ToolRefusal Refused(string named, WriteOutcome outcome) =>
        outcome switch
        {
            WriteOutcome.AlreadyExists => new ToolRefusal(
                ToolCodes.AlreadyExists,
                $"\"{named}\" is already there. Creation never overwrites and never merges - edit it, or create under a different name."),

            WriteOutcome.InvalidName invalid => MutationSupport.InvalidName(invalid.Name),
            WriteOutcome.MissingParent missing => ParentMissing(named, missing.Parent),
            WriteOutcome.Failed failed => Failed(named, failed),

            var other => throw new InvalidOperationException($"Unhandled write outcome {other.GetType().Name}."),
        };

    private static ToolRefusal ParentMissing(string named, string parent) =>
        new(
            ToolCodes.ParentMissing,
            $"\"{named}\" cannot be created because \"{parent}\" is not there. Nothing brings a folder into existence on the way past - create it first.",
            new { parent });

    private static ToolRefusal Failed(string named, WriteOutcome.Failed failed) =>
        new(ToolCodes.WriteFailed, $"\"{named}\" could not be created: {failed.Reason}", new { path = failed.Path });

    private static string Render(LinkRewrite rewrite) => $"{rewrite.Path}:{rewrite.Line}";
}
