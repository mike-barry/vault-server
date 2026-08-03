using VaultShared.Tools;

namespace VaultCli;

/// <summary>
/// The invocation could not be established: the configuration would not load, or no grant could be
/// resolved to run as. Raised before any tool is reached, and rendered as the same
/// <c>{code, message, data?}</c> envelope a tool refusal is - because the exit code says only that
/// the invocation was refused, and <c>code</c> is where the vocabulary lives.
///
/// <b>Every case here is fail-closed.</b> A single-invocation front-end has nothing to stay up for,
/// so where the server records a problem and starts degraded, this fails
/// (<c>Spec-Auth-Requirements.md:Both Front-Ends Are Granted</c>). There is no path from a missing
/// or unreadable value to authority.
/// </summary>
public sealed class CliFailure(ToolRefusal refusal) : Exception(refusal.Message)
{
    /// <summary>The envelope this renders as.</summary>
    public ToolRefusal Refusal { get; } = refusal;

    /// <summary>
    /// A prerequisite of the invocation that the call itself cannot influence: the settings file,
    /// the load cascade, or a grant id naming a row that is not there. Neither the caller's
    /// arguments nor its authority would change the outcome, which is what
    /// <see cref="ToolCodes.DependencyUnavailable"/> means.
    /// </summary>
    public static CliFailure Configuration(string message, object? data = null) =>
        new(new ToolRefusal(ToolCodes.DependencyUnavailable, message, data));

    /// <summary>
    /// A value on the command line that cannot become the argument it stands for - the one shape
    /// question this front-end answers before the binder does. It spends the same code and the same
    /// <c>{argument}</c> payload the execute path spends on a shape mismatch, so a caller branches on
    /// one code however far the value got.
    /// </summary>
    public static CliFailure Argument(string wireName, string message) =>
        new(new ToolRefusal(ToolCodes.InvalidArgument, message, new { argument = wireName }));
}
