using System.ComponentModel;
using System.Text.Json;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests that one <see cref="JsonSerializerOptions"/> instance governs the advertised schema, the
/// argument binder, structured results and refusal payloads. Losing the naming policy renames every
/// multi-word name on both sides at once, so the schema and the binder would still agree with each
/// other while every existing caller broke. These cases pin the names themselves.
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

    /// <summary>A result member carries the same style of name as an argument, so a caller never has
    /// to translate between the two.</summary>
    [Fact]
    public void ResultNames_UseTheSamePolicyAsArgumentNames()
    {
        var json = JsonSerializer.Serialize<object>(new RoundTripResult("abc", true), NativeToolArguments.Options);

        Assert.Equal("""{"content_hash":"abc","frontmatter_changed":true}""", json);
    }

    /// <summary>A tool that takes no arguments advertises an object with an empty <c>properties</c>
    /// member rather than the bare <c>{"type":"object"}</c> the schema exporter produces for a
    /// record with no members.</summary>
    [Fact]
    public void NoArguments_AdvertisesAnEmptyPropertiesObject()
    {
        var schema = NativeToolArguments.SchemaFor(typeof(NoArguments));

        Assert.Equal("""{"type":"object","properties":{},"additionalProperties":false}""", JsonSerializer.Serialize(schema));
        Assert.IsType<NoArguments>(NativeToolArguments.Bind(typeof(NoArguments), new Dictionary<string, JsonElement>()));
    }
}
