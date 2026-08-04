using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultShared.Tools;

/// <summary>
/// The wire contract for an enumerated argument, and the reason binding one never refuses.
///
/// <b>An unrecognized value is refused in the tool's own error vocabulary</b>, which means it must
/// survive binding long
/// enough for <see cref="IToolArguments.Validate"/> to see it. The serializer's own enum handling
/// does the opposite: it throws, and a refusal escaping that way arrives shaped by the binder rather
/// than by the catalogue. So <see cref="Converter"/> answers <see cref="Unrecognized{TEnum}"/>
/// instead - a value outside the declared members, which <see cref="IsRecognized"/> then reports and
/// the tool's own <c>Validate</c> turns into an <see cref="ArgumentFault"/>.
///
/// Comparison is trimmed and case-folded. The wire spelling of a member is
/// its name under the same <see cref="JsonNamingPolicy.SnakeCaseLower"/> policy that governs
/// argument names, so one convention covers names and enumerated values alike and neither is
/// authored twice.
/// </summary>
public static class EnumArgument
{
    /// <summary>Registered on <see cref="NativeToolArguments.Options"/>, so every enumerated
    /// argument in every declaration binds this way rather than per record.</summary>
    public static JsonConverter Converter { get; } = new Factory();

    /// <summary>The wire spelling of one member.</summary>
    public static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>The wire spellings of every declared member, in declaration order - what the
    /// advertised schema lists and what a refusal names.</summary>
    public static IReadOnlyList<string> WireNames(Type enumType) =>
        [.. Enum.GetNames(enumType).Select(JsonNamingPolicy.SnakeCaseLower.ConvertName)];

    /// <summary>Whether the bound value is one of the declared members. False is what the caller
    /// sent something the vocabulary does not carry, whatever it was.</summary>
    public static bool IsRecognized<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Enum.IsDefined(value);

    /// <summary>
    /// The value an unbindable input becomes. Outside the declared members rather than a declared
    /// sentinel, so nothing advertises it, no <c>switch</c> has to carry a case for it, and every
    /// enumerated argument gets the same treatment without its own extra member.
    /// </summary>
    private static TEnum Unrecognized<TEnum>() where TEnum : struct, Enum =>
        (TEnum)Enum.ToObject(typeof(TEnum), -1);

    private sealed class Factory : JsonConverterFactory
    {
        /// <summary>
        /// The enum itself and never its nullable form. A converter must handle exactly the type it
        /// claims - the serializer wraps a converter registered for <c>T</c> to serve <c>T?</c> on
        /// its own, and a factory claiming both hands the schema exporter a converter for the wrong
        /// type. An optional enumerated argument therefore binds through the same lenient reader,
        /// with an absent value staying absent rather than becoming an unrecognized one.
        /// </summary>
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(LenientConverter<>).MakeGenericType(typeToConvert))!;
    }

    /// <summary>
    /// Reads the wire spelling and never throws. A value that is not one of the declared members -
    /// misspelled, differently cased beyond folding, or not a string at all - answers
    /// <see cref="Unrecognized{TEnum}"/> so the refusal is the tool's, coded and payloaded from the
    /// catalogue.
    /// </summary>
    private sealed class LenientConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                // Consume whatever arrived, so an argument of the wrong shape is one refusal from
                // this record rather than a binder failure over the rest of the map.
                reader.Skip();
                return Unrecognized<TEnum>();
            }

            var sent = reader.GetString()?.Trim();
            foreach (var member in Enum.GetValues<TEnum>())
            {
                if (string.Equals(WireName(member), sent, StringComparison.OrdinalIgnoreCase))
                    return member;
            }
            return Unrecognized<TEnum>();
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(IsRecognized(value) ? WireName(value) : value.ToString());
    }
}
