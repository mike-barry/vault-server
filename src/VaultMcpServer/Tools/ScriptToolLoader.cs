using System.Management.Automation.Language;
using System.Text.Json;
using System.Text.RegularExpressions;
using VaultMcpServer.Health;
using VaultShared.Configuration;

namespace VaultMcpServer.Tools;

/// <summary>
/// Loads MCP tool metadata from a flat folder of <c>.ps1</c> scripts, with no separate manifest.
/// </summary>
/// <remarks>
/// <para>
/// Per script, the tool name is the file's base name. The description, the per-parameter
/// descriptions, and the op class come from a leading <c>&lt;# ... #&gt;</c> comment-based-help
/// block: <c>.SYNOPSIS</c>, <c>.PARAMETER &lt;name&gt;</c>, and a <c>.NOTES</c> section
/// containing "OpClass: &lt;name&gt;". The parameter list, types, required or optional status,
/// and any <c>[ValidateSet(...)]</c> members come from static AST parsing of the script's
/// <c>param()</c> block.
/// </para>
/// <para>
/// A parameter whose derived JSON type is too coarse to be a usable contract - an array of
/// objects derives as a bare <c>{type: "array"}</c> - can carry a <c>.SCHEMA &lt;name&gt;</c>
/// side-car block in the same help block, whose body is literal JSON Schema that replaces the
/// whole derived property node (see <see cref="ScriptToolParameter.Schema"/>). Every parameter
/// still needs a <c>.PARAMETER</c> entry; a side-car only supersedes its text for the advertised
/// schema. Two authoring constraints come from the parser: a side-car body containing
/// <c>#&gt;</c> truncates the entire help block, because <see cref="HelpBlockRegex"/> is
/// non-greedy, and a body line matching <see cref="SectionHeaderRegex"/> would end the block
/// early, which valid JSON cannot do because its lines never begin with a <c>.</c> followed by a
/// letter.
/// </para>
/// <para>
/// A script that fails validation - a parse error, a missing <c>.SYNOPSIS</c>, a missing or
/// invalid <c>.NOTES</c> op class, an undocumented parameter, or a malformed or misnamed
/// <c>.SCHEMA</c> - loads zero tools from that file and is reported as a Degraded problem, while
/// the rest of the folder loads normally. A bad <c>.SCHEMA</c> is never quietly replaced by the
/// derived type. That would re-advertise the parameter as a bare <c>{type: "string"}</c> and,
/// because side-car presence is also the argument-encoding signal, strip its encoding, so a typo
/// would degrade the tool instead of breaking it visibly.
/// </para>
/// <para>
/// This runs on every scan, at startup and on each refresh the watcher triggers, so problems are
/// recorded through <c>ServerHealthState.ReplaceDegraded</c> under <see cref="HealthSource"/>,
/// where each scan replaces the previous scan's set.
/// </para>
/// </remarks>
public static partial class ScriptToolLoader
{
    /// <summary>The <see cref="ServerHealthState"/> source key this loader's replaceable per-script problem set is recorded under.</summary>
    public const string HealthSource = "ScriptTools";

    /// <summary>Matches the script's leading <c>&lt;# ... #&gt;</c> comment-help block.</summary>
    [GeneratedRegex(@"<#(.*?)#>", RegexOptions.Singleline)]
    private static partial Regex HelpBlockRegex();

    /// <summary>
    /// Matches one <c>.TAG [arg]</c> comment-help section header on its own line.
    /// </summary>
    /// <remarks>
    /// Keep <c>\r</c> in the trailing character class. Under
    /// <see cref="RegexOptions.Multiline"/>, .NET's <c>$</c> matches only before a <c>\n</c> and
    /// never before the <c>\r</c> of a CRLF pair, so on a script saved with CRLF endings the
    /// <c>\r</c> is left for this class to consume and a class of just <c>[ \t]</c> cannot match
    /// it. Every header would then fail to match, the script would parse zero sections, and the
    /// loader would refuse it for a missing <c>.SYNOPSIS</c> it plainly has. The <c>\r</c> falls
    /// outside the captured groups either way, so nothing downstream sees it.
    /// </remarks>
    [GeneratedRegex(@"^[ \t]*\.(?<tag>[A-Za-z]+)\b[ \t]*(?<arg>\S+)?[ \t\r]*$", RegexOptions.Multiline)]
    private static partial Regex SectionHeaderRegex();

    /// <summary>Matches the <c>OpClass: &lt;name&gt;</c> declaration inside a <c>.NOTES</c> section.</summary>
    [GeneratedRegex(@"OpClass:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex OpClassRegex();

    /// <summary>Scans a folder of <c>.ps1</c> scripts and returns the tools they declare.</summary>
    /// <param name="toolsDir">The directory to scan. A missing directory yields no tools and no
    /// problems.</param>
    /// <param name="opClassRegistry">Validates and canonicalizes the op class each script
    /// declares.</param>
    /// <param name="healthState">
    /// Receives this scan's per-script problem set through
    /// <c>ServerHealthState.ReplaceDegraded</c>, including an empty set when every script is
    /// clean, so a previous scan's problems do not outlive the scripts that caused them.
    /// </param>
    /// <returns>The tools loaded from the folder, ordered by file name.</returns>
    public static IReadOnlyList<ScriptTool> Load(string toolsDir, OpClassRegistry opClassRegistry, ServerHealthState healthState)
    {
        var tools = new List<ScriptTool>();
        var problems = new List<DegradedProblem>();

        if (Directory.Exists(toolsDir))
        {
            foreach (var path in Directory.EnumerateFiles(toolsDir, "*.ps1", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                // IOException is caught because an editor can hold the file locked mid-save. The
                // save completing fires another watcher event, and that re-scan picks the script
                // up and clears the transient problem.
                try
                {
                    tools.Add(LoadOne(path, opClassRegistry));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    // The path is carried as structured data as well as in the message text, so
                    // ScriptToolDispatcher's BROKEN listing entry can recover the script's base
                    // name without parsing it back out of the message.
                    problems.Add(new DegradedProblem($"Tool script skipped - {path}: {ex.Message}", path));
                }
            }
        }

        healthState.ReplaceDegraded(HealthSource, problems);
        return tools;
    }

    /// <summary>Loads one script's tool metadata.</summary>
    /// <param name="path">The full path of the <c>.ps1</c> file.</param>
    /// <param name="opClassRegistry">Validates and canonicalizes the declared op class.</param>
    /// <returns>The tool the script declares.</returns>
    /// <exception cref="InvalidDataException">The script does not parse, or its comment-based
    /// help is missing or inconsistent with its <c>param()</c> block.</exception>
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

        // A `.SCHEMA` naming no parameter in the param() block is refused rather than ignored,
        // and so is a nameless one. A misspelled name is otherwise indistinguishable from an
        // absent block, and the parameter would fall back to its derived type with nothing left
        // to signal the encoding. A nameless `.PARAMETER` is dropped instead, which is safe
        // because the parameter it was meant to document then has no help entry and the script
        // is refused a line earlier. Nothing would catch a dropped `.SCHEMA`.
        foreach (var schemaName in help.Schemas.Keys)
        {
            if (schemaName.Length == 0)
                throw new InvalidDataException(".SCHEMA must name a parameter (\".SCHEMA <param>\"); a nameless block names none.");
            if (!declared.Contains(schemaName))
                throw new InvalidDataException($".SCHEMA \"{schemaName}\" names no parameter in the param() block.");
        }

        return new ScriptTool(name, help.Synopsis!, opClass, path, parameters);
    }

    /// <summary>
    /// Parses a <c>.SCHEMA</c> block body into the detached <see cref="JsonElement"/> that
    /// replaces the parameter's derived property node.
    /// </summary>
    /// <param name="paramName">The parameter the side-car block names.</param>
    /// <param name="schemaText">The block's body, expected to be JSON.</param>
    /// <returns>The parsed schema, detached from the document that parsed it.</returns>
    /// <exception cref="InvalidDataException">The body is not valid JSON. The script is refused
    /// rather than falling back to the derived type.</exception>
    private static JsonElement ParseSideCarSchema(string paramName, string schemaText)
    {
        try
        {
            // Clone() detaches the element from the JsonDocument disposed here, so the parameter
            // carries an independently valid value rather than a window into a buffer that is
            // about to be returned to the pool.
            using var document = JsonDocument.Parse(schemaText);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($".SCHEMA \"{paramName}\" is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Represents the sections parsed out of a script's comment-based-help block.</summary>
    /// <param name="Synopsis">The <c>.SYNOPSIS</c> text, or null when the section is absent.</param>
    /// <param name="Notes">The <c>.NOTES</c> text, or null when the section is absent.</param>
    /// <param name="Parameters">The <c>.PARAMETER</c> texts, keyed by parameter name.</param>
    /// <param name="Schemas">The <c>.SCHEMA</c> block bodies, keyed by parameter name.</param>
    private sealed record CommentHelp(string? Synopsis, string? Notes, IReadOnlyDictionary<string, string> Parameters, IReadOnlyDictionary<string, string> Schemas);

    /// <summary>Parses the leading <c>&lt;# ... #&gt;</c> comment-based-help block, if any.</summary>
    /// <param name="scriptText">The full text of the script file.</param>
    /// <returns>The parsed sections. Every member is empty or null when no help block is
    /// present.</returns>
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
        // matches a parameter name.
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
                // Recorded even with an empty arg, unlike PARAMETER, so that LoadOne can refuse a
                // nameless block rather than silently drop it.
                case "SCHEMA":
                    schemas[arg] = text;
                    break;
            }
        }

        return new CommentHelp(synopsis, notes, parameters, schemas);
    }

    /// <summary>Returns whether a parameter declares <c>[Parameter(Mandatory)]</c>.</summary>
    /// <param name="param">The parsed parameter to inspect.</param>
    /// <returns><c>true</c> when the parameter is mandatory; otherwise <c>false</c>.</returns>
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

    /// <summary>Returns the string members of a parameter's <c>[ValidateSet(...)]</c>.</summary>
    /// <param name="param">The parsed parameter to inspect.</param>
    /// <returns>The declared values, or null when the parameter declares no set.</returns>
    private static IReadOnlyList<string>? AllowedValues(ParameterAst param)
    {
        foreach (var attr in param.Attributes.OfType<AttributeAst>())
        {
            // Attribute types are matched by the name as written, the same way IsMandatory
            // matches "Parameter", because the AST is parsed statically and never type-resolved.
            if (attr.TypeName.Name is not ("ValidateSet" or "ValidateSetAttribute"))
                continue;
            var values = attr.PositionalArguments.OfType<StringConstantExpressionAst>().Select(a => a.Value).ToList();
            if (values.Count > 0)
                return values;
        }
        return null;
    }

    /// <summary>Maps a parameter's .NET static type to a JSON Schema type name.</summary>
    /// <param name="type">The parameter's static type.</param>
    /// <returns>The JSON Schema type name, defaulting to <c>"string"</c> for anything
    /// unrecognized.</returns>
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
