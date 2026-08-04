using System.Text.Json;
using VaultMcpServer.Auth;
using VaultShared.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tests;
using VaultShared.Tools;
using static VaultMcpServer.Tests.DeclaredToolHarness;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers <see cref="ElevationRequestTool"/>: the deny-by-default opt-in gate, the per-principal
/// rate limit, the auto-deny approver, and a successful approval that rebinds the caller's token
/// through the <see cref="GrantStore"/> binding table. Each test builds its own store, because
/// elevation mutates that table, along with its own approver and session.
/// </summary>
public sealed class ElevationRequestToolTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-elevation-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private const string CallerToken = "caller-token";

    // Opt-in caller (Read only) plus a token-less named "elevated" grant that also has Write.
    private const string Grants = """
        {
          "caller-token": { "id": "caller", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] }, "canRequestElevation": true },
          "no-elevate-token": { "id": "no-elevate", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } },
          "elevated": { "named": true, "id": "elevated", "opClasses": ["content:read", "content:write"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } }
        }
        """;

    private GrantStore MakeStore(TimeProvider? timeProvider = null)
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, Grants);
        return GrantStore.Load(path, OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);
    }

    /// <summary>Builds the session the execute path would supply, carrying the principal identity
    /// the middleware derives from the caller's token.</summary>
    /// <param name="token">The caller's bearer token.</param>
    /// <returns>A session whose principal is the hash of that token.</returns>
    private static ClientSession SessionFor(string token) => new(GrantStore.HashKey(token));

    private ElevationRequestTool MakeTool(GrantStore store, IElevationApprover approver, TimeProvider? timeProvider = null, ElevationOptions? options = null) =>
        new(store, approver, OpClasses, _vaultConfig, options ?? new ElevationOptions(), timeProvider ?? TimeProvider.System);

    private static Outcome Elevate(ElevationRequestTool tool, GrantStore store, string token, object arguments) =>
        Run(tool, ElevationRequestTool.Declaration, Args(arguments), store.Authenticate(token)!, SessionFor(token));

    private static bool IsError(Outcome r) => r.IsError;
    private static string Text(Outcome r) => r.ErrorText;
    private static string Code(Outcome r) => r.Code;
    private static Dictionary<string, string>? Data(Outcome r) => r.Data;

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
    public void Invoke_GrantWithoutOptIn_IsDeniedBeforeApprover()
    {
        var store = MakeStore();
        var approver = new FakeApprover(ElevationDecision.Approve());
        var tool = MakeTool(store, approver);

        var result = Elevate(tool, store, "no-elevate-token", Args(new { grant = "elevated" }));

        Assert.True(IsError(result));
        // The canRequestElevation opt-in stands in for an op class on this tool, so its shortfall
        // carries the op-class shortfall's code.
        Assert.Equal("authority_required", Code(result));
        Assert.Contains("not permitted to request elevation", Text(result));
        // This tool is reachable from the minimum-access floor without an op class, so its refusal
        // must not disclose anything about the calling grant.
        Assert.Null(Data(result));
        Assert.Equal(0, approver.Calls); // never reaches the approver
        Assert.Equal("no-elevate", store.Authenticate("no-elevate-token")!.Id); // unchanged
    }

    [Fact]
    public void Invoke_AutoDenyStub_ReturnsInformativeErrorAndDoesNotRebind()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new AutoDenyElevationApprover());

        var result = Elevate(tool, store, CallerToken, Args(new { grant = "elevated" }));

        Assert.True(IsError(result));
        // Deliberately the same code as the opt-in denial above: a caller cannot distinguish "not
        // allowed to ask" from "asked and refused".
        Assert.Equal("authority_required", Code(result));
        Assert.Contains("denied", Text(result));
        Assert.Equal("caller", store.Authenticate(CallerToken)!.Id); // still the base grant, no rebind
    }

    [Fact]
    public void Invoke_ApprovedNamedGrant_RebindsTheCallersToken()
    {
        var store = MakeStore();
        var approver = new FakeApprover(ElevationDecision.Approve());
        var tool = MakeTool(store, approver);

        var result = Elevate(tool, store, CallerToken, Args(new { grant = "elevated", ttl = 10 }));

        Assert.False(IsError(result));
        Assert.Equal(1, approver.Calls);

        var elevated = store.Authenticate(CallerToken);
        Assert.NotNull(elevated);
        Assert.Equal("elevated", elevated.Id);
        Assert.True(elevated.Allows("content:write"));
    }

    [Fact]
    public void Invoke_ApprovedNamedGrant_RevertsAfterTtl()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(timeProvider);
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()), timeProvider);

        Elevate(tool, store, CallerToken, Args(new { grant = "elevated", ttl = 5 }));
        Assert.Equal("elevated", store.Authenticate(CallerToken)!.Id);

        timeProvider.Now = timeProvider.Now.AddMinutes(6);
        Assert.Equal("caller", store.Authenticate(CallerToken)!.Id);
    }

    [Fact]
    public void Invoke_UnknownNamedGrant_IsError()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()));

        var result = Elevate(tool, store, CallerToken, Args(new { grant = "does-not-exist" }));

        Assert.True(IsError(result));
        // Distinct from the approver's authority_required denial. The rate limit is consumed before
        // this check, so probing for grant names stays bounded at MaxRequestsPerMinute.
        Assert.Equal("not_found", Code(result));
        Assert.Contains("Unknown named grant", Text(result));
        Assert.Equal("does-not-exist", Data(result)!["grant"]); // the caller's own argument, echoed
    }

    [Fact]
    public void Invoke_RateLimited_AfterMaxRequestsInWindow()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(timeProvider);
        // Auto-deny so nothing rebinds and every attempt is just a "request" counted by the limiter.
        var tool = MakeTool(store, new AutoDenyElevationApprover(), timeProvider, new ElevationOptions(MaxRequestsPerMinute: 2));

        var first = Elevate(tool, store, CallerToken, Args(new { grant = "elevated" }));
        var second = Elevate(tool, store, CallerToken, Args(new { grant = "elevated" }));
        var third = Elevate(tool, store, CallerToken, Args(new { grant = "elevated" }));

        Assert.Equal("authority_required", Code(first));   // auto-deny, but not rate-limited
        Assert.Equal("authority_required", Code(second));
        // rate_limited is the code for a transient throttle. A capability or argument code would
        // tell the caller a retryable condition was permanent.
        Assert.Equal("rate_limited", Code(third));
        Assert.Equal("2", Data(third)!["limit_per_minute"]);

        // The window resets: after it elapses, a request is allowed through to the approver again.
        timeProvider.Now = timeProvider.Now.AddMinutes(2);
        var afterWindow = Elevate(tool, store, CallerToken, Args(new { grant = "elevated" }));
        Assert.Equal("authority_required", Code(afterWindow));
    }

    /// <summary>
    /// Verifies that the per-principal throttle is consumed before the requested target is
    /// resolved, so a request naming an unknown grant still spends rate-limit budget.
    /// </summary>
    /// <remarks>
    /// Do not move target resolution ahead of the throttle. That ordering is what keeps the
    /// <c>not_found</c> code from being an unthrottled way to enumerate the configured named
    /// grants, and it is why <c>not_found</c> can stay distinct from <c>authority_required</c>.
    /// </remarks>
    [Fact]
    public void Invoke_UnknownNamedGrant_ConsumesRateLimitBudget()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = MakeStore(timeProvider);
        var tool = MakeTool(store, new AutoDenyElevationApprover(), timeProvider, new ElevationOptions(MaxRequestsPerMinute: 2));

        // Spend the whole window on requests that fail target resolution.
        var firstProbe = Elevate(tool, store, CallerToken, Args(new { grant = "does-not-exist-1" }));
        var secondProbe = Elevate(tool, store, CallerToken, Args(new { grant = "does-not-exist-2" }));
        Assert.Equal("not_found", Code(firstProbe));
        Assert.Equal("not_found", Code(secondProbe));

        // Those not_found responses consumed the budget: a request for a target that really exists
        // is now throttled, which is only possible if the throttle ran first.
        var afterExhaustion = Elevate(tool, store, CallerToken, Args(new { grant = "elevated" }));
        Assert.True(IsError(afterExhaustion));
        Assert.Equal("rate_limited", Code(afterExhaustion));
        Assert.Equal("2", Data(afterExhaustion)!["limit_per_minute"]);

        // Enumeration stops with the budget: a further unknown target answers rate_limited rather
        // than not_found, so the probe rate is capped at MaxRequestsPerMinute.
        var throttledProbe = Elevate(tool, store, CallerToken, Args(new { grant = "does-not-exist-3" }));
        Assert.Equal("rate_limited", Code(throttledProbe));
        // No target-existence datum leaks past the throttle - only the caller's configured limit.
        Assert.Null(Data(throttledProbe)!.GetValueOrDefault("grant"));

        // Once the window rolls over, probing resumes at the same capped rate.
        timeProvider.Now = timeProvider.Now.AddMinutes(2);
        var afterWindow = Elevate(tool, store, CallerToken, Args(new { grant = "does-not-exist-4" }));
        Assert.Equal("not_found", Code(afterWindow));
    }

    [Fact]
    public void Invoke_ApprovedScopedMint_RebindsToAScopedGrant()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()));

        var result = Elevate(tool, store, CallerToken, Args(new { scope_roots = new[] { "test/Projects/Foo" }, op_class = new[] { "content:read" }, ttl = 10 }));

        Assert.False(IsError(result));
        var elevated = store.Authenticate(CallerToken);
        Assert.NotNull(elevated);
        Assert.StartsWith("elevated-", elevated.Id);
        Assert.Contains("Projects/Foo", Assert.IsType<VaultScope.RootedScope>(elevated.ScopeRoots["test"]).Roots);
    }

    [Fact]
    public void Invoke_BothNamedAndScopedTarget_IsError()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()));

        var result = Elevate(tool, store, CallerToken, Args(new { grant = "elevated", scope_roots = new[] { "test" }, op_class = new[] { "content:read" } }));

        Assert.True(IsError(result));
        Assert.Equal("invalid_argument", Code(result));
        Assert.Contains("not both", Text(result));
        // No single argument is at fault, so the refusal carries no "argument" datum.
        Assert.Null(Data(result));
    }

    [Fact]
    public void Invoke_ScopedTargetNamingAnUnknownVault_IsUnknownVault()
    {
        var store = MakeStore();
        var tool = MakeTool(store, new FakeApprover(ElevationDecision.Approve()));

        var result = Elevate(tool, store, CallerToken, Args(new { scope_roots = new[] { "no-such-vault/Projects" }, op_class = new[] { "content:read" } }));

        Assert.True(IsError(result));
        // Naming the vault here discloses nothing new: the configured vault set is already
        // readable by any grant through health_check.
        Assert.Equal("unknown_vault", Code(result));
        Assert.Equal("no-such-vault", Data(result)!["vault"]);
    }

    [Fact]
    public void Invoke_UnparseableTtl_IsInvalidArgumentNamingTheArgument()
    {
        var store = MakeStore();
        var registry = new ToolRegistry().Add(
            ElevationRequestTool.Declaration, MakeTool(store, new FakeApprover(ElevationDecision.Approve())));

        // A "*" ttl fails argument binding rather than the tool's own range check, so this goes
        // through the execute path, which is where a binding failure becomes a coded refusal.
        var refusal = Assert.Throws<ToolRefusal>(() => new ToolExecution(_vaultConfig).Invoke(
            registry.Find("request_elevation")!.Value,
            Args(new { grant = "elevated", ttl = "*" }),
            store.Authenticate(CallerToken)!,
            SessionFor(CallerToken)));

        Assert.Equal("invalid_argument", refusal.Code);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("ttl", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }
}
