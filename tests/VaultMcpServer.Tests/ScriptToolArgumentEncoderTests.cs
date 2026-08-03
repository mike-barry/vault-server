using System.Text.Json;
using VaultMcpServer.Tools;

namespace VaultMcpServer.Tests;

/// <summary>
/// The encode signal itself, read off a parameter's side-car schema rather than off any per-tool
/// table (<see cref="ScriptToolParameter.EncodesStructuredArgument"/>), and the arrival-shape rule
/// layered on top of it (<see cref="ScriptToolArgumentEncoder"/>). The end-to-end behaviour these
/// two produce is covered against the real host in <see cref="ScriptToolDispatcherTests"/>; this
/// file pins the schema shapes a hand-authored <c>.SCHEMA</c> block is allowed to take, several of
/// which no landed tool writes today but the mechanism permits.
/// </summary>
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
    // A union declares one if any member is a non-string type. ["string","array"] is coherent under
    // the arrival-shape rule: a string passes verbatim, an array encodes. "null" is the absence of
    // a value rather than a shape, so the idiomatic optional-string union reads like plain "string".
    [InlineData("""{"type":["array","null"]}""", true)]
    [InlineData("""{"type":["string","array"]}""", true)]
    [InlineData("""{"type":["string","null"]}""", false)]
    [InlineData("""{"type":["null"]}""", false)]
    // No top-level type at all - a bare oneOf, enum, const, or $ref. It declares no type, so it
    // declares no non-string type: not encoded, which is the recoverable direction (a caller can
    // always send the already-encoded string).
    [InlineData("""{"oneOf":[{"type":"array"},{"type":"object"}]}""", false)]
    [InlineData("""{"enum":["a","b"]}""", false)]
    [InlineData("""{"$ref":"#/$defs/op"}""", false)]
    // Malformed as a schema: `type` that is neither a string nor an array of them reads as absent.
    [InlineData("""{"type":7}""", false)]
    [InlineData("""{"type":{"const":"array"}}""", false)]
    // JSON Schema's type vocabulary is lowercase and case-sensitive, so "Array" is not the keyword.
    // It is still "not string" and so still encodes - the conservative reading would be to refuse
    // it, but the arrival-shape rule means the only values affected are ones a [string] parameter
    // could not have received anyway.
    [InlineData("""{"type":"Array"}""", true)]
    public void EncodesStructuredArgument_ReadsTheTopLevelTypeOfTheSideCar(string schemaJson, bool expected) =>
        Assert.Equal(expected, Parameter(schemaJson).EncodesStructuredArgument);

    /// <summary>No side-car is the overwhelmingly common case and the one that must stay inert.</summary>
    [Fact]
    public void EncodesStructuredArgument_WithNoSideCar_IsFalse() =>
        Assert.False(Parameter(null).EncodesStructuredArgument);

    /// <summary>
    /// The other half of the signal. A parameter declared <c>[string[]]</c> already binds a real
    /// array through the splat, so encoding it would replace a working binding with a string the
    /// script has no parse for - the derived .NET type has to be <c>string</c> for the side-car's
    /// declaration to mean "the script parses this itself".
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

    /// <summary>The passthrough rule, at the unit level: a string arrival is never touched, so an
    /// already-encoded payload cannot be encoded twice.</summary>
    [Fact]
    public void BindArgument_StringValueForAnEncodingParameter_PassesThroughVerbatim()
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter("""{"type":"array"}"""), Value("""  "[{\"op\":\"set_field\"}]"  """));
        Assert.Equal("""[{"op":"set_field"}]""", Assert.IsType<string>(bound));
    }

    /// <summary>A structured value for a non-encoding parameter converts exactly as it did before
    /// encoding existed - an <c>object[]</c> / dictionary into the splat, whatever the script then
    /// makes of it.</summary>
    [Fact]
    public void BindArgument_StructuredValueForANonEncodingParameter_ConvertsPlainly()
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value("""["a","b"]"""));
        Assert.Equal(new object?[] { "a", "b" }, (object?[])bound!);
    }

    /// <summary>An argument matching no advertised parameter cannot reach here through the
    /// dispatcher (it is refused as <c>invalid_argument</c> first), so the null case only has to
    /// fail safe: convert plainly rather than throw.</summary>
    [Fact]
    public void BindArgument_WithNoMatchingParameter_ConvertsPlainly() =>
        Assert.Equal("text", ScriptToolArgumentEncoder.BindArgument(null, Value("\"text\"")));

    /// <summary>Only an array or an object arrives structured enough to serialize. A scalar against
    /// an encoding parameter takes the plain conversion, so a side-car declaring a scalar non-string
    /// type (<c>"integer"</c>, <c>"boolean"</c>) never turns its value into JSON text.</summary>
    [Fact]
    public void BindArgument_ScalarForAnEncodingParameter_IsNotSerialized()
    {
        var parameter = Parameter("""{"type":"array"}""");
        // Pinned to the exact CLR type, because the plain conversion keeps an integral one. A
        // looser assertion ("not JSON text") would pass just as well against a number branch that
        // widened every value to double, so it would not hold the conversion to what it does.
        Assert.IsType<long>(ScriptToolArgumentEncoder.BindArgument(parameter, Value("42")));
        Assert.Equal(true, ScriptToolArgumentEncoder.BindArgument(parameter, Value("true")));
        Assert.Null(ScriptToolArgumentEncoder.BindArgument(parameter, Value("null")));
    }

    /// <summary>
    /// The plain conversion's number branch, which has to preserve integrality rather than send
    /// everything through <c>double</c>. The branch reads
    /// <c>TryGetInt64(out var l) ? l : GetDouble()</c>, and a conditional expression takes its
    /// operands' best common type - <c>double</c>, since <c>long</c> converts implicitly to it and
    /// not the reverse - so without an explicit <c>object</c> on the integral arm the successful
    /// <c>TryGetInt64</c> is computed and then thrown away.
    ///
    /// Latent while every script-tool parameter is <c>[string]</c>, <c>[bool]</c>, or
    /// <c>[string[]]</c>, because a whole-valued double stringifies to "1" under invariant
    /// formatting. It goes live the moment a tool declares <c>[int]</c> or <c>[long]</c>, which
    /// <c>ScriptToolLoader.MapJsonType</c> already advertises as JSON <c>"integer"</c> - a type the
    /// binding layer would never have delivered.
    /// </summary>
    [Theory]
    [InlineData("42")]
    [InlineData("-7")]
    [InlineData("0")]
    // The boundaries of what TryGetInt64 accepts at all, and the reason the distinction is not
    // cosmetic: past 2^53 a double cannot represent consecutive integers, so widening here is
    // silent data loss rather than a change of label.
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    public void BindArgument_IntegralJsonNumber_ConvertsToLong(string json)
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value(json));
        Assert.Equal(long.Parse(json), Assert.IsType<long>(bound));
    }

    /// <summary>The other side of the same branch: a number <c>TryGetInt64</c> refuses still lands
    /// on <c>double</c>, so nothing about the fix narrows what the conversion accepts.</summary>
    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("-0.25", -0.25)]
    [InlineData("1e3", 1000d)]
    // Integral in value but outside Int64's range, so TryGetInt64 refuses it and double is the
    // only branch left - the case that keeps this from being "integral text means long".
    [InlineData("9223372036854775808", 9223372036854775808d)]
    public void BindArgument_FractionalOrOutOfRangeJsonNumber_ConvertsToDouble(string json, double expected)
    {
        var bound = ScriptToolArgumentEncoder.BindArgument(Parameter(null), Value(json));
        Assert.Equal(expected, Assert.IsType<double>(bound));
    }

    /// <summary>Nested numbers take the same branch, so an array or object argument carries the
    /// distinction into the <c>object?[]</c> and dictionary the splat hands PowerShell.</summary>
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
