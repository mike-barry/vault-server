using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VaultShared.Tools;

/// <summary>
/// <see cref="CreateTool"/>'s arguments: one path, which kind it names, and the arguments that kind
/// takes.
///
/// <b>Kind is required here because the two kinds take different arguments.</b> <see cref="Content"/>
/// is a note's substance and means nothing for a folder; <see cref="GitKeep"/> is a folder's
/// placeholder and means nothing for a note. Where an argument cannot change an outcome for the
/// supplied operand it is refused rather than ignored, which is what makes the kind decide the shape
/// rather than decorate it.
/// </summary>
public sealed record CreateArguments(
    [property: Description("Vault-relative path to bring into existence. A note carries the \".md\" extension; a folder carries none. The extension is never inferred.")]
    string Path,
    [property: Description("Which of the two the path names: \"note\" or \"folder\". Required, because the two take different arguments.")]
    OperandKind? Kind,
    [property: Description("The vault to create in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("The note's finished text, frontmatter block included where it has one. Notes only, and required for one: nothing is templated or composed here, and empty text creates an empty note.")]
    string? Content = null,
    [property: Description("Whether to seed the folder's \".gitkeep\", so a folder expected to exist while empty survives a repository round trip. Folders only; off by default.")]
    bool GitKeep = false) : IVaultArguments
{
    /// <summary>
    /// The kind, once <see cref="Validate"/> has established there is one. It is nullable on the
    /// record so that omitting it is a refusal naming the argument rather than the first enum member
    /// silently standing in - a default here would advertise a requirement nothing enforces.
    /// </summary>
    [JsonIgnore]
    public OperandKind Operand => Kind!.Value;

    /// <summary>
    /// Everything decidable from the record alone: the operand is named, the kind is one this
    /// vocabulary carries, and every argument the chosen kind does not take is absent.
    ///
    /// The extension rule is not here. Whether the path's spelling agrees with the kind is a path
    /// refusal carrying the spelling that would have worked, which an
    /// <see cref="ArgumentFault"/> can carry neither a code nor a payload for.
    /// </summary>
    public ArgumentFault? Validate()
    {
        if (ArgumentRules.Missing(Path, "path") is { } missing)
            return missing;
        if (Kind is not { } operand)
            return new ArgumentFault("kind", "\"kind\" is required - it is \"note\" or \"folder\", and the two take different arguments.");
        if (ArgumentRules.Unrecognized(operand, "kind") is { } kind)
            return kind;

        if (Operand == OperandKind.Note && GitKeep)
            return new ArgumentFault(
                "git_keep",
                "\"git_keep\" is a folder's placeholder and means nothing for a note - a note is itself what makes its folder exist in the repository.");

        if (Operand == OperandKind.Folder && Content is not null)
            return new ArgumentFault(
                "content",
                "\"content\" is a note's text and means nothing for a folder - create the folder, then create the notes in it.");

        // Required rather than defaulted to empty: a creation composes nothing and templates
        // nothing, so the text is the whole substance of the call, and a caller that genuinely
        // wants an empty note says so by sending empty text.
        if (Operand == OperandKind.Note && Content is null)
            return new ArgumentFault(
                "content",
                "\"content\" is required for a note - it is the note's finished text, and nothing here composes or templates one. Send empty text for an empty note.");

        return null;
    }
}
