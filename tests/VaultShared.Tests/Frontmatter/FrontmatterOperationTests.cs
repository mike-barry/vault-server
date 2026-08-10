using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Verifies the semantics of each frontmatter operation. Every case runs through
/// <see cref="FrontmatterWrite.Apply"/>, which is the only entry point consumers use.
/// </summary>
public sealed class FrontmatterOperationTests
{
    /// <summary>
    /// Applies operations to a note through an authority stub holding every class the synthetic
    /// declaration can require, so these cases turn on operation semantics rather than authority.
    /// </summary>
    /// <param name="content">The full note text the operations are applied to.</param>
    /// <param name="operations">The operations to apply, in order.</param>
    /// <returns>The outcome of the write.</returns>
    private static WriteOutcome Apply(string content, params FrontmatterOperation[] operations) =>
        FrontmatterWrite.Apply(Synthetic.Write(
            content, new StubAuthority(Synthetic.OpenClass, Synthetic.ElevatedClass), operations));

    private static FrontmatterRefusal Refused(string content, params FrontmatterOperation[] operations) =>
        Assert.Throws<FrontmatterRefusal>(() => Apply(content, operations));

    private const string Body = "Body text.\n";

    private static string Note(string block) => $"---\n{block}\n---\n{Body}";

    // ---- Setting a scalar ----

    [Fact]
    public void SetScalar_CreatesTheBlockOnANoteThatHasNone()
    {
        var outcome = Apply(Body, new FrontmatterOperation.SetScalar("doc.title", "Widget"));

        Assert.Equal($"---\ndoc.title: Widget\n---\n{Body}", outcome.Content);
        Assert.Equal([0], outcome.AppliedOperations);
        Assert.True(outcome.FrontmatterChanged);
        Assert.False(outcome.BodyChanged);
    }

    [Fact]
    public void SetScalar_ReplacesInPlaceAndKeepsKeyOrder()
    {
        var outcome = Apply(
            Note("doc.title: Widget\ndoc.type: guide"),
            new FrontmatterOperation.SetScalar("doc.title", "Gadget"));

        Assert.Equal(Note("doc.title: Gadget\ndoc.type: guide"), outcome.Content);
    }

    [Fact]
    public void SetScalar_AgainstAFieldHoldingABlockIsRefused()
    {
        // Without the shape check, a mistyped key would replace a whole list with a scalar.
        var declared = Refused(
            Note("doc.notes:\n  - a hint"),
            new FrontmatterOperation.SetScalar("doc.notes", "text"));

        Assert.Equal(RefusalReason.ShapeMismatch, declared.Reason);
        Assert.Equal(
            "\"doc.notes\" is declared a block list, and a flat scalar operation would replace the list with a string.",
            declared.Message);

        // A key declared a scalar can still hold a block in one note, by hand or by an earlier bad
        // write, and only the note's own shape refuses that. The messages are asserted because both
        // refusals carry the same reason.
        var byTheNote = Refused(
            Note("doc.title:\n  - one\n  - two"),
            new FrontmatterOperation.SetScalar("doc.title", "Widget"));

        Assert.Equal(RefusalReason.ShapeMismatch, byTheNote.Reason);
        Assert.Equal("\"doc.title\" holds a block in this note, and a scalar set would replace it.", byTheNote.Message);
        Assert.Equal(["doc.title"], byTheNote.Keys);
    }

    [Fact]
    public void SetScalar_WritingTheValueAlreadyThereChangesNothing()
    {
        var content = Note("doc.title: Widget");

        var outcome = Apply(content, new FrontmatterOperation.SetScalar("doc.title", "Widget"));

        Assert.Empty(outcome.AppliedOperations);
        Assert.Same(content, outcome.Content);
    }

    [Fact]
    public void SetScalar_CanonicalizesTheKeyToTheSpellingAlreadyInTheNote()
    {
        // A differently-cased spelling would otherwise pass the case-insensitive membership test and
        // then land as a second key beside the first.
        var outcome = Apply(Note("doc.title: Widget"), new FrontmatterOperation.SetScalar("DOC.TITLE", "Gadget"));

        Assert.Equal(Note("doc.title: Gadget"), outcome.Content);
    }

    // ---- Removing a field ----

    [Fact]
    public void RemoveField_TakesTheScalarPathFromTheDetectedShape()
    {
        var outcome = Apply(
            Note("doc.title: Widget\ndoc.type: guide"),
            new FrontmatterOperation.RemoveField("doc.type"));

        Assert.Equal(Note("doc.title: Widget"), outcome.Content);
    }

    [Fact]
    public void RemoveField_OnAnAbsentKeyChangesNothing()
    {
        var content = Note("doc.title: Widget");

        var outcome = Apply(content, new FrontmatterOperation.RemoveField("doc.type"));

        Assert.Empty(outcome.AppliedOperations);
        Assert.Same(content, outcome.Content);
    }

    [Fact]
    public void RemoveField_TakesTheBlockPathWhereEveryEntryIsClearableWholesale()
    {
        var outcome = Apply(
            Note("doc.title: Widget\ndoc.notes:\n  - routed: skill-a\n  - routed: skill-b"),
            new FrontmatterOperation.RemoveField("doc.notes"));

        Assert.Equal(Note("doc.title: Widget"), outcome.Content);
    }

    [Fact]
    public void RemoveField_IsRefusedWholeWhereOneEntryIsNotClearableWholesale()
    {
        var refusal = Refused(
            Note("doc.notes:\n  - routed: skill-a\n  - pinned: keep this"),
            new FrontmatterOperation.RemoveField("doc.notes"));

        Assert.Equal(RefusalReason.WholesaleRemovalRefused, refusal.Reason);
        Assert.Equal(["doc.notes"], refusal.Keys);
        Assert.Equal(
            "\"doc.notes\" holds entries that are not clearable wholesale - name them one at a time.",
            refusal.Message);
    }

    /// <summary>
    /// Checks that removing a key the declaration calls a scalar, which this note gives a block, is
    /// refused. Nothing declares those entries clearable, so dropping the key would discard a list no
    /// schema was consulted about. The message is asserted because the other wholesale refusal
    /// carries the same reason and a different remedy.
    /// </summary>
    [Fact]
    public void RemoveField_AgainstAScalarKeyThatTheNoteGivesABlockIsRefused()
    {
        var refusal = Refused(
            Note("doc.title:\n  - one\n  - two"),
            new FrontmatterOperation.RemoveField("doc.title"));

        Assert.Equal(RefusalReason.WholesaleRemovalRefused, refusal.Reason);
        Assert.Equal(["doc.title"], refusal.Keys);
        Assert.Equal(
            "\"doc.title\" is declared a scalar and holds a block in this note, so nothing declares its entries clearable.",
            refusal.Message);
    }

    [Fact]
    public void RemoveField_DroppingTheLastKeyDropsTheFence()
    {
        // Exactly one place writes a fence, and an empty tree emits none.
        var outcome = Apply(Note("doc.title: Widget"), new FrontmatterOperation.RemoveField("doc.title"));

        Assert.Equal(Body, outcome.Content);
        Assert.False(outcome.Split.HasBlock);
    }

    // ---- Adding an entry ----

    [Fact]
    public void AddEntry_CreatesItsKeyAndItsBlock()
    {
        var outcome = Apply(Body, Add("doc.notes", ("routed", "skill-a")));

        Assert.Equal($"---\ndoc.notes:\n  - routed: skill-a\n---\n{Body}", outcome.Content);
    }

    [Fact]
    public void AddEntry_AppendsAbsentAnAnchor()
    {
        var outcome = Apply(Note("doc.notes:\n  - routed: skill-a"), Add("doc.notes", ("routed", "skill-b")));

        Assert.Equal(Note("doc.notes:\n  - routed: skill-a\n  - routed: skill-b"), outcome.Content);
    }

    [Fact]
    public void AddEntry_ADuplicateIdentityIsAPureNoOpAndDoesNotUpdateOtherFields()
    {
        var content = Note("doc.notes:\n  - routed: skill-a\n    when: always");

        var outcome = Apply(content, Add("doc.notes", ("routed", "skill-a"), ("when", "never")));

        Assert.Empty(outcome.AppliedOperations);
        Assert.Same(content, outcome.Content);
    }

    [Fact]
    public void AddEntry_IdentityComparesAsTheVariantDeclares()
    {
        // routed compares case-insensitively; pinned compares exactly, so the same text in each
        // answers differently.
        Assert.Empty(Apply(Note("doc.notes:\n  - routed: Skill-A"), Add("doc.notes", ("routed", "skill-a"))).AppliedOperations);
        Assert.Equal([0], Apply(Note("doc.notes:\n  - pinned: Keep This"), Add("doc.notes", ("pinned", "keep this"))).AppliedOperations);
    }

    [Fact]
    public void AddEntry_AVariantWithNoIdentityAppendsEveryTime()
    {
        // A bare hint declares no identity, so there is nothing to match on.
        var outcome = Apply(
            Note("doc.notes:\n  - first hint"),
            new FrontmatterOperation.AddEntry("doc.notes", new EntryPayload.Scalar("second hint"), null));

        Assert.Equal(Note("doc.notes:\n  - first hint\n  - second hint"), outcome.Content);
    }

    [Fact]
    public void AddEntry_AnAnchorPlacesTheEntryOnEitherSide()
    {
        var content = Note("doc.notes:\n  - routed: skill-a\n  - routed: skill-c");

        var before = Apply(content, Add("doc.notes", AnchorAt(AnchorPlacement.Before, "skill-c"), ("routed", "skill-b")));
        var after = Apply(content, Add("doc.notes", AnchorAt(AnchorPlacement.After, "skill-a"), ("routed", "skill-b")));

        var expected = Note("doc.notes:\n  - routed: skill-a\n  - routed: skill-b\n  - routed: skill-c");
        Assert.Equal(expected, before.Content);
        Assert.Equal(expected, after.Content);
    }

    [Fact]
    public void AddEntry_AnAnchorNamingSeveralEntriesIsRefused()
    {
        var refusal = Refused(
            Note("doc.notes:\n  - routed: skill-a\n  - routed: SKILL-A"),
            Add("doc.notes", AnchorAt(AnchorPlacement.After, "skill-a"), ("routed", "skill-b")));

        Assert.Equal(RefusalReason.AmbiguousIdentity, refusal.Reason);
        Assert.Equal("The anchor \"skill-a\" names more than one entry under \"doc.notes\".", refusal.Message);
    }

    /// <summary>
    /// Checks that an add whose own identity already matches several entries is refused rather than
    /// treated as the duplicate no-op. The no-op is safe only where exactly one entry answers to the
    /// identity.
    /// </summary>
    [Fact]
    public void AddEntry_AnIdentityAlreadyMatchingSeveralEntriesIsRefused()
    {
        var refusal = Refused(
            Note("doc.notes:\n  - routed: skill-a\n  - routed: SKILL-A"),
            Add("doc.notes", ("routed", "skill-a")));

        Assert.Equal(RefusalReason.AmbiguousIdentity, refusal.Reason);
        Assert.Equal("\"skill-a\" already addresses more than one entry under \"doc.notes\".", refusal.Message);
    }

    [Fact]
    public void AddEntry_AgainstAFieldHoldingAnInlineValueIsRefused()
    {
        var refusal = Refused(Note("doc.notes: text"), Add("doc.notes", ("routed", "skill-a")));

        Assert.Equal(RefusalReason.ShapeMismatch, refusal.Reason);
    }

    // ---- Removing an entry ----

    [Fact]
    public void RemoveEntry_AddressesByIdentityAndDropsTheKeyWhenTheListEmpties()
    {
        var outcome = Apply(
            Note("doc.title: Widget\ndoc.notes:\n  - routed: skill-a"),
            Remove("doc.notes", "skill-a"));

        Assert.Equal(Note("doc.title: Widget"), outcome.Content);
    }

    [Fact]
    public void RemoveEntry_EmptyingTheBlockDropsTheFence()
    {
        var outcome = Apply(Note("doc.notes:\n  - routed: skill-a"), Remove("doc.notes", "skill-a"));

        Assert.Equal(Body, outcome.Content);
        Assert.False(outcome.Split.HasBlock);
    }

    [Fact]
    public void RemoveEntry_AnAbsentIdentityReportsNoChange()
    {
        var content = Note("doc.notes:\n  - routed: skill-a");

        var outcome = Apply(content, Remove("doc.notes", "skill-z"));

        Assert.Empty(outcome.AppliedOperations);
        Assert.Same(content, outcome.Content);
    }

    [Fact]
    public void RemoveEntry_ClearsAWholeVariantWhereTheDeclarationPermitsIt()
    {
        var outcome = Apply(
            Note("doc.notes:\n  - routed: skill-a\n  - a hint\n  - routed: skill-b"),
            new FrontmatterOperation.RemoveEntry("doc.notes", new EntryTarget.Variant("routed")));

        Assert.Equal(Note("doc.notes:\n  - a hint"), outcome.Content);
    }

    [Fact]
    public void RemoveEntry_OnAnAbsentKeyReportsNoChange()
    {
        var content = Note("doc.title: Widget");

        var outcome = Apply(content, Remove("doc.notes", "skill-a"));

        Assert.Empty(outcome.AppliedOperations);
        Assert.Same(content, outcome.Content);
    }

    // ---- Ordering ----

    [Fact]
    public void Operations_ApplyInOrderAndEachSeesThePreviousResult()
    {
        // Rewriting an entry is a removal followed by an addition in one ordered batch, which is
        // what makes the duplicate add's no-op safe.
        var outcome = Apply(
            Note("doc.notes:\n  - routed: skill-a\n    when: always"),
            Remove("doc.notes", "skill-a"),
            Add("doc.notes", ("routed", "skill-a"), ("when", "never")));

        Assert.Equal([0, 1], outcome.AppliedOperations);
        Assert.Equal(Note("doc.notes:\n  - routed: skill-a\n    when: never"), outcome.Content);
    }

    [Fact]
    public void Operations_ARemovalAfterAnAddSeesTheEntryTheAddCreated()
    {
        var outcome = Apply(
            Body,
            Add("doc.notes", ("routed", "skill-a")),
            Remove("doc.notes", "skill-a"));

        Assert.Equal([0, 1], outcome.AppliedOperations);
        Assert.Equal(Body, outcome.Content);
    }

    [Fact]
    public void Operations_ThatChangeNothingAreNotReportedApplied()
    {
        var outcome = Apply(
            Note("doc.title: Widget"),
            new FrontmatterOperation.SetScalar("doc.title", "Widget"),
            new FrontmatterOperation.SetScalar("doc.type", "guide"));

        Assert.Equal([1], outcome.AppliedOperations);
    }

    // ---- Decoding ----

    [Fact]
    public void Operations_TrimTheirTextWhereTheyAreConstructed()
    {
        var outcome = Apply(Body, new FrontmatterOperation.SetScalar("  doc.title  ", "  Widget  "));

        Assert.Equal($"---\ndoc.title: Widget\n---\n{Body}", outcome.Content);
    }

    [Fact]
    public void Operations_NameAVariantCaseInsensitively()
    {
        var outcome = Apply(
            Note("doc.notes:\n  - routed: skill-a"),
            new FrontmatterOperation.RemoveEntry("doc.notes", new EntryTarget.Variant("ROUTED")));

        Assert.Equal(Body, outcome.Content);
    }

    // ---- Shorthand ----

    private static FrontmatterOperation.AddEntry Add(string key, params (string Name, string Value)[] fields) =>
        Add(key, null, fields);

    private static FrontmatterOperation.AddEntry Add(
        string key, EntryAnchor? anchor, params (string Name, string Value)[] fields) =>
        new(key, new EntryPayload.Mapping([.. fields.Select(f => new EntryFieldValue(f.Name, f.Value))]), anchor);

    private static EntryAnchor AnchorAt(AnchorPlacement placement, string identity) => new(placement, identity);

    private static FrontmatterOperation.RemoveEntry Remove(string key, string identity) =>
        new(key, new EntryTarget.Identity(identity));
}
