using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace VaultShared.Frontmatter;

/// <summary>
/// Composes a note's content from a frontmatter tree and a body. This is the only type that writes
/// a fence, so an empty tree emits no fence and no note is left with an empty fence pair.
/// </summary>
/// <remarks>
/// Do not change the four emitter settings. They were chosen by measuring round-trip fidelity
/// against the real vault's existing notes: 315 of 320 blocks round-trip byte-identically under
/// these settings, and the five that do not are folded scalars under a dot directory the tooling
/// cannot reach. That measurement cannot be re-run from this repository, and changing any setting
/// reformats existing frontmatter on its next write.
///
/// Composing preserves meaning, not bytes. The result carries every key in its original order with
/// every value's content intact, but may differ from the parsed block in quoting, blank lines,
/// comments, and a folded scalar's line wrapping, none of which a tree can hold. Keeping those out
/// of a note that was not edited is the write path's job: it composes only when a frontmatter
/// operation ran, and a body-only write splices at <see cref="NoteSplit.BodyOffset"/> without
/// coming here.
/// </remarks>
public static class FrontmatterComposer
{
    /// <summary>
    /// Returns a note's content: the frontmatter block, fenced, followed by
    /// <paramref name="body"/> verbatim. An empty tree emits no fence, and the content is the body
    /// alone.
    /// </summary>
    /// <param name="tree">The frontmatter to emit.</param>
    /// <param name="body">The note's body, written through unchanged.</param>
    /// <returns>The composed note content.</returns>
    public static string Compose(FrontmatterTree tree, string body) =>
        tree.IsEmpty ? body : $"---\n{Emit(tree)}---\n{body}";

    /// <summary>
    /// Returns a block's inner text, without fences and ending in a line terminator, or the empty
    /// string for an empty tree. A diff over values emits both sides through this method, so a
    /// reserialization that changed only quoting does not read as a change.
    /// </summary>
    /// <param name="tree">The frontmatter to emit.</param>
    /// <returns>The block's inner text.</returns>
    public static string Emit(FrontmatterTree tree)
    {
        if (tree.IsEmpty)
            return string.Empty;

        var stream = new YamlStream(new YamlDocument(tree.Root));

        var text = new StringBuilder();
        var settings = new EmitterSettings().WithNewLine("\n").WithIndentedSequences();
        using (var writer = new StringWriter(text) { NewLine = "\n" })
            stream.Save(new ImplicitDocEnd(new Emitter(writer, settings)), assignAnchors: false);

        // The closing fence goes on its own line, so the block's text must end with a terminator
        // whatever style the last value used.
        if (text.Length > 0 && text[^1] != '\n')
            text.Append('\n');

        return text.ToString();
    }
}

/// <summary>
/// Wraps an emitter and rewrites both document events to their implicit forms. Without this,
/// YamlDotNet emits its own <c>---</c> opener and <c>...</c> terminator inside the block, where the
/// fence lines already provide them.
/// </summary>
/// <param name="inner">The emitter the rewritten events are passed to.</param>
internal sealed class ImplicitDocEnd(IEmitter inner) : IEmitter
{
    /// <summary>Emits <paramref name="e"/>, replacing a document start or end with its implicit form.</summary>
    /// <param name="e">The event to emit.</param>
    public void Emit(ParsingEvent e) => inner.Emit(e switch
    {
        DocumentStart => new DocumentStart(null, null, isImplicit: true),
        DocumentEnd => new DocumentEnd(isImplicit: true),
        _ => e,
    });
}
