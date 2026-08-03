using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// The whole-block extent over the same checkers the write path applies per key. A note this passes
/// cannot be one an operation refuses over the same key, which is a property of there being one
/// implementation of each check rather than of anybody keeping two in step.
/// </summary>
public sealed class FrontmatterValidatorTests
{
    private static IReadOnlyList<Violation> Validate(string block) =>
        FrontmatterValidator.Validate($"---\n{block}\n---\nBody text.\n", Synthetic.Registry);

    private static Violation Single(string block) => Assert.Single(Validate(block));

    [Fact]
    public void AValidNoteHasNoViolations()
    {
        Assert.Empty(Validate("doc.title: Widget\ndoc.type: guide\ndoc.notes:\n  - routed: skill-a\n  - a hint"));
    }

    [Fact]
    public void ANoteWithNoBlockHasNoViolations()
    {
        Assert.Empty(FrontmatterValidator.Validate("Body text.\n", Synthetic.Registry));
    }

    [Fact]
    public void ItNeverReturnsAVerdict()
    {
        // The return shape is set by the widest consumer: a sweeping tool builds findings and remedy
        // payloads out of these, and a boolean would strand it.
        var violation = Single("doc.created: yesterday");

        Assert.Equal(ViolationRule.ValueConstraintFailed, violation.Rule);
        Assert.Equal("doc.created", violation.Key);
        Assert.Contains("yyyy-MM-dd", violation.Reasoning, StringComparison.Ordinal);
    }

    // ---- Extent ----

    [Fact]
    public void EveryKeyIsCheckedAndAnUnrecognizedOneIsItselfAViolation()
    {
        var violations = Validate("doc.title: Widget\ninvented.key: x\nanother.invention: y");

        Assert.Equal(2, violations.Count);
        Assert.All(violations, v => Assert.Equal(ViolationRule.UnknownKey, v.Rule));
        Assert.Equal(["invented.key", "another.invention"], violations.Select(v => v.Key));
    }

    [Fact]
    public void AnUnparsableBlockIsReportedRatherThanPassedOver()
    {
        // Tolerance means one malformed note does not fail a whole-vault pass, not that the note is
        // called valid - the write path refuses it, and the two extents must agree.
        var violation = Assert.Single(
            FrontmatterValidator.Validate("---\na: b\n  c: d\n\t- x\n---\nBody.\n", Synthetic.Registry));

        Assert.Equal(ViolationRule.UnparsableBlock, violation.Rule);
        Assert.Equal(string.Empty, violation.Key);
    }

    [Fact]
    public void ABlockThatIsNotAMappingIsReportedTheSameWay()
    {
        var violation = Assert.Single(FrontmatterValidator.Validate("---\n- a\n- b\n---\nBody.\n", Synthetic.Registry));

        Assert.Equal(ViolationRule.UnparsableBlock, violation.Rule);
    }

    // ---- What is compiled in ----

    [Fact]
    public void ADeclaredKeyCarryingNoValue()
    {
        Assert.Equal(ViolationRule.DeclaredKeyWithNoValue, Single("doc.title:").Rule);
    }

    [Fact]
    public void AnEmptyBlockList()
    {
        Assert.Equal(ViolationRule.EmptyBlockList, Single("doc.notes: []").Rule);
    }

    [Fact]
    public void AnInlineValueWhereABlockListIsDeclared()
    {
        Assert.Equal(ViolationRule.InlineValueWhereBlockListDeclared, Single("doc.notes: text").Rule);
    }

    [Fact]
    public void ABlockWhereAScalarIsDeclared()
    {
        Assert.Equal(ViolationRule.BlockWhereScalarDeclared, Single("doc.title:\n  - a\n  - b").Rule);
    }

    // ---- The declaration, applied ----

    [Fact]
    public void AnEntryMatchingNoVariant()
    {
        var violation = Single("doc.notes:\n  - invented: x");

        Assert.Equal(ViolationRule.EntryMatchesNoVariant, violation.Rule);
        Assert.Equal(1, violation.EntryPosition);
    }

    [Fact]
    public void ABareScalarWhereNoScalarVariantIsDeclaredIsTheSameCheck()
    {
        Assert.Equal(ViolationRule.EntryMatchesNoVariant, Single("nav.contents:\n  - a bare value").Rule);
    }

    [Fact]
    public void AnEntryMatchingSeveralVariants()
    {
        Assert.Equal(ViolationRule.EntryMatchesSeveralVariants, Single("doc.notes:\n  - pinned: a\n    routed: b").Rule);
    }

    [Fact]
    public void AMissingRequiredField()
    {
        // The required field is not the discriminator, so this is not the no-variant case.
        var violation = Single("doc.notes:\n  - a hint\n  - cited: a claim");

        Assert.Equal(ViolationRule.MissingRequiredField, violation.Rule);
        Assert.Equal(2, violation.EntryPosition);
        Assert.Equal("source", violation.Field);
    }

    [Fact]
    public void AnUnknownField()
    {
        var violation = Single("doc.notes:\n  - routed: skill-a\n    invented: x");

        Assert.Equal(ViolationRule.UnknownField, violation.Rule);
        Assert.Equal("invented", violation.Field);
    }

    [Fact]
    public void AnExclusiveGroupInBothDirections()
    {
        Assert.Equal(ViolationRule.ExclusiveGroupViolated, Single("nav.contents:\n  - label: One").Rule);
        Assert.Equal(
            ViolationRule.ExclusiveGroupViolated,
            Single("nav.contents:\n  - label: One\n    link: '[[A]]'\n    folder: B").Rule);
    }

    [Fact]
    public void AFieldsValueConstraint()
    {
        var violation = Single("nav.contents:\n  - label: One\n    link: not a link");

        Assert.Equal(ViolationRule.ValueConstraintFailed, violation.Rule);
        Assert.Equal("nav.contents", violation.Key);
        Assert.Equal(1, violation.EntryPosition);
        Assert.Equal("link", violation.Field);
    }

    [Fact]
    public void EntryPositionsAreOneBasedSoFindingsFromDifferentSourcesCorrelate()
    {
        var violation = Single("doc.notes:\n  - a hint\n  - another hint\n  - invented: x");

        Assert.Equal(3, violation.EntryPosition);
    }

    // ---- Agreement with the write path ----

    [Fact]
    public void ANoteTheValidatorPassesIsNotOneAnOperationRefusesOverTheSameKey()
    {
        const string block = "doc.notes:\n  - routed: skill-a\n  - a hint";
        Assert.Empty(Validate(block));

        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            $"---\n{block}\n---\nBody text.\n",
            new StubAuthority(Synthetic.OpenClass, Synthetic.ElevatedClass),
            [
                new FrontmatterOperation.AddEntry(
                    "doc.notes", new EntryPayload.Mapping([new EntryFieldValue("routed", "skill-b")]), null)
            ]));

        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void WhatTheValidatorReportsUnderAKeyIsWhatTheWritePathReportsForThatKey()
    {
        const string block = "doc.notes:\n  - routed: skill-a\n    invented: x";

        var reported = Assert.Single(Validate(block));
        var outcome = FrontmatterWrite.Apply(Synthetic.Write(
            $"---\n{block}\n---\nBody text.\n",
            new StubAuthority(Synthetic.OpenClass, Synthetic.ElevatedClass),
            [new FrontmatterOperation.AddEntry("doc.notes", new EntryPayload.Scalar("a hint"), null)]));

        Assert.Equal(reported, Assert.Single(outcome.Violations));
    }
}
