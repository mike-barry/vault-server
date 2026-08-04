using VaultShared.Configuration;
using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// Provides synthetic declaration JSON for the loader tests. The keys are invented rather than
/// taken from any real vault, because these tests cover the loading mechanism - how a declaration
/// is expressed, validated, and refused - and not a particular vocabulary.
/// </summary>
internal static class SyntheticDeclaration
{
    /// <summary>
    /// A well-formed declaration covering every part of the format: both key shapes, a namespace
    /// rule, two keys sharing one entry schema, a scalar variant beside two discriminated ones, an
    /// exclusive group, both identity comparisons, and a wholesale-removable variant. It also
    /// contains comments and a trailing comma, which the loader accepts.
    /// </summary>
    internal const string Valid = """
        {
          "version": 1,

          "namespaces": [
            // Everything under this prefix needs the elevated class, declared or not.
            { "prefix": "synth.", "write_requires": "agentic:write" },
          ],

          "keys": {
            "plain.created": { "shape": "scalar", "value": { "kind": "date" } },
            "plain.kind": {
              "shape": "scalar",
              "value": { "kind": "charclass", "chars": "alpha-dash", "min": 1, "max": 20 },
              "note": "A character class until an authoritative table exists."
            },
            "plain.link": { "shape": "scalar", "value": { "kind": "wikilink" } },
            "plain.state": { "shape": "scalar", "value": { "kind": "enum", "values": ["open", "closed"] } },
            "plain.free": { "shape": "scalar", "value": { "kind": "string" }, "write_requires": "content:write" },

            // Both lists take the same schema, so they cannot drift apart.
            "list.first": { "shape": "block_list", "entry": "link_entry" },
            "list.second": { "shape": "block_list", "entry": "link_entry" },
            "synth.directives": { "shape": "block_list", "entry": "directive_entry" },
          },

          "entry_schemas": {

            "link_entry": {
              "variants": [
                {
                  "name": "entry",
                  "discriminated_by": "label",
                  "fields": {
                    "label":  { "required": true,  "value": { "kind": "string" } },
                    "link":   { "required": false, "value": { "kind": "wikilink" } },
                    "folder": { "required": false, "value": { "kind": "string" } }
                  },
                  "exactly_one_of": ["link", "folder"],
                  "identity": ["link", "folder"],
                  "identity_match": "case-insensitive"
                }
              ]
            },

            "directive_entry": {
              "variants": [
                { "name": "hint", "scalar": { "kind": "string" } },
                {
                  "name": "pinned",
                  "discriminated_by": "pinned",
                  "fields": { "pinned": { "required": true, "value": { "kind": "string" } } },
                  "identity": ["pinned"],
                  "identity_match": "case-sensitive"
                },
                {
                  "name": "routed",
                  "discriminated_by": "routed",
                  "fields": {
                    "routed": { "required": true,  "value": { "kind": "string" } },
                    "when":   { "required": false, "value": { "kind": "string" } }
                  },
                  "identity": ["routed"],
                  "identity_match": "case-insensitive",
                  "removable_wholesale": true
                }
              ]
            }
          }
        }
        """;

    /// <summary>
    /// Builds a minimal declaration from the given parts, for a case that turns on one part of the
    /// format.
    /// </summary>
    /// <param name="keys">The JSON object for the declaration's "keys" member.</param>
    /// <param name="schemas">The JSON object for the declaration's "entry_schemas" member.</param>
    /// <param name="namespaces">The JSON array for the declaration's "namespaces" member.</param>
    /// <returns>The declaration JSON.</returns>
    internal static string Of(string keys, string schemas = "{}", string namespaces = "[]") => $$"""
        { "version": 1, "namespaces": {{namespaces}}, "keys": {{keys}}, "entry_schemas": {{schemas}} }
        """;
}

/// <summary>
/// Verifies what the declaration loader accepts, what it refuses, and the registry it produces.
/// Every validation check runs at load rather than at first use, so an operator sees a health
/// problem naming the file instead of a refusal on an unrelated note.
/// </summary>
public sealed class FrontmatterRegistryLoaderTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-registry-").FullName;

    private static readonly OpClassRegistry OpClasses =
        OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new RecordingProblemSink());

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string json, string name = "registry.json")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private FrontmatterRegistry Loaded(string json) =>
        Assert.IsType<RegistryState.Loaded>(FrontmatterRegistryLoader.Load(Write(json), OpClasses)).Registry;

    /// <summary>Loads a declaration that is expected to be refused.</summary>
    /// <param name="json">The declaration JSON to write and load.</param>
    /// <returns>The reason the loader gave for refusing it.</returns>
    private string Refused(string json)
    {
        var state = FrontmatterRegistryLoader.Load(Write(json), OpClasses);
        return Assert.IsType<RegistryState.Unloadable>(state).Why;
    }

    // ---- Load states ----

    [Fact]
    public void Load_AWellFormedDeclarationIsLoaded()
    {
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.Equal(8, registry.Keys.Count);
        Assert.Equal(2, registry.EntrySchemas.Count);
        Assert.Equal("synth.", Assert.Single(registry.Namespaces).Prefix);
    }

    [Fact]
    public void Load_AMissingFileIsAbsentRatherThanUnloadable()
    {
        var path = Path.Combine(_tempDir, "not-there.json");

        var absent = Assert.IsType<RegistryState.Absent>(FrontmatterRegistryLoader.Load(path, OpClasses));

        // Absent and Unloadable both refuse writes, but they report differently: one names a file
        // to create, the other a file to fix.
        Assert.Equal(path, absent.Path);
    }

    [Fact]
    public void Load_UnparseableJsonIsUnloadableAndNamesTheFile()
    {
        var path = Write("{ not json");

        var unloadable = Assert.IsType<RegistryState.Unloadable>(FrontmatterRegistryLoader.Load(path, OpClasses));

        Assert.Equal(path, unloadable.Path);
        Assert.NotEmpty(unloadable.Why);
    }

    // ---- Format tolerance ----

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        // The valid declaration contains both. Nothing writes the file back, so neither has to
        // survive a read.
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.True(registry.Keys.ContainsKey("plain.created"));
    }

    // ---- The registry it builds ----

    [Fact]
    public void Load_KeysAreMatchedCaseInsensitively()
    {
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.True(registry.Keys.ContainsKey("PLAIN.Created"));
    }

    [Fact]
    public void Load_TwoKeysNamingOneSchemaHoldOneObject()
    {
        // Referring to a schema by name gives both lists the same object, so they cannot drift
        // apart.
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.Same(registry.Keys["list.first"].Entry, registry.Keys["list.second"].Entry);
        Assert.Same(registry.EntrySchemas["link_entry"], registry.Keys["list.first"].Entry);
    }

    [Fact]
    public void Load_CarriesAVariantsSelectorFieldsIdentityAndRemovability()
    {
        var registry = Loaded(SyntheticDeclaration.Valid);
        var schema = registry.EntrySchemas["directive_entry"];

        var hint = schema.Variants[0];
        Assert.Null(hint.DiscriminatedBy);
        Assert.IsType<StringConstraint>(hint.Scalar);
        Assert.Empty(hint.Fields);

        var pinned = schema.Variants[1];
        Assert.Equal("pinned", pinned.DiscriminatedBy);
        Assert.Null(pinned.Scalar);
        Assert.Equal<string[]>(["pinned"], [.. pinned.Identity]);
        Assert.Equal(StringComparison.Ordinal, pinned.IdentityMatch);
        Assert.False(pinned.RemovableWholesale);

        var routed = schema.Variants[2];
        Assert.Equal(StringComparison.OrdinalIgnoreCase, routed.IdentityMatch);
        Assert.True(routed.RemovableWholesale);
        Assert.True(routed.Fields["routed"].Required);
        Assert.False(routed.Fields["when"].Required);
    }

    [Fact]
    public void Load_CarriesTheExclusiveGroupAndTheKeysWriteAuthority()
    {
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.Equal<string[]>(["link", "folder"], [.. registry.EntrySchemas["link_entry"].Variants[0].ExactlyOneOf]);
        Assert.Equal("content:write", registry.Keys["plain.free"].WriteRequires);
        // A key that declares nothing is still covered by a namespace matching it. The union of
        // the two is computed at write time rather than folded into the loaded registry.
        Assert.Null(registry.Keys["synth.directives"].WriteRequires);
        Assert.Equal("agentic:write", registry.Namespaces[0].WriteRequires);
    }

    [Fact]
    public void Load_CarriesTheConstraintKinds()
    {
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.IsType<DateConstraint>(registry.Keys["plain.created"].Value);
        Assert.IsType<WikilinkConstraint>(registry.Keys["plain.link"].Value);
        Assert.IsType<EnumerationConstraint>(registry.Keys["plain.state"].Value);
        var charClass = Assert.IsType<CharClassConstraint>(registry.Keys["plain.kind"].Value);
        Assert.Equal(CharacterClass.AlphaDash, charClass.Class);
        Assert.Equal((1, 20), (charClass.Min, charClass.Max));
        Assert.Equal("A character class until an authoritative table exists.", registry.Keys["plain.kind"].Note);
    }

    [Fact]
    public void Load_ABlockListKeyCarriesNoValueConstraintOfItsOwn()
    {
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.Equal(KeyShape.BlockList, registry.Keys["list.first"].Shape);
        Assert.Null(registry.Keys["list.first"].Value);
        Assert.Null(registry.Keys["plain.created"].Entry);
    }

    // ---- Load-time checks ----

    [Fact]
    public void Load_RefusesAnUnknownConstraintKind()
    {
        var why = Refused(SyntheticDeclaration.Of("""{ "k": { "shape": "scalar", "value": { "kind": "regex", "pattern": ".*" } } }"""));

        // The set of constraint kinds is closed, and there is deliberately no raw-regex kind to
        // fall back to.
        Assert.Contains("unknown constraint kind", why);
    }

    [Fact]
    public void Load_RefusesCharClassBoundsThatCross()
    {
        var why = Refused(SyntheticDeclaration.Of(
            """{ "k": { "shape": "scalar", "value": { "kind": "charclass", "chars": "alnum", "min": 9, "max": 2 } } }"""));

        Assert.Contains("bounds cross", why);
    }

    [Fact]
    public void Load_RefusesAnUnknownCharacterClass()
    {
        var why = Refused(SyntheticDeclaration.Of(
            """{ "k": { "shape": "scalar", "value": { "kind": "charclass", "chars": "utf8-anything", "min": 1, "max": 2 } } }"""));

        Assert.Contains("unknown character class", why);
    }

    [Fact]
    public void Load_RefusesAnEmptyEnumeration()
    {
        var why = Refused(SyntheticDeclaration.Of("""{ "k": { "shape": "scalar", "value": { "kind": "enum", "values": [] } } }"""));

        Assert.Contains("no values", why);
    }

    [Theory]
    [InlineData("""{ "k": { "shape": "block_list", "entry": "s", "value": { "kind": "string" } } }""", "entry schema carries")]
    [InlineData("""{ "k": { "shape": "scalar", "entry": "s" } }""", "only a block_list")]
    [InlineData("""{ "k": { "shape": "block_list" } }""", "names no \"entry\"")]
    public void Load_RefusesAShapeAndConstraintThatContradict(string keys, string expected)
    {
        var schemas = """{ "s": { "variants": [ { "name": "v", "scalar": { "kind": "string" } } ] } }""";

        Assert.Contains(expected, Refused(SyntheticDeclaration.Of(keys, schemas)));
    }

    [Fact]
    public void Load_RefusesABlockListKeyNamingASchemaThatDoesNotExist()
    {
        var why = Refused(SyntheticDeclaration.Of("""{ "k": { "shape": "block_list", "entry": "absent_schema" } }"""));

        Assert.Contains("is not declared", why);
    }

    [Theory]
    [InlineData("\"identity\": [\"nope\"], \"identity_match\": \"case-sensitive\"")]
    [InlineData("\"exactly_one_of\": [\"a\", \"nope\"]")]
    public void Load_RefusesAnIdentityOrGroupNamingAnUndeclaredField(string clause)
    {
        var schemas = $$"""
            { "s": { "variants": [ {
                "name": "v", "discriminated_by": "a",
                "fields": { "a": { "required": true, "value": { "kind": "string" } } },
                {{clause}}
            } ] } }
            """;

        Assert.Contains("does not declare as a field", Refused(SyntheticDeclaration.Of("{}", schemas)));
    }

    [Fact]
    public void Load_RefusesTwoVariantsSharingADiscriminatingField()
    {
        // A mapping carrying the shared field would match both variants, which is refused rather
        // than settled by an invented precedence rule.
        var schemas = """
            { "s": { "variants": [
              { "name": "one", "discriminated_by": "a", "fields": { "a": { "required": true } } },
              { "name": "two", "discriminated_by": "a", "fields": { "a": { "required": true } } }
            ] } }
            """;

        Assert.Contains("two variants discriminated by", Refused(SyntheticDeclaration.Of("{}", schemas)));
    }

    [Theory]
    [InlineData("""{ "name": "v", "discriminated_by": "a", "scalar": { "kind": "string" }, "fields": { "a": { "required": true } } }""", "a selector is one or the other")]
    [InlineData("""{ "name": "v", "fields": { "a": { "required": true } } }""", "nothing selects it")]
    [InlineData("""{ "name": "v", "scalar": { "kind": "string" }, "fields": { "a": { "required": true } } }""", "a scalar variant is a plain value")]
    [InlineData("""{ "name": "v", "discriminated_by": "missing", "fields": { "a": { "required": true } } }""", "does not declare as a field")]
    public void Load_RefusesAVariantWhoseSelectorIsNotOneThing(string variant, string expected)
    {
        var schemas = $$"""{ "s": { "variants": [ {{variant}} ] } }""";

        Assert.Contains(expected, Refused(SyntheticDeclaration.Of("{}", schemas)));
    }

    [Fact]
    public void Load_RefusesAnOpClassTheOpClassConfigurationDoesNotDefine()
    {
        // Checked against a real op-class registry. A key requiring a class nothing defines could
        // never be written, so it would look like a permanently locked key rather than the
        // misconfiguration it is.
        var why = Refused(SyntheticDeclaration.Of("""{ "k": { "shape": "scalar", "write_requires": "invented:class" } }"""));

        Assert.Contains("which the op-class configuration does not define", why);
    }

    [Fact]
    public void Load_RefusesANamespaceRequiringAnUndefinedOpClass()
    {
        var why = Refused(SyntheticDeclaration.Of(
            "{}", "{}", """[ { "prefix": "x.", "write_requires": "invented:class" } ]"""));

        Assert.Contains("which the op-class configuration does not define", why);
    }

    [Fact]
    public void Load_AcceptsAnOpClassTheConfigurationDoesDefine()
    {
        var registry = Loaded(SyntheticDeclaration.Of("""{ "k": { "shape": "scalar", "write_requires": "agentic:write" } }"""));

        Assert.Equal("agentic:write", registry.Keys["k"].WriteRequires);
    }

    // ---- Allowlist validation ----

    [Theory]
    [InlineData("""{ "verzion": 1, "keys": {} }""", "unrecognized top-level key")]
    [InlineData("""{ "version": 2, "keys": {} }""", "\"version\" must be 1")]
    [InlineData("""{ "keys": { "k": { "shape": "scalar", "valu": { "kind": "string" } } } }""", "unrecognized key")]
    [InlineData("""{ "keys": { "k": { "shape": "list" } } }""", "unrecognized shape")]
    [InlineData("""{ "keys": { "k": {} } }""", "declares no \"shape\"")]
    [InlineData("""{ "namespaces": [ { "write_requires": "content:write" } ] }""", "no non-empty \"prefix\"")]
    [InlineData("""{ "namespaces": [ { "prefix": "x." } ] }""", "declares no \"write_requires\"")]
    [InlineData("""{ "entry_schemas": { "s": { "variants": [] } } }""", "declares no variants")]
    [InlineData("""{ "entry_schemas": { "s": { "variants": [ { "scalar": { "kind": "string" } } ] } } }""", "no \"name\"")]
    public void Load_RefusesAMalformedDeclaration(string json, string expected)
    {
        // The loader allowlists the members it knows, as the other configuration parsers do, so a
        // misspelled field is refused rather than silently ignored.
        Assert.Contains(expected, Refused(json));
    }

    [Fact]
    public void Load_RefusesAnIdentityWithNoDeclaredComparison()
    {
        // There is no safe default. A file identity compares the way the filesystem resolves it
        // and a line of prose compares exactly, so guessing would let a scan and an operation
        // disagree about whether an entry is already present.
        var schemas = """
            { "s": { "variants": [ {
              "name": "v", "discriminated_by": "a",
              "fields": { "a": { "required": true } },
              "identity": ["a"]
            } ] } }
            """;

        Assert.Contains("no \"identity_match\"", Refused(SyntheticDeclaration.Of("{}", schemas)));
    }

    [Fact]
    public void Load_RefusesAKeyDeclaredTwiceUnderTwoSpellings()
    {
        var why = Refused("""{ "keys": { "a.b": { "shape": "scalar" }, "A.B": { "shape": "scalar" } } }""");

        Assert.Contains("declared twice", why);
    }

    [Fact]
    public void Load_RefusesTwoScalarVariantsInOneSchema()
    {
        var schemas = """
            { "s": { "variants": [
              { "name": "one", "scalar": { "kind": "string" } },
              { "name": "two", "scalar": { "kind": "date" } }
            ] } }
            """;

        Assert.Contains("two scalar variants", Refused(SyntheticDeclaration.Of("{}", schemas)));
    }
}
