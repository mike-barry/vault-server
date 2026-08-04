using VaultShared.Configuration;
using VaultShared.Frontmatter;

namespace VaultShared.Tests;

/// <summary>
/// A synthetic declaration the loader cases are written against. The vault's own vocabulary is not
/// this suite's subject and must not become one: what is asserted here is the mechanism - how a
/// declaration is expressed, validated, and refused - so the keys below are invented, and a
/// declaration the vault really carries would prove nothing this does not.
/// </summary>
internal static class SyntheticDeclaration
{
    /// <summary>
    /// One well-formed declaration exercising every part of the shape: both key shapes, a namespace
    /// rule, two keys sharing one entry schema, a scalar variant beside two discriminated ones, an
    /// exclusive group, both identity comparisons, and a wholesale-removable variant. Written with
    /// comments and a trailing comma, which the format tolerates on read.
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

    /// <summary>A declaration holding <paramref name="keys"/> and <paramref name="schemas"/> and
    /// nothing else, for a case that turns on one part of the shape.</summary>
    internal static string Of(string keys, string schemas = "{}", string namespaces = "[]") => $$"""
        { "version": 1, "namespaces": {{namespaces}}, "keys": {{keys}}, "entry_schemas": {{schemas}} }
        """;
}

/// <summary>
/// What the declaration loader accepts, what it refuses, and what it answers. Every check the
/// registry spec requires at load is a case here, because a check that runs at first use instead is
/// one an operator meets as a refusal on an unrelated note rather than as a health problem naming
/// the file.
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

    /// <summary>Loads a declaration expected to be refused, and returns the reason it gave.</summary>
    private string Refused(string json)
    {
        var state = FrontmatterRegistryLoader.Load(Write(json), OpClasses);
        return Assert.IsType<RegistryState.Unloadable>(state).Why;
    }

    // ---- The three states ----

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

        // Absent and Unloadable refuse identically and report differently: one is a file to put
        // there, the other a file to fix.
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
        // The valid declaration carries both. They serve the operator and are not preserved
        // through a read, which the read-only rule makes moot.
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.True(registry.Keys.ContainsKey("plain.created"));
    }

    // ---- The shape it builds ----

    [Fact]
    public void Load_KeysAreMatchedCaseInsensitively()
    {
        var registry = Loaded(SyntheticDeclaration.Valid);

        Assert.True(registry.Keys.ContainsKey("PLAIN.Created"));
    }

    [Fact]
    public void Load_TwoKeysNamingOneSchemaHoldOneObject()
    {
        // The property the name-reference form exists to produce: the two lists cannot drift,
        // because there is nothing to drift from.
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
        // A key declaring nothing is not exempt from a namespace that matches it - the requirement
        // is a union, computed where it is needed rather than folded in here.
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

        // The refusal that keeps the vocabulary closed - and there is no raw-regex kind to fall
        // back to, by design.
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
        // A mapping carrying the shared field would match both, and matching two is a violation
        // rather than a precedence rule to invent.
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
        // The cross-file check, run against a real op-class registry. A protection naming an
        // authority nothing defines can never be satisfied, so it reads as a locked key rather
        // than as the misconfiguration it is.
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

    // ---- The allowlist posture ----

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
        // A positive allowlist, the same posture the retained configuration parsers take: a typo'd
        // field cannot read as a declaration someone made.
        Assert.Contains(expected, Refused(json));
    }

    [Fact]
    public void Load_RefusesAnIdentityWithNoDeclaredComparison()
    {
        // No answer is right by default: a file identity compares as the filesystem resolves it and
        // a line of prose compares exactly, and guessing either makes a scan and an operation
        // disagree about whether an entry is already there.
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
