using System.ComponentModel;
using System.Text.Json;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// One <see cref="JsonSerializerOptions"/> instance governs the advertised schema, the binder, the
/// structured result and the refusal payload. The failure it guards against is silent in exactly one
/// direction: dropping the naming policy renames every multi-word name on both sides at once, so the
/// schema and the binder stay consistent with each other while every existing caller breaks.
/// </summary>
public sealed class ToolSerializationTests
{
    private sealed record RoundTripArguments(
        [property: Description("A multi-word member, so the naming policy has something to do.")]
        string? ExpectedContentHash = null,
        bool WithContentHash = false) : IToolArguments
    {
        public ArgumentFault? Validate() => null;
    }

    private sealed record RoundTripResult(string ContentHash, bool FrontmatterChanged);

    /// <summary>The name a caller sends is the name the schema advertises.</summary>
    [Fact]
    public void AdvertisedNamesAndBoundNames_AreTheSame()
    {
        var advertised = NativeToolArguments.SchemaFor(typeof(RoundTripArguments))
            .GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["expected_content_hash", "with_content_hash"], advertised);

        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"expected_content_hash":"abc","with_content_hash":true}""")!;
        var bound = Assert.IsType<RoundTripArguments>(NativeToolArguments.Bind(typeof(RoundTripArguments), values));

        Assert.Equal("abc", bound.ExpectedContentHash);
        Assert.True(bound.WithContentHash);
    }

    /// <summary>And the name a result carries is the same name, so nothing translates between a sent
    /// name and a returned one.</summary>
    [Fact]
    public void ResultNames_UseTheSamePolicyAsArgumentNames()
    {
        var json = JsonSerializer.Serialize<object>(new RoundTripResult("abc", true), NativeToolArguments.Options);

        Assert.Equal("""{"content_hash":"abc","frontmatter_changed":true}""", json);
    }

    /// <summary>A tool taking no arguments advertises an object with an empty <c>properties</c>
    /// member, not a bare <c>{"type":"object"}</c> - which is what the exporter emits for a record
    /// with no members.</summary>
    [Fact]
    public void NoArguments_AdvertisesAnEmptyPropertiesObject()
    {
        var schema = NativeToolArguments.SchemaFor(typeof(NoArguments));

        Assert.Equal("""{"type":"object","properties":{}}""", JsonSerializer.Serialize(schema));
        Assert.IsType<NoArguments>(NativeToolArguments.Bind(typeof(NoArguments), new Dictionary<string, JsonElement>()));
    }
}
