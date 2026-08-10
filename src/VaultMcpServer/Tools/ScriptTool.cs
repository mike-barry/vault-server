using System.Text.Json;

namespace VaultMcpServer.Tools;

/// <summary>
/// Represents one parameter of a script tool, derived from the script's <c>param()</c> block
/// and its comment-based help.
/// </summary>
/// <remarks>
/// A side-car schema is stored as a detached <see cref="JsonElement"/>, a
/// <see cref="JsonElement.Clone"/> of a parsed document's root. Held as a string it would
/// re-serialize as a JSON string and double-encode the schema into the tool listing, whereas a
/// <see cref="JsonElement"/> round-trips verbatim through <c>SerializeToElement</c>.
/// </remarks>
/// <param name="Name">The parameter's name, as written in the <c>param()</c> block.</param>
/// <param name="JsonType">The JSON Schema type derived from the parameter's .NET static type.</param>
/// <param name="Required">Whether the parameter is declared <c>[Parameter(Mandatory)]</c>.</param>
/// <param name="Description">The parameter's <c>.PARAMETER</c> comment-help text.</param>
/// <param name="AllowedValues">The members of the parameter's <c>[ValidateSet(...)]</c>, or null
/// when it declares no set.</param>
/// <param name="Schema">The literal JSON Schema from a <c>.SCHEMA &lt;param&gt;</c> side-car
/// block in the script's comment-based help, or null when it declares none. When present it
/// replaces the whole derived property node in <see cref="ScriptTool.BuildInputSchema"/>, so
/// <paramref name="JsonType"/>, <paramref name="Description"/>, and
/// <paramref name="AllowedValues"/> then describe the <c>param()</c> block rather than the
/// advertised shape.</param>
public sealed record ScriptToolParameter(
    string Name,
    string JsonType,
    bool Required,
    string Description,
    IReadOnlyList<string>? AllowedValues,
    JsonElement? Schema = null)
{
    /// <summary>
    /// Gets a value indicating whether a structured client argument for this parameter is
    /// JSON-encoded before it is splatted into the runspace (see
    /// <see cref="ScriptToolArgumentEncoder"/>). A parameter whose .NET static type is
    /// <c>string</c> but whose side-car schema declares a non-string type is one the script
    /// means to receive as JSON text and parse itself, so the host hands it text.
    /// </summary>
    /// <remarks>
    /// A parameter whose .NET type is not <c>string</c> - a <c>[string[]]</c>, say - already
    /// binds a real array through the splat, so encoding it would break the binding rather than
    /// enable it.
    /// </remarks>
    public bool EncodesStructuredArgument =>
        string.Equals(JsonType, "string", StringComparison.Ordinal)
        && Schema is { } schema
        && DeclaresNonStringType(schema);

    /// <summary>
    /// Returns whether a side-car schema declares a non-string type at its top level.
    /// </summary>
    /// <remarks>
    /// The check covers the three shapes a JSON Schema <c>type</c> keyword can take.
    ///
    /// <list type="bullet">
    /// <item><description>A string, such as <c>"type": "array"</c>. Any name other than
    /// <c>"string"</c> counts. Scalar names such as <c>"integer"</c> and <c>"boolean"</c> count
    /// too. <see cref="ScriptToolArgumentEncoder"/> only encodes an argument that arrives as an
    /// array or an object, so a number or a boolean passes through
    /// regardless.</description></item>
    /// <item><description>An array of strings, such as <c>"type": ["array", "null"]</c>. The
    /// union counts if any member is a non-string type. <c>"null"</c> does not count, because
    /// <c>["string","null"]</c> is the idiomatic spelling of an optional string and must read
    /// exactly like a plain <c>"string"</c>. <c>["string","array"]</c> does count, and behaves
    /// correctly: a string arrival passes verbatim, an array arrival is encoded.</description></item>
    /// <item><description>Absent. A block that constrains only through <c>oneOf</c>,
    /// <c>enum</c>, <c>const</c>, or <c>$ref</c> declares no type, so the parameter is not
    /// encoded. Encoding cannot be undone in the script, which would then parse text it never
    /// asked for, whereas not encoding can be worked around by sending the already-encoded
    /// string, which passes through verbatim. An author who wants such a block encoded adds a
    /// sibling top-level <c>"type"</c>. Any other JSON shape under <c>type</c>, such as a number
    /// or an object, is malformed and is treated as absent.</description></item>
    /// </list>
    ///
    /// Type names are compared ordinally, because JSON Schema's type vocabulary is lowercase and
    /// case-sensitive, so <c>"Array"</c> is not the keyword.
    /// </remarks>
    /// <param name="schema">The side-car schema to inspect.</param>
    /// <returns><c>true</c> when the schema declares a non-string type; otherwise <c>false</c>.</returns>
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

    /// <summary>Returns whether a JSON value is a type name other than <c>"string"</c> or
    /// <c>"null"</c>.</summary>
    /// <param name="name">The candidate type name from a schema's <c>type</c> keyword.</param>
    /// <returns><c>true</c> when the value names a non-string type; otherwise <c>false</c>.</returns>
    private static bool IsNonStringTypeName(JsonElement name) =>
        name.ValueKind == JsonValueKind.String
        && name.GetString() is { } declared
        && !string.Equals(declared, "string", StringComparison.Ordinal)
        && !string.Equals(declared, "null", StringComparison.Ordinal);
}

/// <summary>
/// Represents an MCP tool backed by a PowerShell script. <see cref="ScriptToolLoader"/> derives
/// every member from the script itself.
/// </summary>
/// <param name="Name">The tool name, taken from the script file's base name.</param>
/// <param name="Description">The tool description, taken from the script's <c>.SYNOPSIS</c>.</param>
/// <param name="OpClass">The op class from the script's <c>.NOTES</c> section, validated against
/// <see cref="VaultShared.Configuration.OpClassRegistry"/> at load time.</param>
/// <param name="ScriptPath">The full path of the backing <c>.ps1</c> file.</param>
/// <param name="Parameters">The parameters derived from the script's <c>param()</c> block.</param>
public sealed record ScriptTool(string Name, string Description, string OpClass, string ScriptPath, IReadOnlyList<ScriptToolParameter> Parameters)
{
    /// <summary>Builds the tool's JSON Schema <c>inputSchema</c> for the MCP <c>tools/list</c>
    /// response.</summary>
    /// <returns>An object schema whose properties are the tool's parameters.</returns>
    public JsonElement BuildInputSchema()
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in Parameters)
        {
            properties[p.Name] = p.Schema is { } sideCar ? sideCar : DerivedProperty(p);
            // `required` comes from [Parameter(Mandatory)] even when a side-car is present.
            // It is a keyword of the parent object schema rather than of a property node, so a
            // side-car that replaces the property node cannot express it; and the PowerShell
            // binder enforces mandatoriness itself, so letting a hand-written side-car
            // contradict it would advertise a contract the script will refuse.
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

    /// <summary>
    /// Builds the property node for a parameter that carries no <c>.SCHEMA</c> side-car, from
    /// the <c>param()</c> block alone. A side-car replaces this node wholesale rather than
    /// merging with it, so a <c>[ValidateSet]</c>'s <c>enum</c> is dropped when one is present
    /// and the effective schema stays readable from the side-car alone.
    /// </summary>
    /// <param name="p">The parameter to render.</param>
    /// <returns>The JSON Schema property node for that parameter.</returns>
    private static Dictionary<string, object> DerivedProperty(ScriptToolParameter p)
    {
        var property = new Dictionary<string, object> { ["type"] = p.JsonType, ["description"] = p.Description };
        // On an array parameter the set constrains each element, so it goes under `items`; at
        // the property level it would require the array itself to equal a member.
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
