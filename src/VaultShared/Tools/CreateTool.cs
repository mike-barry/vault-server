using System.Text.Json.Serialization;
using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Links;

// Both namespaces carry a WriteOutcome - the layer's answer to storing bytes, and the write path's
// answer to composing a note. Only the first is named here; the second arrives through var.
using WriteOutcome = VaultShared.Access.WriteOutcome;

namespace VaultShared.Tools;

/// <summary>
/// What a creation turned out to be. Every member is one this tool genuinely populates.
/// </summary>
/// <param name="Path">Where the thing now is, vault-relative and quotable into a later call. It is
/// the resolved spelling rather than the caller's.</param>
/// <param name="ContentHash">The note's hash, so a create chains straight into an edit. Absent for
/// a folder: there is no single note left to describe.</param>
/// <param name="PinnedLinks">
/// The bystander links frozen to what they open today. <b>Absent and empty are different answers</b>:
/// absent means the basename was unique and no pass ran, an empty list means the pass ran and found
/// nothing to protect. A folder creation brings no note into existence, so it is always absent
/// there.
/// </param>
/// <param name="AssertedAuthority">What the outcome required beyond the tool's floor. It stays when
/// empty: absence would not distinguish an operation that asserted nothing from a tool that never
/// asserts.</param>
public sealed record CreateResult(
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContentHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LinkRewrite>? PinnedLinks,
    IReadOnlyList<string> AssertedAuthority);

/// <summary>
/// The only way to bring a note or a folder into existence, and the tool that owns directory
/// creation.
///
/// It brings something into existence, so <b>existence is its failure</b>: an occupied target is
/// refused rather than overwritten or merged, distinguishably from every other refusal, and the
/// refusal is the filesystem's answer to an atomic create rather than a check this runs first.
///
/// <b>Nothing is composed here.</b> A note's content is finished text: no templating, no
/// field-by-field composition, no computed name and no injected provenance. What the caller sends is
/// validated whole through the single frontmatter write path - which is what classifies and asserts
/// the authority the supplied block turned out to require - and stored.
///
/// <b>There is no preview gate.</b> A creation destroys nothing. It can still pin wikilinks in notes
/// the caller never named, and pinning holds the resolution that already applied, so the result
/// reports what was pinned rather than asking permission for it.
/// </summary>
/// <param name="registries">Every vault's loaded declaration, held rather than handed in on the
/// call, because the declaration is per vault and loaded once at startup.</param>
public sealed class CreateTool(FrontmatterRegistries registries) : ITool
{
    public const string ToolName = "create";

    /// <summary>
    /// The floor is the base write class. What a particular creation requires beyond it depends on
    /// which keys the supplied block carries and on what the vault's path policy says about the
    /// target, and both are asserted on the outcome rather than on what the caller said it was
    /// doing.
    ///
    /// The wikilink dimension a duplicate basename triggers contributes that same base class: the
    /// pass replaces the target inside links that already exist and reads no key. What is special
    /// about it is that it is not scope-checked, which is the seam
    /// <see cref="VaultAccess.BypassingScope"/> makes deliberate rather than a class of its own.
    /// </summary>
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

        // ---- 3. Path resolution, scope, and policy ----

        var target = ToolPaths.Write(vault, arguments.Path, ToolPaths.KindOf(arguments.Path, arguments.Operand));

        return new ToolResult.Data(
            arguments.Operand == OperandKind.Note
                ? CreateNote(vault, arguments, target)
                : CreateFolder(vault, arguments, target));
    }

    /// <summary>
    /// The note form. Four steps, and every refusal precedes every change: the artifact assertion,
    /// the whole supplied note through the write path, the basename census, and then - in that order
    /// - the pins and the note.
    /// </summary>
    private CreateResult CreateNote(VaultAccess vault, CreateArguments arguments, WritePath target)
    {
        // ---- 4. Plan ----

        var artifact = ArtifactRequirement(vault, target.Path);
        var asserted = new SortedSet<string>(MutationSupport.AssertArtifactAuthority(vault, [artifact]), StringComparer.Ordinal);

        var supplied = NoteSplit.Of(arguments.Content!);
        var registry = supplied.HasBlock ? registries.For(vault.Vault.Name).Require() : MutationSupport.NoVocabulary;

        // The single write path, over a note with no original. A supplied block is an input to a
        // parse rather than text spliced through: it is validated whole, because the caller supplied
        // every key, and the authority the outcome requires is classified over the composed diff -
        // which against no original counts every key present as changed. Its refusals reach the wire
        // through the one table that names them; this tool catches none of them.
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

        // The basename census, asked before anything is written. A name nothing else carries cannot
        // change what any link resolves to, so no pass runs and no pins are reported.
        var pass = vault.BypassingScope();
        var planned = WikilinkMaintenance.PlanCreation(pass, target.Path);
        var plan = planned switch
        {
            LinkPassOutcome<WikilinkPlan>.Completed completed => completed.Result,
            LinkPassOutcome<WikilinkPlan>.NotNeeded => null,
            var failure => throw MutationSupport.PassFailure(failure),
        };

        // ---- 5. Apply ----

        // Pinning runs before the note is written, and the order is the whole of the guarantee:
        // interrupted after pinning, the vault holds links naming a target that is still correct;
        // interrupted after writing, it holds bare links that have silently changed meaning.
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
    /// The folder form. It brings no note into existence, so no wikilink pass runs and no pins are
    /// reported.
    ///
    /// <b>The placeholder is reached from the operand, never named by the caller.</b> The perimeter
    /// is narrower than an operation's footprint on purpose: a caller can ask folder creation for a
    /// <c>.gitkeep</c> and cannot name one, which is why
    /// <see cref="VaultFiles.CreateGitKeep"/> takes the folder and composes the child path itself.
    /// </summary>
    private static CreateResult CreateFolder(VaultAccess vault, CreateArguments arguments, WritePath target)
    {
        // ---- 4. Plan ----

        // Folder creation routes through no frontmatter write, so this tool consults the vault's
        // path policy itself, in its own validate phase and with zero mutation on denial.
        var asserted = MutationSupport.AssertArtifactAuthority(vault, [ArtifactRequirement(vault, target.Path)]);

        // ---- 5. Apply ----

        switch (VaultFiles.CreateFolder(target))
        {
            case WriteOutcome.Written:
                break;
            case WriteOutcome.InvalidName invalid:
                throw MutationSupport.InvalidName(invalid.Name);
            case WriteOutcome.MissingParent missing:
                throw ParentMissing(arguments.Path, missing.Parent);
            case WriteOutcome.Failed failed:
                throw Failed(arguments.Path, failed);
            case var other:
                throw new InvalidOperationException($"Unhandled write outcome {other.GetType().Name}.");
        }

        // An existing placeholder is the state the caller asked for, so it is not a second failure:
        // the folder now exists and holds the file that makes it survive a repository round trip.
        if (arguments.GitKeep && VaultFiles.CreateGitKeep(target) is WriteOutcome.Failed seeding)
            throw Failed($"{arguments.Path}/{VaultFiles.GitKeepName}", seeding);

        return new CreateResult(target.Path.Value, ContentHash: null, PinnedLinks: null, asserted);
    }

    /// <summary>
    /// What the vault's path policy says mutating this target requires. A creation names one path,
    /// so the set has one member and both target kinds are consulted: creating an artifact always
    /// requires the declared class, whether the declaration named the path or the shape.
    /// </summary>
    private static PathRequirement ArtifactRequirement(VaultAccess vault, VaultPath target) =>
        new(target.Value, vault.Vault.Policy.MutationRequires(target.Value));

    /// <summary>
    /// Stores the composed note and answers the hash of what landed. The refusal on an occupied
    /// target comes from the atomic create, which is what closes the window in which something else
    /// creates the file between a check and a write.
    /// </summary>
    private static string Store(WritePath target, string named, string content) =>
        VaultFiles.Create(target, content) switch
        {
            WriteOutcome.Written written => written.ContentHash!,

            WriteOutcome.AlreadyExists => throw new ToolRefusal(
                ToolCodes.AlreadyExists,
                $"\"{named}\" is already there. Creation never overwrites and never merges - edit it, or create under a different name."),

            WriteOutcome.InvalidName invalid => throw MutationSupport.InvalidName(invalid.Name),
            WriteOutcome.MissingParent missing => throw ParentMissing(named, missing.Parent),
            WriteOutcome.Failed failed => throw Failed(named, failed),

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
