using System.Text.Json;
using VaultShared.Access;
using VaultShared.Auth;
using VaultShared.Configuration;

namespace VaultShared.Tools;

/// <summary>
/// The one path every tool invocation takes, from either front-end. It runs six steps before a
/// tool's own work begins, and <b>a refusal from an earlier step wins over one from a later</b>:
///
/// <list type="number">
/// <item><b>Session.</b> A declaration requiring a client session, invoked without one, refuses
/// <see cref="ToolCodes.ToolUnavailable"/>.</item>
/// <item><b>The op-class floor</b>, skipped on <see cref="Floor.SelfGated"/>.</item>
/// <item><b>Arguments</b>, bound onto the declared record and then validated.</item>
/// <item><b>Vault resolution and membership</b>, for arguments carrying a vault operand.</item>
/// <item><b>The lock</b>, where the declaration says the tool mutates.</item>
/// <item><b>The access object</b>, constructed from the resolved vault and the grant.</item>
/// </list>
///
/// These six <em>precede</em> a tool's own phases rather than interleaving with them: argument
/// validation and vault resolution happen here, above every tool, which is what makes the ordering
/// guarantee structural rather than a rule each tool remembers.
///
/// <b>Authorization precedes argument feedback.</b> The floor is checked before arguments are bound,
/// so a caller holding no authority over a tool cannot use a malformed call to probe the argument
/// surface of one it may not run. It costs nothing, since the lock is later than both.
///
/// <b>Routing precedes authorization within step 4</b>: an unknown vault answers
/// <see cref="ToolCodes.UnknownVault"/> rather than an out-of-scope refusal for a vault nobody
/// configured.
///
/// A tool's declared floor is not its whole authority. Anything more is asserted inside the tool on
/// the path that needs it, through the access object, because the required class depends on what the
/// work turned out to touch.
/// </summary>
/// <param name="engine">Where the search engine is, handed to every access object this constructs
/// rather than read from ambient state.</param>
/// <param name="onInheritedLock">Where <see cref="VaultWriteLock"/>'s recovery signal goes.</param>
public sealed class ToolExecution(VaultConfig vaultConfig, SearchEngine? engine = null, Action<string>? onInheritedLock = null)
{
    /// <summary>
    /// The op-class floor denial, and the only site that decides one. The script-tool branch of the
    /// MCP dispatcher calls it too, over its own declared class, so both tool surfaces are gated by
    /// one check rather than two that can drift.
    ///
    /// <c>data</c> names the required class - a property of the tool, and the class an elevation
    /// request would ask for - but never the classes the grant actually holds.
    /// </summary>
    public static ToolRefusal? FloorRefusal(string toolName, Floor floor, Grant grant)
    {
        if (floor is not Floor.Class(var opClass))
            return null;
        if (grant.Allows(opClass))
            return null;

        return new ToolRefusal(
            ToolCodes.InsufficientCapability,
            $"Grant does not permit {opClass} operations for tool \"{toolName}\".",
            new { tool = toolName, requiredOpClass = opClass });
    }

    /// <summary>
    /// Runs the six steps and invokes the tool.
    /// </summary>
    /// <param name="arguments">The call's raw argument map, as the front-end received it.</param>
    /// <param name="session">The caller's session, or null where the front-end has none.</param>
    /// <exception cref="ToolRefusal">Any of the six steps refused, or the tool itself did.</exception>
    public ToolResult Invoke(
        ToolRegistry.Registration registration,
        IReadOnlyDictionary<string, JsonElement> arguments,
        Grant grant,
        ClientSession? session)
    {
        var (declaration, tool) = registration;

        // 1. Session. Enforced here rather than by omitting a subcommand: a front-end that can reach
        //    a tool by name would otherwise make a listing filter cosmetic.
        if (declaration.RequiresClientSession && session is null)
            throw new ToolRefusal(
                ToolCodes.ToolUnavailable,
                $"Tool \"{declaration.Name}\" needs a client session, which this caller does not have.",
                new { tool = declaration.Name });

        // 2. The op-class floor.
        if (FloorRefusal(declaration.Name, declaration.OpClassFloor, grant) is { } denial)
            throw denial;

        // 3. Arguments.
        var bound = Bind(declaration, arguments);
        if (bound.Validate() is { } fault)
            throw new ToolRefusal(ToolCodes.InvalidArgument, fault.Message, new { argument = fault.Argument });

        // 4. Vault resolution and membership. Whether a call targets a vault is a type question, so
        //    the compiler carries it and no new tool can opt out by accident.
        Vault? vault = bound is IVaultArguments vaultArguments ? Resolve(declaration, vaultArguments, grant) : null;

        // 6. The access object. Constructed here and nowhere else, so no tool holds one it was not
        //    handed.
        var call = new ToolCall(bound, vault is null ? null : new VaultAccess(vault, grant, engine), grant, session);

        if (!declaration.Mutates)
            return tool.Invoke(call);

        // 5. The lock. Its name comes from the resolved vault, which is why resolution precedes it,
        //    and a declaration that mutates with no vault to lock is a wiring defect rather than an
        //    invocation that quietly runs unlocked. The hold spans the whole invocation: a plan takes
        //    it exactly as an apply does, so a preview cannot return a prediction that was already
        //    stale when it was computed.
        if (vault is null)
            throw new InvalidOperationException(
                $"Tool \"{declaration.Name}\" declares that it mutates, but its arguments name no vault to lock.");

        using var hold = VaultWriteLock.Acquire(vault, onInheritedLock);
        return tool.Invoke(call);
    }

    /// <summary>
    /// Binds the raw map onto the declared record. A binding failure is not a coded refusal until it
    /// is re-raised as one here - the one site that decides how a shape mismatch reaches the wire, so
    /// no tool re-derives it from a serializer message.
    /// </summary>
    private static IToolArguments Bind(ToolDeclaration declaration, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        try
        {
            return NativeToolArguments.Bind(declaration.Arguments, arguments);
        }
        catch (JsonException exception)
        {
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
    /// Resolves the call's vault and checks the grant reaches it. Routing beats authorization: a call
    /// naming a vault nobody configured is told that, rather than being refused for a scope that
    /// could never have admitted it.
    /// </summary>
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

        // data carries only the call's own coordinates - the tool, and the vault the call effectively
        // targeted, which the caller may not have named and so is the one thing here it cannot
        // already know. The grant's vault set, op classes and id stay out: a denial must not become
        // an enumeration surface for authority the caller does not hold.
        if (!grant.CanAccessVault(vault.Name))
            throw new ToolRefusal(
                ToolCodes.OutOfScope,
                $"Grant \"{grant.Id}\" is not scoped to vault \"{vault.Name}\".",
                new { tool = declaration.Name, vault = vault.Name });

        return vault;
    }
}
