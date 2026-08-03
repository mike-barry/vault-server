using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// The advertised vocabulary as a projection of the loaded declaration. Generated from the same
/// instance the write path holds, so there is no second copy and no drift test to write - what these
/// cases assert is the projection's shape, not that two copies agree.
/// </summary>
public sealed class FrontmatterVocabularyTests
{
    private static readonly FrontmatterVocabulary Advertised = FrontmatterVocabulary.Of(Synthetic.Registry);

    private static EntryBranch Branch(string variant) =>
        Assert.Single(Advertised.Branches, b => b.Variant == variant);

    [Fact]
    public void EveryDeclaredKeyIsAdvertisedWithItsShape()
    {
        Assert.Equal(Synthetic.Registry.Keys.Count, Advertised.Keys.Count);
        Assert.Equal(KeyShape.Scalar, Assert.Single(Advertised.Keys, k => k.Key == "doc.title").Shape);
        Assert.Equal(KeyShape.BlockList, Assert.Single(Advertised.Keys, k => k.Key == "doc.notes").Shape);
    }

    [Fact]
    public void AScalarKeysConstraintIsCarriedAcross()
    {
        var type = Assert.Single(Advertised.Keys, k => k.Key == "doc.type");

        Assert.Equal("string", type.Value!.Type);
        Assert.Contains("alpha-dash", type.Value.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockListKeyCarriesNoValueOfItsOwn()
    {
        // Its entry branches carry the constraints.
        Assert.Null(Assert.Single(Advertised.Keys, k => k.Key == "doc.notes").Value);
    }

    [Fact]
    public void ThereIsOneBranchPerEntryVariant()
    {
        Assert.Equal(
            ["cited", "hint", "link", "pinned", "routed"],
            Advertised.Branches.Select(b => b.Variant).OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void TwoKeysNamingOneSchemaShareOneBranch()
    {
        // They hold one schema instance, so a client sees one branch both keys accept rather than
        // two identical ones it has to reconcile.
        Assert.Equal(["nav.contents", "nav.see_also"], Branch("link").Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["agent.instructions", "doc.notes"], Branch("routed").Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ADiscriminatingFieldIsPinnedAsPresentOnItsBranch()
    {
        var routed = Branch("routed");

        Assert.Equal("routed", routed.Discriminator);
        Assert.True(Assert.Single(routed.Fields, f => f.Name == "routed").Required);
        Assert.False(Assert.Single(routed.Fields, f => f.Name == "when").Required);
    }

    [Fact]
    public void AScalarVariantAdvertisesItsValueAndNoFields()
    {
        var hint = Branch("hint");

        Assert.Null(hint.Discriminator);
        Assert.Empty(hint.Fields);
        Assert.Equal("string", hint.Scalar!.Type);
    }

    [Fact]
    public void AFieldsConstraintIsCarriedAcross()
    {
        var link = Assert.Single(Branch("link").Fields, f => f.Name == "link");

        Assert.Contains("wikilink", link.Value.Description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhatNoClientFacingSchemaExpressesCleanlyIsCarriedInTheDescription()
    {
        // It advertises; it does not enforce. The write path remains the sole enforcement point
        // whatever a client makes of this sentence.
        var link = Branch("link").Description;

        Assert.Contains("Exactly one of link, folder", link, StringComparison.Ordinal);
        Assert.Contains("case-insensitively", link, StringComparison.Ordinal);
        Assert.Contains("cleared at once", Branch("routed").Description, StringComparison.Ordinal);
        Assert.DoesNotContain("cleared at once", Branch("pinned").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnumerationsMembersAreCarried()
    {
        var registry = new FrontmatterRegistry(
            new Dictionary<string, KeyDeclaration>(StringComparer.OrdinalIgnoreCase)
            {
                ["doc.state"] = new(KeyShape.Scalar, new EnumerationConstraint(["open", "closed"]), null, null),
            },
            [],
            new Dictionary<string, EntrySchema>(StringComparer.OrdinalIgnoreCase));

        var state = Assert.Single(FrontmatterVocabulary.Of(registry).Keys);

        Assert.Equal(["open", "closed"], state.Value!.Enum!);
    }

    [Fact]
    public void WithNoLoadedRegistryTheVariantBranchesAreSimplyAbsent()
    {
        // Advertising was never permission: edit stays listed with its ordinary argument surface, and
        // the frontmatter operations refuse at the write path where every other refusal lives.
        Assert.Same(FrontmatterVocabulary.None, FrontmatterVocabulary.Of(new RegistryState.Absent("registry.json")));
        Assert.Same(FrontmatterVocabulary.None, FrontmatterVocabulary.Of(new RegistryState.Unloadable("registry.json", "why.")));
        Assert.Empty(FrontmatterVocabulary.None.Branches);
        Assert.Empty(FrontmatterVocabulary.None.Keys);
    }

    [Fact]
    public void GenerationFromALoadedStateIsTheSameProjection()
    {
        var projected = FrontmatterVocabulary.Of(new RegistryState.Loaded(Synthetic.Registry));

        Assert.Equal(Advertised.Branches.Count, projected.Branches.Count);
        Assert.Equal(Advertised.Keys.Count, projected.Keys.Count);
    }
}
