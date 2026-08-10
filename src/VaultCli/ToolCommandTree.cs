using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Nodes;
using VaultShared.Tools;

namespace VaultCli;

/// <summary>
/// Builds the command tree from the tool registry: one subcommand per declaration, named for the
/// declared tool name, with one option per member of its argument record. Nothing here is authored
/// per tool, so a tool added to the registry appears with the options, help text and schema its
/// declaration already states.
/// </summary>
/// <remarks>
/// Declarations that require a client session are left out of the tree. The shared execute path
/// refuses such a call whatever route it arrived by.
/// </remarks>
public sealed class ToolCommandTree
{
    /// <summary>The global options. They are recursive so they can be given before or after the
    /// subcommand.</summary>
    private readonly Option<string> _grant = new("--grant")
    {
        Description = "Run as this named grant instead of the one the settings file names. Elevation at this front-end is the operator's choice of grant.",
        Recursive = true,
    };

    private readonly Option<string> _output = new("--output")
    {
        Description = "How to render the answer: \"text\" (the default) or \"json\", which renders both result tiers as a JSON document.",
        Recursive = true,
    };

    private readonly Option<string> _args = new("--args")
    {
        Description = "The whole argument record as JSON, bypassing the options. The only spelling of an argument that is a structure. Refused alongside option-form arguments.",
        Recursive = true,
    };

    private readonly RootCommand _root;

    private ToolCommandTree(ToolRegistry tools, CliHost host, TextWriter output, TextWriter error)
    {
        _root = new RootCommand(
            "Vault access from the command line. Every subcommand is one tool, declared once and " +
            "projected into this front-end and the MCP server alike.");
        _root.Options.Add(_grant);
        _root.Options.Add(_output);
        _root.Options.Add(_args);

        var reserved = new HashSet<string>(_root.Options.SelectMany(o => o.Aliases.Append(o.Name)), StringComparer.Ordinal);

        foreach (var registration in tools.Registrations.Where(r => !r.Declaration.RequiresClientSession))
        {
            var declaration = registration.Declaration;
            var options = ToolOptions.For(declaration.Arguments, reserved);
            var command = new Command(declaration.Name, declaration.Description);
            foreach (var option in options.Symbols)
                command.Options.Add(option);

            // --args carries the whole argument record, so an option beside it would be a second,
            // partial statement of the same record with no defined precedence between them. The
            // fault is in the command line rather than in a tool's arguments, so the parser refuses
            // it and the exit code says usage.
            command.Validators.Add(result =>
            {
                if (result.GetResult(_args) is { Implicit: false } && options.AnySupplied(result.GetResult))
                    result.AddError($"--args carries the whole argument record for \"{declaration.Name}\", so it cannot be combined with option-form arguments.");
            });

            command.SetAction(parsed => Invoke(host, registration, options, parsed, output, error));
            _root.Subcommands.Add(command);
        }
    }

    /// <summary>
    /// Builds the command tree over <paramref name="tools"/>, ready to parse. The registry is a
    /// parameter rather than read from <paramref name="host"/> so that the tree is a projection of a
    /// registry alone; all it takes from the host is the grant and the execute path.
    /// </summary>
    /// <param name="tools">The tools to expose as subcommands.</param>
    /// <param name="host">The host supplying the grant and the shared execute path.</param>
    /// <param name="output">Where a tool's answer is written.</param>
    /// <param name="error">Where refusal envelopes are written.</param>
    /// <returns>The root command to parse against.</returns>
    public static RootCommand Build(ToolRegistry tools, CliHost host, TextWriter output, TextWriter error) =>
        new ToolCommandTree(tools, host, output, error)._root;

    /// <summary>
    /// Runs one tool synchronously. The shared execute path applies every gate on the call, so all
    /// this decides is what a front-end supplies: which grant, which arguments, and how the answer
    /// is written.
    /// </summary>
    /// <param name="host">The host supplying the grant and the execute path.</param>
    /// <param name="registration">The tool to run and its declaration.</param>
    /// <param name="options">The generated options for that tool's argument record.</param>
    /// <param name="parsed">The parsed command line.</param>
    /// <param name="output">Where the answer is written.</param>
    /// <param name="error">Where a refusal envelope is written.</param>
    /// <returns>One of <see cref="ExitCodes"/>.</returns>
    private int Invoke(
        CliHost host,
        ToolRegistry.Registration registration,
        ToolOptions options,
        ParseResult parsed,
        TextWriter output,
        TextWriter error)
    {
        // The format has to be resolved before anything is rendered, so a bad --output value cannot
        // be reported through the renderer and is written directly instead.
        OutputFormat format;
        try
        {
            format = Format(parsed);
        }
        catch (CliFailure failure)
        {
            return CliRendering.Refuse(failure.Refusal, error);
        }

        return CliRendering.Answer(
            () =>
            {
                var grant = host.Grant(parsed.GetValue(_grant));
                var arguments = parsed.GetResult(_args) is { Implicit: false }
                    ? Record(parsed.GetValue(_args) ?? string.Empty)
                    : options.Supplied(parsed);

                // There is no session. The process exits when its one command finishes, so there is
                // no caller whose authority could be rebound, and the execute path refuses the two
                // declarations that require one.
                return host.Execution.Invoke(registration, arguments, grant, session: null);
            },
            format,
            output,
            error);
    }

    /// <summary>
    /// Reads the whole argument record from JSON. It is available on every tool because some
    /// arguments, such as an ordered list of operations with nested anchors, have no flat option
    /// form.
    /// </summary>
    /// <param name="json">The <c>--args</c> value.</param>
    /// <returns>The arguments, keyed by the wire names the tool advertises.</returns>
    /// <exception cref="CliFailure">The text is not a JSON object of arguments.</exception>
    private static IReadOnlyDictionary<string, JsonElement> Record(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw CliFailure.Argument("args", $"\"--args\" is the argument record as JSON: {exception.Message}");
        }

        if (node is not JsonObject record)
            throw CliFailure.Argument("args", "\"--args\" is the argument record as JSON - an object of arguments, keyed by the names the tool advertises.");

        return record.ToDictionary(
            member => member.Key,
            member => JsonSerializer.SerializeToElement(member.Value),
            StringComparer.Ordinal);
    }

    /// <summary>Resolves the output format from <c>--output</c>, defaulting to text.</summary>
    /// <param name="parsed">The parsed command line.</param>
    /// <returns>The format to render the answer in.</returns>
    /// <exception cref="CliFailure">The value names a format nothing renders.</exception>
    private OutputFormat Format(ParseResult parsed) => parsed.GetValue(_output) switch
    {
        null or "" or "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        var other => throw CliFailure.Argument("output", $"\"--output\" is \"text\" or \"json\", not \"{other}\"."),
    };
}
