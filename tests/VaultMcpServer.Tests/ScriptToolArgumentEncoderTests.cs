using System.Text.Json;
using VaultMcpServer.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers <see cref="ScriptToolParameter.EncodesStructuredArgument"/>, which decides from a
/// parameter's side-car schema alone whether its argument is encoded, and
/// <see cref="ScriptToolArgumentEncoder"/>, which decides what to do with the value that arrives.
/// </summary>
/// <remarks>
/// <see cref="ScriptToolDispatcherTests"/> covers the behaviour these two produce against the real
/// host. These tests pin the schema shapes a hand-written <c>.SCHEMA</c> block is allowed to take,
/// including several the mechanism permits but no current tool writes.
/// </remarks>
public sealed class ScriptToolArgumentEncoderTests
{
    private static ScriptToolParameter Parameter(string? schemaJson, string jsonType = "string") =>
        new(
            Name: "p",
            JsonType: jsonType,
            Required: false,
            Description: "A parameter.",
            AllowedValues: null,
            Schema: schemaJson is null ? null : JsonDocument.Parse(schemaJson).RootElement.Clone());

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    // A plain non-string type name is the ordinary case - a parameter carrying an array of objects.
    [InlineData("""{"type":"array"}""", true)]
    [InlineData("""{"type":"object"}""", true)]
    // A side-car declaring the type the .NET parameter already has asks for nothing.
    [InlineData("""{"type":"string"}""", false)]
    // A union declares a non-string type if any member is one. ["string","array"] works because a
    // string arrival passes verbatim and an array arrival encodes. "null" is the absence of a value
    // rather than a shape, so an optional-string union reads the same as plain "string".
    [InlineData("""{"type":["array","null"]}""", true)]
    [InlineData("""{"type":["string","array"]}""", true)]
    [InlineData("""{"type":["string","null"]}""", false)]
    [InlineData("""{"type":["null"]}""", false)]
    // No top-level type at all - a bare oneOf, enum, const or $ref. It declares no type, so it
    // declares no non-string type and is not encoded. That is the recoverable direction, because a
    // caller can always send the already-encoded string.
    [InlineData("""{"oneOf":[{"type":"array"},{"type":"object"}]}""", false)]
    [InlineData("""{"enum":["a","b"]}""", false)]
    [InlineData("""{"$ref":"#/$defs/op"}""", false)]
    // Malformed as a schema: `type` that is neither a string nor an array of them reads as absent.
    [InlineData("""{"type":7}""", false)]
    [InlineData("""{"type":{"const":"array"}}""", false)]
    // JSON Schema's type vocabulary is lowercase and case-sensitive, so "Array" is not the keyword.
    // It is still not "string", so it still encodes. The only values affected are ones a [string]
    // parameter could not have received anyway.
    [InlineData("""{"type":"Array"}""", true)]
    public void EncodesStructuredArgument_ReadsTheTopLevelTypeOfTheSideCar(string schemaJson, bool expected) =>
        Assert.Equal(expected, Parameter(schemaJson).EncodesStructuredArgument);

    /// <summary>Verifies that a parameter with no side-car schema never encodes. This is the common
    /// case and must stay inert.</summary>
    [Fact]
    public void EncodesStructuredArgument_WithNoSideCar_IsFalse() =>
        Assert.False(Parameter(null).EncodesStructuredArgument);

    /// <summary>
    /// Verifies that a parameter whose .NET type is not <c>string</c> never encodes. A
    /// <c>[string[]]</c> parameter already binds a real array through the splat, and encoding it
    /// would replace a working binding with a string the script has no code to parse.
    /// </summary>
    [Fact]
    public void EncodesStructuredArgument_WhenTheDotNetTypeIsNotString_IsFalse() =>
        Assert.False(Parameter("""{"type":"array"}""", jsonType: "array").EncodesStructuredArgument);

    [Fact]
    public void BindArgument_StructuredValueForAnEncodingParameter_IsSerializedToJsonText()
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter("""{"type":"array"}"""), Value("""[{"op":"set_field"}]"""));
        Assert.Equal("""[{"op":"set_field"}]""", Assert.IsType<string>(bound));
    }

    /// <summary>Verifies that a string value is passed through untouched, so an already-encoded
    /// payload is never encoded twice.</summary>
    [Fact]
    public void BindArgument_StringValueForAnEncodingParameter_PassesThroughVerbatim()
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter("""{"type":"array"}"""), Value("""  "[{\"op\":\"set_field\"}]"  """));
        Assert.Equal("""[{"op":"set_field"}]""", Assert.IsType<string>(bound));
    }

    /// <summary>Verifies that a structured value for a non-encoding parameter takes the plain
    /// conversion, becoming an <c>object[]</c> or dictionary passed through the splat.</summary>
    [Fact]
    public void BindArgument_StructuredValueForANonEncodingParameter_ConvertsPlainly()
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value("""["a","b"]"""));
        Assert.Equal(new object?[] { "a", "b" }, (object?[])bound!);
    }

    /// <summary>
    /// Verifies that a null parameter converts plainly rather than throwing. The dispatcher refuses
    /// an argument matching no advertised parameter as <c>invalid_argument</c> before reaching here,
    /// so this case only has to fail safely.
    /// </summary>
    [Fact]
    public void BindArgument_WithNoMatchingParameter_ConvertsPlainly() =>
        Assert.Equal("text", ScriptToolArgumentEncoder.BindArgument(null, Value("\"text\"")));

    /// <summary>
    /// Verifies that a scalar value is never serialized, even for an encoding parameter. Only an
    /// array or an object is serialized, so a side-car declaring <c>"integer"</c> or
    /// <c>"boolean"</c> never turns its value into JSON text.
    /// </summary>
    [Fact]
    public void BindArgument_ScalarForAnEncodingParameter_IsNotSerialized()
    {
        var parameter = Parameter("""{"type":"array"}""");
        // Pinned to the exact CLR type, because the plain conversion keeps an integral value
        // integral. A looser assertion would also pass against a branch that widened every number
        // to double.
        Assert.IsType<long>(ScriptToolArgumentEncoder.BindArgument(parameter, Value("42")));
        Assert.Equal(true, ScriptToolArgumentEncoder.BindArgument(parameter, Value("true")));
        Assert.Null(ScriptToolArgumentEncoder.BindArgument(parameter, Value("null")));
    }

    /// <summary>
    /// Verifies that an integral JSON number converts to <c>long</c> rather than <c>double</c>.
    /// </summary>
    /// <param name="json">The JSON number to convert.</param>
    /// <remarks>
    /// The conversion reads <c>TryGetInt64(out var l) ? l : GetDouble()</c>. A conditional
    /// expression takes its operands' best common type, which here is <c>double</c> because
    /// <c>long</c> converts implicitly to it and not the reverse, so without an explicit
    /// <c>object</c> on the integral arm the <c>long</c> is widened away.
    ///
    /// No current script tool sees the difference, because every parameter is <c>[string]</c>,
    /// <c>[bool]</c> or <c>[string[]]</c> and a whole-valued double stringifies the same way under
    /// invariant formatting. It matters as soon as a tool declares <c>[int]</c> or <c>[long]</c>,
    /// which <c>ScriptToolLoader.MapJsonType</c> advertises as JSON <c>"integer"</c>.
    /// </remarks>
    [Theory]
    [InlineData("42")]
    [InlineData("-7")]
    [InlineData("0")]
    // The boundaries of what TryGetInt64 accepts. Past 2^53 a double cannot represent consecutive
    // integers, so widening would silently lose data.
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    public void BindArgument_IntegralJsonNumber_ConvertsToLong(string json)
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value(json));
        Assert.Equal(long.Parse(json), Assert.IsType<long>(bound));
    }

    /// <summary>
    /// Verifies the other arm of the same branch. A number <c>TryGetInt64</c> refuses still converts
    /// to <c>double</c>, so the conversion accepts everything it did.
    /// </summary>
    /// <param name="json">The JSON number to convert.</param>
    /// <param name="expected">The value it must convert to.</param>
    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("-0.25", -0.25)]
    [InlineData("1e3", 1000d)]
    // Integral in value but outside Int64's range, so TryGetInt64 refuses it and double is the only
    // branch left. Without this case the rule would read as "integral text means long".
    [InlineData("9223372036854775808", 9223372036854775808d)]
    public void BindArgument_FractionalOrOutOfRangeJsonNumber_ConvertsToDouble(string json, double expected)
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value(json));
        Assert.Equal(expected, Assert.IsType<double>(bound));
    }

    /// <summary>Verifies that nested numbers take the same branch, so an array or object argument
    /// carries the CLR types into the <c>object?[]</c> and dictionary the splat hands
    /// PowerShell.</summary>
    [Fact]
    public void BindArgument_NumbersNestedInStructure_KeepTheirClrTypes()
    {
        var array = (object?[])ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value("[1, 1.5]"))!;
        Assert.IsType<long>(array[0]);
        Assert.IsType<double>(array[1]);

        var map = (Dictionary<string, object?>)ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value("""{"whole":1,"fraction":1.5}"""))!;
        Assert.IsType<long>(map["whole"]);
        Assert.IsType<double>(map["fraction"]);
    }
}
