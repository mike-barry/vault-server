using System.ComponentModel;
using VaultShared.Access;
using VaultShared.Frontmatter;

namespace VaultShared.Tools;

/// <summary>Which facet of a note a read returns. These three are the whole set: there is no line
/// range and no section addressing.</summary>
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
/// <see cref="ReadTool"/>'s arguments: the binding target and the source of its advertised schema.
///
/// <b>Requiredness in the advertised schema is a member carrying no default</b>, so every optional
/// argument declares one and follows the required ones. <see cref="Path"/> is the operand and is the
/// only required member; <see cref="Vault"/> selects rather than restricts and resolves to the
/// configured default when it is absent.
/// </summary>
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
    /// <summary>
    /// An omitted operand and an unrecognized enumerated value, both refused here rather than
    /// through the binder so each reaches a caller as a coded refusal naming the argument.
    ///
    /// The path's own rules are not here: missing the markdown extension is <c>invalid_path</c>
    /// carrying the spelling that would have worked, and an argument fault has no code or payload to
    /// carry either, so it is raised where the path is resolved.
    /// </summary>
    public ArgumentFault? Validate() =>
        ArgumentRules.Missing(Path, "path")
        ?? ArgumentRules.Unrecognized(Part, "part");
}

/// <summary>
/// The one content-tier result that carries structure: content and the hash describing the bytes it
/// came from, returned together because a hash from a second read would not describe them.
/// </summary>
/// <param name="ContentHash">SHA-256 of the whole file's stored bytes, lowercase hex - whole-file
/// whichever part was read, so nothing authorizes a write from a partial read.</param>
public sealed record ReadResult(string Content, string ContentHash);

/// <summary>
/// Returns one note's whole content, its frontmatter, or its body, with a whole-file content hash
/// when the caller asks for one.
///
/// It operates on an existing target, so it requires exactly one and fails on a miss: an absent
/// value would push a check into every caller, where one will forget it. The miss is told from a
/// failure to read by the layer's own classification of the failure, never by a probe this tool runs
/// first.
/// </summary>
public sealed class ReadTool : ITool
{
    public const string ToolName = "read";

    /// <summary>
    /// The floor is the read class, and the tool asserts nothing beyond it: every path it touches is
    /// the one the caller named, admitted against the effective read scope before anything is read.
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

    /// <summary>
    /// The facet, through the one split every consumer of a note's two halves takes, so what this
    /// returns is byte-identical to what a write echoes back. A note with no block answers
    /// <see cref="ReadPart.Frontmatter"/> with empty text - the split treats the absent block as a
    /// well-formed value rather than a case each caller writes.
    /// </summary>
    private static string Facet(string text, ReadPart part) => part switch
    {
        ReadPart.All => text,
        ReadPart.Frontmatter => NoteSplit.Of(text).InnerText,
        ReadPart.Body => NoteSplit.Of(text).Body,
        _ => throw new InvalidOperationException($"Unhandled read part {part}."),
    };
}
