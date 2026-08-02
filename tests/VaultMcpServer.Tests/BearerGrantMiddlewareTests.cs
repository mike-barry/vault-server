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
/// Boots the real host (Program) against a temp vaults.json/grants.json and drives it
/// over HTTP through TestServer, proving the bearer gate actually authenticates and
/// scopes requests rather than just unit-testing GrantStore in isolation.
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
    /// Under the fail-closed localhost floor (Batch A), an expired token and an unknown token no
    /// longer 401 - both degrade to the built-in minimum-access grant, so the session runs
    /// harmless rather than hard-failing (a subagent with a stale/mangled token stays recoverable).
    /// Constructs the middleware directly, capturing the grant it attaches, with a FixedTimeProvider
    /// so the "expired" case is deterministic without real elapsed wall-clock time.
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

    /// <summary>A missing Authorization header is a blank token: it floors to minimum access
    /// rather than 401, and the principal token-hash is still stashed for the elevation tool.</summary>
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
    /// When ServerHealthState is fatally degraded, every request is short-circuited with
    /// 503 and the same fixed, detail-free body - regardless of whether a token was
    /// presented at all, so a fatal config error can never look like an auth failure and
    /// can never leak which file or detail was wrong. Constructs the middleware directly
    /// (like <see cref="ExpiredToken_Returns401DistinctFromUnknownToken"/> above) rather
    /// than going through the shared healthy Fixture, since that fixture's whole point is a
    /// working config.
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

    [Fact]
    public async Task KnownToken_IsNotRejectedByTheGate()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.AdminToken);
        using var response = await client.PostAsync("/mcp", JsonBody("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The gate runs on the body peek, before the dispatcher resolves a name to a tool, so the
    /// tool named here does not have to exist - and deliberately does not, since a real name
    /// would suggest the refusal depended on one.
    /// </summary>
    [Fact]
    public async Task ToolCallNamingOutOfScopeVault_Returns403()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"some_tool","arguments":{"vault":"not-configured","from":"a","to":"b"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The vault-scope peek must match the "vault" argument property case-insensitively. A tool's
    /// advertised schema spells it "vault" by the snake_case wire rule, but binders accept any
    /// casing, so a client sending "Vault" must still hit the informative 403 gate rather than
    /// falling through to the vaguer op-class/path-scope denial one layer down.
    /// </summary>
    [Theory]
    [InlineData("vault")]
    [InlineData("Vault")]
    public async Task ToolCallNamingOutOfScopeVault_Returns403_RegardlessOfArgumentCasing(string vaultPropertyName)
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"some_tool","arguments":{"VAULT_PROPERTY":"not-configured","source":"a","dest":"b"}}}""".Replace("VAULT_PROPERTY", vaultPropertyName);
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The gate's admitting side. Unlike the refusals above, this one needs a tool that exists:
    /// with none, the dispatcher answers "unknown tool" as a 200 tool error, so not-401/not-403
    /// holds for any name at all and the assertion cannot tell an admitted call from a missing
    /// tool. Restore it against the first compiled tool that takes a vault argument.
    /// </summary>
    [Fact(Skip = "Needs a compiled tool taking a vault argument; with none, this passes for any tool name.")]
    public async Task ToolCallNamingInScopeVault_IsNotRejectedByTheGate()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ReadOnlyToken);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"some_tool","arguments":{"vault":"personal","path":"a.md"}}}""";
        using var response = await client.PostAsync("/mcp", JsonBody(body));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    public sealed class Fixture : ProgramHostFixtureBase
    {
        public const string AdminToken = "test-admin-token";
        public const string ReadOnlyToken = "test-readonly-token";

        public Fixture() : base("vault-mcp-middleware-tests-")
        {
            var vaultRoot = Path.Combine(TempDir, "personal");
            Directory.CreateDirectory(vaultRoot);
            var vaultsPath = Path.Combine(TempDir, "vaults.json");
            File.WriteAllText(vaultsPath, JsonSerializer.Serialize(new
            {
                default_vault = "personal",
                vaults = new Dictionary<string, object>{ ["personal"] = new { path = vaultRoot } },
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
