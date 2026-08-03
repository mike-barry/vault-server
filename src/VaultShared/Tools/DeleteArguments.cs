using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VaultShared.Tools;

/// <summary>
/// <see cref="DeleteTool"/>'s arguments.
///
/// <b>Kind is required here because the two kinds take different arguments</b>: a note has nothing
/// beneath it, so <see cref="Recursive"/> cannot change an outcome for one and is refused rather
/// than ignored.
///
/// <see cref="Recursive"/> states which operation the caller wants; it does not suppress a check.
/// There is no force, no purge and no skip - a gate the same call can waive stops nobody, because a
/// caller that hits the refusal simply resends with the waiver attached.
/// </summary>
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
    /// The kind, once <see cref="Validate"/> has established there is one. It is nullable on the
    /// record so that omitting it is a refusal naming the argument rather than the first enum member
    /// silently standing in - a default here would advertise a requirement nothing enforces.
    /// </summary>
    [JsonIgnore]
    public OperandKind Operand => Kind!.Value;

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
