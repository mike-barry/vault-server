using System.CommandLine;

namespace VaultCli;

/// <summary>
/// Runs one invocation end to end: reads the settings file, runs the load cascade, builds the
/// command tree from the tool registry, parses the command line, and runs the tool it named.
/// </summary>
/// <remarks>
/// The configuration cascade runs before the command line is parsed, with no exemption for any
/// argument. Configuration that will not load fails the process rather than admitting the invocation
/// unrestricted, and that includes <c>--help</c>: help needs loadable configuration too, and the
/// failure names the file to fix.
///
/// The output and error writers are parameters rather than <see cref="Console"/> so a test can run a
/// whole invocation - cascade, tree, execute path, rendering and exit code - the way the shipped
/// entry point runs it.
/// </remarks>
public static class VaultCliApp
{
    /// <summary>
    /// Runs one invocation of <paramref name="args"/>.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="output">Where the tool's answer is written.</param>
    /// <param name="error">Where refusal envelopes, parse errors and diagnostics are written.</param>
    /// <param name="settingsPath">The CLI's own configuration file, defaulting to the one beside the
    /// executable.</param>
    /// <returns>One of <see cref="ExitCodes"/>.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error, string? settingsPath = null)
    {
        CliHost host;
        try
        {
            host = CliHost.Load(CliSettings.Load(settingsPath ?? CliSettings.DefaultPath), error);
        }
        catch (CliFailure failure)
        {
            return CliRendering.Refuse(failure.Refusal, error);
        }

        var parsed = ToolCommandTree.Build(host.Tools, host, output, error).Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var parseError in parsed.Errors)
                error.WriteLine(parseError.Message);

            return ExitCodes.UsageError;
        }

        // The default exception handler is disabled because it renders an unhandled exception and
        // returns 1, which is the refusal exit code. A crash is not a refusal, and a caller that
        // branches on the exit code must be able to tell them apart.
        return parsed.Invoke(new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
        });
    }
}
