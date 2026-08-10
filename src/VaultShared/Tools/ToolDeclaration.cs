using VaultShared.Access;
using VaultShared.Auth;

namespace VaultShared.Tools;

/// <summary>
/// Describes a tool as data. Both front-ends project from this single declaration - the MCP server
/// as a tool definition and its schema, the CLI as a subcommand and its options - and the shared
/// execute path reads the same values.
/// </summary>
/// <remarks>
/// Neither front-end may restate a tool's arguments. A second statement drifts from this one, and
/// the drift is invisible until a caller hits the side that was not updated. Each tool exposes its
/// declaration as a static member on the tool type itself.
/// </remarks>
public sealed record ToolDeclaration
{
    /// <summary>The wire name, in <c>snake_case</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The caller-visible description both front-ends advertise.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The <see cref="IToolArguments"/> record that is both the binding target and the source of the
    /// advertised schema. It is the only statement of this tool's argument surface.
    /// </summary>
    public required Type Arguments { get; init; }

    /// <summary>The least authority any use of this tool requires. Required rather than optional, so
    /// that no declaration reaches the self-gated case by leaving it out.</summary>
    public required Floor OpClassFloor { get; init; }

    /// <summary>Whether an invocation takes the vault's mutex lock. The execute path acquires it; no
    /// tool acquires it for itself, and a tool that declares no mutation never takes one.</summary>
    public bool Mutates { get; init; }

    /// <summary>
    /// Whether the tool needs a caller with a live session. True for the tools that change
    /// per-process authority state keyed to a caller's token, which have nothing to act on in a
    /// front-end that exits when its one command finishes.
    /// </summary>
    public bool RequiresClientSession { get; init; }
}

/// <summary>
/// Represents a tool's declared authority floor: either an op class the execute path checks before
/// the tool runs, or an indication that the tool checks its own authority.
/// </summary>
/// <remarks>
/// The two cases are one member rather than a class name beside a boolean flag, so a declaration
/// cannot name an op class that is never checked. <see cref="SelfGated"/> exists for tools whose
/// authority is not an op class at all; declaring it on the tool avoids keeping a list of exempt
/// tool names somewhere else.
/// </remarks>
public abstract record Floor
{
    private Floor() { }

    /// <summary>An op class the execute path checks against the caller's grant before the tool
    /// runs.</summary>
    /// <param name="OpClass">The op class the caller's grant must allow.</param>
    public sealed record Class(string OpClass) : Floor;

    /// <summary>Indicates the tool checks authority itself, inside its own body.</summary>
    public sealed record SelfGated : Floor;
}

/// <summary>
/// Identifies the caller's live session, where the front-end has one. It names the principal whose
/// authority can be rebound and carries nothing else. A front-end that runs one command and exits
/// has no session.
/// </summary>
/// <param name="PrincipalKey">The derived, non-reversible value identifying the principal. A raw
/// credential never reaches an operation.</param>
public sealed record ClientSession(string PrincipalKey);

/// <summary>
/// Carries everything the shared execute path hands a tool. It holds no transport and nothing
/// ambient, so a tool reads authority only from what it was given. There is deliberately no scope
/// member, because a caller-supplied scope would amount to a forged grant.
/// </summary>
/// <param name="Arguments">The bound argument record, already validated.</param>
/// <param name="Vault">The per-call access object, or null where the tool's arguments name no
/// vault.</param>
/// <param name="Grant">The caller's grant, already floor-checked where the declaration named an op
/// class.</param>
/// <param name="Session">The caller's session, non-null wherever the declaration required one.</param>
public sealed record ToolCall(IToolArguments Arguments, VaultAccess? Vault, Grant Grant, ClientSession? Session)
{
    /// <summary>Returns the bound arguments as the record type the declaration named.</summary>
    /// <typeparam name="T">The argument record type from the tool's declaration.</typeparam>
    /// <returns>The bound arguments. The execute path binds onto exactly that type, so the cast
    /// cannot fail.</returns>
    public T ArgumentsAs<T>() where T : IToolArguments => (T)Arguments;

    /// <summary>
    /// Returns the access object for a tool whose arguments implement <see cref="IVaultArguments"/>.
    /// The execute path constructs one for exactly those arguments, so a missing access object is a
    /// wiring defect rather than a call to refuse.
    /// </summary>
    /// <param name="toolName">The tool's wire name, used in the exception message.</param>
    /// <returns>The per-call access object.</returns>
    /// <exception cref="InvalidOperationException">The tool was registered with an argument record
    /// that names no vault.</exception>
    public VaultAccess RequireVault(string toolName) =>
        Vault ?? throw new InvalidOperationException(
            $"Tool \"{toolName}\" operates in a vault, but its arguments do not implement {nameof(IVaultArguments)}.");
}

/// <summary>
/// Defines the tool surface. A tool returns a <see cref="ToolResult"/> or throws a
/// <see cref="ToolRefusal"/>.
/// </summary>
/// <remarks>
/// The surface is synchronous, and must stay that way. A <see cref="System.Threading.Mutex"/> is
/// thread-affine, so a mutating invocation has to run to completion on the thread that acquired the
/// lock, and the hold must not cross an await.
/// </remarks>
public interface ITool
{
    /// <summary>Runs the tool.</summary>
    /// <param name="call">The validated arguments, access object, grant and session.</param>
    /// <returns>The tool's result.</returns>
    /// <exception cref="ToolRefusal">The tool refused the call.</exception>
    ToolResult Invoke(ToolCall call);
}
