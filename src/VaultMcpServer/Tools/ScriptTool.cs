using System.Text.Json;

namespace VaultMcpServer.Tools;

/// <summary>One parameter of a script tool, derived from its param() block and comment-based help.</summary>
public sealed record ScriptToolParameter(string Name, string JsonType, bool Required, string Description);

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
            properties[p.Name] = new Dictionary<string, object> { ["type"] = p.JsonType, ["description"] = p.Description };
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
