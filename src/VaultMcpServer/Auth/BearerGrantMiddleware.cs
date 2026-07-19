using System.Text;
using System.Text.Json;
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

    public async Task InvokeAsync(HttpContext context)
    {
        if (healthState.IsFatallyDegraded)
        {
            await Reject(context, StatusCodes.Status503ServiceUnavailable, FatalDegradedBody);
            return;
        }

        // Extract the bearer token (empty when the header is missing or malformed - a blank token,
        // which the localhost floor handles, not a 401). ResolveLocalhostGrant never returns null.
        var header = context.Request.Headers["Authorization"].ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim()
            : "";
        var grant = grantStore.ResolveLocalhostGrant(token);

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
                    vault = a.TryGetProperty("vault", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                }
            }
            return (method, tool, vault, arguments);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }
}
