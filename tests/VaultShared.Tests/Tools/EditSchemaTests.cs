using System.Text.Json;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>edit</c>'s advertised argument surface, and the seam where a vault's own entry vocabulary
/// reaches it.
///
/// One record is the whole statement of the argument surface, so everything but the entry branch is
/// derived from it. The branch is the one part that is per vault, because the declaration is - and
/// it advertises rather than enforces: what a payload satisfying it may still be refused for is the
/// write path's.
/// </summary>
public sealed class EditSchemaTests
{
    private static JsonElement Plain() => NativeToolArguments.SchemaFor(typeof(EditArguments));

    private static JsonElement Entry(JsonElement schema) =>
        schema.GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("properties").GetProperty("entry");

    // ---- Derived from the record ----

    [Fact]
    public void EveryArgumentIsAdvertisedUnderItsWireName()
    {
        var properties = Plain().GetProperty("properties");

        foreach (var name in new[] { "path", "vault", "operations", "body", "expected_content_hash" })
            Assert.True(properties.TryGetProperty(name, out _), name);
    }

    /// <summary>The operand alone. Everything else carries a default, and requiredness in the
    /// derived schema is a member carrying none.</summary>
    [Fact]
    public void OnlyTheOperandIsAdvertisedRequired()
    {
        Assert.Equal(["path"], Plain().GetProperty("required").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>A structured argument stays structured: the operations are an array of objects on
    /// the wire, not a caller-encoded string and not flattened into siblings.</summary>
    [Fact]
    public void TheOperationsAreAdvertisedAsAnArrayOfObjects()
    {
        var operations = Plain().GetProperty("properties").GetProperty("operations");

        Assert.Equal("array", operations.GetProperty("type").GetString());
        Assert.Equal("object", operations.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(["op"], operations.GetProperty("items").GetProperty("required").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>An anchor reuses the operation's own shape, so the schema carries it as a nested
    /// object rather than as a second statement of the same members.</summary>
    [Fact]
    public void TheAnchorIsAdvertisedOnTheOperation()
    {
        Assert.True(Plain().GetProperty("properties").GetProperty("operations").GetProperty("items")
            .GetProperty("properties").TryGetProperty("anchor", out _));
    }

    // ---- The per-vault branch ----

    /// <summary>
    /// A vault whose declaration did not load advertises the ordinary argument surface with its
    /// variant branches simply absent. Advertising was never permission: what refuses a frontmatter
    /// write with no declaration is the write, and the listing does not change with the failure.
    /// </summary>
    [Fact]
    public void WithNoLoadedDeclaration_TheEntryBranchesAreSimplyAbsent()
    {
        var schema = EditTool.Schema(FrontmatterVocabulary.Of(new RegistryState.Absent("declaration.json")));

        Assert.False(Entry(schema).TryGetProperty("anyOf", out _));
        Assert.Equal(
            Plain().GetProperty("properties").GetProperty("path").ToString(),
            schema.GetProperty("properties").GetProperty("path").ToString());
    }

    [Fact]
    public void WithALoadedDeclaration_EveryVariantIsOneBranch()
    {
        var branches = Entry(EditTool.Schema(FrontmatterVocabulary.Of(Synthetic.Registry))).GetProperty("anyOf");

        Assert.Equal(
            ["hint", "pinned", "routed", "cited", "link"],
            branches.EnumerateArray().Select(b => b.GetProperty("title").GetString()));
    }

    /// <summary>Discrimination is by a field's presence, so the discriminating field is pinned by
    /// being required on its branch - which is what presence-based discrimination becomes in a
    /// client-facing schema.</summary>
    [Fact]
    public void TheDiscriminatingFieldIsRequiredOnItsBranch()
    {
        var routed = Branch("routed");

        Assert.Equal(["routed"], routed.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        Assert.True(routed.GetProperty("properties").TryGetProperty("when", out _));
    }

    /// <summary>A variant that is a bare value is one branch of the alternation like any other,
    /// rather than a shape a client has to know to spell differently.</summary>
    [Fact]
    public void AScalarVariantIsABranchThatIsNotAnObject()
    {
        Assert.Equal("string", Branch("hint").GetProperty("type").GetString());
    }

    /// <summary>Two keys naming one schema hold one object, so the branch is one branch that both
    /// keys accept rather than two a client has to reconcile.</summary>
    [Fact]
    public void ABranchNamesEveryKeyThatAcceptsIt()
    {
        var description = Branch("link").GetProperty("description").GetString()!;

        Assert.Contains("nav.contents", description, StringComparison.Ordinal);
        Assert.Contains("nav.see_also", description, StringComparison.Ordinal);
    }

    /// <summary>What no client-facing schema expresses cleanly rides the description: the exclusive
    /// group, what addresses an entry and how it compares, and whether the variant clears
    /// wholesale. Each is enforced at the write path whatever a client makes of the sentence.</summary>
    [Fact]
    public void WhatTheSchemaCannotExpressIsCarriedInTheDescription()
    {
        Assert.Contains("Exactly one of", Branch("link").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("cleared at once", Branch("routed").GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>A field's constraint reaches the client where it has a clean expression, and its
    /// wording is the one a violation would use.</summary>
    [Fact]
    public void AFieldsValueConstraintIsCarriedAcross()
    {
        Assert.Contains(
            "wikilink",
            Branch("link").GetProperty("properties").GetProperty("link").GetProperty("description").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Branch(string variant) =>
        Entry(EditTool.Schema(FrontmatterVocabulary.Of(Synthetic.Registry)))
            .GetProperty("anyOf")
            .EnumerateArray()
            .Single(b => b.GetProperty("title").GetString() == variant);
}
