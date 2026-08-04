using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Covers every value of <see cref="RefusalReason"/>, each reached through
/// <see cref="FrontmatterWrite.Apply"/> and asserted by name. The suite includes a test that
/// checks no reason is left uncovered.
/// </summary>
public sealed class FrontmatterRefusalTests
{
    private const string Body = "Body text.\n";

    private static readonly StubAuthority Everything = new(Synthetic.OpenClass, Synthetic.ElevatedClass);

    private static string Note(string block) => $"---\n{block}\n---\n{Body}";

    private static FrontmatterRefusal Refused(
        string content, IReadOnlyList<FrontmatterOperation>? operations = null, string? suppliedBlock = null,
        string? newBody = null, string path = "Notes/Note.md") =>
        Assert.Throws<FrontmatterRefusal>(() => FrontmatterWrite.Apply(
            Synthetic.Write(content, Everything, operations, suppliedBlock, newBody, path)));

    private static FrontmatterOperation Set(string key, string value) => new FrontmatterOperation.SetScalar(key, value);

    /// <summary>
    /// Checks that every <see cref="RefusalReason"/> value appears in the name of some test in
    /// this suite.
    /// </summary>
    [Fact]
    public void EveryReasonIsReachedBySomeCaseInThisSuite()
    {
        var reached = typeof(FrontmatterRefusalTests)
            .GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0)
            .Select(m => m.Name);

        foreach (var reason in Enum.GetNames<RefusalReason>())
            Assert.Contains(reached, name => name.Contains(reason, StringComparison.Ordinal));
    }

    // ---- Integrity ----

    [Fact]
    public void Unparsable_AStoredBlockThatWillNotParseRefusesAFrontmatterWrite()
    {
        var refusal = Refused("---\na: b\n  c: d\n\t- x\n---\n" + Body, [Set("doc.title", "Widget")]);

        Assert.Equal(RefusalReason.Unparsable, refusal.Reason);
        Assert.Null(refusal.Operation);
    }

    [Fact]
    public void NotAMapping_AStoredBlockThatIsNotAMappingRefuses()
    {
        var refusal = Refused("---\n- a\n- b\n---\n" + Body, [Set("doc.title", "Widget")]);

        Assert.Equal(RefusalReason.NotAMapping, refusal.Reason);
    }

    [Fact]
    public void FenceConflict_ABodyWriteMintingABlockRefuses()
    {
        var refusal = Refused(Body, newBody: "---\ndoc.title: Widget\n---\nSmuggled.\n");

        Assert.Equal(RefusalReason.FenceConflict, refusal.Reason);
    }

    [Fact]
    public void SuppliedBlockInvalid_AnUnclosedFenceIsACallerError()
    {
        var refusal = Refused(string.Empty, suppliedBlock: "---\ndoc.title: Widget\n", newBody: Body);

        Assert.Equal(RefusalReason.SuppliedBlockInvalid, refusal.Reason);
    }

    [Fact]
    public void SuppliedBlockInvalid_ContentAfterTheClosingFenceIsACallerError()
    {
        var refusal = Refused(
            string.Empty, suppliedBlock: "---\ndoc.title: Widget\n---\nTrailing.\n", newBody: Body);

        Assert.Equal(RefusalReason.SuppliedBlockInvalid, refusal.Reason);
    }

    [Fact]
    public void SuppliedBlockInvalid_TextThatWillNotParseIsACallerErrorRatherThanTheNotesProblem()
    {
        // Malformed frontmatter is attributed to whoever supplied it. This text came from the
        // caller, so the reason points at the request rather than at the note.
        var refusal = Refused(string.Empty, suppliedBlock: "a: b\n  c: d\n\t- x", newBody: Body);

        Assert.Equal(RefusalReason.SuppliedBlockInvalid, refusal.Reason);
    }

    // ---- Perimeter and hygiene ----

    [Fact]
    public void PathExcluded_ADotSegmentRefusesWhateverTheOperation()
    {
        var refusal = Refused(Body, [Set("doc.title", "Widget")], path: ".obsidian/Notes/Note.md");

        Assert.Equal(RefusalReason.PathExcluded, refusal.Reason);
    }

    [Fact]
    public void PathExcluded_IsDecidedBeforeAnythingElseAboutTheCall()
    {
        // The exclusion is decided before the block is parsed or the keys are checked, so neither
        // an unparsable block nor an unknown key changes the reason.
        var refusal = Refused("---\nnot: [a mapping\n---\n", [Set("no.such.key", "x")], path: "Notes/.hidden/Note.md");

        Assert.Equal(RefusalReason.PathExcluded, refusal.Reason);
    }

    [Fact]
    public void ThePathIsMandatory()
    {
        // An absent path would read as permission on exactly the files the exclusion protects, so
        // it throws as an upstream defect instead of refusing as a caller error.
        Assert.Throws<ArgumentException>(() => FrontmatterWrite.Apply(
            Synthetic.Write(Body, Everything, [Set("doc.title", "Widget")], path: "  ")));
    }

    [Fact]
    public void ControlCharacter_InAScalarValueRefusesRatherThanStripping()
    {
        var refusal = Refused(Body, [Set("doc.title", "Widget\u001b[31m")]);

        Assert.Equal(RefusalReason.ControlCharacter, refusal.Reason);
        Assert.Equal(0, refusal.Operation);
        Assert.Equal(["doc.title"], refusal.Keys);
    }

    [Fact]
    public void ControlCharacter_InAnEntryFieldRefusesToo()
    {
        // Block-list entries carry the text agents read, so the same check applies there.
        var refusal = Refused(Body,
        [
            new FrontmatterOperation.AddEntry(
                "doc.notes", new EntryPayload.Mapping([new EntryFieldValue("routed", "skill\u0007a")]), null)
        ]);

        Assert.Equal(RefusalReason.ControlCharacter, refusal.Reason);
    }

    [Fact]
    public void ControlCharacter_NewlinesAndQuotesAreNotRefused()
    {
        // The emitter escapes newlines and quotes, so neither is a hygiene problem.
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            Body, Everything, [Set("doc.title", "a \"quoted\" line\nand another")]));

        Assert.Contains("doc.title:", outcome.Content);
        Assert.Equal("a \"quoted\" line\nand another", FrontmatterTree.Read(outcome.Content).Scalar("doc.title"));
    }

    // ---- Declaration checks ----

    [Fact]
    public void UnknownKey_AnUndeclaredKeyIsRefusedBeforeItsShapeMatters()
    {
        var refusal = Refused(Body, [Set("invented.key", "x")]);

        Assert.Equal(RefusalReason.UnknownKey, refusal.Reason);
        Assert.Equal(["invented.key"], refusal.Keys);
    }

    [Fact]
    public void UnknownKey_ASuppliedBlockAdmitsOnlyDeclaredKeys()
    {
        // A supplied block is the other way a key can enter a note, and it is checked the same way.
        var refusal = Refused(string.Empty, suppliedBlock: "invented.key: x", newBody: Body);

        Assert.Equal(RefusalReason.UnknownKey, refusal.Reason);
    }

    [Fact]
    public void ValueRefused_AFailedValueConstraintRefusesOnTheEditPathAsOnTheCreationPath()
    {
        Assert.Equal(RefusalReason.ValueRefused, Refused(Body, [Set("doc.created", "yesterday")]).Reason);
        Assert.Equal(
            RefusalReason.ValueRefused,
            Refused(string.Empty, suppliedBlock: "doc.created: yesterday", newBody: Body).Reason);
    }

    [Fact]
    public void ValueRefused_AnEntryMatchingNoDeclaredVariantIsRefusedRatherThanIgnored()
    {
        var refusal = Refused(Body,
        [
            new FrontmatterOperation.AddEntry(
                "doc.notes", new EntryPayload.Mapping([new EntryFieldValue("invented", "x")]), null)
        ]);

        Assert.Equal(RefusalReason.ValueRefused, refusal.Reason);
    }

    [Fact]
    public void ValueRefused_AnExclusiveGroupIsCheckedInBothDirections()
    {
        FrontmatterOperation Entry(params (string, string)[] fields) =>
            new FrontmatterOperation.AddEntry(
                "nav.contents",
                new EntryPayload.Mapping([.. fields.Select(f => new EntryFieldValue(f.Item1, f.Item2))]),
                null);

        Assert.Equal(RefusalReason.ValueRefused, Refused(Body, [Entry(("label", "One"))]).Reason);
        Assert.Equal(
            RefusalReason.ValueRefused,
            Refused(Body, [Entry(("label", "One"), ("link", "[[A]]"), ("folder", "B"))]).Reason);
    }

    [Fact]
    public void ValueRefused_AnEmptyAddressingValueIsRefusedOnBothSidesOfTheOperation()
    {
        // The same emptiness check applies to both the add and the remove form.
        Assert.Equal(
            RefusalReason.ValueRefused,
            Refused(Body, [new FrontmatterOperation.AddEntry("doc.notes", new EntryPayload.Scalar("   "), null)]).Reason);
        Assert.Equal(
            RefusalReason.ValueRefused,
            Refused(Body, [new FrontmatterOperation.RemoveEntry("doc.notes", new EntryTarget.Identity("   "))]).Reason);
    }

    [Fact]
    public void ValueRefused_AVariantNothingDeclaresIsRefused()
    {
        var refusal = Refused(
            Note("doc.notes:\n  - routed: skill-a"),
            [new FrontmatterOperation.RemoveEntry("doc.notes", new EntryTarget.Variant("invented"))]);

        Assert.Equal(RefusalReason.ValueRefused, refusal.Reason);
    }

    [Fact]
    public void ShapeMismatch_ByTheDeclarationAndByTheNoteAlike()
    {
        Assert.Equal(RefusalReason.ShapeMismatch, Refused(Body, [Set("doc.notes", "x")]).Reason);
        Assert.Equal(
            RefusalReason.ShapeMismatch,
            Refused(Note("doc.notes:\n  - a hint"), [Set("doc.notes", "x")]).Reason);
        Assert.Equal(
            RefusalReason.ShapeMismatch,
            Refused(Body, [new FrontmatterOperation.AddEntry("doc.title", new EntryPayload.Scalar("x"), null)]).Reason);
    }

    [Fact]
    public void UnresolvedIdentity_AnAnchorNamingNoEntryRefuses()
    {
        var refusal = Refused(
            Note("doc.notes:\n  - routed: skill-a"),
            [
                new FrontmatterOperation.AddEntry(
                    "doc.notes",
                    new EntryPayload.Mapping([new EntryFieldValue("routed", "skill-b")]),
                    new EntryAnchor(AnchorPlacement.After, "skill-z"))
            ]);

        Assert.Equal(RefusalReason.UnresolvedIdentity, refusal.Reason);
    }

    [Fact]
    public void AmbiguousIdentity_AnIdentityMatchingSeveralEntriesRefuses()
    {
        var refusal = Refused(
            Note("doc.notes:\n  - routed: skill-a\n  - routed: SKILL-A"),
            [new FrontmatterOperation.RemoveEntry("doc.notes", new EntryTarget.Identity("skill-a"))]);

        Assert.Equal(RefusalReason.AmbiguousIdentity, refusal.Reason);
    }

    [Fact]
    public void WholesaleRemovalRefused_WhereTheVariantDoesNotDeclareItself()
    {
        var refusal = Refused(
            Note("doc.notes:\n  - pinned: keep this"),
            [new FrontmatterOperation.RemoveEntry("doc.notes", new EntryTarget.Variant("pinned"))]);

        Assert.Equal(RefusalReason.WholesaleRemovalRefused, refusal.Reason);
    }

    // ---- Authority and availability ----

    [Fact]
    public void AuthorityDenied_NamesTheChangedKeySetAndNoOperationIndex()
    {
        var refusal = Assert.Throws<FrontmatterRefusal>(() => FrontmatterWrite.Apply(
            Synthetic.Write(Body, new StubAuthority(), [Set("agent.mode", "strict")])));

        Assert.Equal(RefusalReason.AuthorityDenied, refusal.Reason);
        Assert.Equal(["agent.mode"], refusal.Keys);
        Assert.Null(refusal.Operation);
    }

    [Fact]
    public void RegistryUnavailable_IsHowAFailedLoadReachesAWrite()
    {
        // A caller obtains the registry from the state, and a state that did not load has none to
        // give.
        Assert.Equal(
            RefusalReason.RegistryUnavailable,
            Assert.Throws<FrontmatterRefusal>(() => new RegistryState.Absent("registry.json").Require()).Reason);
        Assert.Equal(
            RefusalReason.RegistryUnavailable,
            Assert.Throws<FrontmatterRefusal>(() => new RegistryState.Unloadable("registry.json", "why.").Require()).Reason);
        Assert.Same(Synthetic.Registry, new RegistryState.Loaded(Synthetic.Registry).Require());
    }
}
