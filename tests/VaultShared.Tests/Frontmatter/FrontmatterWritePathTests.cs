using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// The single write path as a path: the three call shapes running the same code, the body splice,
/// the no-op guarantee, the fence guard in both directions, and what the outcome reports. What each
/// operation means is <c>FrontmatterOperationTests</c>'s; what refuses is
/// <c>FrontmatterRefusalTests</c>'s.
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
        // No branch of its own: the diff counts every key because there was nothing to diff against.
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
        // Reading tolerates what writing refuses, and a body write reads no key: refusing here would
        // make a malformed block unfixable from the body side.
        var content = "---\na: b\n  c: d\n\t- x\n---\nOld body.\n";

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(content, Everything, newBody: "New body.\n"));

        Assert.Equal("---\na: b\n  c: d\n\t- x\n---\nNew body.\n", outcome.Content);
    }

    // ---- The no-op ----

    [Fact]
    public void ANoOp_ReturnsTheCallersContentByteIdentical()
    {
        // Not a reserialization of it: the odd quoting and the comment below would not survive one.
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

        // The comment and the blank line are artifacts of text with no place in the tree, and the
        // first operation against such a note loses them. A value's own quoting style survives,
        // because the node layer carries it.
        Assert.Equal("---\ndoc.title: 'Widget'\ndoc.type: guide\n---\n" + Body, outcome.Content);
        Assert.Equal(Body, outcome.Split.Body);
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
        // A block no operation authored, bypassing validation and the authority assertion.
        var refusal = Assert.Throws<FrontmatterRefusal>(() => FrontmatterWrite.Apply(Synthetic.Write(
            "Old body.\n", Everything, newBody: "---\ndoc.title: Widget\n---\nSmuggled.\n")));

        Assert.Equal(RefusalReason.FenceConflict, refusal.Reason);
    }

    [Fact]
    public void TheGuard_RefusesAWriteThatWouldLeaveTheBodysOwnFencePairAsTheBlock()
    {
        // The same inequality from the other side. Removing the last key drops the fence, and the
        // body underneath opens one of its own - so the composed note carries a block nobody
        // validated, standing where the one that was removed used to be.
        var refusal = Assert.Throws<FrontmatterRefusal>(() => FrontmatterWrite.Apply(Synthetic.Write(
            "---\ndoc.title: Widget\n---\n---\nsmuggled: value\n---\nBody text.\n", Everything,
            operations: [new FrontmatterOperation.RemoveField("doc.title")])));

        Assert.Equal(RefusalReason.FenceConflict, refusal.Reason);
    }

    [Fact]
    public void TheGuard_RefusesPrependingABlockToANoteWhoseFirstLineLooksLikeAFence()
    {
        // An ambiguous shape, worse resolved silently than refused.
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
        // The undeclared key sitting elsewhere in the block is passed through untouched and not
        // reported: editing one key is never blocked by an unrelated one.
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
