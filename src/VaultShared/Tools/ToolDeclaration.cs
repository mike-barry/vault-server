using VaultShared.Access;
using VaultShared.Auth;

namespace VaultShared.Tools;

/// <summary>
/// A tool declared once, as data. Both front-ends project from it - the MCP server as a tool
/// definition and its schema, the CLI as a subcommand and its options - and the shared execute path
/// reads the same values. Neither front-end carries a second statement of a tool's arguments: two
/// statements drift, and the drift stays invisible until a caller hits the side that was not updated.
///
/// Each implementing type exposes its declaration as a static member, so the flags sit beside the
/// code that is the reason for them and travel with it.
/// </summary>
public sealed record ToolDeclaration
{
    /// <summary>The wire name, in <c>snake_case</c>.</summary>
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>
    /// The <see cref="IToolArguments"/> record that is both the binding target and the source of the
    /// advertised schema. The only statement of this tool's argument surface.
    /// </summary>
    public required Type Arguments { get; init; }

    /// <summary>The least authority any use of this tool requires. <c>required</c>, so no
    /// declaration reaches the self-gated case by omission.</summary>
    public required Floor OpClassFloor { get; init; }

    /// <summary>Whether an invocation takes the vault's mutex lock. A tool declaring no mutation
    /// never takes one, and no tool acquires it for itself.</summary>
    public bool Mutates { get; init; }

    /// <summary>
    /// Whether the tool needs a caller whose authority can be rebound. True on the two tools that
    /// mutate per-process authority state keyed to a live caller's token, so neither has anything to
    /// act on in a process that exits when its one command finishes.
    /// </summary>
    public bool RequiresClientSession { get; init; }
}

/// <summary>
/// A tool's declared authority floor: an op class checked before the tool runs, or the tool's own to
/// check. One member with two cases rather than a class beside a flag, so no declaration names an op
/// class that is never checked and no pair of members can disagree.
///
/// <see cref="SelfGated"/> is the exemption, and it replaces gating a tool by name: a right of
/// connection and a per-grant opt-in are not op classes, and declared, the exemption is a property
/// of the tool rather than a list a third such tool must be added to.
/// </summary>
public abstract record Floor
{
    private Floor() { }

    /// <summary>Checked by the execute path against the caller's grant before the tool runs.</summary>
    public sealed record Class(string OpClass) : Floor;

    /// <summary>Authority is the tool's own to check, inside its own body.</summary>
    public sealed record SelfGated : Floor;
}

/// <summary>
/// The caller's live session, where one exists. It identifies the principal whose authority can be
/// rebound, and nothing more - a front-end that runs one command and exits has none.
/// </summary>
/// <param name="PrincipalKey">The derived, non-reversible value identifying the principal. A raw
/// credential never reaches an operation.</param>
public sealed record ClientSession(string PrincipalKey);

/// <summary>
/// What the shared execute path hands a tool. Nothing on it is a transport, and nothing on it is
/// ambient - a tool reads authority only from what it was handed, and no member takes a scope,
/// because a caller-supplied scope is a forged grant.
/// </summary>
/// <param name="Arguments">The bound argument record, already validated.</param>
/// <param name="Vault">The per-call access object, or null where the tool's arguments name no
/// vault.</param>
/// <param name="Grant">The caller's grant, floor-checked where the declaration named a class.</param>
/// <param name="Session">The caller's session, non-null wherever the declaration required one.</param>
public sealed record ToolCall(IToolArguments Arguments, VaultAccess? Vault, Grant Grant, ClientSession? Session)
{
    /// <summary>The bound arguments as the record the declaration named. The execute path binds onto
    /// exactly that type, so the cast cannot miss.</summary>
    public T ArgumentsAs<T>() where T : IToolArguments => (T)Arguments;
}

/// <summary>
/// The tool surface. <b>Synchronous</b>: the mutex hold must not cross a suspension point and a
/// <see cref="System.Threading.Mutex"/> is thread-affine, so a mutating invocation runs to
/// completion on the thread that acquired it. An asynchronous surface for the non-mutating tools
/// would put back the two tool kinds that one declaration removes.
///
/// A tool answers a <see cref="ToolResult"/> or raises a <see cref="ToolRefusal"/>.
/// </summary>
public interface ITool
{
    ToolResult Invoke(ToolCall call);
}
