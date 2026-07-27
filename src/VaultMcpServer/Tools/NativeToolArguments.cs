using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace VaultMcpServer.Tools;

/// <summary>
/// The single argument contract for <see cref="INativeTool"/> implementations: one C# record per
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
    /// the same rule the PowerShell tools' <c>param()</c> blocks follow. Dropping the policy renames
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
    public static JsonElement Schema<T>() =>
        JsonSerializer.SerializeToElement(Options.GetJsonSchemaAsNode(typeof(T), ExporterOptions));

    /// <summary>
    /// Binds the dispatcher's raw argument map onto <typeparamref name="T"/>. Throws
    /// <see cref="JsonException"/> for anything that does not fit the advertised shape - which is not
    /// a <c>{code, message, data?}</c> error, so every caller must catch it at the tool boundary and
    /// re-raise it through <see cref="ToolError"/> as <c>invalid_argument</c>.
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
    /// The one <c>TransformSchemaNode</c> callback for the server, closing the exporter's two gaps
    /// against what an MCP <c>inputSchema</c> should look like. Authored once here rather than per
    /// tool, since both gaps are properties of the exporter, not of any tool.
    ///
    /// <list type="number">
    /// <item>It emits <c>["string","null"]</c>-style unions for every optional or nullable member
    /// (and <c>["array","null"]</c> on the parameter itself), plus a <c>"default": null</c> for a
    /// member whose C# default is null. Both are C# nullability leaking onto the wire: optionality is
    /// already carried by absence from <c>required</c>, and <c>"default": null</c> is worse than
    /// noise where the real default is a value the tool substitutes itself (<c>ttl</c>'s 60).</item>
    /// <item>It does not read <see cref="DescriptionAttribute"/> at all, so a member's
    /// <c>description</c> - the part of the schema a model actually reads - would vanish.</item>
    /// </list>
    /// </summary>
    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject node)
            return schema;

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
}
