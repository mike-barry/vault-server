using System.ComponentModel;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using VaultShared.Tools;

namespace VaultCli;

/// <summary>
/// Generates a tool's command-line options from the argument record its declaration names. Nothing
/// here is authored per tool: the record is the only statement of a tool's argument surface, and
/// this reads the same contract the binder and the advertised schema read, so there can be neither
/// an option that binds nothing nor an argument with no option.
/// </summary>
/// <remarks>
/// Each option is presented in kebab-case, with the <c>snake_case</c> wire spelling registered as an
/// alias. Both come from the member's single wire name, and a single-word argument spells the same
/// either way, so it registers only one name.
///
/// The member's JSON shape alone decides what an option's text is converted to. A value that shape
/// cannot take is passed through to the binder, which refuses it as <c>invalid_argument</c> naming
/// the argument - the same refusal an MCP caller gets for sending the wrong type.
/// </remarks>
public sealed class ToolOptions
{
    private readonly IReadOnlyList<ToolOption> _options;

    private ToolOptions(IReadOnlyList<ToolOption> options) => _options = options;

    /// <summary>Gets the generated option symbols, in the record's own member order, which is also
    /// the order the advertised schema lists its properties in.</summary>
    public IReadOnlyList<Option> Symbols => [.. _options.Select(o => o.Symbol)];

    /// <summary>
    /// Generates the option set for the argument record <paramref name="arguments"/>.
    /// </summary>
    /// <param name="arguments">The argument record type the tool's declaration names.</param>
    /// <param name="reserved">The names the command tree's global options already hold. A generated
    /// option that collided with one would be silently shadowed, so this throws instead: the
    /// collision is a wiring defect between a declaration and this front-end, and it has to surface
    /// at startup rather than at the one call that uses the argument.</param>
    /// <returns>The options for that record.</returns>
    /// <exception cref="InvalidOperationException">An argument's option name collides with a global
    /// option.</exception>
    public static ToolOptions For(Type arguments, IReadOnlySet<string> reserved)
    {
        List<ToolOption> options = [];
        foreach (var property in NativeToolArguments.Options.GetTypeInfo(arguments).Properties)
        {
            var name = "--" + Kebab(property.Name);
            var alias = "--" + property.Name;

            if (reserved.Contains(name) || reserved.Contains(alias))
            {
                throw new InvalidOperationException(
                    $"Argument \"{property.Name}\" of {arguments.Name} collides with a global option of the same name.");
            }

            options.Add(ToolOption.For(property, name, alias));
        }

        return new ToolOptions(options);
    }

    /// <summary>Returns the options the caller actually supplied, as the raw argument map the
    /// execute path binds. An option left off the command line is absent from the map rather than
    /// present as null, so the record's defaults and its required-argument faults behave exactly as
    /// they do for a caller that omitted the member.</summary>
    /// <param name="parsed">The parsed command line.</param>
    /// <returns>The supplied arguments, keyed by wire name.</returns>
    public IReadOnlyDictionary<string, JsonElement> Supplied(ParseResult parsed)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var option in _options)
            if (option.Supplied(parsed.GetResult))
                values[option.WireName] = option.Value(parsed);

        return values;
    }

    /// <summary>Returns whether the caller supplied any of these options, which is what
    /// <c>--args</c>'s exclusivity check turns on. It takes a lookup function rather than a parse
    /// result because that check runs in a command validator, which is handed the command's own
    /// result.</summary>
    /// <param name="resolve">Looks up an option's result on the command being validated.</param>
    /// <returns>True if at least one option was given on the command line.</returns>
    public bool AnySupplied(Func<Option, OptionResult?> resolve) => _options.Any(o => o.Supplied(resolve));

    /// <summary>Returns the kebab-case presentation of a <c>snake_case</c> wire name.</summary>
    /// <param name="wireName">The argument's wire name.</param>
    /// <returns>The same name with underscores replaced by hyphens.</returns>
    public static string Kebab(string wireName) => wireName.Replace('_', '-');

    /// <summary>
    /// Represents one generated option: the symbol the parser matches, and how its text becomes the
    /// JSON the binder reads.
    /// </summary>
    private abstract class ToolOption
    {
        /// <summary>Initializes the option for <paramref name="property"/>.</summary>
        /// <param name="property">The argument record member this option stands for.</param>
        protected ToolOption(JsonPropertyInfo property) => WireName = property.Name;

        /// <summary>Gets the argument's wire name.</summary>
        public string WireName { get; }

        /// <summary>Gets the option symbol the parser matches.</summary>
        public abstract Option Symbol { get; }

        /// <summary>Converts the parsed value into the JSON the binder reads.</summary>
        /// <param name="parsed">The parsed command line.</param>
        /// <returns>The argument value as JSON.</returns>
        public abstract JsonElement Value(ParseResult parsed);

        /// <summary>Returns whether this option was given on the command line. A result the parser
        /// synthesized from a default does not count as the caller having supplied it.</summary>
        /// <param name="resolve">Looks up an option's result.</param>
        /// <returns>True if the caller supplied the option.</returns>
        public bool Supplied(Func<Option, OptionResult?> resolve) => resolve(Symbol) is { Implicit: false };

        /// <summary>Creates the option for <paramref name="property"/>, choosing its kind from the
        /// member's type.</summary>
        /// <param name="property">The argument record member.</param>
        /// <param name="name">The kebab-case option name.</param>
        /// <param name="alias">The wire-name alias, which may equal <paramref name="name"/>.</param>
        /// <returns>The generated option.</returns>
        public static ToolOption For(JsonPropertyInfo property, string name, string alias)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            // A boolean becomes a flag, so the common form ("--apply") needs no value. An explicit
            // "--apply false" still parses, so both values remain expressible.
            if (type == typeof(bool))
                return new Flag(property, Build<bool>(property, name, alias));

            var option = Build<string>(property, name, alias);

            // An enum member is carried across as text and is deliberately not validated here. The
            // tool refuses an unrecognized value in its own vocabulary, naming the argument. A
            // parser-side list of accepted values would instead make it a usage error, giving a
            // different exit code and message than the MCP front-end gives for the same call.
            if (type == typeof(string) || type.IsEnum)
                return new Text(property, option);

            if (Numeric(type))
                return new Number(property, option);

            // Everything else is a structure with no flat option form - an ordered list of
            // operations, a nested body change - so its value is JSON. This is what --args offers
            // for a whole record, applied to one argument.
            return new Structured(property, option);
        }

        /// <summary>Converts a JSON node to the element form the binder reads.</summary>
        /// <param name="node">The node, which may be null.</param>
        /// <returns>The node as a <see cref="JsonElement"/>.</returns>
        protected static JsonElement Element(JsonNode? node) => JsonSerializer.SerializeToElement(node);

        /// <summary>Creates the option symbol, carrying the member's own description so
        /// <c>--help</c> and the advertised schema say the same thing about it.</summary>
        /// <typeparam name="T">The value type the parser produces for this option.</typeparam>
        /// <param name="property">The argument record member.</param>
        /// <param name="name">The kebab-case option name.</param>
        /// <param name="alias">The wire-name alias, which may equal <paramref name="name"/>.</param>
        /// <returns>The option symbol.</returns>
        private static Option<T> Build<T>(JsonPropertyInfo property, string name, string alias)
        {
            // A single-word argument spells the same in both cases, and registering the alias anyway
            // would be a duplicate the parser refuses.
            var option = alias == name ? new Option<T>(name) : new Option<T>(name, alias);
            if (property.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true) is [DescriptionAttribute described, ..])
                option.Description = described.Description;

            return option;
        }

        /// <summary>Returns whether <paramref name="type"/> is one of the built-in numeric
        /// types.</summary>
        /// <param name="type">The member's type, with any nullable wrapper already removed.</param>
        /// <returns>True if the member holds a number.</returns>
        private static bool Numeric(Type type) =>
            Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
                or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

        /// <summary>Represents a boolean member, presented as a flag.</summary>
        /// <param name="property">The argument record member.</param>
        /// <param name="option">The option symbol.</param>
        private sealed class Flag(JsonPropertyInfo property, Option<bool> option) : ToolOption(property)
        {
            /// <inheritdoc/>
            public override Option Symbol => option;

            /// <inheritdoc/>
            public override JsonElement Value(ParseResult parsed) => Element(JsonValue.Create(parsed.GetValue(option)));
        }

        /// <summary>Represents a string or enum member, whose value is carried across as
        /// text.</summary>
        /// <param name="property">The argument record member.</param>
        /// <param name="option">The option symbol.</param>
        private sealed class Text(JsonPropertyInfo property, Option<string> option) : ToolOption(property)
        {
            /// <inheritdoc/>
            public override Option Symbol => option;

            /// <inheritdoc/>
            public override JsonElement Value(ParseResult parsed) => Element(JsonValue.Create(parsed.GetValue(option) ?? string.Empty));
        }

        /// <summary>Represents a numeric member.</summary>
        /// <param name="property">The argument record member.</param>
        /// <param name="option">The option symbol.</param>
        private sealed class Number(JsonPropertyInfo property, Option<string> option) : ToolOption(property)
        {
            /// <inheritdoc/>
            public override Option Symbol => option;

            /// <summary>Converts the text to a number, or passes it through unchanged when it is not
            /// one. Passing it through lets the binder refuse it naming the argument, instead of the
            /// parser refusing it as a usage error.</summary>
            /// <param name="parsed">The parsed command line.</param>
            /// <returns>The value as a JSON number, or as a JSON string when it does not
            /// parse.</returns>
            public override JsonElement Value(ParseResult parsed)
            {
                var text = parsed.GetValue(option) ?? string.Empty;
                return Element(decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : JsonValue.Create(text));
            }
        }

        /// <summary>Represents a member with no flat option form, whose value is JSON.</summary>
        /// <param name="property">The argument record member.</param>
        /// <param name="option">The option symbol.</param>
        private sealed class Structured(JsonPropertyInfo property, Option<string> option) : ToolOption(property)
        {
            /// <inheritdoc/>
            public override Option Symbol => option;

            /// <summary>Parses the option's value as JSON.</summary>
            /// <param name="parsed">The parsed command line.</param>
            /// <returns>The parsed structure.</returns>
            /// <exception cref="CliFailure">The text is not JSON. It is refused here rather than at
            /// the binder, which cannot describe the problem as clearly.</exception>
            public override JsonElement Value(ParseResult parsed)
            {
                var text = parsed.GetValue(option) ?? string.Empty;
                try
                {
                    return Element(JsonNode.Parse(text));
                }
                catch (JsonException exception)
                {
                    throw CliFailure.Argument(
                        WireName,
                        $"\"--{Kebab(WireName)}\" carries a structure, so its value is JSON: {exception.Message}");
                }
            }
        }
    }
}
