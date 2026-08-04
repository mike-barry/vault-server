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
/// Serves MCP's <c>tools/list</c> and <c>tools/call</c> over both tool surfaces, the compiled
/// declared tools and the PowerShell script tools.
/// </summary>
/// <remarks>
/// <para>
/// The handlers are registered through <c>WithListToolsHandler</c> and
/// <c>WithCallToolHandler</c> rather than as compiled <c>[McpServerTool]</c> methods, because the
/// script tool set is only known by scanning the tools directory at runtime.
/// </para>
/// <para>
/// This type routes and renders; it does not authorize. A declared tool goes through
/// <see cref="ToolExecution"/>, which checks the session, the op-class floor, the arguments, and
/// vault membership before the tool runs. A script tool has no declaration to carry those
/// checks, so this class does its own argument-name and vault-membership checks and calls
/// <see cref="ToolExecution.FloorRefusal"/> for the floor, keeping one enforcement site for both
/// surfaces.
/// </para>
/// <para>
/// <see cref="BearerGrantMiddleware"/> only authenticates the caller and scopes the vault; the
/// grant it attached to <c>HttpContext.Items</c> is what both branches authorize against. Every
/// denial reaching a client has the same <c>{code, message, data?}</c> shape a tool script's own
/// error carries, so a caller can branch on <c>code</c> whichever layer refused.
/// </para>
/// <para>
/// The dispatcher also tracks the <see cref="McpServer"/> of every session that has made a
/// request, so <see cref="ScriptToolsWatcher"/> can push a
/// <c>notifications/tools/list_changed</c> message after a refresh. Under the stateless HTTP
/// transport a session's <c>McpServer</c> is normally torn down as soon as its single
/// request completes, so the notification is best-effort: a session that has already finished
/// will not receive it, and a failed send drops that session from the tracked set.
/// </para>
/// </remarks>
/// <param name="registry">Supplies the current script tools.</param>
/// <param name="toolRegistry">Supplies the declared tools and their declarations.</param>
/// <param name="execution">The shared execute path for declared tools and the floor check.</param>
/// <param name="runner">Runs a script tool in a pooled runspace.</param>
/// <param name="httpContextAccessor">Reads the grant and principal key the middleware attached.</param>
/// <param name="healthState">Supplies the problems that become BROKEN listing entries.</param>
/// <param name="vaultConfig">Supplies the default vault for schemas and vault resolution.</param>
public sealed class ScriptToolDispatcher(ScriptToolRegistry registry, ToolRegistry toolRegistry, ToolExecution execution, ScriptToolRunner runner, IHttpContextAccessor httpContextAccessor, ServerHealthState healthState, VaultConfig vaultConfig)
{
    private readonly ConcurrentDictionary<McpServer, byte> _sessions = new();

    /// <summary>
    /// The script-tool parameter name that targets a vault. A schema exposes it verbatim from
    /// the script's <c>param()</c> block, which spells it in snake_case. It is still matched
    /// case-insensitively, because the PowerShell binder is.
    /// </summary>
    private const string VaultParameterName = "vault";

    /// <summary>Placeholder <c>inputSchema</c> for a synthesized broken-script listing entry - it takes no real parameters since the script never loaded far enough to have any.</summary>
    private static readonly JsonElement EmptyObjectSchema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>(),
    });

    /// <summary>
    /// The declared tools' listing entries, rendered once at construction.
    /// </summary>
    /// <remarks>
    /// The schemas carry the default vault's frontmatter vocabulary, because a listing is not
    /// per vault and the default vault is what an absent <c>vault</c> argument targets. A caller
    /// naming another vault is validated against that vault's own declaration at the write path,
    /// which is the only enforcement point either way, so advertising one vault's vocabulary
    /// grants nothing for another. Where no declaration loaded, the vocabulary-dependent
    /// branches are simply absent from the schema.
    ///
    /// Rendering happens once because nothing these entries depend on changes: declarations are
    /// compiled, and the frontmatter registries load at startup with no hot reload. Script tools
    /// are the part of a listing that does change, and they are assembled per call.
    /// </remarks>
    private readonly IReadOnlyList<Tool> _declaredTools =
    [
        .. toolRegistry.Registrations.Select(r => new Tool
        {
            Name = r.Declaration.Name,
            Description = r.Declaration.Description,
            InputSchema = r.AdvertisedSchema(vaultConfig.DefaultVault),
        })
    ];

    // This type is constructed before builder.Build(), so no ILogger is resolvable at
    // construction time. Program.cs calls AttachLogger once the service provider exists.
    private ILogger<ScriptToolDispatcher> _logger = NullLogger<ScriptToolDispatcher>.Instance;

    /// <summary>Sets the logger used for dispatch diagnostics.</summary>
    /// <param name="logger">The logger to write to.</param>
    public void AttachLogger(ILogger<ScriptToolDispatcher> logger) => _logger = logger;

    /// <summary>
    /// Gets the number of times <see cref="NotifyToolsListChangedAsync"/> has run. Tests read it
    /// to confirm the watcher calls the dispatcher after a refresh, whether or not any session
    /// was still reachable to receive the notification.
    /// </summary>
    public int NotifyCount { get; private set; }

    /// <summary>Answers an MCP <c>tools/list</c> request.</summary>
    /// <param name="context">The request context, which also carries the calling session's server.</param>
    /// <param name="cancellationToken">Not used; the listing is assembled from state already in memory.</param>
    /// <returns>The declared tools, then the loaded script tools, then one entry per broken script.</returns>
    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        TrackSession(context.Server);

        // A script that fails to load produces no ScriptTool instance, so there is nothing to
        // mark as broken. Instead one listing entry is synthesized per current script-tool
        // problem, so a broken script still appears in tools/list rather than vanishing from
        // it. Vault problems are not reflected here: a script tool never declares a fixed vault,
        // taking one as a call-time argument instead, so a vault problem cannot be attributed to
        // a particular tool.
        var brokenScriptTools = healthState.Problems
            .Where(p => p.Source == ScriptToolLoader.HealthSource && p.SubjectPath is not null)
            .Select(p => new Tool { Name = Path.GetFileNameWithoutExtension(p.SubjectPath!), Description = $"BROKEN: {p.Detail}", InputSchema = EmptyObjectSchema });

        var tools = _declaredTools
            .Concat(registry.Tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.BuildInputSchema() }))
            .Concat(brokenScriptTools)
            .ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    /// <summary>Answers an MCP <c>tools/call</c> request over either tool surface.</summary>
    /// <param name="context">The request context, carrying the tool name and arguments.</param>
    /// <param name="cancellationToken">Stops a running script tool when cancelled.</param>
    /// <returns>The tool's result, or an <c>isError</c> result carrying a refusal envelope.</returns>
    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        TrackSession(context.Server);
        var name = context.Params?.Name ?? "";

        // Declared tools are checked first. A name collision between the two surfaces would be
        // surprising either way, but a declared tool wins because it is compiled and reviewed
        // rather than dropped into the tools directory at runtime.
        if (toolRegistry.Find(name) is { } registration)
        {
            var declaredGrant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
            if (declaredGrant is null)
                return NoGrantError(name);

            // The execute path applies everything a declared tool is gated by: the session, the
            // floor, the arguments, and the vault. That includes the two tools gated by
            // something other than an op class, which declare Floor.SelfGated rather than being
            // matched by name here.
            return Render(() => execution.Invoke(registration, DeclaredArguments(context), declaredGrant, Session()));
        }

        var tool = registry.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return ToolError.Result(ToolCodes.NotFound, $"Unknown tool: \"{name}\".", new { tool = name });

        var grant = httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.GrantItemKey] as Grant;
        if (grant is null)
            return NoGrantError(name);
        // The same floor check the execute path makes, over the class the script declares in its
        // .NOTES block, so one site decides what a floor denial is on either surface.
        if (ToolExecution.FloorRefusal(name, new Floor.Class(tool.OpClass), grant) is { } floorDenial)
            return ToolError.Result(floorDenial);

        // The authoritative vault-membership check, made against the same deserialized arguments
        // the runner will use. The middleware's body peek already rejects an out-of-scope named
        // vault with a 403, but that peek is best-effort: it misses an omitted vault argument
        // that resolves to the default vault, a non-first batch element, and a chunked or
        // malformed body it could not parse. This check cannot be bypassed.
        //
        // This is the script surface's copy of the check the execute path makes on a declared
        // tool's arguments type. A param() block offers nothing to mark, so a script's vault
        // operand is found by parameter name; a declared tool implements IVaultArguments
        // instead.
        if (ToolTakesVaultParameter(tool))
        {
            var effectiveVault = ResolveEffectiveVault(context.Params?.Arguments);
            if (!grant.CanAccessVault(effectiveVault))
                return ToolError.Result(
                    ToolCodes.OutOfScope,
                    $"Grant \"{grant.Id}\" is not scoped to vault \"{effectiveVault}\".",
                    // data carries only the call's own coordinates: the tool, and the vault the
                    // call effectively targeted, which the caller may not have named. The
                    // grant's vault set, op classes, and id stay out, so a denial cannot be used
                    // to enumerate authority the caller does not hold.
                    new { tool = name, vault = effectiveVault });
        }

        // Argument-name validation, and the last of the dispatcher's checks before the runner.
        // Every client-supplied name must match one of the tool's advertised parameters, matched
        // case-insensitively because that is how PowerShell's binder matches; a stricter check
        // would refuse a name the binder would have bound.
        //
        // Do not reorder this. It runs after the grant, op-class, and vault-membership checks so
        // a caller holding no authority over the tool gets the authorization denial, and a
        // malformed call cannot be used to probe which argument names a tool it cannot call
        // advertises. It runs before the runner because under [CmdletBinding()] the binder
        // refuses an unadvertised name as a raw ParameterBindingException string, which would
        // reach the client outside the {code, message, data?} shape every other refusal carries.
        // That same binding behavior would also make PowerShell's common parameters (-Verbose,
        // -Debug, -ErrorAction, -ErrorVariable, -OutVariable, and the rest) settable by the
        // client on every tool; they are unadvertised names like any other and are refused here,
        // so a tool's param() block stays its entire client-facing argument surface.
        //
        // Names only. Type and value validation stay in-script, where the tools already do them
        // and the refusal already carries a code.
        if (UnadvertisedArgumentName(tool, context.Params?.Arguments) is { } unknownArgument)
            return ToolError.Result(
                ToolCodes.InvalidArgument,
                $"Tool \"{name}\" has no parameter named \"{unknownArgument}\".",
                // As with the denials above, data carries only the caller's own input: the tool
                // named and the offending argument name. The tool's advertised parameter set
                // stays out, because tools/list is where a caller learns it.
                new { tool = name, param = unknownArgument });

        var arguments = ConvertArguments(tool, context.Params?.Arguments);
        var scriptExecution = await runner.InvokeAsync(tool.ScriptPath, arguments, cancellationToken, grant.ScopeRoots, grant.OpClasses);
        // A script's own error text is surfaced verbatim and never re-wrapped. A tool script
        // that threw a refusal already carries the {code, message, data?} shape, so re-shaping
        // it here would either double-encode it or replace its code with one chosen here.
        return scriptExecution.HadErrors
            ? ErrorResult(scriptExecution.ErrorText)
            : new CallToolResult { Content = [new TextContentBlock { Text = scriptExecution.OutputText }] };
    }

    /// <summary>
    /// Builds the refusal for a call that arrived with no grant on <c>HttpContext.Items</c>,
    /// meaning the request did not pass through <see cref="BearerGrantMiddleware"/>, which always
    /// attaches one even if only the minimum-access floor. It uses the same code as an op-class
    /// shortfall, because the effect is the same - the call holds no authority - and a caller
    /// should not have to tell the two apart to route the failure.
    /// </summary>
    /// <param name="toolName">The tool the call named.</param>
    /// <returns>An <c>authority_required</c> refusal result.</returns>
    private static CallToolResult NoGrantError(string toolName) => ToolError.Result(
        ToolCodes.AuthorityRequired,
        $"No grant is attached to this request, so tool \"{toolName}\" cannot be dispatched.",
        new { tool = toolName });

    /// <summary>
    /// Returns the caller's session, built from the principal key
    /// <see cref="BearerGrantMiddleware"/> derived for this request, or null when the middleware
    /// did not run. A declaration that requires a session turns a null into
    /// <c>tool_unavailable</c>.
    /// </summary>
    /// <returns>The caller's session, or null when no principal key is attached.</returns>
    private ClientSession? Session() =>
        httpContextAccessor.HttpContext?.Items[BearerGrantMiddleware.PrincipalTokenHashItemKey] is string principalKey
            ? new ClientSession(principalKey)
            : null;

    /// <summary>Returns the call's raw argument map, which the execute path binds onto the
    /// declared arguments record.</summary>
    /// <param name="context">The request context to read arguments from.</param>
    /// <returns>The arguments by name, or an empty map when the call supplied none.</returns>
    private static IReadOnlyDictionary<string, JsonElement> DeclaredArguments(RequestContext<CallToolRequestParams> context) =>
        context.Params?.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];

    /// <summary>
    /// Invokes a declared tool and renders its result, or its refusal, as an MCP result. A
    /// content result becomes a text block; a structured result is serialized under the shared
    /// options, so results and arguments share one naming policy and no name is translated
    /// between the two directions.
    /// </summary>
    /// <remarks>
    /// Which exceptions count as refusals, and what each becomes, is
    /// <see cref="ToolRefusalEnvelope.Refusal"/>'s decision, shared with the other front-end so
    /// the two cannot translate different sets. Only the MCP wrapping is here.
    /// </remarks>
    /// <param name="invoke">The tool invocation to run.</param>
    /// <returns>The rendered result, or an <c>isError</c> result carrying the refusal.</returns>
    /// <exception cref="InvalidOperationException">The tool returned a result kind this method
    /// does not render.</exception>
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
        catch (Exception exception) when (ToolRefusalEnvelope.Refusal(exception) is { } refusal)
        {
            return ToolError.Result(refusal);
        }
    }

    /// <summary>Returns whether a tool declares a vault-targeting parameter (see
    /// <see cref="VaultParameterName"/>), which is what the vault-membership check applies
    /// to.</summary>
    /// <param name="tool">The script tool to inspect.</param>
    /// <returns><c>true</c> when the tool takes a vault parameter; otherwise <c>false</c>.</returns>
    private static bool ToolTakesVaultParameter(ScriptTool tool) =>
        tool.Parameters.Any(p => string.Equals(p.Name, VaultParameterName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the first client-supplied argument name that matches no parameter the tool
    /// advertises. Names are matched case-insensitively, the same way PowerShell's binder
    /// matches. A tool's advertised parameters are exactly its <c>param()</c> block, so
    /// PowerShell's common parameters, which <c>[CmdletBinding()]</c> adds at bind time but which
    /// never appear in the parsed param block, are unrecognized here like any other unadvertised
    /// name.
    /// </summary>
    /// <param name="tool">The script tool whose parameters the names are checked against.</param>
    /// <param name="arguments">The client-supplied arguments, or null when none were supplied.</param>
    /// <returns>The first unrecognized name, or null when every supplied name matches.</returns>
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
    /// Returns the vault a call effectively targets: the string <c>vault</c> argument when
    /// present, looked up case-insensitively, and the configured default vault otherwise. A blank
    /// value counts as absent, matching how vault resolution treats a null or blank name.
    /// </summary>
    /// <param name="arguments">The client-supplied arguments, or null when none were supplied.</param>
    /// <returns>The vault name the call targets.</returns>
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
    /// matched case-insensitively to the parameter it names, so
    /// <see cref="ScriptToolArgumentEncoder"/> can apply that parameter's encoding rule. The
    /// lookup cannot miss in practice, because <see cref="UnadvertisedArgumentName"/> already
    /// refused any unrecognized name, and an unmatched name would simply convert plainly rather
    /// than throw.
    /// </summary>
    /// <param name="tool">The script tool whose parameters the arguments are matched against.</param>
    /// <param name="arguments">The client-supplied arguments, or null when none were supplied.</param>
    /// <returns>The splat values by argument name.</returns>
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

    /// <summary>
    /// Wraps already-formed error text as an <c>isError</c> result. Use this only for a script's
    /// own thrown text, which must not be re-shaped; every denial the dispatcher makes itself
    /// goes through <see cref="ToolError"/>.
    /// </summary>
    /// <param name="message">The error text to return unchanged.</param>
    /// <returns>An error result carrying that text.</returns>
    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    /// <summary>Records a session's server so it can be notified of tool-list changes.</summary>
    /// <param name="server">The calling session's server, ignored when null.</param>
    private void TrackSession(McpServer? server)
    {
        if (server is not null)
            _sessions[server] = 0;
    }

    /// <summary>
    /// Broadcasts <c>notifications/tools/list_changed</c> to every session tracked so far.
    /// <see cref="ScriptToolsWatcher"/> calls this after a refresh. A session whose send fails,
    /// usually because its request has already completed under the stateless transport, is
    /// dropped from the tracked set rather than retried.
    /// </summary>
    /// <param name="cancellationToken">Cancels the outstanding sends.</param>
    /// <returns>A task that completes once every tracked session has been tried.</returns>
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
