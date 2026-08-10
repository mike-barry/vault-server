using System.Net;
using System.Text;
using System.Text.Json;
using VaultShared.Auth;
using VaultMcpServer.Health;

namespace VaultMcpServer.Auth;

/// <summary>
/// Authenticates every request against a bearer token and attaches the resolved grant to
/// <c>HttpContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// A loopback request resolves its token through <see cref="GrantStore.ResolveLocalhostGrant"/>,
/// which never fails: a token matching a grant row, a minted grant, or an active elevation
/// binding resolves to that authority; the localhost selector token binds to the configured
/// default grant; and a blank, unrecognized, or expired token falls back to the built-in
/// minimum-access floor instead of a 401. That fallback exists because the server binds
/// loopback only, so a subagent with a missing or mangled token runs harmlessly rather than
/// failing hard. A non-loopback request gets neither the selector nor the floor and must present
/// a token <see cref="GrantStore.Authenticate"/> accepts.
/// </para>
/// <para>
/// The resolved grant is placed in <c>HttpContext.Items[GrantItemKey]</c> for downstream
/// op-class enforcement, and the presented token's hash in
/// <c>HttpContext.Items[PrincipalTokenHashItemKey]</c> so the elevation tool can rebind the
/// principal without the raw token reaching a tool.
/// </para>
/// <para>
/// One audit line is written per request: the grant id (never the token), the JSON-RPC method,
/// and, for a <c>tools/call</c>, the tool name, vault argument, and full arguments.
/// </para>
/// <para>
/// Authentication is all this decides. Whether the grant reaches the vault a call targets is
/// answered further in, where the call's arguments are bound and its vault resolved, and arrives
/// as a coded <c>out_of_scope</c> refusal. A body peek cannot see a caller that omitted the vault
/// argument and took the configured default, so deciding it here would refuse only the callers
/// that spelled the argument.
/// </para>
/// <para>
/// Before any of that, <see cref="ServerHealthState.IsFatallyDegraded"/> short-circuits every
/// request with a 503 regardless of the token.
/// </para>
/// </remarks>
/// <param name="next">The next middleware in the pipeline.</param>
/// <param name="grantStore">Resolves a presented token to a grant.</param>
/// <param name="healthState">Supplies the fatal-degradation flag that gates every request.</param>
/// <param name="logger">Receives the per-request audit lines.</param>
public sealed class BearerGrantMiddleware(RequestDelegate next, GrantStore grantStore, ServerHealthState healthState, ILogger<BearerGrantMiddleware> logger)
{
    /// <summary>The <c>HttpContext.Items</c> key holding the grant resolved for the request.</summary>
    public const string GrantItemKey = "Grant";

    /// <summary>
    /// The <c>HttpContext.Items</c> key holding the hash of the presented token (see
    /// <see cref="GrantStore.HashKey"/>). The elevation tool reads it to rebind the principal
    /// without ever seeing the raw token.
    /// </summary>
    public const string PrincipalTokenHashItemKey = "PrincipalTokenHash";

    /// <summary>
    /// The response body returned for every request while
    /// <see cref="ServerHealthState.IsFatallyDegraded"/> is true. The same text is returned for
    /// every request and every underlying error so that it cannot reveal which config file or
    /// which validation failed; <see cref="ServerHealthState.Problems"/>, via the logs, carries
    /// that detail.
    /// </summary>
    public const string FatalDegradedBody =
        "A fatal server configuration error is preventing this server from operating. Check the server logs for details.";

    /// <summary>
    /// The response body returned with a 401 when a non-loopback request fails to authenticate.
    /// It does not say whether the token was unknown or expired, so a remote caller cannot probe
    /// the response to tell the two apart; the server-side audit log records the reason from
    /// <see cref="GrantStore.GetAuthFailureReason"/>.
    /// </summary>
    public const string UnauthorizedBody = "Unauthorized.";

    /// <summary>Authenticates the request, audits it, and passes it to the next middleware.</summary>
    /// <param name="context">The request being handled.</param>
    /// <returns>A task that completes once the request has been rejected or forwarded.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (healthState.IsFatallyDegraded)
        {
            await Reject(context, StatusCodes.Status503ServiceUnavailable, FatalDegradedBody);
            return;
        }

        // A missing or malformed header yields an empty token, which the localhost floor handles
        // rather than a 401.
        var header = context.Request.Headers["Authorization"].ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim()
            : "";

        // The selector-to-default binding and the minimum-access floor apply only to loopback
        // requests. A null RemoteIpAddress counts as non-loopback, so the check fails closed.
        var remoteIp = context.Connection.RemoteIpAddress;
        var isLoopback = remoteIp is not null && IPAddress.IsLoopback(remoteIp);

        Grant grant;
        if (isLoopback)
        {
            grant = grantStore.ResolveLocalhostGrant(token);
        }
        else
        {
            var authenticated = grantStore.Authenticate(token);
            if (authenticated is null)
            {
                // The failure reason goes in the log line and never in the response body.
                logger.LogWarning(
                    "Audit: grant=(none) remoteIp={RemoteIp} reason={Reason} result=rejected-non-loopback",
                    remoteIp, grantStore.GetAuthFailureReason(token));
                await Reject(context, StatusCodes.Status401Unauthorized, UnauthorizedBody);
                return;
            }
            grant = authenticated;
        }

        var (method, tool, vault, arguments) = await PeekJsonRpcCall(context.Request);

        logger.LogInformation(
            "Audit: grant={GrantId} method={Method} tool={Tool} vault={Vault} args={Args}",
            grant.Id, method, tool, vault, arguments);

        context.Items[GrantItemKey] = grant;
        context.Items[PrincipalTokenHashItemKey] = GrantStore.HashKey(token);
        await next(context);
    }

    /// <summary>Writes a plain-text refusal and ends the request.</summary>
    /// <param name="context">The request to respond to.</param>
    /// <param name="statusCode">The HTTP status code to return.</param>
    /// <param name="message">The response body.</param>
    /// <returns>A task that completes once the body has been written.</returns>
    private static async Task Reject(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(message);
    }

    /// <summary>
    /// Reads the JSON-RPC request body for the audit line without disturbing it for downstream
    /// handlers. The request stream is buffered and rewound.
    /// Non-JSON, empty, and malformed bodies return all-null rather than failing; the MCP
    /// handler reports protocol errors itself.
    /// </summary>
    /// <param name="request">The request whose body is inspected.</param>
    /// <returns>The JSON-RPC method, and for a <c>tools/call</c> the tool name, the vault
    /// argument, and the raw arguments JSON. Any element is null when it is not present.</returns>
    private static async Task<(string? Method, string? Tool, string? Vault, string? Arguments)> PeekJsonRpcCall(HttpRequest request)
    {
        if (request.ContentLength is null or 0)
            return (null, null, null, null);

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return (null, null, null, null);
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, null, null);

            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            string? tool = null;
            string? vault = null;
            string? arguments = null;
            if (method == "tools/call" && root.TryGetProperty("params", out var p))
            {
                tool = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (p.TryGetProperty("arguments", out var a))
                {
                    arguments = a.GetRawText();
                    vault = TryGetPropertyIgnoreCase(a, "vault", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                }
            }
            return (method, tool, vault, arguments);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Looks up a JSON object property by name, ignoring case. Tool-argument names are matched
    /// case-insensitively because the PowerShell binder is, so a client that sends "Vault" is
    /// audited under the same field as one that sends "vault". The JSON-RPC envelope's own field
    /// names are protocol-defined lowercase and are matched exactly.
    /// </summary>
    /// <param name="obj">The JSON object to search.</param>
    /// <param name="name">The property name to look for.</param>
    /// <param name="value">The matched property value, or default when no property matches.</param>
    /// <returns><c>true</c> when a property matched; otherwise <c>false</c>.</returns>
    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
