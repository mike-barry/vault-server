using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultShared.Tools;

/// <summary>
/// Defines the wire contract for enumerated arguments and the converter that binds them.
/// </summary>
/// <remarks>
/// <para>
/// An unrecognized value must be refused with a code from <see cref="ToolCodes"/>, so it has to
/// survive binding long enough for <see cref="IToolArguments.Validate"/> to see it. The serializer's
/// own enum handling throws instead, which would produce a refusal shaped by the binder rather than
/// by the catalogue. <see cref="Converter"/> therefore returns a value outside the declared members,
/// which <see cref="IsRecognized"/> reports and the tool's own validation turns into an
/// <see cref="ArgumentFault"/>.
/// </para>
/// <para>
/// Comparison trims the input and ignores case. A member's wire spelling is its name under the same
/// <see cref="JsonNamingPolicy.SnakeCaseLower"/> policy that governs argument names, so one
/// convention covers argument names and enumerated values alike.
/// </para>
/// </remarks>
public static class EnumArgument
{
    /// <summary>The converter registered on <see cref="NativeToolArguments.Options"/>, so every
    /// enumerated argument in every declaration binds this way rather than per record.</summary>
    public static JsonConverter Converter { get; } = new Factory();

    /// <summary>Returns the wire spelling of one enumeration member.</summary>
    /// <typeparam name="TEnum">The enumeration type.</typeparam>
    /// <param name="value">The member.</param>
    /// <returns>The member's name under <see cref="JsonNamingPolicy.SnakeCaseLower"/>.</returns>
    public static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Returns the wire spellings of every declared member, in declaration order. This is
    /// what the advertised schema lists and what a refusal names.</summary>
    /// <param name="enumType">The enumeration type.</param>
    /// <returns>The wire spellings.</returns>
    public static IReadOnlyList<string> WireNames(Type enumType) =>
        [.. Enum.GetNames(enumType).Select(JsonNamingPolicy.SnakeCaseLower.ConvertName)];

    /// <summary>Reports whether a bound value is one of the declared members.</summary>
    /// <typeparam name="TEnum">The enumeration type.</typeparam>
    /// <param name="value">The bound value.</param>
    /// <returns>False when the caller sent anything the enumeration does not declare.</returns>
    public static bool IsRecognized<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Enum.IsDefined(value);

    /// <summary>
    /// Returns the value an unbindable input becomes. It lies outside the declared members rather
    /// than being a declared sentinel, so nothing advertises it, no <c>switch</c> needs a case for
    /// it, and every enumerated argument is treated the same way without an extra member of its own.
    /// </summary>
    /// <typeparam name="TEnum">The enumeration type.</typeparam>
    /// <returns>A value no declared member uses.</returns>
    private static TEnum Unrecognized<TEnum>() where TEnum : struct, Enum =>
        (TEnum)Enum.ToObject(typeof(TEnum), -1);

    private sealed class Factory : JsonConverterFactory
    {
        /// <summary>
        /// Claims enumeration types and never their nullable forms.
        /// </summary>
        /// <param name="typeToConvert">The type the serializer is asking about.</param>
        /// <returns>True for an enumeration type.</returns>
        /// <remarks>
        /// A converter must handle exactly the type it claims. The serializer wraps a converter
        /// registered for <c>T</c> to serve <c>T?</c> by itself, and a factory claiming both hands
        /// the schema exporter a converter for the wrong type. An optional enumerated argument still
        /// binds through the same lenient reader, and an absent value stays absent rather than
        /// becoming an unrecognized one.
        /// </remarks>
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        /// <summary>Creates the lenient converter for an enumeration type.</summary>
        /// <param name="typeToConvert">The enumeration type.</param>
        /// <param name="options">The serializer options in force.</param>
        /// <returns>The converter.</returns>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(LenientConverter<>).MakeGenericType(typeToConvert))!;
    }

    /// <summary>
    /// Reads an enumeration member's wire spelling and never throws. A value that is not a declared
    /// member - misspelled, cased beyond what folding covers, or not a string at all - becomes the
    /// unrecognized value, so the refusal comes from the tool with a code and payload from the
    /// catalogue.
    /// </summary>
    /// <typeparam name="TEnum">The enumeration type.</typeparam>
    private sealed class LenientConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                // Consume whatever arrived, so an argument of the wrong shape produces one refusal
                // from this record rather than a binder failure over the rest of the map.
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
