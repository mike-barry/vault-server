using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Verifies <see cref="FrontmatterTree"/>: how a block parses, what shape a key holds, which
/// spelling a write lands on, and the tolerant reads that accept a block the write path would
/// refuse. No case here needs a vault or a declaration.
/// </summary>
public sealed class FrontmatterTreeTests
{
    private static FrontmatterTree Parsed(string innerText)
    {
        Assert.Equal(TreeParse.Parsed, FrontmatterTree.TryParse(innerText, out var tree));
        return tree;
    }

    // ---- Parsing ----

    [Fact]
    public void AMappingParses()
    {
        var tree = Parsed("title: Widget\ncount: 3");

        Assert.Equal(["title", "count"], tree.Keys);
    }

    [Fact]
    public void AnEmptyBlockParsesToATreeWithNoKeys()
    {
        var tree = Parsed(string.Empty);

        Assert.True(tree.IsEmpty);
        Assert.Empty(tree.Keys);
    }

    [Fact]
    public void TextThatIsNotYamlIsUnparsable()
    {
        Assert.Equal(TreeParse.Unparsable, FrontmatterTree.TryParse("a: b\n  c: d\n\t- x", out var tree));
        Assert.True(tree.IsEmpty);
    }

    [Fact]
    public void ADuplicateKeyIsUnparsable()
    {
        Assert.Equal(TreeParse.Unparsable, FrontmatterTree.TryParse("a: 1\na: 2", out _));
    }

    [Fact]
    public void SomethingOtherThanAMappingIsToldApartFromTextThatWillNotParse()
    {
        // The write path refuses the two for distinct reasons, so the parse has to distinguish them.
        Assert.Equal(TreeParse.NotAMapping, FrontmatterTree.TryParse("just a string", out var scalar));
        Assert.True(scalar.IsEmpty);
        Assert.Equal(TreeParse.NotAMapping, FrontmatterTree.TryParse("- a\n- b", out _));
    }

    /// <summary>
    /// Text that will not parse reports where it failed, and text that parsed to the wrong kind of
    /// node reports nothing, because it failed at no particular point in itself.
    /// </summary>
    [Fact]
    public void TheParsePositionIsCarriedForTextThatWillNotParseAndForNothingElse()
    {
        // The mistake is the colon on the third line, at its seventh character.
        Assert.Equal(
            TreeParse.Unparsable,
            FrontmatterTree.TryParse("doc.title: Widget\ndoc.type: note\n  oops: 1\n", out _, out var failedAt));
        Assert.Equal(new ParsePosition(3, 7), failedAt);

        Assert.Equal(TreeParse.NotAMapping, FrontmatterTree.TryParse("- a\n- b", out _, out var noPosition));
        Assert.Null(noPosition);
    }

    [Fact]
    public void ADateShapedScalarStaysAString()
    {
        // The node layer carries a scalar's text, not a typed value, so nothing is coerced.
        var tree = Parsed("created: 2024-01-31\nstamped: 2024-01-31T09:30:00Z\ncount: 42\nflagged: true");

        Assert.Equal("2024-01-31", tree.Scalar("created"));
        Assert.Equal("2024-01-31T09:30:00Z", tree.Scalar("stamped"));
        Assert.Equal("42", tree.Scalar("count"));
        Assert.Equal("true", tree.Scalar("flagged"));
    }

    // ---- Tolerant reads ----

    [Fact]
    public void AMalformedBlockReadsAsNoBlock()
    {
        // Reads run over every file in the vault and report on notes rather than gating them, so a
        // malformed block is empty rather than an error.
        var tree = FrontmatterTree.Read("---\na: b\n  c: d\n\t- x\n---\nBody\n");

        Assert.True(tree.IsEmpty);
        Assert.Equal(FrontmatterShape.Absent, tree.ShapeOf("a"));
        Assert.Empty(tree.Sequence("a"));
        Assert.Empty(tree.Entries("a"));
    }

    [Fact]
    public void ANoteWithNoBlockReadsAsAnEmptyTree()
    {
        Assert.True(FrontmatterTree.Read("Just prose.\n").IsEmpty);
        Assert.True(FrontmatterTree.Read("---\nunclosed: true\n").IsEmpty);
    }

    [Fact]
    public void AWholeNoteReadsToItsBlock()
    {
        var tree = FrontmatterTree.Read("---\ntitle: Widget\n---\nBody\n");

        Assert.Equal("Widget", tree.Scalar("title"));
    }

    [Fact]
    public void CollectionAccessorsAnswerEmptyRatherThanAbsent()
    {
        var tree = Parsed("title: Widget");

        Assert.Empty(tree.Sequence("missing"));
        Assert.Empty(tree.Entries("missing"));
        Assert.Empty(tree.Sequence("title"));
        Assert.Empty(tree.Entries("title"));
        Assert.Empty(FrontmatterTree.Empty().Keys);
    }

    // ---- Shape ----

    [Fact]
    public void AnAbsentKeyHasNoShape()
    {
        Assert.Equal(FrontmatterShape.Absent, Parsed("title: Widget").ShapeOf("missing"));
    }

    [Fact]
    public void AValueBearingKeyIsScalar()
    {
        Assert.Equal(FrontmatterShape.Scalar, Parsed("title: Widget").ShapeOf("title"));
    }

    [Fact]
    public void AValuelessKeyIsScalar()
    {
        // There is no list under it to lose; the validator reports it separately as an empty block.
        var tree = Parsed("empty:\nother: x");

        Assert.Equal(FrontmatterShape.Scalar, tree.ShapeOf("empty"));
        Assert.Equal(string.Empty, tree.Scalar("empty"));
    }

    [Fact]
    public void ASequenceIsBlock()
    {
        Assert.Equal(FrontmatterShape.Block, Parsed("tags:\n  - a\n  - b").ShapeOf("tags"));
        Assert.Equal(FrontmatterShape.Block, Parsed("tags: [a, b]").ShapeOf("tags"));
    }

    [Fact]
    public void ANestedMappingIsBlock()
    {
        Assert.Equal(FrontmatterShape.Block, Parsed("agent:\n  role: writer").ShapeOf("agent"));
    }

    [Fact]
    public void ReadingAValueCannotAnswerShape()
    {
        // A block-valued field, an absent field, and a valueless field all read back the same.
        var tree = Parsed("blocked:\n  - a\nvalueless:\n");

        Assert.Null(tree.Scalar("blocked"));
        Assert.Null(tree.Scalar("absent"));
        Assert.Equal(FrontmatterShape.Block, tree.ShapeOf("blocked"));
        Assert.Equal(FrontmatterShape.Absent, tree.ShapeOf("absent"));
    }

    // ---- Canonical keys ----

    [Fact]
    public void MembershipIsCaseInsensitive()
    {
        var tree = Parsed("Title: Widget");

        Assert.True(tree.Contains("title"));
        Assert.Equal("Widget", tree.Scalar("TITLE"));
        Assert.Equal(FrontmatterShape.Scalar, tree.ShapeOf("tItLe"));
    }

    [Fact]
    public void APresentKeyCanonicalizesToTheTreesOwnSpelling()
    {
        Assert.Equal("Title", Parsed("Title: Widget").Canonicalize("title", "TITLE"));
    }

    [Fact]
    public void AnAbsentKeyCanonicalizesToTheDeclaredSpelling()
    {
        Assert.Equal("Declared", Parsed("other: 1").Canonicalize("declared", "Declared"));
    }

    [Fact]
    public void AnAbsentKeyWithNoDeclaredSpellingCanonicalizesToItself()
    {
        Assert.Equal("whatever", Parsed("other: 1").Canonicalize("whatever"));
    }

    [Fact]
    public void ADifferentlyCasedWriteLandsOnTheKeyAlreadyThere()
    {
        // Without canonicalization, a differently-cased spelling passes the membership test and
        // then lands as a second key beside the first.
        var tree = Parsed("Title: Widget\ncount: 3");
        tree.Set("TITLE", "Gizmo");

        Assert.Equal(["Title", "count"], tree.Keys);
        Assert.Equal("Gizmo", tree.Scalar("title"));
    }

    [Fact]
    public void ANewKeyTakesTheDeclaredSpellingAndAppends()
    {
        var tree = Parsed("count: 3");
        tree.Set("title", "Widget", "Title");

        Assert.Equal(["count", "Title"], tree.Keys);
    }

    [Fact]
    public void RemovalIsCaseInsensitiveAndAnswersWhetherTheKeyWasThere()
    {
        var tree = Parsed("Title: Widget\ncount: 3");

        Assert.True(tree.Remove("TITLE"));
        Assert.False(tree.Remove("title"));
        Assert.Equal(["count"], tree.Keys);
    }

    [Fact]
    public void RemovingEveryKeyEmptiesTheTree()
    {
        var tree = Parsed("only: 1");
        tree.Remove("only");

        Assert.True(tree.IsEmpty);
    }

    // ---- Entries ----

    [Fact]
    public void ASequenceOfScalarsReadsAsItsItems()
    {
        Assert.Equal(["a", "b"], Parsed("tags:\n  - a\n  - b").Sequence("tags"));
    }

    [Fact]
    public void AScalarEntryAndAMappingEntryAreToldApart()
    {
        var entries = Parsed("agent.instructions:\n  - Write clearly.\n  - use_skill: research\n    when: always")
            .Entries("agent.instructions");

        Assert.Collection(
            entries,
            first =>
            {
                Assert.True(first.IsScalar);
                Assert.Equal("Write clearly.", first.Value);
                Assert.Empty(first.Fields);
            },
            second =>
            {
                Assert.False(second.IsScalar);
                Assert.Null(second.Value);
                Assert.Equal(["use_skill", "when"], second.Fields);
                Assert.Equal("research", second.Field("use_skill"));
                Assert.Equal("research", second.Field("USE_SKILL"));
                Assert.Null(second.Field("absent"));
            });
    }
}
