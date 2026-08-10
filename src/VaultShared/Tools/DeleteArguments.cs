using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VaultShared.Tools;

/// <summary>
/// Represents <see cref="DeleteTool"/>'s arguments.
/// </summary>
/// <remarks>
/// The kind is required because the two kinds take different arguments. A note has nothing beneath
/// it, so <see cref="Recursive"/> cannot change the outcome for one and is refused rather than
/// ignored.
///
/// <see cref="Recursive"/> says which operation the caller wants; it does not suppress a check. There
/// is deliberately no force, purge or skip argument, because a guard the same call can waive stops
/// nobody.
/// </remarks>
/// <param name="Path">The vault-relative path to remove.</param>
/// <param name="Kind">Which kind the path names. Required.</param>
/// <param name="Vault">The vault to remove from, or null for the configured default.</param>
/// <param name="Recursive">Whether to remove a folder together with everything under it. Folders
/// only.</param>
/// <param name="Apply">Whether to perform the removal. False previews it.</param>
public sealed record DeleteArguments(
    [property: Description("Vault-relative path to remove. A note carries the \".md\" extension; a folder carries none. The extension is never inferred.")]
    string Path,
    [property: Description("Which of the two the path names: \"note\" or \"folder\". Required, because the two take different arguments.")]
    OperandKind? Kind,
    [property: Description("The vault to remove from. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("Remove a folder together with everything under it. Folders only. Without it, a folder that holds anything is refused and the refusal carries the full set the removal would destroy.")]
    bool Recursive = false,
    [property: Description("Apply the removal. Absent or false answers the same result with \"applied\" false: the full set that would be destroyed, and every surviving wikilink into it.")]
    bool Apply = false) : IVaultArguments
{
    /// <summary>
    /// The kind, valid once <see cref="Validate"/> has established there is one.
    /// <see cref="Kind"/> is nullable on the record so that omitting it refuses and names the
    /// argument, rather than letting the first enumeration member stand in for a requirement the
    /// schema advertises but nothing enforces.
    /// </summary>
    [JsonIgnore]
    public OperandKind Operand => Kind!.Value;

    /// <summary>Checks that the path is named, that the kind is supplied and recognized, and that
    /// no argument the chosen kind does not take is present.</summary>
    /// <returns>The first fault found, or null.</returns>
    public ArgumentFault? Validate()
    {
        if (ArgumentRules.Missing(Path, "path") is { } missing)
            return missing;
        if (Kind is not { } operand)
            return new ArgumentFault("kind", "\"kind\" is required - it is \"note\" or \"folder\", and the two take different arguments.");
        if (ArgumentRules.Unrecognized(operand, "kind") is { } kind)
            return kind;

        if (Operand == OperandKind.Note && Recursive)
            return new ArgumentFault(
                "recursive",
                "\"recursive\" says a folder is removed with everything under it, and a note has nothing under it.");

        return null;
    }
}
