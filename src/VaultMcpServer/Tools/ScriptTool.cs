using System.Text.Json;

namespace VaultMcpServer.Tools;

/// <summary>
/// One parameter of a script tool, derived from its param() block and comment-based help.
/// <paramref name="AllowedValues"/> carries the members of a `[ValidateSet(...)]` on the
/// parameter, and is null for a parameter that declares no such set.
/// <paramref name="Schema"/> carries the literal JSON Schema from a `.SCHEMA &lt;param&gt;`
/// side-car block in the script's comment-based help, and is null for a parameter that
/// declares none. When present it <em>replaces</em> the whole derived property node in
/// <see cref="ScriptTool.BuildInputSchema"/> - <paramref name="JsonType"/>,
/// <paramref name="Description"/>, and <paramref name="AllowedValues"/> are all superseded
/// (they are still carried, since they remain the true facts about the `param()` block, but
/// nothing reads them for that parameter's advertised shape).
///
/// It is a detached <see cref="JsonElement"/> - a <see cref="JsonElement.Clone"/> of a parsed
/// document's root - rather than a raw string or a <c>JsonNode</c>: a string would re-serialize
/// as a JSON <em>string</em> (double-encoding the schema into the tool listing), whereas
/// <see cref="JsonElement"/> is the same immutable, already-validated representation
/// <see cref="ScriptTool.BuildInputSchema"/> returns and MCP's <c>inputSchema</c> is, so it
/// round-trips verbatim through <c>SerializeToElement</c>. Being structured rather than opaque
/// is also what lets the argument-encoding signal be read off it directly, with no re-parse -
/// see <see cref="EncodesStructuredArgument"/>.
/// </summary>
public sealed record ScriptToolParameter(
    string Name,
    string JsonType,
    bool Required,
    string Description,
    IReadOnlyList<string>? AllowedValues,
    JsonElement? Schema = null)
{
    /// <summary>
    /// Whether a structured client argument for this parameter is JSON-encoded before it is
    /// splatted into the runspace (see <see cref="ScriptToolArgumentEncoder"/>). The signal is
    /// <em>derived</em>, never a hand-maintained per-tool table: a parameter whose side-car
    /// schema declares a non-string type while its .NET static type is <c>string</c> is one the
    /// script means to receive as JSON text and parse itself, so the host hands it text.
    ///
    /// It lives here because both of its inputs are this record's own fields - it is a pure
    /// derived fact about one parameter, the same kind of thing as <see cref="Required"/> - so
    /// every consumer that holds a parameter can read it without a lookup helper. The dispatcher
    /// reads it per client argument today; the remedy-payload path reads the same property one
    /// level up, over the parameters of the tool a remedy names.
    ///
    /// Both halves matter. A parameter with no side-car has nothing declaring a shape, so it is
    /// untouched. A side-car declaring <c>type: "string"</c> is the script asking for a plain
    /// string, so it is untouched. And a parameter whose .NET type is <em>not</em> string (a
    /// <c>[string[]]</c>, say) already binds a real array through the splat, so encoding it would
    /// break the binding rather than enable it.
    /// </summary>
    public bool EncodesStructuredArgument =>
        string.Equals(JsonType, "string", StringComparison.Ordinal)
        && Schema is { } schema
        && DeclaresNonStringType(schema);

    /// <summary>
    /// Whether a side-car schema affirmatively declares a non-string type at its top level.
    /// Deliberately narrow, over the three shapes a real JSON Schema `type` can take:
    ///
    /// <list type="bullet">
    /// <item><description><b>A string</b> (<c>"type": "array"</c>) - a non-string name declares
    /// one. Scalar names (<c>"integer"</c>, <c>"boolean"</c>) satisfy this too, harmlessly: the
    /// arrival-shape rule in <see cref="ScriptToolArgumentEncoder"/> only ever encodes an array
    /// or an object, so a number or a bool passes through whatever this returns.</description></item>
    /// <item><description><b>An array of strings</b> (<c>"type": ["array", "null"]</c>) - a union
    /// declares one if any member is a non-string type. <c>"null"</c> does not count: it is the
    /// absence of a value rather than a shape, and <c>["string","null"]</c> is the idiomatic
    /// spelling of an optional string, which has to read exactly like a plain <c>"string"</c> here.
    /// <c>["string","array"]</c> does count, and is exactly right under the arrival-shape rule: a
    /// string arrival passes verbatim, an array arrival encodes.</description></item>
    /// <item><description><b>Absent</b> - a block constraining only through <c>oneOf</c>,
    /// <c>enum</c>, <c>const</c>, or <c>$ref</c> declares no type, so it declares no non-string
    /// type and the parameter is <em>not</em> encoded. This is the conservative reading, and the
    /// deliberate one: encoding is not recoverable in-script (the script would parse text it never
    /// asked for), whereas not encoding is, because a caller can always send the already-encoded
    /// string, which passes through verbatim. An author who wants a `oneOf` block encoded writes a
    /// sibling top-level <c>"type"</c> alongside it - which is also what makes the block honest to
    /// a client reading the advertised schema. Any other JSON shape under <c>type</c> (a number, an
    /// object) is malformed as a schema and is read the same way as absent.</description></item>
    /// </list>
    ///
    /// Type names are compared ordinally: JSON Schema's type vocabulary is lowercase and
    /// case-sensitive, so <c>"Array"</c> is not the keyword and must not be read as one.
    /// </summary>
    private static bool DeclaresNonStringType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
            return false;
        return type.ValueKind switch
        {
            JsonValueKind.String => IsNonStringTypeName(type),
            JsonValueKind.Array => type.EnumerateArray().Any(IsNonStringTypeName),
            _ => false,
        };
    }

    private static bool IsNonStringTypeName(JsonElement name) =>
        name.ValueKind == JsonValueKind.String
        && name.GetString() is { } declared
        && !string.Equals(declared, "string", StringComparison.Ordinal)
        && !string.Equals(declared, "null", StringComparison.Ordinal);
}

/// <summary>
/// An MCP tool backed by a PowerShell script: name and description from comment-based
/// help, parameters from the param() block, op class from the ".NOTES" help section - a
/// name validated against <see cref="VaultMcpServer.Vault.OpClassRegistry"/> by
/// <see cref="ScriptToolLoader"/>, which derives these.
/// </summary>
public sealed record ScriptTool(string Name, string Description, string OpClass, string ScriptPath, IReadOnlyList<ScriptToolParameter> Parameters)
{
    /// <summary>Builds the tool's JSON Schema `inputSchema`, as required by the MCP `tools/list` response.</summary>
    public JsonElement BuildInputSchema()
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in Parameters)
        {
            properties[p.Name] = p.Schema is { } sideCar ? sideCar : DerivedProperty(p);
            // `required` stays driven by [Parameter(Mandatory)] even where a side-car is
            // present. `required` is a keyword of the *parent* object schema, not of a
            // property node, so a block that replaces the property node cannot express it
            // without reaching outside its own scope; and mandatoriness is a statically
            // derived fact about the param() block that the PowerShell binder actually
            // enforces, so letting a hand-authored blob contradict it would advertise a
            // contract the script will refuse. The block governs the shape of the value;
            // the param() block governs whether it has to be supplied.
            if (p.Required)
                required.Add(p.Name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>The property node derived from the param() block, for a parameter carrying no
    /// `.SCHEMA` side-car. A side-car replaces this node wholesale rather than overriding parts
    /// of it, so nothing here merges with one - notably a `[ValidateSet]`, whose `enum` a
    /// side-car deliberately drops so the effective schema is readable from the block alone.</summary>
    private static Dictionary<string, object> DerivedProperty(ScriptToolParameter p)
    {
        var property = new Dictionary<string, object> { ["type"] = p.JsonType, ["description"] = p.Description };
        // `enum` is emitted only for a parameter that constrains its values, so an
        // unconstrained one carries no `enum` key rather than an empty or null one.
        // A set on an array parameter constrains each element, so it lands under
        // `items`: at the property level it would demand the array itself equal a member.
        if (p.AllowedValues is { Count: > 0 })
        {
            if (p.JsonType == "array")
                property["items"] = new Dictionary<string, object> { ["enum"] = p.AllowedValues };
            else
                property["enum"] = p.AllowedValues;
        }
        return property;
    }
}
