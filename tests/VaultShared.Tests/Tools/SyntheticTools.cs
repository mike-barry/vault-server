using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Represents the arguments of a synthetic tool that takes no vault. The shared execute path's
/// behaviour depends on a tool's declaration rather than on what the tool does, so it is tested
/// against declarations built here, each stating only the flag its test is about.
/// </summary>
/// <param name="Note">An optional note operand, which must end in <c>.md</c>.</param>
internal sealed record PlainArguments(string? Note = null) : IToolArguments
{
    /// <summary>Checks that a note operand carries the markdown extension. The extension is never
    /// inferred.</summary>
    /// <returns>The fault, or null if the arguments are valid.</returns>
    public ArgumentFault? Validate() =>
        Note is { Length: > 0 } && !Note.EndsWith(".md", StringComparison.Ordinal)
            ? new ArgumentFault("note", $"\"{Note}\" is not a note - a note operand ends in \".md\".")
            : null;
}

/// <summary>Represents the arguments of a synthetic tool that names a vault.</summary>
/// <param name="Vault">The vault the call names, or null to leave it to the caller's default.</param>
/// <param name="Note">An optional note operand, which must end in <c>.md</c>.</param>
internal sealed record VaultBearingArguments(string? Vault = null, string? Note = null) : IVaultArguments
{
    /// <summary>Applies the same note operand rule as <see cref="PlainArguments"/>.</summary>
    /// <returns>The fault, or null if the arguments are valid.</returns>
    public ArgumentFault? Validate() => new PlainArguments(Note).Validate();
}

/// <summary>Records the call it was given and returns a result, so a test can assert on the
/// <see cref="ToolCall"/> the execute path built rather than on a side effect.</summary>
/// <param name="answer">Produces the result for a call; returns a fixed text result if null.</param>
internal sealed class RecordingTool(Func<ToolCall, ToolResult>? answer = null) : ITool
{
    /// <summary>Gets the most recent call, or null if the tool has not been invoked.</summary>
    public ToolCall? LastCall { get; private set; }

    /// <summary>Gets the number of times the tool has been invoked.</summary>
    public int Invocations { get; private set; }

    /// <summary>Records the call and returns the configured result.</summary>
    /// <param name="call">The call the execute path built.</param>
    /// <returns>The result the answer produced, or a fixed text result.</returns>
    public ToolResult Invoke(ToolCall call)
    {
        LastCall = call;
        Invocations++;
        return answer?.Invoke(call) ?? new ToolResult.Content("ran");
    }
}

/// <summary>Builds the declarations, grants and session the execute path tests run against.</summary>
internal static class SyntheticTools
{
    /// <summary>The name of the vault these fixtures grant access to.</summary>
    public const string VaultName = "scratch";

    /// <summary>Builds a tool declaration, defaulting every part a test does not care about.</summary>
    /// <param name="name">The tool's name.</param>
    /// <param name="arguments">The arguments type; <see cref="PlainArguments"/> if null.</param>
    /// <param name="floor">The op class floor; self-gated if null.</param>
    /// <param name="mutates">Whether the tool declares that it mutates the vault.</param>
    /// <param name="requiresClientSession">Whether the tool requires a client session.</param>
    /// <returns>The declaration.</returns>
    public static ToolDeclaration Declaration(
        string name = "probe",
        Type? arguments = null,
        Floor? floor = null,
        bool mutates = false,
        bool requiresClientSession = false) => new()
        {
            Name = name,
            Description = $"A synthetic tool named {name}.",
            Arguments = arguments ?? typeof(PlainArguments),
            OpClassFloor = floor ?? new Floor.SelfGated(),
            Mutates = mutates,
            RequiresClientSession = requiresClientSession,
        };

    /// <summary>Builds an unscoped grant over <see cref="VaultName"/>.</summary>
    /// <param name="opClasses">The op classes the grant holds, and no others.</param>
    /// <returns>The grant.</returns>
    public static Grant Grant(params string[] opClasses) => GrantIn(VaultName, opClasses);

    /// <summary>Builds an unscoped grant over a named vault.</summary>
    /// <param name="vault">The vault the grant covers.</param>
    /// <param name="opClasses">The op classes the grant holds, and no others.</param>
    /// <returns>The grant.</returns>
    public static Grant GrantIn(string vault, params string[] opClasses) => new(
        "synthetic",
        new HashSet<string>(opClasses, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { vault },
        null,
        new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { [vault] = VaultScope.Unscoped });

    /// <summary>Gets a client session for tools that require one.</summary>
    public static ClientSession Session { get; } = new("synthetic-principal");
}
