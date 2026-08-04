using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace VaultShared.Access;

/// <summary>
/// Locates the ripgrep executable that backs both enumeration surfaces and content search. The
/// caller that constructs <see cref="VaultAccess"/> hands one of these in.
/// </summary>
/// <remarks>
/// The location must be passed in. This layer never reads it from an environment variable, a
/// configuration file, or any other ambient source, for the same reason it never reads scope from
/// one.
/// </remarks>
/// <param name="Executable">
/// The engine's location, or null to find it on <c>PATH</c>. Null is what a host that configures no
/// explicit path falls back to.
/// </param>
public sealed record SearchEngine(string? Executable = null)
{
    /// <summary>Gets the engine as <c>PATH</c> resolves it.</summary>
    public static SearchEngine OnPath { get; } = new();

    private string Command => Executable is { Length: > 0 } path ? path : "rg";

    /// <summary>
    /// Runs the engine once and reads both of its streams to completion before returning.
    /// </summary>
    /// <remarks>
    /// Starting the engine in the vault directory is what makes every path it emits vault-relative.
    /// Reading both streams to completion is what makes "this search could not read a file"
    /// decidable; streaming results would not be.
    ///
    /// The call is synchronous on purpose. A mutating tool invocation above holds a thread-affine
    /// mutex across the whole call, so nothing below it may suspend. Neither front-end installs a
    /// synchronization context, so blocking here cannot deadlock.
    /// </remarks>
    /// <param name="workingDirectory">The directory to start the engine in, always the vault root.</param>
    /// <param name="arguments">The engine's command-line arguments.</param>
    /// <returns>The exit code and both streams of the completed run.</returns>
    internal EngineRun Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            var result = Cli.Wrap(Command)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                // The engine's exit codes are how a run is classified, so a non-zero code must be
                // returned for the caller to read rather than thrown.
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(Encoding.UTF8)
                .GetAwaiter()
                .GetResult();

            return new EngineRun(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // The engine is not where it was said to be, or the platform refused to start it.
            return new EngineRun(LaunchFailed, string.Empty, ex.Message);
        }
    }

    /// <summary>Marks a run that never started. The engine itself never produces this exit code.</summary>
    internal const int LaunchFailed = -1;
}

/// <summary>Represents one completed run of the engine, with both streams read whole.</summary>
/// <param name="ExitCode">The engine's exit code, or <see cref="SearchEngine.LaunchFailed"/>.</param>
/// <param name="StandardOutput">Everything the run wrote to standard output.</param>
/// <param name="StandardError">Everything the run wrote to standard error.</param>
internal sealed record EngineRun(int ExitCode, string StandardOutput, string StandardError);
