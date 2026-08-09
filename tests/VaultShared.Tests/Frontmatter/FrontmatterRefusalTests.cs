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
        // Otherwise the body would carry in a block that no operation authored, skipping validation
        // and the authority assertion.
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
        Assert.Equal("The supplied block will not parse as frontmatter.", refusal.Message);
    }

    [Fact]
    public void SuppliedBlockInvalid_TextThatParsesToSomethingOtherThanAMappingSaysSo()
    {
        // A sequence is valid YAML, so telling the caller it will not parse would point at the
        // wrong fix. The reason is shared with the unparsable case because the caller supplied the
        // text either way, and only the message separates the two mistakes.
        var refusal = Refused(string.Empty, suppliedBlock: "- a\n- b", newBody: Body);

        Assert.Equal(RefusalReason.SuppliedBlockInvalid, refusal.Reason);
        Assert.Equal("The supplied block is not a mapping of keys to values.", refusal.Message);
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

    [Theory]
    [InlineData("doc.title", "Widget\u001b[31m")]
    [InlineData("doc.created", "2026-08-02\u001b[31m")]
    [InlineData("doc.type", "guide\u001b[31m")]
    [InlineData("doc.title\u001b[31m", "Widget")]
    public void ControlCharacter_InAScalarValueRefusesRatherThanStripping(string key, string value)
    {
        // The hygiene screen runs ahead of the key's own value constraint and ahead of the lookup
        // that recognizes the key, so text carrying an escape sequence is reported as such rather
        // than as a value in the wrong format or as a key nothing declares. A caller told to
        // reformat a date, or handed the list of declared keys, resubmits the same control
        // characters.
        var refusal = Refused(Body, [Set(key, value)]);

        Assert.Equal(RefusalReason.ControlCharacter, refusal.Reason);
        Assert.Equal(0, refusal.Operation);
        Assert.Equal([key], refusal.Keys);
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
    public void ControlCharacter_ASuppliedBlocksEntriesAreScreenedAsItsScalarValuesAre()
    {
        // A supplied block admits more caller text in one call than any other route into a note, and
        // its block-list entries are where the text agents read lives. Both entry forms are
        // screened: a field of a mapping entry, and the text of a bare scalar entry. Neither value
        // here is one its own declared constraint would refuse, so what refuses is the hygiene
        // screen. The escape is written as YAML's own \x1b so the file's bytes carry the escape
        // sequence and the parser hands back the single control character it denotes.
        var field = Refused(
            string.Empty, suppliedBlock: "doc.notes:\n  - routed: \"skill\\x1b[31m-a\"", newBody: Body);

        Assert.Equal(RefusalReason.ControlCharacter, field.Reason);
        Assert.Equal(["doc.notes"], field.Keys);

        var bare = Refused(string.Empty, suppliedBlock: "doc.notes:\n  - \"a hint\\x1b[31m\"", newBody: Body);

        Assert.Equal(RefusalReason.ControlCharacter, bare.Reason);
        Assert.Equal(["doc.notes"], bare.Keys);
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

    [Fact]
    public void ControlCharacter_AnEntryFieldsNewlinesAreNotRefusedAndCannotBecomeStructure()
    {
        // An entry's field value becomes a tree node the emitter escapes, exactly as a scalar
        // value does, so text shaped like a fence and a key is written as a value and reads back
        // as one.
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(Body, Everything,
        [
            new FrontmatterOperation.AddEntry(
                "doc.notes",
                new EntryPayload.Mapping([new EntryFieldValue("routed", "skill-a\n---\ninjected: true")]),
                null)
        ]));

        var tree = FrontmatterTree.Read(outcome.Content);
        var entry = Assert.Single(tree.Entries("doc.notes"));

        Assert.Equal("skill-a\n---\ninjected: true", entry.Field("routed"));
        Assert.Equal(FrontmatterShape.Absent, tree.ShapeOf("injected"));
        Assert.Equal(Body, NoteSplit.Of(outcome.Content).Body);
    }

    // ---- Declaration checks ----

    [Theory]
    [InlineData("invented.key")]
    [InlineData("agent.smuggled")]
    public void UnknownKey_AnUndeclaredKeyIsRefusedBeforeItsShapeMatters(string key)
    {
        // Admission is the declared key map alone. A namespace rule sits beside the key
        // declarations and reads like a second way to declare, but it only widens the authority a
        // matching key requires, so a key under a declared prefix is as undeclared as one under no
        // prefix at all. The refusal lands under an authority holding every operation class, which
        // is what shows the key was never admitted rather than merely unaffordable.
        var refusal = Refused(Body, [Set(key, "x")]);

        Assert.Equal(RefusalReason.UnknownKey, refusal.Reason);
        Assert.Equal([key], refusal.Keys);
    }

    [Theory]
    [InlineData("invented.key")]
    [InlineData("agent.smuggled")]
    public void UnknownKey_ASuppliedBlockAdmitsOnlyDeclaredKeys(string key)
    {
        // A supplied block is the other way a key can enter a note, and it is checked the same way.
        var refusal = Refused(string.Empty, suppliedBlock: $"{key}: x", newBody: Body);

        Assert.Equal(RefusalReason.UnknownKey, refusal.Reason);
        Assert.Equal([key], refusal.Keys);
    }

    [Fact]
    public void UnknownKey_TheRefusalCarriesTheDeclaredKeyNamesSortedOrdinally()
    {
        // A payload exists so the caller can lift a value into a corrected call, and the corrected
        // call for an undeclared key is one naming a declared one. Both raise sites carry it, so a
        // caller does not meet the same refusal with a payload one way and without it the other.
        string[] declared = [.. Synthetic.Registry.Keys.Keys.Order(StringComparer.Ordinal)];

        Assert.Equal(declared, Refused(Body, [Set("invented.key", "x")]).DeclaredKeys);
        Assert.Equal(declared, Refused(string.Empty, suppliedBlock: "invented.key: x", newBody: Body).DeclaredKeys);
    }

    [Fact]
    public void UnknownKey_IsTheOnlyRefusalCarryingDeclaredKeys()
    {
        // Every other refusal names a key the declaration already recognizes, so the declared set
        // is not what its caller needs.
        Assert.Empty(Refused(Body, [Set("doc.created", "yesterday")]).DeclaredKeys);
        Assert.Empty(Refused(Body, [Set("doc.notes", "x")]).DeclaredKeys);
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
    public void ValueRefused_AnEntryFieldCarryingNoValueIsRefusedAsAnUnnamedFieldIs()
    {
        // Empty and whitespace-only text are the same emptiness, and a field's value is tested for
        // it exactly as the field's name is. An entry added this way would carry no identity, so no
        // later removal could name it.
        FrontmatterOperation Pinned(string value) =>
            new FrontmatterOperation.AddEntry(
                "doc.notes", new EntryPayload.Mapping([new EntryFieldValue("pinned", value)]), null);

        Assert.Equal(RefusalReason.ValueRefused, Refused(Body, [Pinned(string.Empty)]).Reason);
        Assert.Equal(RefusalReason.ValueRefused, Refused(Body, [Pinned("   ")]).Reason);
    }

    [Fact]
    public void ValueRefused_AnOptionalEntryFieldCarryingNoValueIsRefusedToo()
    {
        // The test is applied to every field of the payload rather than to the ones the schema
        // requires. Sending a field with nothing under it is a caller error whose remedy is to omit
        // the field.
        var refusal = Refused(Body,
        [
            new FrontmatterOperation.AddEntry(
                "doc.notes",
                new EntryPayload.Mapping([new EntryFieldValue("routed", "skill-a"), new EntryFieldValue("when", " ")]),
                null)
        ]);

        Assert.Equal(RefusalReason.ValueRefused, refusal.Reason);
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
        // The note alone: doc.title is declared a scalar, so nothing in the declaration objects, and
        // only the block this note gives the key does. The two cases above are both refused by the
        // declaration, since doc.notes is declared a block list whatever any note holds.
        Assert.Equal(
            RefusalReason.ShapeMismatch,
            Refused(Note("doc.title:\n  - one\n  - two"), [Set("doc.title", "Widget")]).Reason);
        Assert.Equal(
            RefusalReason.ShapeMismatch,
            Refused(Body, [new FrontmatterOperation.AddEntry("doc.title", new EntryPayload.Scalar("x"), null)]).Reason);
    }

    [Fact]
    public void ShapeMismatch_AnEmptyInlineValueUnderABlockListKeyRefusesAsANonEmptyOneDoes()
    {
        // The shape a key holds is read from the node the block wrote, not from what that node
        // carries, so a block-list key given an empty scalar is a shape error rather than a key
        // with no value or a list with no entries. Admitting it as "no entries yet" would let a
        // note that lost its whole list to a bad write pass as valid.
        var refusal = Refused(string.Empty, suppliedBlock: "doc.notes: ''", newBody: Body);

        Assert.Equal(RefusalReason.ShapeMismatch, refusal.Reason);
        Assert.Equal(["doc.notes"], refusal.Keys);
    }

    [Fact]
    public void UnresolvedIdentity_AnAnchorNamingNoEntryRefuses()
    {
        // An anchor that resolves to nothing refuses rather than falling back to appending, so an
        // entry never lands somewhere the caller did not ask for.
        var refusal = Refused(
            Note("doc.notes:\n  - routed: skill-a"),
            [
                new FrontmatterOperation.AddEntry(
                    "doc.notes",
                    new EntryPayload.Mapping([new EntryFieldValue("routed", "skill-b")]),
                    new EntryAnchor(AnchorPlacement.After, "skill-z"))
            ]);

        Assert.Equal(RefusalReason.UnresolvedIdentity, refusal.Reason);
        Assert.Equal(0, refusal.Operation);
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
