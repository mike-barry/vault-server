using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using VaultMcpServer.Auth;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultShared.Configuration;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers the bearer gate end to end. Boots the real host against a temporary vaults.json and
/// grants.json and drives it over HTTP through TestServer, so the gate is exercised in the
/// pipeline rather than through GrantStore alone.
/// </summary>
[Collection("Program host tests")]
public sealed class BearerGrantMiddlewareTests : IClassFixture<BearerGrantMiddlewareTests.Fixture>, IClassFixture<TestVaultFixture>
{
    private readonly Fixture _fixture;
    private readonly TestVaultFixture _vaultFixture;

    public BearerGrantMiddlewareTests(Fixture fixture, TestVaultFixture vaultFixture)
    {
        _fixture = fixture;
        _vaultFixture = vaultFixture;
    }

    /// <summary>
    /// Verifies that an expired token and an unknown token both fall back to the built-in
    /// minimum-access grant instead of returning 401, so a caller holding a stale token can still
    /// recover. Builds the middleware directly to capture the grant it attaches, and uses a
    /// FixedTimeProvider so expiry is deterministic.
    /// </summary>
    [Fact]
    public async Task ExpiredOrUnknownToken_FloorsToMinimumAccessNot401()
    {
        var opClassRegistry = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());
        var tempDir = Directory.CreateTempSubdirectory("vault-mcp-middleware-ttl-tests-").FullName;
        try
        {
            var grantsPath = Path.Combine(tempDir, "grants.json");
            File.WriteAllText(grantsPath, """{ "expiring-token": { "id": "short-lived", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 1, "scopeRoots": { "test": ["*"] } } }""");

            var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
            var grantStore = GrantStore.Load(grantsPath, opClassRegistry, _vaultFixture.Config, new ServerHealthState(), timeProvider);
            timeProvider.Now = timeProvider.Now.AddMinutes(10); // the 1-minute grant is now expired

            Grant? expiredGrant = null;
            Grant? unknownGrant = null;

            var expiredContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            expiredContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            expiredContext.Request.Headers.Authorization = "Bearer expiring-token";
            await new BearerGrantMiddleware(c => { expiredGrant = c.Items[BearerGrantMiddleware.GrantItemKey] as Grant; return Task.CompletedTask; }, grantStore, new ServerHealthState(), NullLogger<BearerGrantMiddleware>.Instance).InvokeAsync(expiredContext);

            var unknownContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
            unknownContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            unknownContext.Request.Headers.Authorization = "Bearer not-a-real-token";
            await new BearerGrantMiddleware(c => { unknownGrant = c.Items[BearerGrantMiddleware.GrantItemKey] as Grant; return Task.CompletedTask; }, grantStore, new ServerHealthState(), NullLogger<BearerGrantMiddleware>.Instance).InvokeAsync(unknownContext);

            Assert.NotEqual(StatusCodes.Status401Unauthorized, expiredContext.Response.StatusCode);
            Assert.NotEqual(StatusCodes.Status401Unauthorized, unknownContext.Response.StatusCode);
            Assert.Equal(GrantStore.MinimumGrant.Id, expiredGrant?.Id);
            Assert.Equal(GrantStore.MinimumGrant.Id, unknownGrant?.Id);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a missing Authorization header is treated as a blank token: it falls back to
    /// minimum access rather than 401, and the principal token hash the elevation tool reads is
    /// still stored on the request.
    /// </summary>
    [Fact]
    public async Task MissingAuthorizationHeader_FloorsToMinimumAccess()
    {
        var opClassRegistry = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());
        var grantStore = GrantStore.Empty();

        Grant? grant = null;
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        await new BearerGrantMiddleware(c => { grant = c.Items[BearerGrantMiddleware.GrantItemKey] as Grant; return Task.CompletedTask; }, grantStore, new ServerHealthState(), NullLogger<BearerGrantMiddleware>.Instance).InvokeAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(GrantStore.MinimumGrant.Id, grant?.Id);
        Assert.NotNull(context.Items[BearerGrantMiddleware.PrincipalTokenHashItemKey]);
    }

    /// <summary>
    /// Verifies that a fatally degraded <see cref="ServerHealthState"/> short-circuits every
    /// request with 503 and the same fixed, detail-free body, whether or not a token was
    /// presented. A fatal config error must not be distinguishable from an auth failure, and the
    /// response must not name the file or setting that was wrong. Builds the middleware directly
    /// rather than using the shared fixture, which exists to supply a working config.
    /// </summary>
    [Fact]
    public async Task FatallyDegraded_ShortCircuitsEveryRequestWith503RegardlessOfToken()
    {
        var opClassRegistry = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());
        var healthState = new ServerHealthState();
        healthState.RecordFatal("VaultConfig", "vaults.json: missing or empty \"default_vault\".");
        var grantStore = GrantStore.Empty();
        var middleware = new BearerGrantMiddleware(_ => Task.CompletedTask, grantStore, healthState, NullLogger<BearerGrantMiddleware>.Instance);

        var noAuthContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await middleware.InvokeAsync(noAuthContext);

        var withAuthContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        withAuthContext.Request.Headers.Authorization = "Bearer whatever-token";
        await middleware.InvokeAsync(withAuthContext);

        foreach (var context in new[] { noAuthContext, withAuthContext })
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Equal(BearerGrantMiddleware.FatalDegradedBody, body);
            // The body must never mention the recorded problem's own detail - that would
            // leak config specifics through an unauthenticated endpoint.
            Assert.DoesNotContain("vaults.json", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("default_vault", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task UnknownToken_IsNotRejectedByTheGate_FloorsToMinimum()
    {
        // An unrecognized localhost token degrades to the minimum-access floor rather than 401,
        // so the gate does not reject it (the ping reaches the MCP handler).
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Verifies that naming a vault outside the grant's scope is refused whatever the casing of the
    /// vault argument, and that the refusal arrives as a tool error carrying the shared
    /// <c>{code, message, data?}</c> JSON over HTTP 200. The gate authenticates and answers nothing
    /// about authority, so the refusal comes from the execute path rather than from a status code.
    /// </summary>
    /// <param name="vaultPropertyName">The casing of the vault argument name to send.</param>
    /// <param name="expectedCode">The refusal code that casing produces.</param>
    /// <remarks>
    /// The two casings are refused for different reasons and neither reaches the tool. A declared
    /// tool advertises the argument as "vault" and binds it case-sensitively, so "Vault" is a
    /// member its arguments record does not declare and is refused before any vault is resolved.
    /// </remarks>
    [Theory]
    [InlineData("vault", "out_of_scope")]
    [InlineData("Vault", "invalid_argument")]
    public async Task ToolCallNamingOutOfScopeVault_IsRefusedInACodedEnvelope_RegardlessOfArgumentCasing(string vaultPropertyName, string expectedCode)
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ReadOnlyToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // "unheld" is configured but outside this grant, so the call is refused for scope rather
        // than for naming a vault nobody configured.
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"VAULT_PROPERTY":"unheld","path":"a.md"}}}""".Replace("VAULT_PROPERTY", vaultPropertyName);
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (isError, text) = await ReadToolResultAsync(response);
        Assert.True(isError, text);
        using var refusal = JsonDocument.Parse(text);
        Assert.Equal(expectedCode, refusal.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// Verifies that the gate admits a call naming a vault the grant covers. This case needs a
    /// real tool name: an unknown name also yields HTTP 200 with a tool error, so asserting only
    /// that the call was not refused would pass for any name. <c>read</c> takes a vault argument
    /// and this grant covers <c>personal</c>, so the call reaches the tool, which returns its own
    /// <c>path_not_found</c> error over HTTP 200.
    /// </summary>
    [Fact]
    public async Task ToolCallNamingInScopeVault_IsNotRejectedByTheGate()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ReadOnlyToken);
        // The MCP endpoint answers 406 without both of these.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"vault":"personal","path":"a.md"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // path_not_found is the tool's own answer, and out_of_scope is what a grant that did not
        // cover the vault would have been given, so its absence is what says this one was admitted.
        var (_, text) = await ReadToolResultAsync(response);
        Assert.Contains("path_not_found", text, StringComparison.Ordinal);
        Assert.DoesNotContain("out_of_scope", text, StringComparison.Ordinal);
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Reads the tool result out of an MCP response, which the endpoint sends either as plain JSON
    /// or as a single SSE data frame depending on what the client accepts.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <returns>Whether the result is a tool error, and the text of its first content block.</returns>
    private static async Task<(bool IsError, string Text)> ReadToolResultAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var trimmed = body.TrimStart();
        var payload = trimmed.StartsWith("event:", StringComparison.Ordinal) || trimmed.StartsWith("data:", StringComparison.Ordinal)
            ? (body.Split('\n').FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No SSE data: line found in response: {body}"))["data:".Length..].Trim()
            : body;
        using var doc = JsonDocument.Parse(payload);
        var result = doc.RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        return (isError, result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString() ?? "");
    }

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-admin-token";
        public const string ReadOnlyToken = "test-readonly-token";

        public Fixture() : base("vault-mcp-middleware-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            // A second configured vault that neither grant holds, so a call naming it is refused
            // for scope rather than for being unknown.
            var unheldRoot = Path.Combine(TempDir, "unheld");
            Directory.CreateDirectory(unheldRoot);
            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, object>
                {
                    ["personal"] = new { path = vaultRoot },
                    ["unheld"] = new { path = unheldRoot },
                },
            }));

            var grantsPath = Path.Combine(TempDir, "grants.json");
            File.WriteAllText(grantsPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [AdminToken] = new { id = "admin", opClasses = new[] { "content:read", "content:write" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } } },
                [ReadOnlyToken] = new { id = "read-only", opClasses = new[] { "content:read" }, vaults = new[] { "personal" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["personal"] = new[] { "*" } } },
            }));

            ConfigureEnvironment(vaultsPath, grantsPath);
        }
    }
}
