using VaultShared.Auth;
using VaultShared.Health;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Tests;

/// <summary>
/// Tests the localhost auth model on <see cref="GrantStore"/>: token-less named grants parsed
/// from grants.json, localhost token resolution in
/// <see cref="GrantStore.ResolveLocalhostGrant"/>, and the elevation binding table, including
/// rebinding with a TTL and reverting on expiry inside <see cref="GrantStore.Authenticate"/>.
/// </summary>
/// <remarks>
/// Every test builds its own store from a temp grants.json, so a test that mutates the binding
/// table never shares a store with another. Binding tests use <see cref="FixedTimeProvider"/> to
/// drive TTL expiry without waiting on the wall clock.
/// </remarks>
public sealed class GrantStoreBindingTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new RecordingProblemSink());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-binding-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private GrantStore Load(string json, TimeProvider? timeProvider = null, LocalhostAuthOptions? localhostOptions = null)
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, json);
        return GrantStore.Load(path, OpClasses, _vaultConfig, new RecordingProblemSink(), timeProvider, localhostOptions);
    }

    // A token-keyed opt-in caller row plus a token-less named "elevated" grant with broader access.
    private const string CallerAndNamedTarget = """
        {
          "caller-token": { "id": "caller", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] }, "canRequestElevation": true },
          "elevated": { "named": true, "id": "elevated", "opClasses": ["content:read", "content:write"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["*"], "scratch": ["*"] } }
        }
        """;

    [Fact]
    public void NamedGrant_IsNotAuthenticatableByAnyToken()
    {
        var store = Load(CallerAndNamedTarget);

        // The named grant's object key ("elevated") is not a bearer token - presenting it must not
        // authenticate, and neither does its id.
        Assert.Null(store.Authenticate("elevated"));
        Assert.NotNull(store.Authenticate("caller-token"));
    }

    [Fact]
    public void TryGetNamedGrant_FindsNamedTarget_NotTokenRows()
    {
        var store = Load(CallerAndNamedTarget);

        Assert.True(store.TryGetNamedGrant("elevated", out var named));
        Assert.Equal("elevated", named.Id);
        Assert.True(named.Allows("content:write"));

        // A token-keyed grant is not a named-grant elevation target.
        Assert.False(store.TryGetNamedGrant("caller", out _));
    }

    [Fact]
    public void Load_DuplicateIdAcrossTokenAndNamedGrant_SkipsSecond()
    {
        var sink = new RecordingProblemSink();
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, """
            {
              "some-token": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } },
              "admin": { "named": true, "id": "admin", "opClasses": ["*"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } }
            }
            """);
        var store = GrantStore.Load(path, OpClasses, _vaultConfig, sink);

        // The second "admin" (the named grant) is the duplicate and is skipped; the token row wins.
        Assert.Single(sink.Problems, p => p.Severity == ProblemSeverity.Degraded);
        Assert.NotNull(store.Authenticate("some-token"));
        Assert.False(store.TryGetNamedGrant("admin", out _));
    }

    [Fact]
    public void Load_CanRequestElevationFlag_ParsesAndDefaultsFalse()
    {
        var store = Load(CallerAndNamedTarget);
        Assert.True(store.Authenticate("caller-token")!.CanRequestElevation);

        var noFlag = Load("""{ "t": { "id": "plain", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }""");
        Assert.False(noFlag.Authenticate("t")!.CanRequestElevation);
    }

    [Fact]
    public void ResolveLocalhostGrant_RealTokenResolvesToItsGrant()
    {
        var store = Load(CallerAndNamedTarget);
        var grant = store.ResolveLocalhostGrant("caller-token");
        Assert.Equal("caller", grant.Id);
    }

    [Fact]
    public void ResolveLocalhostGrant_SelectorTokenBindsToConfiguredDefaultNamedGrant()
    {
        var store = Load(CallerAndNamedTarget, localhostOptions: new LocalhostAuthOptions("the-selector", DefaultGrantId: "elevated"));

        var grant = store.ResolveLocalhostGrant("the-selector");
        Assert.Equal("elevated", grant.Id);
    }

    [Fact]
    public void ResolveLocalhostGrant_SelectorWithNoDefaultConfigured_FloorsToMinimum()
    {
        // DefaultGrantId absent => the selector is just another unrecognized token => minimum.
        var store = Load(CallerAndNamedTarget, localhostOptions: new LocalhostAuthOptions("the-selector"));
        Assert.Equal(GrantStore.MinimumGrant.Id, store.ResolveLocalhostGrant("the-selector").Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("totally-unknown-token")]
    public void ResolveLocalhostGrant_BlankOrUnknownTokenFloorsToMinimum(string token)
    {
        var store = Load(CallerAndNamedTarget, localhostOptions: new LocalhostAuthOptions("the-selector", DefaultGrantId: "elevated"));

        var grant = store.ResolveLocalhostGrant(token);
        Assert.Equal(GrantStore.MinimumGrant.Id, grant.Id);
        Assert.Empty(grant.OpClasses);
        Assert.Empty(grant.Vaults);
        Assert.True(grant.CanRequestElevation);
    }

    [Fact]
    public void ResolveLocalhostGrant_StrictMode_OnlyExpectedTokenSelectsDefault()
    {
        var store = Load(CallerAndNamedTarget, localhostOptions: new LocalhostAuthOptions(
            SelectorToken: "the-selector", ExpectedLocalhostToken: "strict-only", DefaultGrantId: "elevated"));

        // In strict mode the expected token replaces the well-known selector as the one that binds.
        Assert.Equal("elevated", store.ResolveLocalhostGrant("strict-only").Id);
        Assert.Equal(GrantStore.MinimumGrant.Id, store.ResolveLocalhostGrant("the-selector").Id);
    }

    [Fact]
    public void RebindToNamedGrant_MakesAuthenticateReturnTheElevatedGrant()
    {
        var store = Load(CallerAndNamedTarget);

        store.RebindToNamedGrant(GrantStore.HashKey("caller-token"), "elevated", DateTimeOffset.UtcNow.AddMinutes(10));

        var grant = store.Authenticate("caller-token");
        Assert.NotNull(grant);
        Assert.Equal("elevated", grant.Id);
        Assert.True(grant.Allows("content:write")); // the base "caller" grant is Read-only - proving the rebind took effect
    }

    [Fact]
    public void RebindToNamedGrant_RevertsToBaseGrantAfterTtlExpires()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = Load(CallerAndNamedTarget, timeProvider);

        store.RebindToNamedGrant(GrantStore.HashKey("caller-token"), "elevated", timeProvider.Now.AddMinutes(5));
        Assert.Equal("elevated", store.Authenticate("caller-token")!.Id);

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        // The binding is gone, so the token resolves to its base "caller" grant again.
        var reverted = store.Authenticate("caller-token");
        Assert.NotNull(reverted);
        Assert.Equal("caller", reverted.Id);
        Assert.False(reverted.Allows("content:write"));
    }

    [Fact]
    public void RebindToNamedGrant_UnknownTarget_Throws()
    {
        var store = Load(CallerAndNamedTarget);
        Assert.Throws<ArgumentException>(() =>
            store.RebindToNamedGrant(GrantStore.HashKey("caller-token"), "no-such-grant", DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    [Fact]
    public void RebindToMintedGrant_BindsAFreshScopedGrantWithoutANewToken()
    {
        var store = Load(CallerAndNamedTarget);

        var opClasses = (IReadOnlySet<string>)new HashSet<string> { "content:read" };
        var vaults = (IReadOnlySet<string>)new HashSet<string> { "test" };
        var scopeRoots = new Dictionary<string, VaultScope> { ["test"] = VaultScope.Of(new HashSet<string> { "Projects/Foo" }) };

        store.RebindToMintedGrant(GrantStore.HashKey("caller-token"), "elevated-abc", opClasses, vaults, scopeRoots, DateTimeOffset.UtcNow.AddMinutes(10));

        var grant = store.Authenticate("caller-token");
        Assert.NotNull(grant);
        Assert.Equal("elevated-abc", grant.Id);
        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Projects/Foo" },
            Assert.IsType<VaultScope.RootedScope>(grant.ScopeRoots["test"]).Roots);

        // A minted elevation target is not a valid named-grant target for a later request.
        Assert.False(store.TryGetNamedGrant("elevated-abc", out _));
    }
}
