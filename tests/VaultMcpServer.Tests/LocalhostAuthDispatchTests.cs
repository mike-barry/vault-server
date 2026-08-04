using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the localhost auth model over HTTP through the real host: the well-known selector token
/// resolving to the configured default named grant, and blank or unknown tokens falling back to the
/// minimum-access floor, where health_check still works but vault content is denied.
/// </summary>
/// <remarks>
/// The fixture boots Program with a token-less named <c>admin</c> grant and
/// <c>LocalhostAuth__SelectorToken</c> and <c>LocalhostAuth__DefaultGrantId</c> set, so the whole
/// path from configuration through LocalhostAuthOptions and GrantStore.ResolveLocalhostGrant to
/// BearerGrantMiddleware runs. The class owns its host, and so its own GrantStore.
/// </remarks>
[Collection("Program host tests")]
public sealed class LocalhostAuthDispatchTests : IClassFixture<LocalhostAuthDispatchTests.Fixture>
{
    /// <summary>A documentation-range address, so no test here names a real host.</summary>
    private const string NonLoopback = "203.0.113.5";

    private readonly Fixture _fixture;

    public LocalhostAuthDispatchTests(Fixture fixture) => _fixture = fixture;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>Creates a client for the MCP endpoint with the given bearer token and remote
    /// address.</summary>
    /// <param name="fixture">The host fixture to create the client from.</param>
    /// <param name="token">The bearer token to send, or null to send no Authorization header.</param>
    /// <param name="remoteIp">The address the request is presented from. Null means loopback, which
    /// is what the real server sees; a value drives the non-loopback branch through the whole host
    /// pipeline. See <see cref="ProgramHostFixtureBase.RemoteIpHeader"/>.</param>
    /// <returns>A client carrying those headers plus the Accept types the MCP endpoint requires.</returns>
    private static HttpClient AuthedClient(Fixture fixture, string? token, string? remoteIp = null)
    {
        var client = fixture.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (remoteIp is not null)
            client.DefaultRequestHeaders.Add(ProgramHostFixtureBase.RemoteIpHeader, remoteIp);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client;
    }

    private static string ExtractJsonRpcPayload(string body)
    {
        if (!body.TrimStart().StartsWith("event:", StringComparison.Ordinal) && !body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            return body;
        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}");
        return dataLine["data:".Length..].Trim();
    }

    private static async Task<(HttpStatusCode Status, JsonElement? Result)> CallToolAsync(Fixture fixture, string? token, string toolCallJson)
    {
        using var client = AuthedClient(fixture, token);
        using var response = await client.PostAsync("/mcp", JsonBody(toolCallJson));
        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, null);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(text));
        return (response.StatusCode, doc.RootElement.GetProperty("result").Clone());
    }

    [Fact]
    public async Task SelectorToken_ResolvesToDefaultAdminGrant_CanMint()
    {
        // mint_token requires ManageGrants; the selector binding to admin is what grants it.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"]}}}""";
        var (status, result) = await CallToolAsync(_fixture, Fixture.SelectorToken, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!.Value.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    [Fact]
    public async Task UnknownToken_FloorsToMinimum_HealthCheckWorks()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var (status, result) = await CallToolAsync(_fixture, "some-random-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!.Value.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    [Fact]
    public async Task UnknownToken_FloorsToMinimum_ManageGrantsToolDenied()
    {
        // The minimum floor has no op classes, so mint_token (ManageGrants) is a tool error.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"mint_token","arguments":{"scope_roots":["personal"],"op_class":["content:read"]}}}""";
        var (status, result) = await CallToolAsync(_fixture, "some-random-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!.Value.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task MinimumFloor_VaultNamingToolIsRejectedWith403()
    {
        // The minimum floor has no vaults, so a tools/call naming one is rejected at the gate.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"greet","arguments":{"vault":"personal","Name":"x"}}}""";
        var (status, _) = await CallToolAsync(_fixture, "some-random-unrecognized-token", body);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    // ---- Off loopback ----

    /// <summary>Calls <c>health_check</c> from a given remote address and returns the raw
    /// response.</summary>
    /// <param name="fixture">The host fixture to call.</param>
    /// <param name="token">The bearer token to send, or null to send none.</param>
    /// <param name="remoteIp">The address the request is presented from.</param>
    /// <returns>The response status and body text.</returns>
    /// <remarks>
    /// The selector binding and the minimum-access floor both apply only to a loopback remote
    /// address, so a request presented from anywhere else must carry a token
    /// <c>GrantStore.Authenticate</c> accepts. <c>LoopbackAuthMiddlewareTests</c> asserts the same
    /// gate against the middleware alone; what these tests add is that the address the host hands
    /// the middleware is the connection's, so nothing earlier in the pipeline turns a remote request
    /// into a loopback one.
    /// </remarks>
    private static async Task<(HttpStatusCode Status, string Body)> HealthCheckFromAsync(Fixture fixture, string? token, string remoteIp)
    {
        using var client = AuthedClient(fixture, token, remoteIp);
        using var response = await client.PostAsync(
            "/mcp",
            JsonBody("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}"""));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonLoopback_UnknownToken_IsDeniedRatherThanFlooredToMinimum()
    {
        var (status, body) = await HealthCheckFromAsync(_fixture, "some-random-unrecognized-token", NonLoopback);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(Auth.BearerGrantMiddleware.UnauthorizedBody, body);
    }

    [Fact]
    public async Task NonLoopback_SelectorToken_IsJustAnUnrecognizedToken()
    {
        // Off loopback the selector carries no default binding, so the token that reaches admin over
        // loopback reaches nothing here.
        var (status, body) = await HealthCheckFromAsync(_fixture, Fixture.SelectorToken, NonLoopback);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(Auth.BearerGrantMiddleware.UnauthorizedBody, body);
    }

    [Fact]
    public async Task NonLoopback_MissingHeader_IsDenied()
    {
        var (status, _) = await HealthCheckFromAsync(_fixture, token: null, NonLoopback);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    /// <summary>
    /// Verifies that a real token authenticates from any address. The remote address decides only
    /// whether the selector binding and the minimum-access floor apply.
    /// </summary>
    [Fact]
    public async Task NonLoopback_RealToken_Authenticates()
    {
        var (status, body) = await HealthCheckFromAsync(_fixture, Fixture.ReadOnlyToken, NonLoopback);

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(ExtractJsonRpcPayload(body));
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    [Fact]
    public async Task BlankToken_FloorsToMinimum_HealthCheckWorks()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var (status, result) = await CallToolAsync(_fixture, null, body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!.Value.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string SelectorToken = "test-localhost-selector";

        /// <summary>The ordinary token row beside the named grant, and the only credential a request
        /// from off loopback can present successfully.</summary>
        public const string ReadOnlyToken = "a-real-readonly-token";

        public Fixture() : base("vault-mcp-localhost-auth-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, object>{ ["personal"] = new { path = vaultRoot } },
            }));

            // A token-less named "admin" grant, which the selector binds to, plus one ordinary
            // read-only token row.
            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["admin"] = new { named = true, id = "admin", opClasses = new[] { "*" }, vaults = new[] { "*" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["*"] = new[] { "*" } } },
                ["a-real-readonly-token"] = new { id = "read-only", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } } },
            }));

            var toolsDir = Path.Combine(TempDir, "tools");
            Directory.CreateDirectory(toolsDir);
            File.WriteAllText(Path.Combine(toolsDir, "greet.ps1"), """
                <#
                .SYNOPSIS
                Greets someone by name.
                .PARAMETER Name
                Who to greet.
                .NOTES
                OpClass: content:write
                #>
                param([Parameter(Mandatory=$true)] [string]$Name)
                Write-Output "hello $Name"
                """);

            ConfigureEnvironment(vaultsPath, grantsPath, toolsDirectory: toolsDir);
            SetEnvironmentVariable("LocalhostAuth__SelectorToken", SelectorToken);
            SetEnvironmentVariable("LocalhostAuth__DefaultGrantId", "admin");
        }
    }
}
