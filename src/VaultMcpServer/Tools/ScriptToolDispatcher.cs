using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultShared.Configuration;
using VaultShared.Frontmatter;
using VaultShared.Access;
using VaultShared.Tools;
using VaultMcpServer.Auth;

namespace VaultMcpServer.Tools;

/// <summary>
/// Wires both tool surfaces into MCP's `tools/list` and `tools/call` - registered
/// via <c>WithListToolsHandler</c>/<c>WithCallToolHandler</c> rather than compiled
/// `[McpServerTool]` methods, since the script tool set is only known by scanning `tools/` at
/// runtime.
///
/// It routes and renders; it does not authorize. A declared tool goes through
/// <see cref="ToolExecution"/>, which checks the session, the op-class floor, the arguments, and
/// vault membership before the tool runs. The script branch has no declaration to carry those
/// checks, so it keeps its own argument-name and vault-membership handling and calls
/// <see cref="ToolExecution.FloorRefusal"/> for the floor - one enforcement site for both surfaces.
///
/// The bearer middleware (see <see cref="BearerGrantMiddleware"/>) only authenticates the caller and
/// scopes the vault; the grant it attached to <c>HttpContext.Items</c> is what both branches
/// authorize against. Every denial reaching a client is the same <c>{code, message, data?}</c> shape
/// a tool script's own error carries, so a caller branches on <c>code</c> whichever layer refused.
///
/// Also tracks the <see cref="McpServer"/> for every session that has made a request, so
/// the `tools/` file watcher (<see cref="ScriptToolsWatcher"/>) can push a
/// `notifications/tools/list_changed` message after a <see cref="ScriptToolRegistry.Refresh"/>.
/// Under the host's stateless HTTP transport a session's <c>McpServer</c> is normally torn
/// down as soon as its one request/response completes, so this is best-effort: a session
/// that has already finished by the time a refresh happens simply won't receive it, and a
/// failed send just drops that (dead) session from the tracked set.
/// </summary>
public sealed class ScriptToolDispatcher(ScriptToolRegistry registry, ToolRegistry toolRegistry, ToolExecution execution, ScriptToolRunner runner, IHttpContextAccessor httpContextAccessor, ServerHealthState healthState, VaultConfig vaultConfig)
{
    private readonly ConcurrentDictionary<McpServer, byte> _sessions = new();

    /// <summary>The script-tool parameter name that targets a vault. A schema exposes it verbatim
    /// from its PowerShell param() block, which spells it "vault" by the snake_case wire-name rule.
    /// Still matched case-insensitively, because the PowerShell binder is - the same reason
    /// <see cref="BearerGrantMiddleware"/>'s body-peek looks it up case-insensitively.</summary>
    private const string VaultParameterName = "vault";

    /// <summary>Placeholder <c>inputSchema</c> for a synthesized broken-script listing entry - it takes no real parameters since the script never loaded far enough to have any.</summary>
    private static readonly JsonElement EmptyObjectSchema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>(),
    });

    // Constructed directly and registered before builder.Build() (see the
    // httpContextAccessor comment in Program.cs), so no ILogger is resolvable yet at
    // construction time. Program.cs calls AttachLogger once the service provider exists.
    private ILogger<ScriptToolDispatcher> _logger = NullLogger<ScriptToolDispatcher>.Instance;

    public void AttachLogger(ILogger<ScriptToolDispatcher> logger) => _logger = logger;

    /// <summary>Number of times <see cref="NotifyToolsListChangedAsync"/> has run - lets tests confirm the <c>tools/</c> watcher calls it after a refresh, independent of whether any session was actually reachable to receive it.</summary>
    public int NotifyCount { get; private set; }

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        TrackSession(context.Server);

        // A script that fails to load produces zero ScriptTool instances - there is nothing
        // to attach a "BROKEN:" prefix to. Instead, synthesize a listing entry per current
        // ScriptToolLoader.HealthSource problem, so a broken script still shows up (as
        // visibly broken) rather than silently vanishing from tools/list. Vault-related
        // problems (source "VaultConfig") are deliberately NOT reflected here: a ScriptTool
        // never declares a fixed vault (it takes one as a call-time argument), so there is no
        // clean, non-heuristic way to attribute a vault problem to a specific tool listing.
        var brokenScriptTools = healthState.Problems
            .Where(p => p.Source == ScriptToolLoader.HealthSource && p.SubjectPath is not null)
            .Select(p => new Tool { Name = Path.GetFileNameWithoutExtension(p.SubjectPath!), Description = $"BROKEN: {p.Detail}", InputSchema = EmptyObjectSchema });

        var tools = toolRegistry.Declarations
            .Select(d => new Tool { Name = d.Name, Description = d.Description, InputSchema = NativeToolArguments.SchemaFor(d.Arguments) })
            .Concat(registry.Tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.BuildInputSchema() }))
            .Concat(brokenScriptTools)
            .ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        TrackSession(context.Server);
        var name = context.Params?.Name ?? "";

        // Declared tools are checked first (a small, fixed set); a name collision with a script
        // tool would be surprising either way, but a declared tool wins since it's compiled and
        // reviewed rather than dropped into tools/ at runtime.
        if (toolRegistry.Find(name) is { } registration)
        {
            var declaredGrant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
            if (declaredGrant is null)
                return NoGrantError(name);

            // Everything a declared tool is gated by - the session, the floor, the arguments, the
            // vault - is the execute path's, including the two tools gated by something other than
            // an op class, which declare Floor.SelfGated rather than being matched by name here.
            return Render(() => execution.Invoke(registration, DeclaredArguments(context), declaredGrant, Session()));
        }

        var tool = registry.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return ToolError.Result(ToolCodes.NotFound, $"Unknown tool: \"{name}\".", new { tool = name });

        var grant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
        if (grant is null)
            return NoGrantError(name);
        // The same floor check the execute path makes, over the class a script declares in its
        // .NOTES block. One site decides what a floor denial is, whichever surface the tool came from.
        if (ToolExecution.FloorRefusal(name, new Floor.Class(tool.OpClass), grant) is { } floorDenial)
            return ToolError.Result(floorDenial);

        // Authoritative vault-membership enforcement, alongside the op-class check above and off
        // the same deserialized arguments the runner will use. The middleware's body-peek (see
        // BearerGrantMiddleware) already rejects an out-of-scope named vault early with a 403, but
        // that peek is best-effort: it can be skipped by an omitted vault arg (which then resolves
        // to the default vault), a non-first batch element, or a chunked/malformed body it could
        // not parse. Re-checking here - the effective vault is the call's vault argument when
        // present, else the configured default vault - is the one that cannot be bypassed.
        //
        // This is the script surface's copy of the check the execute path makes on a type. A
        // param() block offers nothing to mark, so a script's vault operand is still found by
        // matching the parameter name; a declared tool implements IVaultArguments instead, which
        // the compiler carries.
        if (ToolTakesVaultParameter(tool))
        {
            var effectiveVault = ResolveEffectiveVault(context.Params?.Arguments);
            if (!grant.CanAccessVault(effectiveVault))
                return ToolError.Result(
                    ToolCodes.OutOfScope,
                    $"Grant \"{grant.Id}\" is not scoped to vault \"{effectiveVault}\".",
                    // data carries only the call's own coordinates - the tool, and the vault the
                    // call effectively targeted (which the caller may not have named, so it is the
                    // one thing here it cannot already know). The grant's vault set, op classes, and
                    // id stay out: a denial must not become an enumeration surface for authority the
                    // caller does not hold. Same posture as the middleware's detail-free 401.
                    new { tool = name, vault = effectiveVault });
        }

        // Argument-name validation, deliberately the last of the dispatcher's pre-runner checks.
        // Every client-supplied name must match one of the tool's advertised parameters, matched
        // case-insensitively because that is how PowerShell's binder matches - a stricter check
        // here would refuse a name the binder would have bound. Ordering: after the grant,
        // op-class, and vault-membership checks, so a caller holding no authority over this tool
        // gets the authorization denial and a malformed call never doubles as a way to probe which
        // argument names a tool it cannot call advertises; before the runner, because under
        // [CmdletBinding()] an unadvertised name is refused by the binder as a raw
        // ParameterBindingException string surfaced through ErrorResult - outside the
        // {code, message, data?} shape every other refusal here carries. The same binding change
        // is also what would otherwise make PowerShell's common parameters (-Verbose, -Debug,
        // -ErrorAction, -ErrorVariable, -OutVariable, ...) settable by the client on every tool:
        // they are unadvertised names like any other and are refused by this check, so a tool's
        // param() block stays its entire client-facing argument surface. Name-only - type and
        // value validation stay in-script, where the tools already do them and the refusal is
        // already coded.
        if (UnadvertisedArgumentName(tool, context.Params?.Arguments) is { } unknownArgument)
            return ToolError.Result(
                ToolCodes.InvalidArgument,
                $"Tool \"{name}\" has no parameter named \"{unknownArgument}\".",
                // Same posture as the denials above: data carries only the call's own coordinates -
                // the tool named and the offending argument name, both of them the caller's own
                // input. The tool's advertised parameter set stays out; tools/list is where a
                // caller learns it, not a denial.
                new { tool = name, param = unknownArgument });

        var arguments = ConvertArguments(tool, context.Params?.Arguments);
        var scriptExecution = await runner.InvokeAsync(tool.ScriptPath, arguments, cancellationToken, grant.ScopeRoots, grant.OpClasses);
        // A script's own error text is surfaced verbatim, never re-wrapped: a tool script that threw
        // a refusal already carries the {code, message, data?} shape, and re-shaping here
        // would either double-encode it or overwrite its code with a dispatcher-chosen one.
        return scriptExecution.HadErrors
            ? ErrorResult(scriptExecution.ErrorText)
            : new CallToolResult { Content = [new TextContentBlock { Text = scriptExecution.OutputText }] };
    }

    /// <summary>
    /// Defensive branch: no grant on <c>HttpContext.Items</c>, meaning the request did not pass
    /// through <see cref="BearerGrantMiddleware"/> (which always attaches one, down to the
    /// minimum-access floor). Coded as a capability denial because the effect is identical - the
    /// call holds no authority - and a caller should not have to distinguish "no grant" from
    /// "grant lacking the class" to route the failure.
    /// </summary>
    private static CallToolResult NoGrantError(string toolName) => ToolError.Result(
        ToolCodes.InsufficientCapability,
        $"No grant is attached to this request, so tool \"{toolName}\" cannot be dispatched.",
        new { tool = toolName });

    /// <summary>
    /// The caller's session, from the principal identity <see cref="BearerGrantMiddleware"/> derived
    /// for this request. Null only where the middleware did not run, which the declaration's own
    /// session requirement then turns into <c>tool_unavailable</c>.
    /// </summary>
    private ClientSession? Session() =>
        httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.PrincipalTokenHashItemKey] is string principalKey
            ? new ClientSession(principalKey)
            : null;

    /// <summary>The call's raw argument map, which the execute path binds onto the declared record.</summary>
    private static IReadOnlyDictionary<string, JsonElement> DeclaredArguments(RequestContext<CallToolRequestParams> context) =>
        context.Params?.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];

    /// <summary>
    /// MCP's rendering of the neutral result and of every refusal that can reach it. A content
    /// result becomes the text block; a structured result is serialized under the shared options, so
    /// results and arguments share one naming policy and nothing translates between a sent name and
    /// a returned one.
    ///
    /// Three refusal vocabularies converge here, and this is the only site that translates any of
    /// them: no tool catches a refusal it did not raise.
    /// </summary>
    private static CallToolResult Render(Func<ToolResult> invoke)
    {
        try
        {
            return invoke() switch
            {
                ToolResult.Content content => new CallToolResult { Content = [new TextContentBlock { Text = content.Text }] },
                ToolResult.Data data => new CallToolResult
                {
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(data.Value, NativeToolArguments.Options) }],
                },
                var other => throw new InvalidOperationException($"Unhandled tool result {other.GetType().Name}."),
            };
        }
        catch (ToolRefusal refusal)
        {
            return ToolError.Result(refusal);
        }
        catch (FrontmatterRefusal refusal)
        {
            return ToolError.Result(ToolRefusalEnvelope.Translate(refusal));
        }
        catch (AuthorityRefusal refusal)
        {
            return ToolError.Result(ToolRefusalEnvelope.Translate(refusal));
        }
    }

    /// <summary>Whether <paramref name="tool"/> declares a vault-targeting parameter (see
    /// <see cref="VaultParameterName"/>) - the tools this dispatcher's membership check applies to.</summary>
    private static bool ToolTakesVaultParameter(ScriptTool tool) =>
        tool.Parameters.Any(p => string.Equals(p.Name, VaultParameterName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The first client-supplied argument name that matches no parameter <paramref name="tool"/>
    /// advertises, or null when every supplied name is recognized. Matched case-insensitively, the
    /// same way PowerShell's binder matches (and the same reason
    /// <see cref="ResolveEffectiveVault"/> looks up the vault argument case-insensitively). The
    /// tool's advertised parameters are exactly its <c>param()</c> block, so PowerShell's common
    /// parameters - which <c>[CmdletBinding()]</c> adds at bind time but which never appear in the
    /// AST param block <see cref="ScriptToolLoader"/> derives from - are unrecognized here like any
    /// other unadvertised name.
    /// </summary>
    private static string? UnadvertisedArgumentName(ScriptTool tool, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
            return null;
        foreach (var key in arguments.Keys)
        {
            if (!tool.Parameters.Any(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)))
                return key;
        }
        return null;
    }

    /// <summary>
    /// The vault a call effectively targets: the string <c>vault</c> argument when present
    /// (looked up case-insensitively, since the schema casing comes from the param() block), else
    /// the configured default vault. A blank value is treated as absent, matching vault
    /// resolution's own null/blank-to-default handling.
    /// </summary>
    private string ResolveEffectiveVault(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is not null)
        {
            foreach (var (key, value) in arguments)
            {
                if (string.Equals(key, VaultParameterName, StringComparison.OrdinalIgnoreCase)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } named)
                {
                    return named;
                }
            }
        }
        return vaultConfig.DefaultVault;
    }

    /// <summary>
    /// Builds the splat hashtable's contents from the call's JSON arguments. Each argument is
    /// matched to the parameter it names - case-insensitively, the same way the binder matches and
    /// the same way <see cref="UnadvertisedArgumentName"/> above just proved every name resolves -
    /// so <see cref="ScriptToolArgumentEncoder"/> can apply that parameter's own encoding rule. The
    /// match cannot miss in practice, since an unrecognized name was already refused; a null
    /// parameter would simply convert plainly, so the lookup fails safe rather than throwing.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ConvertArguments(ScriptTool tool, IDictionary<string, JsonElement>? arguments)
    {
        var result = new Dictionary<string, object?>();
        if (arguments is null)
            return result;
        foreach (var (key, element) in arguments)
        {
            var parameter = tool.Parameters.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            result[key] = ScriptToolArgumentEncoder.BindArgument(parameter, element);
        }
        return result;
    }

    /// <summary>Passes an already-formed error text through as an <c>isError</c> result. Used only for
    /// a script's own thrown text, which must not be re-shaped - every denial the dispatcher itself
    /// makes goes through <see cref="ToolError"/> instead.</summary>
    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private void TrackSession(McpServer? server)
    {
        if (server is not null)
            _sessions[server] = 0;
    }

    /// <summary>
    /// Broadcasts `notifications/tools/list_changed` to every session tracked so far.
    /// Called by <see cref="ScriptToolsWatcher"/> after <see cref="ScriptToolRegistry.Refresh"/>.
    /// A session whose send fails (its request/response has already completed under the
    /// stateless transport) is dropped from the tracked set rather than retried.
    /// </summary>
    public async Task NotifyToolsListChangedAsync(CancellationToken cancellationToken)
    {
        NotifyCount++;
        foreach (var server in _sessions.Keys)
        {
            try
            {
                await server.SendMessageAsync(
                    new JsonRpcNotification { Method = NotificationMethods.ToolListChangedNotification },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dropping a tracked session that could not be notified of a tools list change.");
                _sessions.TryRemove(server, out _);
            }
        }
    }
}
