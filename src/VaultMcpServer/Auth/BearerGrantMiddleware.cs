using System.Net;
using System.Text;
using System.Text.Json;
using VaultShared.Auth;
using VaultMcpServer.Health;

namespace VaultMcpServer.Auth;

/// <summary>
/// Localhost bearer-token gate in front of every request. Resolves the presented token to a grant
/// via <see cref="GrantStore.ResolveLocalhostGrant"/>, which never errors: a token matching a real
/// grant row, minted grant, or active elevation binding resolves to that authority, the localhost
/// selector token binds to the configured default grant, and a blank, unrecognized, or expired
/// token degrades to the built-in minimum-access floor rather than a 401. This is the fail-closed
/// localhost model (the server binds <c>localhost:5111</c> only) - a subagent with a missing or
/// mangled token runs harmless instead of failing hard. The resolved grant is attached to
/// <c>HttpContext.Items[GrantItemKey]</c> for downstream op-class enforcement, and the principal's
/// token-hash to <c>HttpContext.Items[PrincipalTokenHashItemKey]</c> so the elevation tool can
/// rebind it without the raw token reaching a tool.
///
/// Writes one audit log line per request: grant id (never the token), JSON-RPC method, and - for a
/// tools/call - the tool name, vault argument, and full arguments. A tools/call naming a vault
/// outside the grant's <see cref="Grant.Vaults"/> is still rejected with 403 (the minimum-access
/// floor has no vaults, so it 403s on any vault content while health_check and request_elevation,
/// which name no vault, pass through).
///
/// Before any of that, checks <see cref="ServerHealthState.IsFatallyDegraded"/> and, if set,
/// short-circuits every request (regardless of token) with 503 and a fixed generic body -
/// deliberately the same text for every request and every underlying error, so it can never
/// leak which config file or which detail is broken. That 503 gate is unchanged.
/// </summary>
public sealed class BearerGrantMiddleware(RequestDelegate next, GrantStore grantStore, ServerHealthState healthState, ILogger<BearerGrantMiddleware> logger)
{
    public const string GrantItemKey = "Grant";

    /// <summary>Items key for the presented token's hash (see <see cref="GrantStore.HashKey"/>) -
    /// the elevation tool reads it to rebind the principal without ever seeing the raw token.</summary>
    public const string PrincipalTokenHashItemKey = "PrincipalTokenHash";

    /// <summary>
    /// Fixed, detail-free body returned for every request while <see cref="ServerHealthState.IsFatallyDegraded"/>
    /// is true. Intentionally says nothing about which file or which validation failed -
    /// see <see cref="ServerHealthState.Problems"/> (via the logs) for that.
    /// </summary>
    public const string FatalDegradedBody =
        "A fatal server configuration error is preventing this server from operating. Check the server logs for details.";

    /// <summary>
    /// Fixed, detail-free body returned with a 401 when a non-loopback request fails to
    /// authenticate. Deliberately says nothing about why (unknown vs expired token): the
    /// server-side audit log carries that (via <see cref="GrantStore.GetAuthFailureReason"/>),
    /// but the response must never be an oracle a remote caller can probe.
    /// </summary>
    public const string UnauthorizedBody = "Unauthorized.";

    public async Task InvokeAsync(HttpContext context)
    {
        if (healthState.IsFatallyDegraded)
        {
            await Reject(context, StatusCodes.Status503ServiceUnavailable, FatalDegradedBody);
            return;
        }

        // Extract the bearer token (empty when the header is missing or malformed - a blank token,
        // which the localhost floor handles, not a 401).
        var header = context.Request.Headers["Authorization"].ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim()
            : "";

        // The selector->default binding and the minimum-access floor are a localhost-only
        // convenience (the server's whole trust model is that it binds loopback). Only apply them
        // to a request whose remote address is loopback; a null RemoteIpAddress is treated as
        // non-loopback (fail closed). A non-loopback request must present a real token that
        // Authenticate accepts - there is no floor and no selector for it - otherwise it gets a
        // fixed generic 401 with no Expired-vs-Unknown oracle in the body.
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
                // GetAuthFailureReason is diagnostic-only and stays server-side (the log line),
                // never the response body. Same audit shape as the vault-scope rejection below.
                logger.LogWarning(
                    "Audit: grant=(none) remoteIp={RemoteIp} reason={Reason} result=rejected-non-loopback",
                    remoteIp, grantStore.GetAuthFailureReason(token));
                await Reject(context, StatusCodes.Status401Unauthorized, UnauthorizedBody);
                return;
            }
            grant = authenticated;
        }

        var (method, tool, vault, arguments) = await PeekJsonRpcCall(context.Request);

        if (vault is not null && !grant.CanAccessVault(vault))
        {
            logger.LogWarning(
                "Audit: grant={GrantId} method={Method} tool={Tool} vault={Vault} args={Args} result=rejected-vault-scope",
                grant.Id, method, tool, vault, arguments);
            await Reject(context, StatusCodes.Status403Forbidden, $"Grant \"{grant.Id}\" is not scoped to vault \"{vault}\".");
            return;
        }

        logger.LogInformation(
            "Audit: grant={GrantId} method={Method} tool={Tool} vault={Vault} args={Args}",
            grant.Id, method, tool, vault, arguments);

        context.Items[GrantItemKey] = grant;
        context.Items[PrincipalTokenHashItemKey] = GrantStore.HashKey(token);
        await next(context);
    }

    private static async Task Reject(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(message);
    }

    /// <summary>
    /// Best-effort peek at the JSON-RPC request body for audit logging and vault-scope
    /// enforcement, without disturbing it for downstream handlers. Buffers and rewinds the
    /// request stream; tolerates non-JSON, empty, or malformed bodies (the MCP handler
    /// reports protocol errors itself).
    /// </summary>
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
    /// Looks up a JSON object property by name, case-insensitively. Tool-argument property
    /// names are client-schema-cased: a script tool exposes its parameters verbatim from the
    /// param() block, which spells them snake_case, but the PowerShell binder itself matches
    /// case-insensitively, so a client may legitimately send "Vault" and must still be gated
    /// here. The JSON-RPC envelope's own field names are protocol-defined lowercase and exact.
    /// </summary>
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
