using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tools that exist only to be executed. The seven real ones land later, and the execute path's
/// obligations are about a declaration rather than about any tool's work, so they are asserted
/// against declarations built here - which is also what keeps a case able to state the one flag it
/// is about.
/// </summary>
internal sealed record PlainArguments(string? Note = null) : IToolArguments
{
    /// <summary>The one pure rule these cases need: a note operand must carry the markdown
    /// extension. It is never inferred.</summary>
    public ArgumentFault? Validate() =>
        Note is { Length: > 0 } && !Note.EndsWith(".md", StringComparison.Ordinal)
            ? new ArgumentFault("note", $"\"{Note}\" is not a note - a note operand ends in \".md\".")
            : null;
}

internal sealed record VaultBearingArguments(string? Vault = null, string? Note = null) : IVaultArguments
{
    public ArgumentFault? Validate() => new PlainArguments(Note).Validate();
}

/// <summary>Records what it was handed and answers it, so a case can assert on the
/// <see cref="ToolCall"/> the execute path constructed rather than on a side effect.</summary>
internal sealed class RecordingTool(Func<ToolCall, ToolResult>? answer = null) : ITool
{
    public ToolCall? LastCall { get; private set; }

    public int Invocations { get; private set; }

    public ToolResult Invoke(ToolCall call)
    {
        LastCall = call;
        Invocations++;
        return answer?.Invoke(call) ?? new ToolResult.Content("ran");
    }
}

internal static class SyntheticTools
{
    public const string VaultName = "scratch";

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

    /// <summary>A grant over <see cref="VaultName"/> holding exactly <paramref name="opClasses"/>.</summary>
    public static Grant Grant(params string[] opClasses) => GrantIn(VaultName, opClasses);

    public static Grant GrantIn(string vault, params string[] opClasses) => new(
        "synthetic",
        new HashSet<string>(opClasses, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { vault },
        null,
        new Dictionary<string, VaultScope>(StringComparer.OrdinalIgnoreCase) { [vault] = VaultScope.Unscoped });

    public static ClientSession Session { get; } = new("synthetic-principal");
}
