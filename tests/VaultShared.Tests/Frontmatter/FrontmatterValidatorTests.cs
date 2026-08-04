using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Verifies <see cref="FrontmatterValidator"/>, which applies the write path's checks to a whole
/// block instead of to one key. Both paths call the same check implementations, so a note the
/// validator passes is not one an operation refuses over the same key.
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
        // Callers build findings and remedy payloads from the violations, so validation returns the
        // full list rather than a pass/fail verdict.
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
        // A malformed note does not fail a whole-vault pass, but it is still reported, because the
        // write path refuses it and the two must agree.
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

    // ---- Shape rules ----

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

    // ---- Entry schema rules ----

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
