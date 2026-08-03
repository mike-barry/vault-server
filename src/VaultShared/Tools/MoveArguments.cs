using System.ComponentModel;

namespace VaultShared.Tools;

/// <summary>
/// <see cref="MoveTool"/>'s arguments. <see cref="Path"/> names what exists and
/// <see cref="Destination"/> is constructed: the difference in spelling is the difference between
/// resolving a path and building one, and a constructed path is checked for occupancy,
/// well-formedness and containment rather than being resolved to exactly one thing.
///
/// <b>Kind is optional here</b>, unlike on <c>create</c> and <c>delete</c>. Both kinds take the same
/// arguments, so the assertion cannot decide which apply; it survives only as a fail-fast for a
/// caller that thinks it is renaming one note and would otherwise relocate a folder of fifty.
/// </summary>
public sealed record MoveArguments(
    [property: Description("Vault-relative path of the note or folder to relocate. A note carries the \".md\" extension; a folder carries none.")]
    string Path,
    [property: Description("Vault-relative path to move it to, of the same kind. Its parent must already exist - nothing brings a folder into existence on the way past - and it must be unoccupied.")]
    string Destination,
    [property: Description("The vault to move within. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("Assert which kind the path names: \"note\" or \"folder\". Optional, and a fail-fast only - both kinds take the same arguments here.")]
    OperandKind? Kind = null,
    [property: Description("Apply the move. Absent or false answers the same result with \"applied\" false: where everything lands, and every wikilink the move would repair or pin.")]
    bool Apply = false) : IVaultArguments
{
    public ArgumentFault? Validate()
    {
        if (ArgumentRules.Missing(Path, "path") is { } missing)
            return missing;
        if (ArgumentRules.Missing(Destination, "destination") is { } destination)
            return destination;
        if (Kind is { } kind && ArgumentRules.Unrecognized(kind, "kind") is { } unrecognized)
            return unrecognized;

        return null;
    }
}
