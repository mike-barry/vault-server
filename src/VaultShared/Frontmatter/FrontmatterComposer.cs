using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace VaultShared.Frontmatter;

/// <summary>
/// Composes a note's content from a tree and a body, and it is the only thing in the build that
/// writes a fence. One site, so a removal that empties the block drops the fence with it and no note
/// is ever left holding an empty fence pair.
///
/// The emitter configuration lives here for the same reason. It was established by measurement over
/// the real corpus (<c>Spec-Vault-Access-Design.md:Frontmatter Emitter Settings</c>) and carries four
/// parts, each of which changes what lands on disk; spread across call sites they would drift.
///
/// Composing guarantees meaning, not bytes: what comes back carries every key in its original order
/// with every value's content intact, and may differ from the block that was parsed in quoting, and
/// in blank lines, comments, and a folded scalar's line wrapping, which are artifacts of text with no
/// place in a tree. Keeping those out of a note that was not edited is not this type's job -
/// reserialization is confined to writes that ran a frontmatter operation, and that confinement is
/// the write path's decision. A body-only write reaches for <see cref="NoteSplit.BodyOffset"/>
/// instead and never arrives here.
/// </summary>
public static class FrontmatterComposer
{
    /// <summary>
    /// A note's content: the block, fenced, followed by <paramref name="body"/> verbatim. An empty
    /// tree emits no fence and the content is the body alone.
    /// </summary>
    public static string Compose(FrontmatterTree tree, string body) =>
        tree.IsEmpty ? body : $"---\n{Emit(tree)}---\n{body}";

    /// <summary>
    /// A block's inner text, unfenced and terminated, or the empty string for an empty tree. This is
    /// also what a diff over values compares: emitting both sides through these settings is what
    /// keeps a reserialization that changed only quoting from reading as a change.
    /// </summary>
    public static string Emit(FrontmatterTree tree)
    {
        if (tree.IsEmpty)
            return string.Empty;

        var stream = new YamlStream(new YamlDocument(tree.Root));

        var text = new StringBuilder();
        var settings = new EmitterSettings().WithNewLine("\n").WithIndentedSequences();
        using (var writer = new StringWriter(text) { NewLine = "\n" })
            stream.Save(new ImplicitDocEnd(new Emitter(writer, settings)), assignAnchors: false);

        // The closing fence goes on its own line, so the block's text ends with a terminator whatever
        // the last value's style was.
        if (text.Length > 0 && text[^1] != '\n')
            text.Append('\n');

        return text.ToString();
    }
}

/// <summary>
/// Rewrites both document events to their implicit forms. Without it YamlDotNet emits its own
/// <c>---</c> opener and <c>...</c> terminator inside the block, where the fence lines already
/// provide them.
///
/// Internal because it is an implementation detail of the one composer: a public decorator would put
/// YamlDotNet's emitter types on this assembly's surface, and no caller outside holds a node.
/// </summary>
internal sealed class ImplicitDocEnd(IEmitter inner) : IEmitter
{
    public void Emit(ParsingEvent e) => inner.Emit(e switch
    {
        DocumentStart => new DocumentStart(null, null, isImplicit: true),
        DocumentEnd => new DocumentEnd(isImplicit: true),
        _ => e,
    });
}
