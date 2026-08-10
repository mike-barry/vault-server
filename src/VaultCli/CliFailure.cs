using VaultShared.Tools;

namespace VaultCli;

/// <summary>
/// Represents a failure to establish the invocation: the configuration would not load, or no grant
/// could be resolved to run as. It is thrown before any tool is reached and renders as the same
/// envelope a tool refusal renders as.
/// </summary>
/// <remarks>
/// Every case here is fail-closed. Where the server records a configuration problem and starts in a
/// degraded state, this front-end fails the process instead. No missing or unreadable value may lead
/// to an invocation running unrestricted.
/// </remarks>
/// <param name="refusal">The refusal envelope this failure renders as.</param>
public sealed class CliFailure(ToolRefusal refusal) : Exception(refusal.Message)
{
    /// <summary>Gets the refusal envelope this failure renders as.</summary>
    public ToolRefusal Refusal { get; } = refusal;

    /// <summary>
    /// Creates a failure for a prerequisite the call itself cannot influence: the settings file, the
    /// load cascade, or a grant id naming a row that is not there. Neither the caller's arguments nor
    /// its authority would change the outcome, which is what
    /// <see cref="ToolCodes.DependencyUnavailable"/> means.
    /// </summary>
    /// <param name="message">The message shown to the operator.</param>
    /// <param name="data">Optional payload for the envelope's <c>data</c> member.</param>
    /// <returns>The failure to throw.</returns>
    public static CliFailure Configuration(string message, object? data = null) =>
        new(new ToolRefusal(ToolCodes.DependencyUnavailable, message, data));

    /// <summary>
    /// Creates a failure for a command-line value that cannot become the argument it stands for. It
    /// uses the same code and the same <c>{argument}</c> payload the shared execute path uses for a
    /// type mismatch, so a caller branches on one code wherever the value was rejected.
    /// </summary>
    /// <param name="wireName">The argument's wire name, reported in the envelope's <c>data</c>.</param>
    /// <param name="message">The message shown to the operator.</param>
    /// <returns>The failure to throw.</returns>
    public static CliFailure Argument(string wireName, string message) =>
        new(new ToolRefusal(ToolCodes.InvalidArgument, message, new { argument = wireName }));
}
