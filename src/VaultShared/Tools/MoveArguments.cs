using System.ComponentModel;

namespace VaultShared.Tools;

/// <summary>
/// Represents <see cref="MoveTool"/>'s arguments. <see cref="Path"/> names something that exists and
/// is resolved to exactly one entry. <see cref="Destination"/> is a path being constructed and is
/// instead checked for occupancy, well-formedness and containment.
/// </summary>
/// <remarks>
/// The kind is optional here, unlike on create and delete, because both kinds take the same arguments
/// and the assertion cannot decide which ones apply. It survives as a fail-fast for a caller that
/// believes it is renaming one note and would otherwise relocate a folder of fifty.
/// </remarks>
/// <param name="Path">The vault-relative path of the note or folder to relocate.</param>
/// <param name="Destination">The vault-relative path to move it to, of the same kind.</param>
/// <param name="Vault">The vault to move within, or null for the configured default.</param>
/// <param name="Kind">The kind the caller asserts the path names, or null to assert none.</param>
/// <param name="Apply">Whether to perform the move. False previews it.</param>
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
    /// <summary>Checks that both paths are named and that any asserted kind is recognized.</summary>
    /// <returns>The first fault found, or null.</returns>
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
