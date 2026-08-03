using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// The one thing in the build that writes a fence, and the one site the emitter settings live at.
/// What is asserted is what the settings were chosen for - key order, indented sequences, LF alone,
/// no document markers of YamlDotNet's own - and what composing does and does not guarantee.
/// </summary>
public sealed class FrontmatterComposerTests
{
    private static FrontmatterTree Parsed(string innerText)
    {
        Assert.Equal(TreeParse.Parsed, FrontmatterTree.TryParse(innerText, out var tree));
        return tree;
    }

    private static FrontmatterTree Of(string content) => Parsed(NoteSplit.Of(content).InnerText);

    // ---- The fence ----

    [Fact]
    public void ComposingWrapsTheBlockAndLeavesTheBodyVerbatim()
    {
        Assert.Equal(
            "---\ntitle: Widget\n---\nBody text.\n\nMore.\n",
            FrontmatterComposer.Compose(Parsed("title: Widget"), "Body text.\n\nMore.\n"));
    }

    [Fact]
    public void AnEmptyTreeEmitsNoFence()
    {
        // A removal that empties the block drops the fence with it, and no note is left holding an
        // empty fence pair.
        Assert.Equal("Body text.\n", FrontmatterComposer.Compose(FrontmatterTree.Empty(), "Body text.\n"));
        Assert.Equal(string.Empty, FrontmatterComposer.Emit(FrontmatterTree.Empty()));
    }

    [Fact]
    public void ATreeEmptiedByRemovalEmitsNoFence()
    {
        var tree = Of("---\nonly: 1\n---\nBody.\n");
        tree.Remove("only");

        Assert.Equal("Body.\n", FrontmatterComposer.Compose(tree, "Body.\n"));
    }

    [Fact]
    public void AnEmptyBodyComposesToABlockAlone()
    {
        Assert.Equal("---\ntitle: Widget\n---\n", FrontmatterComposer.Compose(Parsed("title: Widget"), string.Empty));
    }

    [Fact]
    public void ComposedContentSplitsBackToWhatItWasComposedFrom()
    {
        var composed = FrontmatterComposer.Compose(Parsed("title: Widget\ntags:\n  - a"), "Body.\n");
        var split = NoteSplit.Of(composed);

        Assert.True(split.HasBlock);
        Assert.Equal("title: Widget\ntags:\n  - a", split.InnerText);
        Assert.Equal("Body.\n", split.Body);
    }

    // ---- The settings ----

    [Fact]
    public void KeyOrderSurvivesParseAndEmit()
    {
        // Without an order-preserving structure the first operation against a note reshuffles every
        // key in it.
        Assert.Equal(
            "zeta: 1\nalpha: 2\nmiddle: 3\n",
            FrontmatterComposer.Emit(Parsed("zeta: 1\nalpha: 2\nmiddle: 3")));
    }

    [Fact]
    public void AnEditedKeyKeepsItsPositionAndANewOneAppends()
    {
        var tree = Parsed("zeta: 1\nalpha: 2");
        tree.Set("alpha", "changed");
        tree.Set("added", "3");

        Assert.Equal("zeta: 1\nalpha: changed\nadded: 3\n", FrontmatterComposer.Emit(tree));
    }

    [Fact]
    public void SequenceItemsIndentBeneathTheirKey()
    {
        // Both forms are valid and the parse cannot tell them apart, so the emitter's default would
        // reformat every list in the vault on its first write.
        Assert.Equal("tags:\n  - a\n  - b\n", FrontmatterComposer.Emit(Parsed("tags:\n- a\n- b")));
    }

    [Fact]
    public void OutputCarriesLineFeedsAlone()
    {
        var composed = FrontmatterComposer.Compose(Parsed("title: Widget\ntags:\n  - a\n  - b"), "Body.\n");

        Assert.DoesNotContain('\r', composed);
    }

    [Fact]
    public void NoDocumentMarkersAreEmittedInsideTheBlock()
    {
        // The fence lines already provide the document's start and end; YamlDotNet's own `---` and
        // `...` inside the block would be a second pair.
        var emitted = FrontmatterComposer.Emit(Parsed("title: Widget"));

        Assert.Equal("title: Widget\n", emitted);
    }

    [Fact]
    public void NoAnchorsAreIntroduced()
    {
        var emitted = FrontmatterComposer.Emit(Parsed("a:\n  k: v\nb:\n  k: v"));

        Assert.DoesNotContain('&', emitted);
        Assert.DoesNotContain('*', emitted);
    }

    [Fact]
    public void TheBlockAlwaysEndsWithATerminatorSoTheFenceGetsItsOwnLine()
    {
        var composed = FrontmatterComposer.Compose(Parsed("desc: |\n  line one\n  line two"), "Body.\n");

        Assert.Equal("---\ndesc: |\n  line one\n  line two\n---\nBody.\n", composed);
    }

    // ---- What composing preserves, and what it does not ----

    [Fact]
    public void ValuesAndTheirQuotingStyleSurvive()
    {
        Assert.Equal(
            "a: \"quoted\"\nb: 'single'\nc: plain\nd: \"has: colon\"\n",
            FrontmatterComposer.Emit(Parsed("a: \"quoted\"\nb: 'single'\nc: plain\nd: \"has: colon\"")));
    }

    [Fact]
    public void ADateShapedScalarComesBackAsTheTextThatWasWritten()
    {
        Assert.Equal(
            "created: 2024-01-31\nstamped: 2024-01-31T09:30:00Z\n",
            FrontmatterComposer.Emit(Parsed("created: 2024-01-31\nstamped: 2024-01-31T09:30:00Z")));
    }

    [Fact]
    public void BlankLinesAndCommentsAreDroppedOnReserialization()
    {
        // Artifacts of text with no place in the tree. Keeping them out of a note nobody edited is
        // the write path's confinement of reserialization, not something composing can do.
        Assert.Equal(
            "a: 1\nb: 2\n",
            FrontmatterComposer.Emit(Parsed("# leading\na: 1\n\n# between\nb: 2")));
    }

    [Fact]
    public void ALiteralBlockScalarKeepsItsChompingIndicator()
    {
        // The split hands over the block's text without the final inner line's terminator, which is
        // where a block scalar reads its trailing newline from. Both indicators have to survive, or
        // the first operation on such a note rewrites one it never touched.
        const string kept = "---\ndesc: |\n  line one\n  line two\n---\nBody.\n";
        const string stripped = "---\ndesc: |-\n  line one\n  line two\n---\nBody.\n";

        Assert.Equal(kept, FrontmatterComposer.Compose(Of(kept), NoteSplit.Of(kept).Body));
        Assert.Equal(stripped, FrontmatterComposer.Compose(Of(stripped), NoteSplit.Of(stripped).Body));
        Assert.Equal("line one\nline two\n", Of(kept).Scalar("desc"));
        Assert.Equal("line one\nline two", Of(stripped).Scalar("desc"));
    }

    [Fact]
    public void AFoldedScalarsLineWrappingReflows()
    {
        var emitted = FrontmatterComposer.Emit(Parsed("note: >\n  one\n  two\n"));

        Assert.Equal("note: >\n  one two\n", emitted);
    }

    [Fact]
    public void ComposingAnUntouchedTreeReproducesABlockThatWasAlreadyCanonical()
    {
        // Composing guarantees meaning, not bytes. What it does guarantee is that a block already in
        // the emitter's own shape comes back byte-identical, so a write that changed nothing about a
        // canonical block moves no byte.
        const string content = "---\ntitle: Widget\ntags:\n  - a\n  - b\ncreated: 2024-01-31\n---\nBody text.\n";
        var split = NoteSplit.Of(content);

        Assert.Equal(content, FrontmatterComposer.Compose(Parsed(split.InnerText), split.Body));
    }

    [Fact]
    public void AValuelessKeySurvivesComposing()
    {
        // The emitter writes the space after the colon whether or not a value follows it.
        Assert.Equal("empty: \nother: x\n", FrontmatterComposer.Emit(Parsed("empty:\nother: x")));
    }

    [Fact]
    public void ACallerValueCannotBecomeStructure()
    {
        // A value that enters a tree and leaves through an emitter is escaped, never spliced.
        var tree = Parsed("title: Widget");
        tree.Set("title", "Gizmo\n---\ninjected: true");

        var composed = FrontmatterComposer.Compose(tree, "Body.\n");
        var reread = FrontmatterTree.Read(composed);

        Assert.Equal("Gizmo\n---\ninjected: true", reread.Scalar("title"));
        Assert.Equal(FrontmatterShape.Absent, reread.ShapeOf("injected"));
        Assert.Equal("Body.\n", NoteSplit.Of(composed).Body);
    }
}
