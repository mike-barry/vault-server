using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VaultShared.Tools;

/// <summary>
/// Binds a compiled tool's arguments and derives its advertised <c>inputSchema</c>. One C# record
/// per tool is both the deserialization target and the source of the schema, which
/// <c>JsonSchemaExporter</c> derives from the same <see cref="JsonTypeInfo"/> contract that drives
/// binding, so the shape is stated once.
/// </summary>
/// <remarks>
/// <para>
/// The exporter and the binder must share the same <see cref="Options"/> instance, not two instances
/// configured alike. A policy set on one and not the other advertises one wire vocabulary while
/// binding another.
/// </para>
/// <para>
/// The exporter states an argument's shape - names, types, and which are required - and cannot state
/// its meaning. Every semantic rule a tool enforces, such as a scope root that is not <c>"*"</c>, a
/// positive TTL, or a choice of exactly one of two forms, stays as explicit validation inside the
/// tool, raising that tool's own coded refusal.
/// </para>
/// </remarks>
public static class NativeToolArguments
{
    /// <summary>
    /// The serializer options shared by the schema exporter and the binder, so an advertised property
    /// name and the name that actually binds can never disagree.
    /// </summary>
    /// <remarks>
    /// Do not remove <see cref="JsonNamingPolicy.SnakeCaseLower"/>. The wire vocabulary is
    /// <c>snake_case</c> (<c>scope_roots</c>, <c>op_class</c>, <c>session_context</c>), the same rule
    /// a script tool's <c>param()</c> block follows. Dropping the policy renames every multi-word
    /// argument to <c>camelCase</c> on both sides at once, so the schema and the binder stay
    /// consistent with each other while every existing caller breaks. That is the one way this layer
    /// can regress the wire contract without anything failing, which is why
    /// <c>NativeToolSchemaTests</c> pins each tool's advertised property names verbatim.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        // JsonSchemaExporter refuses an options instance with no resolver; the reflection-based
        // default is what the natives' plain records need.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // A member no argument record declares is refused rather than dropped. A misspelled argument
        // is almost always a typo, and silently ignoring one turns a guarded call into an unguarded
        // one that reports success: "expected_content_hash" misspelled on a wholesale body
        // replacement would overwrite the note with no staleness check at all. This also makes the
        // exporter state "additionalProperties": false on every object it derives, which is what
        // keeps the advertised schema and the binder saying the same thing.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // An enumerated argument binds by its wire spelling and never refuses here, so an
        // unrecognized value reaches the tool's own Validate rather than escaping as a binder
        // failure. See EnumArgument.
        Converters = { EnumArgument.Converter },
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = new() { TransformSchemaNode = Transform };

    /// <summary>
    /// The <c>inputSchema</c> for a tool that takes no arguments. The exporter emits
    /// <c>{"type":"object"}</c> for a record with no members, which drops the <c>properties</c>
    /// member callers expect on the wire, so the correct shape is stated here once instead of as a
    /// literal in each tool.
    /// </summary>
    /// <remarks>
    /// <c>additionalProperties</c> is false here because the binder refuses a member no argument
    /// record declares, and a tool taking no arguments declares none at all.
    /// </remarks>
    public static readonly JsonElement None = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>(),
        ["additionalProperties"] = false,
    });

    /// <summary>Derives the MCP <c>inputSchema</c> for an argument record type.</summary>
    /// <typeparam name="T">The argument record type.</typeparam>
    /// <returns>The schema. It is immutable and shared for the process lifetime.</returns>
    public static JsonElement Schema<T>() => SchemaFor(typeof(T));

    /// <summary>
    /// Returns the advertised schema for the argument record a <see cref="ToolDeclaration"/> names.
    /// </summary>
    /// <param name="arguments">The argument record type.</param>
    /// <returns>The schema, or <see cref="None"/> for <see cref="NoArguments"/>.</returns>
    /// <remarks>
    /// Cached per type. A declaration carries the type rather than a derived schema, so a front-end
    /// asks for the schema on every listing and the derivation has to happen only once.
    /// </remarks>
    public static JsonElement SchemaFor(Type arguments) =>
        arguments == typeof(NoArguments)
            ? None
            : Schemas.GetOrAdd(arguments, static type => JsonSerializer.SerializeToElement(Options.GetJsonSchemaAsNode(type, ExporterOptions)));

    private static readonly ConcurrentDictionary<Type, JsonElement> Schemas = new();

    /// <summary>
    /// Binds a raw argument map onto an argument record type.
    /// </summary>
    /// <typeparam name="T">The argument record type.</typeparam>
    /// <param name="arguments">The call's raw argument map.</param>
    /// <returns>The bound record. A member the caller omitted arrives as null rather than throwing;
    /// the schema's <c>required</c> list advertises the requirement and the tool's own validation
    /// enforces it.</returns>
    /// <exception cref="JsonException">A value does not fit the advertised shape, or a member the
    /// record does not declare was sent. This is not a <c>{code, message, data?}</c> refusal, so a
    /// caller must catch it at the tool boundary and re-raise it through
    /// <see cref="ToolRefusalEnvelope"/> as <c>invalid_argument</c>.</exception>
    public static T Bind<T>(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        // The naming policy governs member names, not dictionary keys, so the caller's keys survive
        // this round trip verbatim and are matched against the record by the same policy the schema
        // advertised them under.
        var json = JsonSerializer.SerializeToUtf8Bytes(arguments, Options);
        return JsonSerializer.Deserialize<T>(json, Options)!;
    }

    /// <summary>
    /// Binds a raw argument map onto the type a <see cref="ToolDeclaration"/> names. The shared
    /// execute path handles every tool and holds the argument record as a <see cref="Type"/>, so this
    /// is the form it uses.
    /// </summary>
    /// <param name="arguments">The argument record type.</param>
    /// <param name="values">The call's raw argument map.</param>
    /// <returns>The bound record.</returns>
    /// <exception cref="JsonException">A value does not fit the advertised shape, or a member the
    /// record does not declare was sent.</exception>
    public static IToolArguments Bind(Type arguments, IReadOnlyDictionary<string, JsonElement> values)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(values, Options);
        return (IToolArguments)JsonSerializer.Deserialize(json, arguments, Options)!;
    }

    /// <summary>
    /// Returns the wire argument name a binding failure is about, read off the exception's
    /// <see cref="JsonException.Path"/> (<c>$.ttl</c>, <c>$.scope_roots[0]</c>).
    /// </summary>
    /// <param name="exception">The exception binding threw.</param>
    /// <returns>The argument name, or null when the failure cannot be attributed to one argument.
    /// The caller writes its own refusal message around the name.</returns>
    public static string? OffendingArgument(JsonException exception)
    {
        if (exception.Path is not { Length: > 2 } path || !path.StartsWith("$.", StringComparison.Ordinal))
            return null;
        var name = path.AsSpan(2);
        var end = name.IndexOfAny('.', '[');
        if (end >= 0)
            name = name[..end];
        return name.Length > 0 ? name.ToString() : null;
    }

    /// <summary>
    /// Returns the wire path of the member a binding failure is about when the argument record
    /// declares no such member, which is the one binding failure a caller fixes by removing an
    /// argument rather than by correcting its value.
    /// </summary>
    /// <param name="arguments">The argument record type the call was bound onto.</param>
    /// <param name="exception">The exception binding threw.</param>
    /// <returns>The member's wire path, dotted for a nested member (<c>body.contnt</c>), or null when
    /// every name on the failure's path is declared - which is a value of the wrong shape rather than
    /// a member the tool does not take.</returns>
    /// <remarks>
    /// The failure's <see cref="JsonException.Path"/> is walked against the same
    /// <see cref="JsonTypeInfo"/> contract the binder used, rather than read out of the exception's
    /// message, because that message is prose and carries no stable structure. The walk stops and
    /// answers null at the first name whose type is not an object contract, since a member that
    /// admits any JSON has no declared member set to be absent from.
    /// </remarks>
    public static string? UndeclaredMember(Type arguments, JsonException exception)
    {
        if (exception.Path is not { Length: > 2 } path || !path.StartsWith("$.", StringComparison.Ordinal))
            return null;

        var info = Options.GetTypeInfo(arguments);
        List<string> reached = [];

        foreach (var segment in path[2..].Split('.'))
        {
            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];

            if (info.Kind != JsonTypeInfoKind.Object)
                return null;
            if (info.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal)) is not { } property)
                return string.Join('.', reached.Append(name));

            // The segment rather than the name, so a list index stays in the path a caller is given.
            reached.Add(segment);
            info = Options.GetTypeInfo(property.PropertyType);

            // One element type per index, so a member inside an array of records is reached under the
            // record's own contract rather than under the collection's.
            for (var at = bracket; at >= 0; at = segment.IndexOf('[', at + 1))
            {
                if (info.ElementType is null)
                    return null;
                info = Options.GetTypeInfo(info.ElementType);
            }
        }

        return null;
    }

    /// <summary>
    /// Adjusts each exported schema node into the shape an MCP <c>inputSchema</c> needs. This is the
    /// only <c>TransformSchemaNode</c> callback; every difference it corrects belongs to the
    /// exporter, not to any one tool.
    /// </summary>
    /// <param name="context">The exporter's context for the node.</param>
    /// <param name="schema">The node the exporter produced.</param>
    /// <returns>The corrected node.</returns>
    /// <remarks>
    /// There are three differences to correct:
    /// <list type="number">
    /// <item>The exporter emits type unions such as <c>["string","null"]</c> for optional or nullable
    /// members, and a <c>"default": null</c> where the C# default is null. Both leak C# nullability
    /// onto the wire. Optionality is already carried by absence from <c>required</c>, and
    /// <c>"default": null</c> is actively misleading where the real default is a value the tool
    /// substitutes itself.</item>
    /// <item>The exporter does not read <see cref="DescriptionAttribute"/>, so a member's
    /// <c>description</c>, the part of the schema a model reads, would be missing.</item>
    /// <item>The exporter emits an unconstrained node for a type behind a custom converter and never
    /// calls this callback for it. <see cref="EnumArgument"/> is such a converter on every enumerated
    /// argument, so <see cref="Enumerations"/> restates the permitted values from the owning object's
    /// node, which does reach here. <see cref="Unconstrained"/> handles the same gap for a member
    /// whose type genuinely admits any JSON.</item>
    /// </list>
    /// </remarks>
    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject node)
            return schema;

        Enumerations(context, node);
        Unconstrained(context, node);

        if (node["type"] is JsonArray types && types.Count == 2 && types.Any(t => t?.GetValue<string>() == "null"))
            node["type"] = types.First(t => t?.GetValue<string>() != "null")!.GetValue<string>();

        if (node.TryGetPropertyValue("default", out var @default) && @default is null)
            node.Remove("default");

        // A property node carries the member's own attributes; a bare type node (an array's items,
        // the root) carries the type's.
        var attributes = (context.PropertyInfo?.AttributeProvider ?? context.TypeInfo.Type)
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true);
        if (attributes is [DescriptionAttribute description, ..])
            node["description"] = description.Description;

        return node;
    }

    /// <summary>
    /// Restates any member the exporter left with no shape at all, because its type admits any JSON
    /// and sits behind a converter this callback is never offered. Such a node arrives as
    /// <c>{"default": null}</c> with no description. The replacement states no type, so anything is
    /// still accepted, and carries the member's own description across.
    /// </summary>
    /// <param name="context">The exporter's context for the owning object's node.</param>
    /// <param name="node">The owning object's node, modified in place.</param>
    private static void Unconstrained(JsonSchemaExporterContext context, JsonObject node)
    {
        if (node["properties"] is not JsonObject properties)
            return;

        foreach (var property in context.TypeInfo.Properties)
        {
            // Leave any node the exporter did state a shape for exactly as it is. This replaces a
            // node that says nothing; it must never widen one that says something.
            if (!properties.TryGetPropertyValue(property.Name, out var exported)
                || exported is not JsonObject slot
                || slot.Any(member => member.Key is not "default"))
                continue;

            var replacement = new JsonObject();
            if (property.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true) is [DescriptionAttribute described, ..])
                replacement["description"] = described.Description;

            properties[property.Name] = replacement;
        }
    }

    /// <summary>
    /// Restates every enum-valued member of the node's own type, which the exporter left
    /// unconstrained and never offered to <see cref="Transform"/>.
    /// </summary>
    /// <param name="context">The exporter's context for the owning object's node.</param>
    /// <param name="node">The owning object's node, modified in place.</param>
    /// <remarks>
    /// This works from the owning object's node because that is the innermost node the callback
    /// reaches, and it iterates <see cref="JsonTypeInfo.Properties"/> so the wire name written here
    /// is the one the binder matches on rather than a second rendering of it. The permitted values
    /// come from <see cref="EnumArgument"/>'s own wire spellings, so what is advertised and what
    /// binds cannot disagree. The member's declared default and description are carried across,
    /// because nothing else will put them back.
    /// </remarks>
    private static void Enumerations(JsonSchemaExporterContext context, JsonObject node)
    {
        if (node["properties"] is not JsonObject properties)
            return;

        foreach (var property in context.TypeInfo.Properties)
        {
            if ((Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is not { IsEnum: true } enumeration)
                continue;
            if (!properties.TryGetPropertyValue(property.Name, out var exported))
                continue;

            var replacement = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray([.. EnumArgument.WireNames(enumeration).Select(name => (JsonNode)JsonValue.Create(name))]),
            };
            if (exported is JsonObject slot && slot["default"] is { } declared)
                replacement["default"] = declared.DeepClone();
            if (property.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true) is [DescriptionAttribute described, ..])
                replacement["description"] = described.Description;

            properties[property.Name] = replacement;
        }
    }
}
