using System.Management.Automation.Language;
using System.Text.Json;
using System.Text.RegularExpressions;
using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tools;

/// <summary>
/// Loads MCP tool metadata from a flat folder of `.ps1` scripts - no separate manifest.
/// Per script: the tool name is the file's base name; description, per-parameter
/// descriptions, and op class come from a leading `&lt;# ... #&gt;` comment-based-help
/// block (`.SYNOPSIS`, `.PARAMETER &lt;name&gt;`, `.NOTES` containing "OpClass: Read" or
/// "OpClass: Write"); the parameter list, types, required/optional status, and any
/// `[ValidateSet(...)]` members come from static AST parsing of the script's `param()` block.
///
/// A parameter whose derived JSON type is too coarse to be a usable contract - an array of
/// objects derives as a bare `{type: "array"}` - can carry a `.SCHEMA &lt;name&gt;` side-car
/// block in the same help block, whose body is literal JSON Schema that replaces the whole
/// derived property node (see <see cref="ScriptToolParameter.Schema"/>). `.PARAMETER` stays
/// mandatory for every parameter regardless; a side-car simply supersedes its text for the
/// advertised schema. Two authoring constraints, both inherited from the surrounding parser:
/// a body containing `#&gt;` truncates the entire help block, because
/// <see cref="HelpBlockRegex"/> is non-greedy; and a body line that would itself match
/// <see cref="SectionHeaderRegex"/> would end the block early - not reachable from valid JSON,
/// whose lines can never begin with `.` followed by a letter.
///
/// A script that fails validation (parse error, missing `.SYNOPSIS`, missing/invalid
/// `.NOTES` op class, an undocumented parameter, a malformed or misnamed `.SCHEMA`) loads
/// zero tools from that file and is reported as a Degraded problem; the rest of the folder
/// loads normally. A bad `.SCHEMA` never falls back to <c>MapJsonType</c>: the fallback would
/// re-advertise the parameter as a bare `{type: "string"}` and, because side-car presence is
/// also the argument-encoding signal, silently strip its encoding - so a typo would degrade
/// the tool rather than break it visibly. Because this
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
        var problems = new List<DegradedProblem>();

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
                    // The path is carried as structured data (DegradedProblem.SubjectPath) so
                    // ScriptToolDispatcher's BROKEN-prefix listing can recover the script's base
                    // name without parsing it back out of the Detail string. The path is still
                    // included in the human-readable Detail for the log line and the listing
                    // description; it just isn't the machine-readable source of the name anymore.
                    problems.Add(new DegradedProblem($"Tool script skipped - {path}: {ex.Message}", path));
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
            throw new InvalidDataException(string.Join("; ", parseErrors.Select(e => e.Message)));

        var help = ParseCommentHelp(text);
        if (string.IsNullOrWhiteSpace(help.Synopsis))
            throw new InvalidDataException("missing a .SYNOPSIS comment-help entry.");

        var opClassMatch = help.Notes is null ? null : OpClassRegex().Match(help.Notes);
        var opClass = opClassMatch is { Success: true }
            ? opClassRegistry.Canonicalize(opClassMatch.Groups[1].Value)
            : null;
        if (opClass is null)
            throw new InvalidDataException($".NOTES must declare a valid \"OpClass: <name>\" (one of: {string.Join(", ", opClassRegistry.Names)}).");

        var parameters = new List<ScriptToolParameter>();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ast.ParamBlock?.Parameters ?? [])
        {
            var paramName = p.Name.VariablePath.UserPath;
            if (!help.Parameters.TryGetValue(paramName, out var description))
                throw new InvalidDataException($"parameter \"{paramName}\" is missing a .PARAMETER comment-help entry.");
            declared.Add(paramName);

            JsonElement? schema = null;
            if (help.Schemas.TryGetValue(paramName, out var schemaText))
                schema = ParseSideCarSchema(paramName, schemaText);

            parameters.Add(new ScriptToolParameter(paramName, MapJsonType(p.StaticType), IsMandatory(p), description, AllowedValues(p), schema));
        }

        // A `.SCHEMA` whose name matches nothing in the param() block is refused rather than
        // ignored, mirroring the refusal of a parameter carrying no `.PARAMETER` entry: a
        // misspelled name is otherwise indistinguishable from an absent block, and the
        // parameter would silently re-derive through MapJsonType with nothing to read as a
        // signal. A nameless `.SCHEMA` is the same refusal - it too names no parameter. That
        // is deliberately *not* the treatment a nameless `.PARAMETER` gets (it is dropped),
        // because dropping one is still caught: the parameter it was meant to document then
        // has no help entry and the script is refused a line later. Nothing catches a dropped
        // `.SCHEMA`, so consistency in effect means refusing here.
        foreach (var schemaName in help.Schemas.Keys)
        {
            if (schemaName.Length == 0)
                throw new InvalidDataException(".SCHEMA must name a parameter (\".SCHEMA <param>\"); a nameless block names none.");
            if (!declared.Contains(schemaName))
                throw new InvalidDataException($".SCHEMA \"{schemaName}\" names no parameter in the param() block.");
        }

        return new ScriptTool(name, help.Synopsis!, opClass, path, parameters);
    }

    /// <summary>Parses a `.SCHEMA` block body into the detached <see cref="JsonElement"/> that
    /// replaces the parameter's derived property node. A malformed body is an
    /// <see cref="InvalidDataException"/> like the loader's other per-script refusals - never a
    /// fallback to <c>MapJsonType</c>.</summary>
    private static JsonElement ParseSideCarSchema(string paramName, string schemaText)
    {
        try
        {
            // Clone() detaches the element from the JsonDocument being disposed here, so the
            // parameter carries an independently valid value rather than a window into a
            // buffer that is about to be returned to the pool.
            using var document = JsonDocument.Parse(schemaText);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($".SCHEMA \"{paramName}\" is not valid JSON: {ex.Message}");
        }
    }

    private sealed record CommentHelp(string? Synopsis, string? Notes, IReadOnlyDictionary<string, string> Parameters, IReadOnlyDictionary<string, string> Schemas);

    /// <summary>Parses the leading `&lt;# ... #&gt;` comment-based-help block, if any.</summary>
    private static CommentHelp ParseCommentHelp(string scriptText)
    {
        var blockMatch = HelpBlockRegex().Match(scriptText);
        if (!blockMatch.Success)
            return new CommentHelp(null, null, new Dictionary<string, string>(), new Dictionary<string, string>());

        var body = blockMatch.Groups[1].Value;
        var headers = SectionHeaderRegex().Matches(body);

        string? synopsis = null;
        string? notes = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Keyed case-insensitively like `parameters`, because that is how PowerShell's binder
        // matches a parameter name and so how the rest of the pipeline spells one.
        var schemas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                // Recorded unconditionally, including with an empty arg - unlike PARAMETER,
                // which is guarded above. LoadOne refuses a nameless block; see the comment
                // there for why a drop would be the wrong kind of consistency.
                case "SCHEMA":
                    schemas[arg] = text;
                    break;
            }
        }

        return new CommentHelp(synopsis, notes, parameters, schemas);
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

    /// <summary>The string members of the parameter's `[ValidateSet(...)]`, or null when it declares none.</summary>
    private static IReadOnlyList<string>? AllowedValues(ParameterAst param)
    {
        foreach (var attr in param.Attributes.OfType<AttributeAst>())
        {
            // Attribute types are matched by the name as written, the same way IsMandatory
            // matches "Parameter" - the AST is parsed statically, never type-resolved.
            if (attr.TypeName.Name is not ("ValidateSet" or "ValidateSetAttribute"))
                continue;
            var values = attr.PositionalArguments.OfType<StringConstantExpressionAst>().Select(a => a.Value).ToList();
            if (values.Count > 0)
                return values;
        }
        return null;
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
