using System.Text.Json;

namespace VaultMcpServer.Tools;

/// <summary>
/// One parameter of a script tool, derived from its param() block and comment-based help.
/// <paramref name="AllowedValues"/> carries the members of a `[ValidateSet(...)]` on the
/// parameter, and is null for a parameter that declares no such set.
/// </summary>
public sealed record ScriptToolParameter(string Name, string JsonType, bool Required, string Description, IReadOnlyList<string>? AllowedValues);

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
            properties[p.Name] = property;
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
}
