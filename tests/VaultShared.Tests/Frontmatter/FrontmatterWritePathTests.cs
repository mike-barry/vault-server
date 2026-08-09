using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Verifies <see cref="FrontmatterWrite"/> as a path: the three call shapes that run through the
/// same code, the body splice, the byte-identical no-op, the fence guard in both directions, and
/// what the outcome reports. Individual operation semantics are covered by
/// <see cref="FrontmatterOperationTests"/> and refusals by <see cref="FrontmatterRefusalTests"/>.
/// </summary>
public sealed class FrontmatterWritePathTests
{
    private const string Body = "Body text.\n";

    private static readonly StubAuthority Everything = new(Synthetic.OpenClass, Synthetic.ElevatedClass);

    private static string Note(string block) => $"---\n{block}\n---\n{Body}";

    // ---- The three call shapes ----

    [Fact]
    public void MutationByOperations_IsTheOrdinaryShape()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("doc.title: Widget"), Everything,
            operations: [new FrontmatterOperation.SetScalar("doc.type", "guide")]));

        Assert.Equal(Note("doc.title: Widget\ndoc.type: guide"), outcome.Content);
        Assert.Equal(["doc.type"], outcome.ChangedKeys);
    }

    [Fact]
    public void MutationByOperations_CarriesABodyChangeOnTheSameCall()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("doc.title: Widget"), Everything,
            operations: [new FrontmatterOperation.SetScalar("doc.type", "guide")],
            newBody: "Rewritten.\n"));

        Assert.Equal("---\ndoc.title: Widget\ndoc.type: guide\n---\nRewritten.\n", outcome.Content);
        Assert.True(outcome.FrontmatterChanged);
        Assert.True(outcome.BodyChanged);
    }

    [Fact]
    public void ComposingANewNote_HasNoOriginalSoEveryKeyPresentIsChanged()
    {
        // There is no separate creation branch. The diff counts every key because there was no
        // original block to compare against.
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            string.Empty, Everything,
            suppliedBlock: "doc.title: Widget\ndoc.type: guide",
            newBody: Body));

        Assert.Equal(Note("doc.title: Widget\ndoc.type: guide"), outcome.Content);
        Assert.Equal(["doc.title", "doc.type"], outcome.ChangedKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ComposingANewNote_AcceptsTheBlockWithItsOwnFences()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            string.Empty, Everything,
            suppliedBlock: "---\ndoc.title: Widget\n---\n",
            newBody: Body));

        Assert.Equal(Note("doc.title: Widget"), outcome.Content);
    }

    [Fact]
    public void ANarrowScalarPass_RequiresNothingAndAssertsNothing()
    {
        var authority = new StubAuthority();

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("doc.title: Widget"), authority,
            operations: [new FrontmatterOperation.SetScalar("doc.type", "guide")]));

        Assert.Empty(outcome.AssertedAuthority);
        Assert.Empty(authority.Calls);
    }

    // ---- The body splice ----

    [Fact]
    public void ABodyOnlyWrite_SplicesAtTheOffsetAndDisturbsNoFrontmatterByte()
    {
        // The block is left exactly as found, quoting and all, because no operation ran.
        var content = "---\ndoc.title: 'Widget'   \n\n# a comment\n---\nOld body.\n";

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(content, Everything, newBody: "New body.\n"));

        Assert.Equal("---\ndoc.title: 'Widget'   \n\n# a comment\n---\nNew body.\n", outcome.Content);
        Assert.False(outcome.FrontmatterChanged);
        Assert.True(outcome.BodyChanged);
        Assert.Empty(outcome.ChangedKeys);
    }

    [Fact]
    public void ABodyOnlyWrite_OnANoteWithNoBlockIsTheWholeContent()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write("Old body.\n", Everything, newBody: "New body.\n"));

        Assert.Equal("New body.\n", outcome.Content);
        Assert.False(outcome.Split.HasBlock);
    }

    [Fact]
    public void ABodyOnlyWrite_DoesNotParseABlockNobodyCouldParse()
    {
        // A body write reads no key, so it does not parse the block. Refusing here would make a
        // malformed block unfixable from the body side.
        var content = "---\na: b\n  c: d\n\t- x\n---\nOld body.\n";

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(content, Everything, newBody: "New body.\n"));

        Assert.Equal("---\na: b\n  c: d\n\t- x\n---\nNew body.\n", outcome.Content);
    }

    // ---- The no-op ----

    [Fact]
    public void ANoOp_ReturnsTheCallersContentByteIdentical()
    {
        // The original string is returned, not a reserialization of it: the quoting and the comment
        // below would not survive one.
        var content = "---\ndoc.title: 'Widget'\n\n# a comment\n---\nBody text.\n";

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            content, Everything, operations: [new FrontmatterOperation.SetScalar("doc.title", "Widget")]));

        Assert.Same(content, outcome.Content);
        Assert.False(outcome.FrontmatterChanged);
        Assert.False(outcome.BodyChanged);
        Assert.Empty(outcome.AppliedOperations);
    }

    [Fact]
    public void ANoOp_WithNoOperationsAtAllIsAlsoByteIdentical()
    {
        var content = Note("doc.title: Widget");

        Assert.Same(content, FrontmatterWrite.Apply(Synthetic.Write(content, Everything)).Content);
    }

    [Fact]
    public void ABodyWriteMatchingTheBodyAlreadyThereChangesNothing()
    {
        var content = Note("doc.title: Widget");

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(content, Everything, newBody: Body));

        Assert.Same(content, outcome.Content);
        Assert.False(outcome.BodyChanged);
    }

    // ---- Preservation ----

    [Fact]
    public void AnOperationReserializesTheBlockAndLeavesEveryBodyByteAlone()
    {
        var content = "---\ndoc.title: 'Widget'\n\n# a comment\n---\n" + Body;

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            content, Everything, operations: [new FrontmatterOperation.SetScalar("doc.type", "guide")]));

        // The comment and the blank line have no place in the tree, so the first operation on such
        // a note loses them. A value's quoting style survives, because the node layer carries it.
        Assert.Equal("---\ndoc.title: 'Widget'\ndoc.type: guide\n---\n" + Body, outcome.Content);
        Assert.Equal(Body, outcome.Split.Body);
    }

    [Theory]
    [InlineData("A line with trailing spaces.   \n")]
    [InlineData("A\ttabbed\tline.\n")]
    [InlineData("Café, naïve, résumé.\n")]
    [InlineData("```\nkey: not frontmatter\n```\n")]
    [InlineData("Before the rule.\n---\nAfter the rule.\n")]
    [InlineData("key: value\n")]
    [InlineData("No final newline.")]
    public void AnOperationLeavesEveryByteOfAnAwkwardBodyAlone(string body)
    {
        // The body is concatenated after the block with no inspection, so nothing in it is
        // trimmed, terminated, expanded, or re-encoded on the way through.
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            "---\ndoc.title: Widget\n---\n" + body, Everything,
            operations: [new FrontmatterOperation.SetScalar("doc.type", "guide")]));

        Assert.Equal("---\ndoc.title: Widget\ndoc.type: guide\n---\n" + body, outcome.Content);
    }

    [Fact]
    public void AnUntouchedBooleanSpellingComesBackAsTheTextTheNoteCarried()
    {
        // A scalar is carried as its parsed text rather than as a decoded value, so a key spelled
        // `no` survives a reserialization the note's other keys caused as plain `no`, neither
        // coerced to false nor quoted.
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("doc.published: no\ndoc.draft: yes\ndoc.title: Widget"), Everything,
            operations: [new FrontmatterOperation.SetScalar("doc.type", "guide")]));

        Assert.Equal(Note("doc.published: no\ndoc.draft: yes\ndoc.title: Widget\ndoc.type: guide"), outcome.Content);
    }

    [Fact]
    public void LineTerminatorsNormalizeOnEntry()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            "---\r\ndoc.title: Widget\r\n---\r\nBody text.\r\n", Everything,
            operations: [new FrontmatterOperation.SetScalar("doc.type", "guide")]));

        Assert.Equal(Note("doc.title: Widget\ndoc.type: guide"), outcome.Content);
    }

    [Fact]
    public void TheOutcomesSplitIsTheComposedNotesOwn()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Body, Everything, operations: [new FrontmatterOperation.SetScalar("doc.title", "Widget")]));

        Assert.True(outcome.Split.HasBlock);
        Assert.Equal("doc.title: Widget", outcome.Split.InnerText);
        Assert.Equal(Body, outcome.Split.Body);
    }

    // ---- The fence guard ----

    [Fact]
    public void TheGuard_RefusesABodyWriteThatWouldMintABlock()
    {
        // Otherwise the body would carry in a block that no operation authored, skipping validation
        // and the authority assertion.
        var refusal = Assert.Throws<FrontmatterRefusal>(() => FrontmatterWrite.Apply(Synthetic.Write(
            "Old body.\n", Everything, newBody: "---\ndoc.title: Widget\n---\nSmuggled.\n")));

        Assert.Equal(RefusalReason.FenceConflict, refusal.Reason);
    }

    [Fact]
    public void TheGuard_RefusesAWriteThatWouldLeaveTheBodysOwnFencePairAsTheBlock()
    {
        // The same hazard from the other side. Removing the last key drops the fence, and the body
        // underneath opens a fence pair of its own, so the composed note would carry an unvalidated
        // block where the removed one used to be.
        var refusal = Assert.Throws<FrontmatterRefusal>(() => FrontmatterWrite.Apply(Synthetic.Write(
            "---\ndoc.title: Widget\n---\n---\nsmuggled: value\n---\nBody text.\n", Everything,
            operations: [new FrontmatterOperation.RemoveField("doc.title")])));

        Assert.Equal(RefusalReason.FenceConflict, refusal.Reason);
    }

    [Fact]
    public void TheGuard_RefusesPrependingABlockToANoteWhoseFirstLineLooksLikeAFence()
    {
        // The result would be ambiguous, so it is refused rather than resolved silently.
        var refusal = Assert.Throws<FrontmatterRefusal>(() => FrontmatterWrite.Apply(Synthetic.Write(
            "---\nNot a block, because nothing closes it.\n", Everything,
            operations: [new FrontmatterOperation.SetScalar("doc.title", "Widget")])));

        Assert.Equal(RefusalReason.FenceConflict, refusal.Reason);
    }

    [Fact]
    public void TheGuard_LetsABodyThatMerelyMentionsAFenceThrough()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("doc.title: Widget"), Everything, newBody: "A line.\n---\nA rule.\n"));

        Assert.Equal("---\ndoc.title: Widget\n---\nA line.\n---\nA rule.\n", outcome.Content);
    }

    // ---- Violations as data ----

    [Fact]
    public void ViolationsCoverTheKeysTheOperationsTouchedAndNothingElse()
    {
        // The undeclared key elsewhere in the block is passed through untouched and not reported,
        // so editing one key is never blocked by an unrelated one.
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("undeclared.key: kept\ndoc.notes:\n  - routed: skill-a\n    unknown_field: x"), Everything,
            operations: [new FrontmatterOperation.SetScalar("doc.title", "Widget")]));

        Assert.Empty(outcome.Violations);
        Assert.Contains("undeclared.key: kept", outcome.Content);
    }

    [Fact]
    public void AViolationUnderATouchedKeyComesBackAsDataRatherThanRefusing()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("doc.notes:\n  - routed: skill-a\n    unknown_field: x"), Everything,
            operations: [new FrontmatterOperation.AddEntry("doc.notes", new EntryPayload.Scalar("a hint"), null)]));

        var violation = Assert.Single(outcome.Violations);
        Assert.Equal(ViolationRule.UnknownField, violation.Rule);
        Assert.Equal("doc.notes", violation.Key);
        Assert.Equal(1, violation.EntryPosition);
        Assert.Equal("unknown_field", violation.Field);
        Assert.Equal([0], outcome.AppliedOperations);
    }

    [Fact]
    public void ARemovedKeyHasNothingLeftToReport()
    {
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Note("doc.title: Widget\ndoc.type: guide"), Everything,
            operations: [new FrontmatterOperation.RemoveField("doc.type")]));

        Assert.Empty(outcome.Violations);
    }
}
