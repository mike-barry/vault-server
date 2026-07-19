using System.Text;
using System.Text.Json;

namespace VaultMcpServer.Auth;

/// <summary>
/// Deny-by-default bearer-token gate in front of every request. Looks the presented token up
/// in the injected <see cref="GrantStore"/> via <see cref="GrantStore.Authenticate"/> (which
/// treats an expired grant as a non-match) and rejects a missing, unknown, or expired token
/// with 401 - consulting <see cref="GrantStore.GetAuthFailureReason"/> only to pick a distinct
/// rejection message so "unknown" and "expired" can be told apart - and attaches the matched
/// grant to
/// <c>HttpContext.Items[GrantItemKey]</c> for downstream tool dispatch to enforce per-tool op
/// class against.
///
/// Writes one audit log line per request: grant id (never the token), JSON-RPC method,
/// and - for a tools/call - the tool name, vault argument, and full arguments. A
/// tools/call naming a vault outside the grant's <see cref="Grant.Vaults"/> is rejected
/// with 403, the same as an unrecognized token.
/// </summary>
public sealed class BearerGrantMiddleware(RequestDelegate next, GrantStore grantStore, ILogger<BearerGrantMiddleware> logger)
{
    public const string GrantItemKey = "Grant";

    public async Task InvokeAsync(HttpContext context)
    {
        var header = context.Request.Headers["Authorization"].ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "Missing or malformed Authorization header.");
            return;
        }

        var token = header["Bearer ".Length..].Trim();
        var grant = grantStore.Authenticate(token);
        if (grant is null)
        {
            // Authenticate already excludes expired grants; consult the failure reason purely
            // to pick the right 401 message - it plays no role in the authorization decision
            // above.
            var message = grantStore.GetAuthFailureReason(token) == AuthFailureReason.Expired
                ? "Expired bearer token."
                : "Unknown bearer token.";
            await Reject(context, StatusCodes.Status401Unauthorized, message);
            return;
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
