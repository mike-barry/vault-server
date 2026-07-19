using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using VaultMcpServer.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

/// <summary>
/// Unit coverage for <see cref="ElevationRequestTool"/>: the deny-by-default opt-in gate, the
/// per-principal rate limit, the auto-deny stub path, and a successful approval that rebinds the
/// caller's token via the <see cref="GrantStore"/> binding table. Each test builds its own store
/// (elevation mutates the binding table), a fake <see cref="IElevationApprover"/>, and an
/// <see cref="IHttpContextAccessor"/> carrying the principal token-hash the middleware would have
/// stashed.
/// </summary>
public sealed class ElevationRequestToolTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-elevation-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private const string CallerToken = "caller-token";

    // Opt-in caller (Read only) plus a token-less named "elevated" grant that also has Write.
    private const string Grants = """
        {
          "caller-token": { "id": "caller", "opClasses": ["Read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] }, "canRequestElevation": true },
          "no-elevate-token": { "id": "no-elevate", "opClasses": ["Read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } },
          "elevated": { "named": true, "id": "elevated", "opClasses": ["Read", "Write"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } }
        }
        """;

    private GrantStore MakeStore(TimeProvider? timeProvider = null)
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, Grants);
        return GrantStore.Load(path, OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);
    }

    private static IHttpContextAccessor AccessorFor(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[BearerGrantMiddleware.PrincipalTokenHashItemKey] = GrantStore.HashKey(token);
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private ElevationRequestTool MakeTool(GrantStore store, IElevationApprover approver, IHttpContextAccessor accessor, TimeProvider? timeProvider = null, ElevationOptions? options = null) =>
        new(store, approver, accessor, OpClasses, _vaultConfig, options ?? new ElevationOptions(), timeProvider ?? TimeProvider.System);

    private static Dictionary<string, JsonElement> Args(object obj) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(obj))!;

    private static bool IsError(CallToolResult r) => r.IsError == true;
    private static string Text(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    private sealed class FakeApprover(ElevationDecision decision) : IElevationApprover
    {
        public int Calls { get; private set; }
        public Task<ElevationDecision> ReviewAsync(ElevationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    [Fact]
    public async Task Invoke_GrantWithoutOptIn_IsDeniedBeforeApprover()
    {
        var store = MakeStore();
        var approver = new FakeApprover(ElevationDecision.Approve());
        var tool = MakeTool(store, approver, AccessorFor("no-elevate-token"));
        var caller = store.Authenticate("no-elevate-token")!;

        var result = await tool.InvokeAsync(Args(new { grant = "elevated" }), caller, CancellationToken.None);

        Assert.True(IsError(result));
        Assert.Contains("not permitted to request elevation", Text(result));
        Assert.Equal(0, approver.Calls); // never reaches the approver
        Assert.Equal("no-elevate", store.Authenticate("no-elevate-token")!.Id); // unchanged
    }

    [Fact]
    public async Task Invoke_AutoDenyStub_ReturnsInformativeErrorAndDoesNotRebind()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new AutoDenyElevationApprover(), AccessorFor(CallerToken));
        var caller = store.Authenticate(CallerToken)!;

        var result = await tool.InvokeAsync(Args(new { grant = "elevated" }), caller, CancellationToken.None);

        Assert.True(IsError(result));
        Assert.Contains("denied", Text(result));
        Assert.Equal("caller", store.Authenticate(CallerToken)!.Id); // still the base grant, no rebind
    }

    [Fact]
    public async Task Invoke_ApprovedNamedGrant_RebindsTheCallersToken()
    {
        var store = MakeStore();
        var approver = new FakeApprover(ElevationDecision.Approve());
        var tool = MakeTool(store, approver, AccessorFor(CallerToken));
        var caller = store.Authenticate(CallerToken)!;

        var result = await tool.InvokeAsync(Args(new { grant = "elevated", ttl = 10 }), caller, CancellationToken.None);

        Assert.False(IsError(result));
        Assert.Equal(1, approver.Calls);

        var elevated = store.Authenticate(CallerToken);
        Assert.NotNull(elevated);
        Assert.Equal("elevated", elevated.Id);
        Assert.True(elevated.Allows("Write"));
    }

    [Fact]
    public async Task Invoke_ApprovedNamedGrant_RevertsAfterTtl()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(timeProvider);
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()), AccessorFor(CallerToken), timeProvider);
        var caller = store.Authenticate(CallerToken)!;

        await tool.InvokeAsync(Args(new { grant = "elevated", ttl = 5 }), caller, CancellationToken.None);
        Assert.Equal("elevated", store.Authenticate(CallerToken)!.Id);

        timeProvider.Now = timeProvider.Now.AddMinutes(6);
        Assert.Equal("caller", store.Authenticate(CallerToken)!.Id);
    }

    [Fact]
    public async Task Invoke_UnknownNamedGrant_IsError()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()), AccessorFor(CallerToken));
        var caller = store.Authenticate(CallerToken)!;

        var result = await tool.InvokeAsync(Args(new { grant = "does-not-exist" }), caller, CancellationToken.None);

        Assert.True(IsError(result));
        Assert.Contains("Unknown named grant", Text(result));
    }

    [Fact]
    public async Task Invoke_RateLimited_AfterMaxRequestsInWindow()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(timeProvider);
        // Auto-deny so nothing rebinds and every attempt is just a "request" counted by the limiter.
        var tool = MakeTool(store, new AutoDenyElevationApprover(), AccessorFor(CallerToken), timeProvider, new ElevationOptions(MaxRequestsPerMinute: 2));
        var caller = store.Authenticate(CallerToken)!;

        var first = await tool.InvokeAsync(Args(new { grant = "elevated" }), caller, CancellationToken.None);
        var second = await tool.InvokeAsync(Args(new { grant = "elevated" }), caller, CancellationToken.None);
        var third = await tool.InvokeAsync(Args(new { grant = "elevated" }), caller, CancellationToken.None);

        Assert.Contains("denied", Text(first));   // auto-deny, but not rate-limited
        Assert.Contains("denied", Text(second));
        Assert.Contains("rate-limited", Text(third));

        // The window resets: after it elapses, a request is allowed through to the approver again.
        timeProvider.Now = timeProvider.Now.AddMinutes(2);
        var afterWindow = await tool.InvokeAsync(Args(new { grant = "elevated" }), caller, CancellationToken.None);
        Assert.Contains("denied", Text(afterWindow));
        Assert.DoesNotContain("rate-limited", Text(afterWindow));
    }

    [Fact]
    public async Task Invoke_ApprovedScopedMint_RebindsToAScopedGrant()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()), AccessorFor(CallerToken));
        var caller = store.Authenticate(CallerToken)!;

        var result = await tool.InvokeAsync(
            Args(new { scope_roots = new[] { "test/Projects/Foo" }, op_class = new[] { "Read" }, ttl = 10 }),
            caller, CancellationToken.None);

        Assert.False(IsError(result));
        var elevated = store.Authenticate(CallerToken);
        Assert.NotNull(elevated);
        Assert.StartsWith("elevated-", elevated.Id);
        Assert.Contains("Projects/Foo", elevated.ScopeRoots["test"]);
    }

    [Fact]
    public async Task Invoke_BothNamedAndScopedTarget_IsError()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()), AccessorFor(CallerToken));
        var caller = store.Authenticate(CallerToken)!;

        var result = await tool.InvokeAsync(
            Args(new { grant = "elevated", scope_roots = new[] { "test" }, op_class = new[] { "Read" } }),
            caller, CancellationToken.None);

        Assert.True(IsError(result));
        Assert.Contains("not both", Text(result));
    }
}
