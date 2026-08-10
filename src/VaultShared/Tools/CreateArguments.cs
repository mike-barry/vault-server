using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VaultShared.Tools;

/// <summary>
/// Represents <see cref="CreateTool"/>'s arguments: one path, which kind it names, and the arguments
/// that kind takes.
/// </summary>
/// <remarks>
/// The kind is required because the two kinds take different arguments. <see cref="Content"/> is a
/// note's substance and means nothing for a folder; <see cref="GitKeep"/> is a folder's placeholder
/// and means nothing for a note. An argument that cannot change the outcome for the supplied kind is
/// refused rather than ignored.
/// </remarks>
/// <param name="Path">The vault-relative path to bring into existence.</param>
/// <param name="Kind">Which kind the path names. Required.</param>
/// <param name="Vault">The vault to create in, or null for the configured default.</param>
/// <param name="Content">The note's finished text. Notes only, and required for one.</param>
/// <param name="GitKeep">Whether to seed the folder's placeholder file. Folders only.</param>
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
    /// The kind, valid once <see cref="Validate"/> has established there is one.
    /// <see cref="Kind"/> is nullable on the record so that omitting it refuses and names the
    /// argument, rather than letting the first enumeration member stand in for a requirement the
    /// schema advertises but nothing enforces.
    /// </summary>
    [JsonIgnore]
    public OperandKind Operand => Kind!.Value;

    /// <summary>
    /// Checks everything decidable from the record alone: that the path is named, that the kind is
    /// supplied and recognized, and that no argument the chosen kind does not take is present.
    /// </summary>
    /// <returns>The first fault found, or null.</returns>
    /// <remarks>
    /// Whether the path's spelling agrees with the kind is checked where the path is resolved. It is
    /// a path refusal carrying the spelling that would have worked, and an
    /// <see cref="ArgumentFault"/> can carry neither that code nor that payload.
    /// </remarks>
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

        if (Operand == OperandKind.Note && Content is null)
            return new ArgumentFault(
                "content",
                "\"content\" is required for a note - it is the note's finished text, and nothing here composes or templates one. Send empty text for an empty note.");

        return null;
    }
}
