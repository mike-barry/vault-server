using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VaultMcpServer.Auth;
using VaultShared.Auth;
using VaultShared.Health;
using VaultShared.Configuration;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Unit coverage for the middleware's loopback gate (Batch B hardening): the selector->default
/// binding and the minimum-access floor apply only to a loopback remote, while a non-loopback
/// request must present a real token that <see cref="GrantStore.Authenticate"/> accepts - otherwise
/// a fixed generic 401 with no Expired-vs-Unknown oracle in the body. Constructs the middleware
/// directly with a <see cref="DefaultHttpContext"/> whose <c>RemoteIpAddress</c> is set explicitly,
/// which the hosted TestServer can't vary per case. Uses <see cref="TestVaultFixture"/> only for a
/// valid <see cref="VaultConfig"/> to validate the grant rows against.
/// </summary>
public sealed class LoopbackAuthMiddlewareTests : IClassFixture<TestVaultFixture>, IDisposable
{
    private const string SelectorToken = "test-loopback-selector";
    private const string RealToken = "real-worker-token";
    private const string ExpiringToken = "expiring-worker-token";
    private static readonly IPAddress NonLoopback = IPAddress.Parse("203.0.113.5");

    private readonly TestVaultFixture _vaultFixture;
    private readonly OpClassRegistry _opClassRegistry =
        OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-loopback-auth-tests-").FullName;
    private readonly string _grantsPath;

    public LoopbackAuthMiddlewareTests(TestVaultFixture vaultFixture)
    {
        _vaultFixture = vaultFixture;
        _grantsPath = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(_grantsPath, """
            {
              "admin": { "named": true, "id": "admin", "opClasses": ["*"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } },
              "real-worker-token": { "id": "worker", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } },
              "expiring-worker-token": { "id": "worker-expiring", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 1, "scopeRoots": { "test": ["*"] } }
            }
            """);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private GrantStore BuildStore(TimeProvider timeProvider) => GrantStore.Load(
        _grantsPath, _opClassRegistry, _vaultFixture.Config, new ServerHealthState(), timeProvider,
        new LocalhostAuthOptions(SelectorToken, ExpectedLocalhostToken: null, DefaultGrantId: "admin"));

    private static async Task<(int Status, string Body, Grant? Grant)> RunAsync(GrantStore store, IPAddress? remoteIp, string? token)
    {
        Grant? captured = null;
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        context.Connection.RemoteIpAddress = remoteIp;
        if (token is not null)
            context.Request.Headers.Authorization = "Bearer " + token;

        await new BearerGrantMiddleware(
            c => { captured = c.Items[BearerGrantMiddleware.GrantItemKey] as Grant; return Task.CompletedTask; },
            store, new ServerHealthState(), NullLogger<BearerGrantMiddleware>.Instance).InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body, captured);
    }

    [Fact]
    public async Task Loopback_SelectorToken_BindsToDefaultAdminGrant()
    {
        var (status, _, grant) = await RunAsync(BuildStore(TimeProvider.System), IPAddress.Loopback, SelectorToken);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("admin", grant?.Id);
    }

    [Fact]
    public async Task Loopback_UnknownToken_FloorsToMinimumAccess()
    {
        var (status, _, grant) = await RunAsync(BuildStore(TimeProvider.System), IPAddress.Loopback, "not-a-real-token");

        Assert.NotEqual(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(GrantStore.MinimumGrant.Id, grant?.Id);
    }

    [Fact]
    public async Task NonLoopback_RealToken_Authenticates()
    {
        var (status, _, grant) = await RunAsync(BuildStore(TimeProvider.System), NonLoopback, RealToken);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("worker", grant?.Id);
    }

    [Fact]
    public async Task NonLoopback_SelectorToken_Is401NoFloorNoSelector()
    {
        // Off loopback the selector is just an unrecognized token - no default binding, no floor.
        var (status, body, grant) = await RunAsync(BuildStore(TimeProvider.System), NonLoopback, SelectorToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(BearerGrantMiddleware.UnauthorizedBody, body);
        Assert.Null(grant);
    }

    [Fact]
    public async Task NonLoopback_MissingHeader_Is401()
    {
        var (status, body, grant) = await RunAsync(BuildStore(TimeProvider.System), NonLoopback, token: null);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(BearerGrantMiddleware.UnauthorizedBody, body);
        Assert.Null(grant);
    }

    [Fact]
    public async Task NonLoopback_UnknownAndExpiredToken_Return401WithIdenticalBody_NoOracle()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = BuildStore(timeProvider);
        timeProvider.Now = timeProvider.Now.AddMinutes(10); // the 1-minute expiring grant has now lapsed

        var (unknownStatus, unknownBody, _) = await RunAsync(store, NonLoopback, "not-a-real-token");
        var (expiredStatus, expiredBody, _) = await RunAsync(store, NonLoopback, ExpiringToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, unknownStatus);
        Assert.Equal(StatusCodes.Status401Unauthorized, expiredStatus);
        // Unknown and expired are indistinguishable in the response body - the Expired-vs-Unknown
        // distinction stays server-side (the audit log), never an oracle a remote caller can probe.
        Assert.Equal(BearerGrantMiddleware.UnauthorizedBody, unknownBody);
        Assert.Equal(unknownBody, expiredBody);
        Assert.DoesNotContain("expired", expiredBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ExpiringToken, expiredBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullRemoteIp_UnknownToken_IsDeniedNotFloored()
    {
        // A null RemoteIpAddress is treated as non-loopback (fail closed): no floor, so an
        // unrecognized token is a 401 rather than the minimum-access grant.
        var (status, body, grant) = await RunAsync(BuildStore(TimeProvider.System), remoteIp: null, "not-a-real-token");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(BearerGrantMiddleware.UnauthorizedBody, body);
        Assert.Null(grant);
    }

    [Fact]
    public async Task NullRemoteIp_RealToken_StillAuthenticates()
    {
        // Fail-closed applies only to the floor/selector fallback; a real token authenticates
        // regardless of remote address.
        var (status, _, grant) = await RunAsync(BuildStore(TimeProvider.System), remoteIp: null, RealToken);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("worker", grant?.Id);
    }
}
