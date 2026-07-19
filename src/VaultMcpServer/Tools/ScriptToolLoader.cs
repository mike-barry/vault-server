using System.Management.Automation.Language;
using System.Text.RegularExpressions;
using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tools;

/// <summary>
/// Loads MCP tool metadata from a flat folder of `.ps1` scripts - no separate manifest.
/// Per script: the tool name is the file's base name; description, per-parameter
/// descriptions, and op class come from a leading `&lt;# ... #&gt;` comment-based-help
/// block (`.SYNOPSIS`, `.PARAMETER &lt;name&gt;`, `.NOTES` containing "OpClass: Read" or
/// "OpClass: Write"); the parameter list, types, and required/optional status come from
/// static AST parsing of the script's `param()` block.
///
/// A script that fails validation (parse error, missing `.SYNOPSIS`, missing/invalid
/// `.NOTES` op class, an undocumented parameter) loads zero tools from that file and is
/// reported as a Degraded problem; the rest of the folder loads normally. Because this
/// runs on every re-scan (startup and each <see cref="ScriptToolsWatcher"/>-triggered
/// <see cref="ScriptToolRegistry.Refresh"/>), problems are recorded via
/// <see cref="ServerHealthState.ReplaceDegraded"/> under <see cref="HealthSource"/> - each
/// scan replaces the previous scan's set, so a fixed script stops appearing as a problem
/// and a newly-broken one appears.
/// </summary>
public static partial class ScriptToolLoader
{
    /// <summary>The <see cref="ServerHealthState"/> source key this loader's replaceable per-script problem set is recorded under.</summary>
    public const string HealthSource = "ScriptTools";

    [GeneratedRegex(@"<#(.*?)#>", RegexOptions.Singleline)]
    private static partial Regex HelpBlockRegex();

    [GeneratedRegex(@"^[ \t]*\.(?<tag>[A-Za-z]+)\b[ \t]*(?<arg>\S+)?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex SectionHeaderRegex();

    [GeneratedRegex(@"OpClass:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex OpClassRegex();

    /// <param name="healthState">
    /// Receives this scan's per-script problem set via
    /// <see cref="ServerHealthState.ReplaceDegraded"/> - replaced wholesale on every call,
    /// including with an empty set when every script (or a missing/empty folder) is clean,
    /// so a previous scan's problems don't outlive the scripts that caused them. Required,
    /// deliberately with no default, same as the other loaders: a defaulted throwaway
    /// instance would silently discard recorded problems.
    /// </param>
    public static IReadOnlyList<ScriptTool> Load(string toolsDir, OpClassRegistry opClassRegistry, ServerHealthState healthState)
    {
        var tools = new List<ScriptTool>();
        var problems = new List<string>();

        if (Directory.Exists(toolsDir))
        {
            foreach (var path in Directory.EnumerateFiles(toolsDir, "*.ps1", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                // Per-script skip-and-report: a script failing validation loads zero tools
                // from that file; the rest of the folder loads normally. IOException is
                // included for a file locked mid-save by an editor - the save completing
                // fires another watcher event, and that re-scan picks the script up (and
                // clears the transient problem).
                try
                {
                    tools.Add(LoadOne(path, opClassRegistry));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    problems.Add($"Tool script skipped - {ex.Message}");
                }
            }
        }

        healthState.ReplaceDegraded(HealthSource, problems);
        return tools;
    }

    private static ScriptTool LoadOne(string path, OpClassRegistry opClassRegistry)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var text = File.ReadAllText(path);

        var ast = Parser.ParseFile(path, out _, out var parseErrors);
        if (parseErrors.Length > 0)
            throw new InvalidDataException($"{path}: {string.Join("; ", parseErrors.Select(e => e.Message))}");

        var help = ParseCommentHelp(text);
        if (string.IsNullOrWhiteSpace(help.Synopsis))
            throw new InvalidDataException($"{path}: missing a .SYNOPSIS comment-help entry.");

        var opClassMatch = help.Notes is null ? null : OpClassRegex().Match(help.Notes);
        var opClass = opClassMatch is { Success: true }
            ? opClassRegistry.Canonicalize(opClassMatch.Groups[1].Value)
            : null;
        if (opClass is null)
            throw new InvalidDataException($"{path}: .NOTES must declare a valid \"OpClass: <name>\" (one of: {string.Join(", ", opClassRegistry.Names)}).");

        var parameters = new List<ScriptToolParameter>();
        foreach (var p in ast.ParamBlock?.Parameters ?? [])
        {
            var paramName = p.Name.VariablePath.UserPath;
            if (!help.Parameters.TryGetValue(paramName, out var description))
                throw new InvalidDataException($"{path}: parameter \"{paramName}\" is missing a .PARAMETER comment-help entry.");
            parameters.Add(new ScriptToolParameter(paramName, MapJsonType(p.StaticType), IsMandatory(p), description));
        }

        return new ScriptTool(name, help.Synopsis!, opClass, path, parameters);
    }

    private sealed record CommentHelp(string? Synopsis, string? Notes, IReadOnlyDictionary<string, string> Parameters);

    /// <summary>Parses the leading `&lt;# ... #&gt;` comment-based-help block, if any.</summary>
    private static CommentHelp ParseCommentHelp(string scriptText)
    {
        var blockMatch = HelpBlockRegex().Match(scriptText);
        if (!blockMatch.Success)
            return new CommentHelp(null, null, new Dictionary<string, string>());

        var body = blockMatch.Groups[1].Value;
        var headers = SectionHeaderRegex().Matches(body);

        string? synopsis = null;
        string? notes = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var tag = header.Groups["tag"].Value.ToUpperInvariant();
            var arg = header.Groups["arg"].Value;
            var start = header.Index + header.Length;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : body.Length;
            var text = body[start..end].Trim();

            switch (tag)
            {
                case "SYNOPSIS":
                    synopsis = text;
                    break;
                case "NOTES":
                    notes = text;
                    break;
                case "PARAMETER" when arg.Length > 0:
                    parameters[arg] = text;
                    break;
            }
        }

        return new CommentHelp(synopsis, notes, parameters);
    }

    private static bool IsMandatory(ParameterAst param)
    {
        foreach (var attr in param.Attributes.OfType<AttributeAst>())
        {
            if (attr.TypeName.Name != "Parameter")
                continue;
            foreach (var named in attr.NamedArguments)
            {
                if (!string.Equals(named.ArgumentName, "Mandatory", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (named.ExpressionOmitted)
                    return true;
                return named.Argument is not ConstantExpressionAst { Value: bool value } || value;
            }
        }
        return false;
    }

    private static string MapJsonType(Type type)
    {
        if (type == typeof(string))
            return "string";
        if (type == typeof(bool) || type == typeof(System.Management.Automation.SwitchParameter))
            return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            return "integer";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return "number";
        if (type.IsArray || (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string)))
            return "array";
        return "string";
    }
}
