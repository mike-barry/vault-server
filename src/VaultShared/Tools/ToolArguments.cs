namespace VaultShared.Tools;

/// <summary>
/// Implemented by the one record per tool that is both the binding target and the source of the
/// advertised schema (see <see cref="NativeToolArguments"/>). Each wire name is the PascalCase
/// member name under <see cref="System.Text.Json.JsonNamingPolicy.SnakeCaseLower"/>, so no name is
/// written twice.
/// </summary>
public interface IToolArguments
{
    /// <summary>Checks the argument surface: shape, exclusivity, parameters that would have no
    /// effect, and unrecognized enumerated values.</summary>
    /// <returns>The first fault found, or null when the arguments hold up. The execute path raises
    /// it as <see cref="ToolCodes.InvalidArgument"/> with the argument in the payload, so one site
    /// decides how an argument refusal reaches the wire.</returns>
    /// <remarks>
    /// Implementations must be pure and must not touch a vault or the filesystem. Every argument
    /// check has to be decidable before a vault is resolved and before anything is locked.
    /// </remarks>
    ArgumentFault? Validate();
}

/// <summary>
/// Implemented by the argument record of a tool that operates in a vault. The execute path finds the
/// vault operand through this interface rather than by matching a member name, which a tool could
/// spell differently and silently escape.
/// </summary>
/// <remarks>
/// A tool never reads this value. Vault resolution runs above the tool, takes the name as sent and
/// substitutes the default vault when it is absent, so a tool holds either a resolved vault or none.
/// </remarks>
public interface IVaultArguments : IToolArguments
{
    /// <summary>The vault name as the caller sent it, or null for the configured default.</summary>
    string? Vault { get; }
}

/// <summary>
/// Represents a fault in a single argument. It carries no code of its own; the execute path decides
/// the code, once.
/// </summary>
/// <param name="Argument">The wire name of the argument at fault.</param>
/// <param name="Message">What was wrong with it.</param>
public sealed record ArgumentFault(string Argument, string Message);

/// <summary>Identifies which kind of operand a caller means. It records the caller's intent and
/// never resolves an ambiguous path.</summary>
public enum OperandKind
{
    /// <summary>The operand is a note.</summary>
    Note,

    /// <summary>The operand is a folder.</summary>
    Folder,
}

/// <summary>
/// Represents the argument surface of a tool that takes no arguments. Declared once here rather than
/// per tool, and <see cref="NativeToolArguments.SchemaFor"/> renders it as the empty-properties
/// schema an MCP <c>inputSchema</c> expects.
/// </summary>
public sealed record NoArguments : IToolArguments
{
    /// <summary>Always returns null; there is nothing to validate.</summary>
    /// <returns>Null.</returns>
    public ArgumentFault? Validate() => null;
}
