using System.ComponentModel;
using VaultShared.Access;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>Selects which part of a note a read returns. These three are the whole set. There is no
/// line range and no section addressing.</summary>
public enum ReadPart
{
    /// <summary>The note's whole text, frontmatter block and body together.</summary>
    All,

    /// <summary>The block's inner text, without its fences.</summary>
    Frontmatter,

    /// <summary>Everything after the closing fence.</summary>
    Body,
}

/// <summary>
/// Represents <see cref="ReadTool"/>'s arguments: the binding target and the source of its advertised
/// schema.
/// </summary>
/// <remarks>
/// A member appears in the schema's <c>required</c> list exactly when it carries no default, so every
/// optional argument declares one and follows the required members.
/// </remarks>
/// <param name="Path">The vault-relative path of the note to read.</param>
/// <param name="Vault">The vault to read in, or null for the configured default.</param>
/// <param name="Part">Which part of the note to return.</param>
/// <param name="WithContentHash">Whether to return the whole file's content hash alongside the
/// content.</param>
public sealed record ReadArguments(
    [property: Description("Vault-relative path of the note, carrying the \".md\" extension. The extension is never inferred.")]
    string Path,
    [property: Description("The vault to read in. Optional; the configured default vault is used when it is absent.")]
    string? Vault = null,
    [property: Description("Which facet to return: the whole note, the frontmatter block's inner text without its fences, or the body. Defaults to the whole note.")]
    ReadPart Part = ReadPart.All,
    [property: Description("Whether to return the whole file's content hash alongside the content, for a later write to guard against a change it never saw. Defaults to false.")]
    bool WithContentHash = false) : IVaultArguments
{
    /// <summary>Checks that a path was supplied and that the part is one this tool declares.</summary>
    /// <returns>The first fault found, or null. Both are refused here rather than by the binder, so
    /// each reaches the caller as a coded refusal naming the argument.</returns>
    /// <remarks>
    /// The path's own rules are checked where the path is resolved, not here. A path missing the
    /// markdown extension is refused as <c>invalid_path</c> carrying the spelling that would have
    /// worked, and an <see cref="ArgumentFault"/> can carry neither that code nor that payload.
    /// </remarks>
    public ArgumentFault? Validate() =>
        ArgumentRules.Missing(Path, "path")
        ?? ArgumentRules.Unrecognized(Part, "part");
}

/// <summary>
/// Represents a read that returns content together with the hash of the bytes it came from. They are
/// returned together because a hash taken by a second read would not describe this content.
/// </summary>
/// <param name="Content">The part of the note that was read.</param>
/// <param name="ContentHash">SHA-256 of the whole file's stored bytes, in lowercase hex. It covers
/// the whole file whichever part was read, so a partial read never authorizes a write.</param>
public sealed record ReadResult(string Content, string ContentHash);

/// <summary>
/// Returns one note's whole content, its frontmatter, or its body, with a whole-file content hash
/// when the caller asks for one.
/// </summary>
/// <remarks>
/// The note must exist; a miss is refused rather than returned as an absent value, which would push a
/// check into every caller. A miss is told apart from a failed read by how the access layer
/// classified the failure, never by a probe this tool runs first.
/// </remarks>
public sealed class ReadTool : ITool
{
    /// <summary>The tool's wire name.</summary>
    public const string ToolName = "read";

    /// <summary>
    /// The tool's declaration. The floor is the read class and the tool asserts nothing beyond it,
    /// because every path it touches is the one the caller named, admitted against the effective read
    /// scope before anything is read.
    /// </summary>
    public static readonly ToolDeclaration Declaration = new()
    {
        Name = ToolName,
        Description =
            "Returns one note's whole text, its frontmatter block's inner text, or its body. " +
            "The path is vault-relative and carries the \".md\" extension, which is never inferred. " +
            "Ask for a content hash when the read is going to be followed by a write.",
        Arguments = typeof(ReadArguments),
        OpClassFloor = new Floor.Class("content:read"),
    };

    public ToolResult Invoke(ToolCall call)
    {
        var arguments = call.ArgumentsAs<ReadArguments>();
        var vault = call.RequireVault(ToolName);
        var path = ToolPaths.ReadNote(vault, arguments.Path);

        var read = VaultFiles.Read(path) switch
        {
            ReadOutcome.Content stored => stored,
            ReadOutcome.NotFound => throw new ToolRefusal(
                ToolCodes.PathNotFound,
                $"No note is at \"{arguments.Path}\"."),
            ReadOutcome.Failed failed => throw new ToolRefusal(
                ToolCodes.ReadFailed,
                $"\"{arguments.Path}\" is there and could not be read: {failed.Reason}",
                new { path = failed.Path }),
            var other => throw new InvalidOperationException($"Unhandled read outcome {other.GetType().Name}."),
        };

        var content = Facet(read.Text, arguments.Part);
        return arguments.WithContentHash
            ? new ToolResult.Data(new ReadResult(content, read.ContentHash))
            : new ToolResult.Content(content);
    }

    /// <summary>Returns the requested part of a note's text.</summary>
    /// <param name="text">The note's whole text.</param>
    /// <param name="part">Which part to return.</param>
    /// <returns>The requested part. A note with no frontmatter block returns empty text for
    /// <see cref="ReadPart.Frontmatter"/>, because the split treats an absent block as a well-formed
    /// value rather than a case each caller has to handle.</returns>
    /// <remarks>
    /// This uses the same split every consumer of a note's two halves uses, so what a read returns is
    /// byte-identical to what a write echoes back.
    /// </remarks>
    private static string Facet(string text, ReadPart part) => part switch
    {
        ReadPart.All => text,
        ReadPart.Frontmatter => NoteSplit.Of(text).InnerText,
        ReadPart.Body => NoteSplit.Of(text).Body,
        _ => throw new InvalidOperationException($"Unhandled read part {part}."),
    };
}
