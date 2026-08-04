using System.CommandLine;

namespace VaultCli;

/// <summary>
/// One invocation, end to end: read the settings file, run the load cascade, build the command tree
/// from the registry, parse, and run the one tool the command line named.
///
/// <b>The cascade runs before the parse, with no exemption.</b> Configuration that will not load
/// fails the process rather than admitting the invocation unrestricted, and a
/// front-end that answered <c>--help</c> from a process it had refused to configure would be
/// carving the first hole in that rule. The cost is that help needs loadable configuration; the
/// failure names the file to fix.
///
/// The writers are parameters rather than <see cref="Console"/> so a test can run a whole invocation
/// - cascade, tree, execute path, rendering, exit code - as the shipped entry point runs it.
/// </summary>
public static class VaultCliApp
{
    /// <summary>
    /// Runs <paramref name="args"/>.
    /// </summary>
    /// <param name="settingsPath">The CLI's own configuration, defaulting to the file beside the
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

        // The default exception handler is off: it renders an unhandled exception and answers 1,
        // which is the refusal code. A crash is not a refusal, and a caller branching on the two
        // must not have them collapsed.
        return parsed.Invoke(new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
        });
    }
}
