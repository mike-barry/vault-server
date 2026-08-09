using System.Text.Json;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the JSON schema <c>edit</c> advertises, and the point where a vault's own entry vocabulary
/// enters it.
///
/// Every part of the schema except the entry branches is derived from the <see cref="EditArguments"/>
/// record. The entry branches vary per vault, because the frontmatter declaration does. The schema
/// only advertises: a payload that satisfies it can still be refused by the write path.
/// </summary>
public sealed class EditSchemaTests
{
    private static JsonElement Plain() => NativeToolArguments.SchemaFor(typeof(EditArguments));

    private static JsonElement Entry(JsonElement schema) =>
        schema.GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("properties").GetProperty("entry");

    // ---- Derived from the arguments record ----

    /// <summary>The advertised set is exactly these five names, in this order. An argument the record
    /// no longer declares and one it declares under a different spelling are both changes every
    /// caller sees, and the order is the order a model reads the arguments in.</summary>
    [Fact]
    public void EveryArgumentIsAdvertisedUnderItsWireName()
    {
        var properties = Plain().GetProperty("properties");

        Assert.Equal(
            ["path", "vault", "operations", "body", "expected_content_hash"],
            properties.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>Only <c>path</c> is required. A member is advertised as required when it has no
    /// default, and every other member has one.</summary>
    [Fact]
    public void OnlyTheOperandIsAdvertisedRequired()
    {
        Assert.Equal(["path"], Plain().GetProperty("required").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>The operations are advertised as an array of objects, not as an encoded string and
    /// not flattened into sibling arguments.</summary>
    [Fact]
    public void TheOperationsAreAdvertisedAsAnArrayOfObjects()
    {
        var operations = Plain().GetProperty("properties").GetProperty("operations");

        Assert.Equal("array", operations.GetProperty("type").GetString());
        Assert.Equal("object", operations.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(["op"], operations.GetProperty("items").GetProperty("required").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>An anchor reuses the operation's own shape, so it is advertised as a nested object
    /// on the operation rather than as a second copy of the same members.</summary>
    [Fact]
    public void TheAnchorIsAdvertisedOnTheOperation()
    {
        Assert.True(Plain().GetProperty("properties").GetProperty("operations").GetProperty("items")
            .GetProperty("properties").TryGetProperty("anchor", out _));
    }

    // ---- The per-vault branch ----

    /// <summary>
    /// A vault whose declaration failed to load advertises the ordinary argument surface with the
    /// entry variant branches absent. The rest of the schema is unchanged, because the refusal of a
    /// frontmatter write with no declaration comes from the write path, not from the schema.
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

    /// <summary>A variant is recognized by the presence of one field, and the schema expresses that
    /// by listing the field as required on its branch.</summary>
    [Fact]
    public void TheDiscriminatingFieldIsRequiredOnItsBranch()
    {
        var routed = Branch("routed");

        Assert.Equal(["routed"], routed.GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        Assert.True(routed.GetProperty("properties").TryGetProperty("when", out _));
    }

    /// <summary>A variant that is a bare scalar appears as a branch of type <c>string</c>, alongside
    /// the object branches.</summary>
    [Fact]
    public void AScalarVariantIsABranchThatIsNotAnObject()
    {
        Assert.Equal("string", Branch("hint").GetProperty("type").GetString());
    }

    /// <summary>Two keys that share an entry schema produce a single branch, whose description names
    /// both keys.</summary>
    [Fact]
    public void ABranchNamesEveryKeyThatAcceptsIt()
    {
        var description = Branch("link").GetProperty("description").GetString()!;

        Assert.Contains("nav.contents", description, StringComparison.Ordinal);
        Assert.Contains("nav.see_also", description, StringComparison.Ordinal);
    }

    /// <summary>Rules JSON Schema cannot express are stated in the branch description instead: which
    /// fields are mutually exclusive, what identifies an entry and how it is compared, and whether
    /// the variant is cleared as a whole. The write path enforces each of them regardless of what a
    /// client does with the description.</summary>
    [Fact]
    public void WhatTheSchemaCannotExpressIsCarriedInTheDescription()
    {
        Assert.Contains("Exactly one of", Branch("link").GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("cleared at once", Branch("routed").GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>A declared constraint on a field reaches the client in that field's description,
    /// worded as the refusal for violating it would word it.</summary>
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
