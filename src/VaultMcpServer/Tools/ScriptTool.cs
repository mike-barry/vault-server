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
/// also makes the later encoding signal readable off it directly: "does this parameter's
/// side-car declare a non-string type while its .NET type is string?" is
/// <c>Schema?.TryGetProperty("type", ...)</c> against <see cref="JsonType"/>, with no re-parse.
/// </summary>
public sealed record ScriptToolParameter(
    string Name,
    string JsonType,
    bool Required,
    string Description,
    IReadOnlyList<string>? AllowedValues,
    JsonElement? Schema = null);

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
