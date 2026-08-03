namespace VaultShared.Tools;

/// <summary>
/// One record per tool is both the binding target and the source of the advertised schema
/// (<see cref="NativeToolArguments"/>). Every wire name is the PascalCase member under
/// <see cref="System.Text.Json.JsonNamingPolicy.SnakeCaseLower"/>; none is authored twice.
///
/// <see cref="Validate"/> is pure - shape, exclusivity, an inert parameter, an unrecognized
/// enumerated value - so the whole argument surface can be refused before a vault is resolved and
/// before anything is locked. It reaches no vault and no filesystem, which is what makes the
/// execute path's argument phase real rather than a claim about where the checks happen to sit.
/// </summary>
public interface IToolArguments
{
    /// <summary>The first fault in the argument surface, or null when it holds up. The execute
    /// path raises it as <see cref="ToolCodes.InvalidArgument"/> with the argument in the payload,
    /// so one site decides how an argument refusal reaches the wire.</summary>
    ArgumentFault? Validate();
}

/// <summary>
/// The argument surface of a tool that operates in a vault. Implementing it is how the execute path
/// finds the operand without matching a member name: a compiled tool spelling it differently would
/// escape a name match silently, which is the same gap in a new place.
///
/// A tool never sees this value. Resolution takes the name as sent and answers the default vault
/// when it is absent, and it runs above the tool, so a tool holds a resolved vault or none.
/// </summary>
public interface IVaultArguments : IToolArguments
{
    /// <summary>The vault name as sent, or null for the configured default.</summary>
    string? Vault { get; }
}

/// <summary>
/// A fault in one argument: which one, and what was wrong with it. It carries no code of its own -
/// the execute path decides that, once.
/// </summary>
public sealed record ArgumentFault(string Argument, string Message);

/// <summary>Which of the two operand kinds a caller means. It checks intent; it never resolves an
/// ambiguous path.</summary>
public enum OperandKind
{
    Note,
    Folder,
}

/// <summary>
/// The argument surface of a tool that takes none. A zero-member record is a deserialization target
/// with nothing to deserialize, so it is stated once here rather than per tool, and
/// <see cref="NativeToolArguments.SchemaFor"/> gives it the empty-properties schema an MCP
/// <c>inputSchema</c> wants.
/// </summary>
public sealed record NoArguments : IToolArguments
{
    public ArgumentFault? Validate() => null;
}
