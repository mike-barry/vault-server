using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Nodes;
using VaultShared.Tools;

namespace VaultCli;

/// <summary>
/// The command tree, generated from the tool registry: one subcommand per projected declaration,
/// named for the declared tool name, with one option per member of its argument record. <b>Neither
/// is authored per tool</b> - this front-end holds no per-tool file, and a tool added to the registry
/// arrives here with its options, its help text and its schema already stated once in its
/// declaration.
///
/// <b>Declarations requiring a client session are not listed.</b> That filter is cosmetic on top of
/// the real enforcement: the shared execute path refuses such a call whatever route reached it, and
/// <c>--args</c> reaches a tool by name without needing the tree at all.
/// </summary>
public sealed class ToolCommandTree
{
    /// <summary>The global options, recursive so they may be spelled before or after the
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

            // Supplying both is a statement about the command line rather than about a tool, so the
            // parser refuses it and the exit code says usage. --args is whole-record: an option
            // beside it could only be a second, partial statement of the same record, and which one
            // won would be a rule this build invented.
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
    /// The tree over <paramref name="tools"/>, ready to parse. The registry is a parameter rather
    /// than read off <paramref name="host"/> because the tree is a projection of a registry and
    /// nothing else: what it takes from the host is the grant and the execute path, which are what a
    /// front-end supplies.
    /// </summary>
    public static RootCommand Build(ToolRegistry tools, CliHost host, TextWriter output, TextWriter error) =>
        new ToolCommandTree(tools, host, output, error)._root;

    /// <summary>
    /// One tool, once, synchronously. Everything a call is gated by - the session, the floor, the
    /// arguments, the vault, the lock - is the shared execute path's, so what is decided here is
    /// only what a front-end supplies: which grant, which arguments, and how the answer is written.
    /// </summary>
    private int Invoke(
        CliHost host,
        ToolRegistry.Registration registration,
        ToolOptions options,
        ParseResult parsed,
        TextWriter output,
        TextWriter error)
    {
        // The format is resolved before the answer, because rendering one needs it - so its own
        // refusal cannot go through the renderer and is written directly.
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

                // No session: this process exits when its one command finishes, so there is no
                // caller whose authority could be rebound. The execute path refuses the two
                // declarations that need one.
                return host.Execution.Invoke(registration, arguments, grant, session: null);
            },
            format,
            output,
            error);
    }

    /// <summary>
    /// The argument record as sent whole. It is general rather than one tool's escape hatch: an
    /// ordered list of operations with nested anchors has no flat option surface, and inventing an
    /// encoding for it would be a second statement of the argument contract.
    /// </summary>
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

    /// <exception cref="CliFailure">A format nothing renders.</exception>
    private OutputFormat Format(ParseResult parsed) => parsed.GetValue(_output) switch
    {
        null or "" or "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        var other => throw CliFailure.Argument("output", $"\"--output\" is \"text\" or \"json\", not \"{other}\"."),
    };
}
