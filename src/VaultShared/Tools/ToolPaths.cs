using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>
/// Turns a path the caller named into a path the access layer has admitted, for the tools that take
/// one.
/// </summary>
/// <remarks>
/// A path the caller wrote explicitly is refused distinguishably when the layer will not admit it -
/// malformed, under a dot-directory, or outside the grant - rather than being reported as a miss.
/// Enumeration and search do the opposite and filter their results silently, which is why these
/// helpers sit beside the tools rather than inside a search.
/// </remarks>
public static class ToolPaths
{
    /// <summary>The extension every note operand carries. It is never inferred.</summary>
    public const string NoteExtension = ".md";

    /// <summary>
    /// Admits a note operand for reading. The extension rule is checked before resolution because it
    /// is decidable from the caller's own text: a caller who wrote a folder-shaped name gets the
    /// spelling that would have worked, at the cost of one retry, rather than a scope answer about a
    /// path they never meant to name.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="path">The vault-relative path the caller named.</param>
    /// <returns>The admitted read path.</returns>
    /// <exception cref="ToolRefusal">The path is not a note, or the layer refuses it.</exception>
    public static ReadPath ReadNote(VaultAccess vault, string path)
    {
        RequireNoteExtension(path);
        var resolution = vault.ForRead(path);
        return resolution is PathResolution<ReadPath>.Admitted admitted
            ? admitted.Path
            : throw ToolRefusalEnvelope.Translate(resolution);
    }

    /// <summary>
    /// Admits a note operand for writing, against the grant's own roots and nothing wider. A note
    /// under an always-readable root is refused here and still readable through
    /// <see cref="ReadNote"/>; that asymmetry is what the two path types carry.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="path">The vault-relative path the caller named.</param>
    /// <returns>The admitted write path. A mutation that reads the note first should take its read
    /// path from <see cref="WritePath.AsRead"/> rather than resolving the same text twice; anything
    /// writable is readable.</returns>
    /// <exception cref="ToolRefusal">The path is not a note, or the layer refuses it.</exception>
    public static WritePath WriteNote(VaultAccess vault, string path)
    {
        RequireNoteExtension(path);
        var resolution = vault.ForWrite(path);
        return resolution is PathResolution<WritePath>.Admitted admitted
            ? admitted.Path
            : throw ToolRefusalEnvelope.Translate(resolution);
    }

    /// <summary>
    /// Admits a folder operand for writing. Nothing beyond the operand rule is checked here: a
    /// folder already on disk is not held to the name grammar, which applies only to a segment a
    /// call brings into existence.
    /// </summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="path">The vault-relative path the caller named.</param>
    /// <returns>The admitted write path.</returns>
    /// <exception cref="ToolRefusal">The path names a note, or the layer refuses it.</exception>
    public static WritePath WriteFolder(VaultAccess vault, string path)
    {
        RequireNoNoteExtension(path);
        var resolution = vault.ForWrite(path);
        return resolution is PathResolution<WritePath>.Admitted admitted
            ? admitted.Path
            : throw ToolRefusalEnvelope.Translate(resolution);
    }

    /// <summary>Admits an operand for writing, as either a note or a folder.</summary>
    /// <param name="vault">The per-call access object.</param>
    /// <param name="path">The vault-relative path the caller named.</param>
    /// <param name="kind">The kind the call is acting on.</param>
    /// <returns>The admitted write path.</returns>
    /// <exception cref="ToolRefusal">The path is not that kind, or the layer refuses it.</exception>
    public static WritePath Write(VaultAccess vault, string path, OperandKind kind) =>
        kind == OperandKind.Note ? WriteNote(vault, path) : WriteFolder(vault, path);

    /// <summary>
    /// Returns the kind an operand's own spelling names, and refuses where the caller's assertion
    /// disagrees with it.
    /// </summary>
    /// <param name="path">The vault-relative path the caller named.</param>
    /// <param name="asserted">The kind the caller asserted, or null where it asserted none.</param>
    /// <returns>The kind the spelling names.</returns>
    /// <exception cref="ToolRefusal">The assertion disagrees with the spelling.</exception>
    /// <remarks>
    /// The extension tells the two kinds apart - a note ends in <c>.md</c> and a folder does not - so
    /// what the caller wrote already says which one it named. The extension is still never added: an
    /// extensionless path is not read as a note, and a call asserting a note over one is refused,
    /// naming the spelling that would have worked.
    ///
    /// A kind assertion therefore only checks the caller's intent and resolves nothing. Its value is
    /// that a caller who believes it is acting on one note fails before acting on a folder of fifty.
    /// </remarks>
    public static OperandKind KindOf(string path, OperandKind? asserted = null)
    {
        var spelled = path.EndsWith(NoteExtension, StringComparison.OrdinalIgnoreCase)
            ? OperandKind.Note
            : OperandKind.Folder;

        if (asserted is null || asserted == spelled)
            return spelled;

        // A note asserted over a folder-shaped path is refused as a bad path, which carries the
        // spelling that would have worked. The reverse has no such remedy and is refused as a wrong
        // kind.
        if (asserted == OperandKind.Note)
            RequireNoteExtension(path);

        throw WrongKind(path);
    }

    /// <summary>
    /// Requires that a folder operand carry no markdown extension. Checked before resolution for the
    /// same reason the note rule is: it is decidable from the caller's own text alone.
    /// </summary>
    /// <param name="path">The vault-relative path the caller named.</param>
    /// <exception cref="ToolRefusal">The path names a note.</exception>
    public static void RequireNoNoteExtension(string path)
    {
        if (path.EndsWith(NoteExtension, StringComparison.OrdinalIgnoreCase))
            throw WrongKind(path);
    }

    private static ToolRefusal WrongKind(string path) =>
        new(
            ToolCodes.WrongKind,
            $"\"{path}\" carries the \"{NoteExtension}\" extension, so it names a note rather than a folder. A folder operand carries no extension.");

    /// <summary>
    /// Requires that a note operand carry the markdown extension. The extension is never added on
    /// the caller's behalf.
    /// </summary>
    /// <param name="path">The vault-relative path the caller named.</param>
    /// <exception cref="ToolRefusal">The path does not name a note.</exception>
    /// <remarks>
    /// Folders share the operand space, so a name without an extension is genuinely ambiguous
    /// between a note and a folder, and appending the extension would guess. An extensionless path
    /// is refused with the spelling that would have worked in the payload. A path carrying some
    /// other extension is refused with no suggestion, because appending <c>.md</c> to it would name
    /// a third thing the caller did not mean.
    ///
    /// The comparison ignores case, like every read of a name already on disk. Enumeration can hand
    /// back a note spelled <c>.MD</c>, and any path this surface produces has to be one it accepts
    /// back.
    /// </remarks>
    public static void RequireNoteExtension(string path)
    {
        if (path.EndsWith(NoteExtension, StringComparison.OrdinalIgnoreCase))
            return;

        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var extensionless = name.LastIndexOf('.') <= 0;

        throw new ToolRefusal(
            ToolCodes.InvalidPath,
            extensionless
                ? $"\"{path}\" names no note - a note operand carries the \"{NoteExtension}\" extension, which is never inferred because a folder could be spelled the same way."
                : $"\"{path}\" names no note - every operand here is a markdown note or a folder.",
            extensionless ? new { suggested = path + NoteExtension } : null);
    }
}
