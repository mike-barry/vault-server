using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace VaultShared.Access;

/// <summary>
/// Where the search engine is. Ripgrep backs both enumeration surfaces and content search
/// (<c>Spec-Vault-Access-Design.md:Fixed Constraints</c>), and this type is how its location reaches
/// the layer: handed to <see cref="VaultAccess"/> by the caller that constructed it. The layer never
/// reads the location from an environment variable, a configuration file, or anything else it could
/// reach unhanded - the same rule scope is held to, for the same reason.
///
/// <see cref="Executable"/> unset means the engine is found on <c>PATH</c>, which is what a host
/// that configures no explicit path falls back to.
/// </summary>
public sealed record SearchEngine(string? Executable = null)
{
    /// <summary>The engine as <c>PATH</c> resolves it.</summary>
    public static SearchEngine OnPath { get; } = new();

    private string Command => Executable is { Length: > 0 } path ? path : "rg";

    /// <summary>
    /// Runs the engine once, in <paramref name="workingDirectory"/>, and reads both streams to
    /// completion before answering. Rooted at the vault directory is what makes every path the
    /// engine emits vault-relative; reading to completion is what makes "this search could not read
    /// a file" decidable, which streaming results would not be.
    ///
    /// Synchronous, because the tool surface above is: a mutating invocation holds a thread-affine
    /// mutex across the whole call, so nothing below it may suspend. Neither front-end installs a
    /// synchronization context, so blocking here cannot deadlock.
    /// </summary>
    internal EngineRun Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            var result = Cli.Wrap(Command)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                // The engine's own exit codes are the classification, so a non-zero one is an
                // answer to read rather than an exception to raise.
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

    /// <summary>Not an exit code the engine produces; it marks a run that never started.</summary>
    internal const int LaunchFailed = -1;
}

/// <summary>One completed run of the engine, both streams whole.</summary>
internal sealed record EngineRun(int ExitCode, string StandardOutput, string StandardError);
