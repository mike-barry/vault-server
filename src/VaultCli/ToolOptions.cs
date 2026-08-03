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
/// A tool's options, generated from the argument record its declaration names. <b>Nothing here is
/// authored per tool</b>: the record is the only statement of a tool's argument surface, and this
/// reads the same contract the binder and the advertised schema read, so an option that binds
/// nothing cannot exist and an argument with no option cannot either.
///
/// <b>Two spellings, one declared name.</b> Each option is presented in kebab-case with the
/// <c>snake_case</c> wire spelling registered as an alias, both derived from the member's one wire
/// name. A single-word argument spells the same either way and registers one.
///
/// What an option's text becomes is decided by the member's own JSON shape and nothing else, so a
/// value the shape cannot take reaches the binder and is refused as <c>invalid_argument</c> naming
/// the argument - the same refusal an MCP caller gets for sending the wrong type.
/// </summary>
public sealed class ToolOptions
{
    private readonly IReadOnlyList<ToolOption> _options;

    private ToolOptions(IReadOnlyList<ToolOption> options) => _options = options;

    /// <summary>The generated options, in the record's own member order - which is the order the
    /// advertised schema lists its properties in.</summary>
    public IReadOnlyList<Option> Symbols => [.. _options.Select(o => o.Symbol)];

    /// <summary>
    /// Generates the option set for the argument record <paramref name="arguments"/>.
    /// </summary>
    /// <param name="reserved">Names the command tree's own global options already hold. A generated
    /// option colliding with one would be silently shadowed, so it throws instead: the collision is
    /// a wiring defect between a declaration and this front-end, and it has to be visible at
    /// startup rather than at the one call that spells the argument.</param>
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

    /// <summary>The options the caller actually supplied, as the raw argument map the execute path
    /// binds. An option left off the command line is absent from the map rather than present as
    /// null, so the record's own defaults and its required-argument faults both behave exactly as
    /// they do for a caller that omitted the member.</summary>
    public IReadOnlyDictionary<string, JsonElement> Supplied(ParseResult parsed)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var option in _options)
            if (option.Supplied(parsed.GetResult))
                values[option.WireName] = option.Value(parsed);

        return values;
    }

    /// <summary>Whether the caller spelled any of these options - the question <c>--args</c>'s
    /// exclusivity turns on. It takes the lookup rather than a parse result, because the exclusivity
    /// is decided by a command validator, which is handed the command's own result.</summary>
    public bool AnySupplied(Func<Option, OptionResult?> resolve) => _options.Any(o => o.Supplied(resolve));

    /// <summary>The kebab-case presentation of a <c>snake_case</c> wire name.</summary>
    public static string Kebab(string wireName) => wireName.Replace('_', '-');

    /// <summary>
    /// One generated option: the symbol the parser matches, and how its text becomes the JSON the
    /// binder reads.
    /// </summary>
    private abstract class ToolOption
    {
        protected ToolOption(JsonPropertyInfo property) => WireName = property.Name;

        public string WireName { get; }

        public abstract Option Symbol { get; }

        public abstract JsonElement Value(ParseResult parsed);

        /// <summary>Whether this option is on the command line. An implicit result - one the parser
        /// synthesized from a default - is not the caller having spelled it.</summary>
        public bool Supplied(Func<Option, OptionResult?> resolve) => resolve(Symbol) is { Implicit: false };

        public static ToolOption For(JsonPropertyInfo property, string name, string alias)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            // A boolean is a flag, which is what a CLI reader expects of one and what keeps the
            // commonest spelling ("--apply") from needing a value. An explicit "--apply false" still
            // parses, so nothing is unsayable.
            if (type == typeof(bool))
                return new Flag(property, Build<bool>(property, name, alias));

            var option = Build<string>(property, name, alias);

            // An enumerated member takes its wire spelling as text and is never validated here: an
            // unrecognized value is refused in the tool's own vocabulary, naming the argument, and a
            // parser-side value list would refuse it as a usage error instead - a different exit code
            // and a different message for the same call the other front-end refuses as a tool does.
            if (type == typeof(string) || type.IsEnum)
                return new Text(property, option);

            if (Numeric(type))
                return new Number(property, option);

            // Everything else is a structure with no flat option encoding - an ordered list of
            // operations, a nested body change - and takes JSON. It is the same escape hatch --args
            // is, per argument rather than per call.
            return new Structured(property, option);
        }

        protected static JsonElement Element(JsonNode? node) => JsonSerializer.SerializeToElement(node);

        /// <summary>The option symbol, carrying the member's own description so <c>--help</c> and the
        /// advertised schema say the same thing about it.</summary>
        private static Option<T> Build<T>(JsonPropertyInfo property, string name, string alias)
        {
            // A single-word argument spells the same in both cases; registering the alias anyway
            // would be a duplicate the parser refuses.
            var option = alias == name ? new Option<T>(name) : new Option<T>(name, alias);
            if (property.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true) is [DescriptionAttribute described, ..])
                option.Description = described.Description;

            return option;
        }

        private static bool Numeric(Type type) =>
            Type.GetTypeCode(type) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
                or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

        private sealed class Flag(JsonPropertyInfo property, Option<bool> option) : ToolOption(property)
        {
            public override Option Symbol => option;

            public override JsonElement Value(ParseResult parsed) => Element(JsonValue.Create(parsed.GetValue(option)));
        }

        private sealed class Text(JsonPropertyInfo property, Option<string> option) : ToolOption(property)
        {
            public override Option Symbol => option;

            public override JsonElement Value(ParseResult parsed) => Element(JsonValue.Create(parsed.GetValue(option) ?? string.Empty));
        }

        private sealed class Number(JsonPropertyInfo property, Option<string> option) : ToolOption(property)
        {
            public override Option Symbol => option;

            /// <summary>Text that is not a number is carried across as the text it is, so the binder
            /// refuses it naming the argument rather than the parser refusing it as a usage
            /// error.</summary>
            public override JsonElement Value(ParseResult parsed)
            {
                var text = parsed.GetValue(option) ?? string.Empty;
                return Element(decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : JsonValue.Create(text));
            }
        }

        private sealed class Structured(JsonPropertyInfo property, Option<string> option) : ToolOption(property)
        {
            public override Option Symbol => option;

            /// <exception cref="CliFailure">The text is not JSON at all, which no binder message
            /// would describe as well as this one.</exception>
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
