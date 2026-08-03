using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>
/// Turning a caller-named operand into an admitted path, for the tools that take one.
///
/// <b>Named is loud.</b> A path a caller wrote is refused where the layer will not admit it -
/// malformed, under a dot-directory, or outside the grant - distinguishably from a miss. That is the
/// counterpart of enumeration and search, whose results the layer filters silently, and it is why
/// this sits beside the tools rather than inside a search.
/// </summary>
public static class ToolPaths
{
    /// <summary>The extension every note operand carries. Never inferred.</summary>
    public const string NoteExtension = ".md";

    /// <summary>
    /// A note operand, admitted for reading. The extension rule is checked first because it is a
    /// statement about the caller's own text alone: a caller who wrote a folder-shaped name gets the
    /// spelling that would have worked, which costs one retry, rather than a scope answer about a
    /// path they never meant to name.
    /// </summary>
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
    /// A note operand a mutation names, admitted for writing against the grant's own roots and
    /// nothing wider - so a note under an always-readable root is refused here and readable
    /// elsewhere, which is the asymmetry the two path types carry.
    ///
    /// A mutation that reads the note first takes its read proof from
    /// <see cref="WritePath.AsRead"/> rather than resolving the same text twice: anything writable
    /// is readable, and a second resolution would be a second answer to one question.
    /// </summary>
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
    /// The never-inferred extension, as a refusal.
    ///
    /// Folders share the operand space, so a name without an extension is genuinely ambiguous
    /// between a note and a folder and resolving it would reintroduce the resolution ladder the
    /// operand vocabulary exists to remove. An extensionless path is therefore refused
    /// <em>naming the spelling that would have worked</em>; a path carrying some other extension is
    /// refused with no suggestion, because appending <c>.md</c> to it would name a third thing the
    /// caller did not mean.
    ///
    /// Matched case-insensitively, like every other read of a name already on disk: enumeration
    /// hands back a note spelled <c>.MD</c>, and a path this surface produces has to be one it takes
    /// back.
    /// </summary>
    /// <exception cref="ToolRefusal">The path does not name a note.</exception>
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
