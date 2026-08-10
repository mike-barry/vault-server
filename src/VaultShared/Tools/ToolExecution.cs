using System.Text.Json;
using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Configuration;

namespace VaultShared.Tools;

/// <summary>
/// Runs every tool invocation from either front-end. It performs a fixed sequence of steps before a
/// tool's own work begins: session check, op-class floor, argument binding and validation, vault
/// resolution and membership, construction of the access object, and the write lock where the tool
/// mutates.
/// </summary>
/// <remarks>
/// <para>
/// Do not reorder these steps. A refusal from an earlier step wins over one from a later step, and
/// callers depend on that:
/// </para>
/// <list type="bullet">
/// <item>The floor is checked before arguments are bound, so a caller with no authority over a tool
/// cannot use a malformed call to probe the argument surface of a tool it may not run.</item>
/// <item>Within vault resolution, routing is decided before authority. A vault nobody configured
/// answers <see cref="ToolCodes.UnknownVault"/> rather than an out-of-scope refusal.</item>
/// <item>The lock is taken last, after every check that can refuse without it.</item>
/// </list>
/// <para>
/// Argument validation and vault resolution happen here, above every tool, so no tool repeats them
/// and none can get the order wrong.
/// </para>
/// <para>
/// A tool's declared floor is not its whole authority. A tool asserts anything further inside the
/// path that needs it, through the access object, because the required op class depends on what the
/// work turns out to touch.
/// </para>
/// </remarks>
/// <param name="vaultConfig">The configured vaults a call's vault name resolves against.</param>
/// <param name="engine">The search engine, handed to every access object this constructs rather than
/// read from ambient state.</param>
/// <param name="onInheritedLock">Receives <see cref="VaultWriteLock"/>'s recovery signal.</param>
public sealed class ToolExecution(VaultConfig vaultConfig, SearchEngine? engine = null, Action<string>? onInheritedLock = null)
{
    /// <summary>
    /// Returns the op-class floor denial for a tool, or null where the grant allows it. This is the
    /// only site that decides a floor denial; the MCP dispatcher's script-tool branch calls it too,
    /// with its own declared class, so both tool surfaces are gated by the same check.
    /// </summary>
    /// <param name="toolName">The tool's wire name.</param>
    /// <param name="floor">The tool's declared floor.</param>
    /// <param name="grant">The caller's grant.</param>
    /// <returns>A refusal naming the required op class, or null where the floor is satisfied or the
    /// tool is self-gated. The payload names the class the tool requires, which is what an elevation
    /// request would ask for, and never the classes the grant holds.</returns>
    public static ToolRefusal? FloorRefusal(string toolName, Floor floor, Grant grant)
    {
        if (floor is not Floor.Class(var opClass))
            return null;
        if (grant.Allows(opClass))
            return null;

        return new ToolRefusal(
            ToolCodes.AuthorityRequired,
            $"Grant does not permit {opClass} operations for tool \"{toolName}\".",
            new { tool = toolName, requiredOpClass = opClass });
    }

    /// <summary>
    /// Runs the pre-invocation steps and then invokes the tool.
    /// </summary>
    /// <param name="registration">The tool and its declaration.</param>
    /// <param name="arguments">The call's raw argument map, as the front-end received it.</param>
    /// <param name="grant">The caller's grant.</param>
    /// <param name="session">The caller's session, or null where the front-end has none.</param>
    /// <returns>The tool's result.</returns>
    /// <exception cref="ToolRefusal">A pre-invocation step refused, or the tool itself did.</exception>
    /// <exception cref="InvalidOperationException">The declaration says the tool mutates but its
    /// arguments name no vault to lock.</exception>
    public ToolResult Invoke(
        ToolRegistry.Registration registration,
        IReadOnlyDictionary<string, JsonElement> arguments,
        Grant grant,
        ClientSession? session)
    {
        var (declaration, tool) = registration;

        // Session. Enforced here so the refusal holds on every route into a tool, whatever a
        // front-end lists.
        if (declaration.RequiresClientSession && session is null)
            throw new ToolRefusal(
                ToolCodes.ToolUnavailable,
                $"Tool \"{declaration.Name}\" needs a client session, which this caller does not have.",
                new { tool = declaration.Name });

        // The op-class floor.
        if (FloorRefusal(declaration.Name, declaration.OpClassFloor, grant) is { } denial)
            throw denial;

        // Arguments.
        var bound = Bind(declaration, arguments);
        if (bound.Validate() is { } fault)
            throw new ToolRefusal(ToolCodes.InvalidArgument, fault.Message, new { argument = fault.Argument });

        // Vault resolution and membership. Whether a call targets a vault is decided by the argument
        // record's type, so the compiler enforces it and a new tool cannot opt out by accident.
        Vault? vault = bound is IVaultArguments vaultArguments ? Resolve(declaration, vaultArguments, grant) : null;

        // The access object. Constructed here and nowhere else, so no tool holds one it was not
        // handed.
        var call = new ToolCall(bound, vault is null ? null : new VaultAccess(vault, grant, engine), grant, session);

        if (!declaration.Mutates)
            return tool.Invoke(call);

        // The lock. Its name is derived from the resolved vault, so resolution has to come first. A
        // declaration that mutates with no vault to lock is a wiring defect, not an invocation that
        // quietly runs unlocked. The hold spans the whole invocation, and a preview takes it exactly
        // as an apply does, so a preview cannot return a prediction that was already stale when it
        // was computed.
        if (vault is null)
            throw new InvalidOperationException(
                $"Tool \"{declaration.Name}\" declares that it mutates, but its arguments name no vault to lock.");

        using var hold = VaultWriteLock.Acquire(vault, onInheritedLock);
        return tool.Invoke(call);
    }

    /// <summary>
    /// Binds the raw argument map onto the record type the declaration names, and converts a binding
    /// failure into a coded refusal. This is the only site that decides how a binding failure reaches
    /// the wire, so no tool has to derive one from a serializer message.
    /// </summary>
    /// <param name="declaration">The tool's declaration, naming the argument record type.</param>
    /// <param name="arguments">The call's raw argument map.</param>
    /// <returns>The bound argument record.</returns>
    /// <exception cref="ToolRefusal">The arguments do not match the declared shape.</exception>
    /// <remarks>
    /// Two binding failures are told apart, because their remedies differ. A member the tool does not
    /// declare is removed from the call; a member whose value is the wrong shape is corrected. Both
    /// carry the member's wire name as <c>argument</c>, so either way a caller can lift the name
    /// straight into the call it retries.
    /// </remarks>
    private static IToolArguments Bind(ToolDeclaration declaration, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        try
        {
            return NativeToolArguments.Bind(declaration.Arguments, arguments);
        }
        catch (JsonException exception)
        {
            if (NativeToolArguments.UndeclaredMember(declaration.Arguments, exception) is { } undeclared)
            {
                throw new ToolRefusal(
                    ToolCodes.InvalidArgument,
                    $"Tool \"{declaration.Name}\" takes no argument named \"{undeclared}\". Check the spelling against the arguments it advertises, and send nothing it does not.",
                    new { argument = undeclared });
            }

            var argument = NativeToolArguments.OffendingArgument(exception);
            throw new ToolRefusal(
                ToolCodes.InvalidArgument,
                argument is null
                    ? $"The arguments do not match the shape tool \"{declaration.Name}\" advertises."
                    : $"\"{argument}\" does not match the shape tool \"{declaration.Name}\" advertises for it.",
                argument is null ? null : new { argument });
        }
    }

    /// <summary>
    /// Resolves the call's vault and checks that the grant reaches it.
    /// </summary>
    /// <param name="declaration">The tool's declaration, whose name appears in the refusal
    /// payload.</param>
    /// <param name="arguments">The bound arguments, carrying the vault name the caller gave.</param>
    /// <param name="grant">The caller's grant.</param>
    /// <returns>The resolved vault.</returns>
    /// <exception cref="ToolRefusal">The vault is unknown or unavailable, or the grant is not scoped
    /// to it.</exception>
    private Vault Resolve(ToolDeclaration declaration, IVaultArguments arguments, Grant grant)
    {
        var vault = Vault.Resolve(vaultConfig, arguments.Vault) switch
        {
            VaultResolution.Resolved resolved => resolved.Vault,
            VaultResolution.Unavailable unavailable => throw new ToolRefusal(
                ToolCodes.VaultUnavailable,
                $"Vault \"{unavailable.Name}\" is configured but currently unavailable: {unavailable.Reason}",
                new { vault = unavailable.Name }),
            VaultResolution.Unknown unknown => throw new ToolRefusal(
                ToolCodes.UnknownVault,
                $"No vault is configured under the name \"{unknown.Name}\".",
                new { vault = unknown.Name }),
            var other => throw new InvalidOperationException($"Unhandled vault resolution {other.GetType().Name}."),
        };

        // data carries only the call's own coordinates: the tool, and the vault the call effectively
        // targeted. Keep the grant's vault set, op classes and id out of it, so that a denial cannot
        // be used to enumerate authority the caller does not hold.
        if (!grant.CanAccessVault(vault.Name))
            throw new ToolRefusal(
                ToolCodes.OutOfScope,
                $"Grant \"{grant.Id}\" is not scoped to vault \"{vault.Name}\".",
                new { tool = declaration.Name, vault = vault.Name });

        return vault;
    }
}
