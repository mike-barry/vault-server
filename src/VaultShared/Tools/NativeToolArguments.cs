using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace VaultShared.Tools;

/// <summary>
/// The single argument contract for a compiled tool: one C# record per
/// tool is both the deserialization target and the source of the advertised <c>inputSchema</c>,
/// derived by <c>System.Text.Json.Schema</c>'s <c>JsonSchemaExporter</c> from the same
/// <see cref="JsonTypeInfo"/> contract that drives binding. A hand-authored
/// <c>Dictionary&lt;string, object&gt;</c> schema literal beside a hand-walked
/// <see cref="JsonElement"/> reader is two statements of one contract that drift independently;
/// this is one statement.
///
/// The schema exporter and the binder deliberately share <see cref="Options"/> - the same instance,
/// not two configured alike - because a policy set on one and not the other advertises one wire
/// vocabulary while binding another.
///
/// What derives and what does not: the exporter states the argument's <em>shape</em> - names, types,
/// required-ness. It cannot state <em>meaning</em>, so every semantic rule a native tool enforces
/// (a scope root that is not <c>"*"</c>, a positive TTL, "exactly one of these two forms") stays as
/// explicit post-binding validation inside the tool, raising that tool's own coded refusal. See
/// <see cref="OffendingArgument"/> for the boundary between the two.
/// </summary>
public static class NativeToolArguments
{
    /// <summary>
    /// Shared by the exporter and the binder, so an advertised property name and the name that
    /// actually binds can never disagree.
    ///
    /// <b><see cref="JsonNamingPolicy.SnakeCaseLower"/> is pinned, not incidental.</b> The wire
    /// vocabulary is <c>snake_case</c> (<c>scope_roots</c>, <c>op_class</c>, <c>session_context</c>) -
    /// the same rule a script tool's <c>param()</c> block follows. Dropping the policy renames
    /// every multi-word argument to <c>camelCase</c> on <em>both</em> sides at once: the schema and
    /// the binder stay consistent with each other while every existing caller breaks. That is the one
    /// way this layer can regress the wire contract with nothing failing, which is why
    /// <c>NativeToolSchemaTests</c> pins each tool's advertised property names verbatim.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        // JsonSchemaExporter refuses an options instance with no resolver; the reflection-based
        // default is what the natives' plain records need.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // An enumerated argument binds by its wire spelling and never refuses here, so an
        // unrecognized value reaches the tool's own Validate rather than escaping as a binder
        // failure - see EnumArgument.
        Converters = { EnumArgument.Converter },
    };

    private static readonly JsonSchemaExporterOptions ExporterOptions = new() { TransformSchemaNode = Transform };

    /// <summary>
    /// The <c>inputSchema</c> for a native tool that takes no arguments. A zero-member record would
    /// be a deserialization target with nothing to deserialize, and the exporter emits
    /// <c>{"type":"object"}</c> for one - dropping the <c>properties</c> member that has always been
    /// on the wire. Stating it once here keeps that shape and keeps the literal out of the tools.
    /// </summary>
    public static readonly JsonElement None = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>(),
    });

    /// <summary>Derives <typeparamref name="T"/>'s MCP <c>inputSchema</c>. Called once per tool, from a
    /// field initializer - the result is immutable and shared for the process lifetime.</summary>
    public static JsonElement Schema<T>() => SchemaFor(typeof(T));

    /// <summary>
    /// The advertised schema for the argument record a <see cref="ToolDeclaration"/> names. Cached
    /// per type: a declaration carries the type rather than a derived schema, so the projection asks
    /// for it on every listing and the derivation should happen once.
    ///
    /// <see cref="NoArguments"/> answers <see cref="None"/> - the exporter emits
    /// <c>{"type":"object"}</c> for a record with no members, dropping the <c>properties</c> member
    /// that has always been on the wire.
    /// </summary>
    public static JsonElement SchemaFor(Type arguments) =>
        arguments == typeof(NoArguments)
            ? None
            : Schemas.GetOrAdd(arguments, static type => JsonSerializer.SerializeToElement(Options.GetJsonSchemaAsNode(type, ExporterOptions)));

    private static readonly ConcurrentDictionary<Type, JsonElement> Schemas = new();

    /// <summary>
    /// Binds the dispatcher's raw argument map onto <typeparamref name="T"/>. Throws
    /// <see cref="JsonException"/> for anything that does not fit the advertised shape - which is not
    /// a <c>{code, message, data?}</c> error, so every caller must catch it at the tool boundary and
    /// re-raise it through the shared error envelope as <c>invalid_argument</c>.
    ///
    /// A member the caller omitted arrives as <c>null</c> rather than throwing: the schema's
    /// <c>required</c> list advertises the requirement, and the tool's own refusal - which names the
    /// argument and says what it needs - states it.
    /// </summary>
    public static T Bind<T>(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        // The naming policy governs member names, not dictionary keys, so the caller's keys survive
        // this round trip verbatim and are matched against the record by the same policy the schema
        // advertised them under.
        var json = JsonSerializer.SerializeToUtf8Bytes(arguments, Options);
        return JsonSerializer.Deserialize<T>(json, Options)!;
    }

    /// <summary>
    /// The same binding, onto the type a <see cref="ToolDeclaration"/> names rather than a compile-time
    /// one. The shared execute path is generic over every tool and holds the argument record as a
    /// <see cref="Type"/>, so this is the form it needs.
    /// </summary>
    public static IToolArguments Bind(Type arguments, IReadOnlyDictionary<string, JsonElement> values)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(values, Options);
        return (IToolArguments)JsonSerializer.Deserialize(json, arguments, Options)!;
    }

    /// <summary>
    /// The wire argument name a binding <see cref="JsonException"/> is about, read off its
    /// <see cref="JsonException.Path"/> (<c>$.ttl</c>, <c>$.scope_roots[0]</c>), or null when the
    /// failure is not attributable to one argument. Callers turn the name into their own refusal
    /// prose: the schema derives, the refusal vocabulary does not.
    /// </summary>
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
    /// The one <c>TransformSchemaNode</c> callback in the build, closing the exporter's three gaps
    /// against what an MCP <c>inputSchema</c> should look like. Authored once here rather than per
    /// tool, since every gap is a property of the exporter, not of any tool.
    ///
    /// <list type="number">
    /// <item>It emits <c>["string","null"]</c>-style unions for every optional or nullable member
    /// (and <c>["array","null"]</c> on the parameter itself), plus a <c>"default": null</c> for a
    /// member whose C# default is null. Both are C# nullability leaking onto the wire: optionality is
    /// already carried by absence from <c>required</c>, and <c>"default": null</c> is worse than
    /// noise where the real default is a value the tool substitutes itself (<c>ttl</c>'s 60).</item>
    /// <item>It does not read <see cref="DescriptionAttribute"/> at all, so a member's
    /// <c>description</c> - the part of the schema a model actually reads - would vanish.</item>
    /// <item>It emits an unconstrained node for a type behind a custom converter, and never calls
    /// this callback for one. <see cref="EnumArgument"/> is that converter on every enumerated
    /// argument, so an enum-valued member's permitted values - the whole of what a model needs in
    /// order to spell one - are restated by <see cref="Enumerations"/> from the owning object's node,
    /// which does reach here. <see cref="Unconstrained"/> is the same gap for a member whose type
    /// genuinely admits any JSON: the node the exporter leaves has no shape to state and would
    /// otherwise carry a bare <c>"default": null</c> and no description at all.</item>
    /// </list>
    /// </summary>
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
    /// Restates a member the exporter left with no shape at all - a type that admits any JSON, and
    /// so sits behind a converter this callback is never offered. What comes out is
    /// <c>{"default": null}</c>: a stale nullability artifact, and no description, which is the part
    /// a model reads. The replacement keeps "anything goes" by stating no type and carries the
    /// member's own description across.
    /// </summary>
    private static void Unconstrained(JsonSchemaExporterContext context, JsonObject node)
    {
        if (node["properties"] is not JsonObject properties)
            return;

        foreach (var property in context.TypeInfo.Properties)
        {
            // Anything the exporter did state a shape for is left exactly as it is: this is for the
            // node that says nothing, never a widening of one that says something.
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
    /// Restates every enum-valued member of <paramref name="node"/>'s own type, which the exporter
    /// left unconstrained and never offered to <see cref="Transform"/>. Done from the owning object's
    /// node because that is the innermost node the callback does reach, and driven off
    /// <see cref="JsonTypeInfo.Properties"/> so the wire name here is the one the binder matches on
    /// rather than a second rendering of it.
    ///
    /// The values are <see cref="EnumArgument"/>'s own wire spellings, so what is advertised and what
    /// binds cannot disagree. A member's declared default and description are carried across, since
    /// nothing else will put them back.
    /// </summary>
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
