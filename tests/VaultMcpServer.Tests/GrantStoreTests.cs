using VaultMcpServer.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class GrantStoreTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-grants-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteGrants(string json)
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string TwoGrants = """
        {
          "admin-token": { "id": "admin", "opClasses": ["Read", "Write"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": [] } },
          "readonly-token": { "id": "read-only", "opClasses": ["Read"], "vaults": ["personal", "test"], "ttl": "*", "scopeRoots": { "personal": [], "test": [] } }
        }
        """;

    [Fact]
    public void Authenticate_KnownTokenReturnsItsGrant()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());

        var admin = store.Authenticate("admin-token");
        Assert.NotNull(admin);
        Assert.Equal("admin", admin.Id);
        Assert.True(admin.Allows(OpClass.Read));
        Assert.True(admin.Allows(OpClass.Write));
        Assert.True(admin.CanAccessVault("personal"));
        Assert.False(admin.CanAccessVault("test"));

        var readOnly = store.Authenticate("readonly-token");
        Assert.NotNull(readOnly);
        Assert.Equal("read-only", readOnly.Id);
        Assert.True(readOnly.Allows(OpClass.Read));
        Assert.False(readOnly.Allows(OpClass.Write));
        Assert.True(readOnly.CanAccessVault("test"));
    }

    [Fact]
    public void Authenticate_UnknownTokenReturnsNull()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());
        Assert.Null(store.Authenticate("not-a-real-token"));
    }

    [Fact]
    public void Authenticate_EmptyTokenReturnsNull()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());
        Assert.Null(store.Authenticate(""));
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(Path.Combine(_tempDir, "absent.json"), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_EmptyObjectThrows()
    {
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants("{}"), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_DuplicateIdThrows()
    {
        var json = """
            {
              "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"] },
              "token-b": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"] }
            }
            """;
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_UnknownOpClassThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Delete"], "vaults": ["personal"] } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_EmptyOpClassesThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": [], "vaults": ["personal"] } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_EmptyVaultsThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": [] } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_MissingIdThrows()
    {
        var json = """{ "token-a": { "opClasses": ["Read"], "vaults": ["personal"] } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_OpClassesWildcardExpandsToAllRegistryClasses()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["*"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var opClass in OpClasses.Names)
            Assert.True(grant.Allows(opClass));
    }

    [Fact]
    public void Load_VaultsWildcardExpandsToAllConfiguredVaults()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var vault in _vaultConfig.Vaults.Keys)
            Assert.True(grant.CanAccessVault(vault));
    }

    [Fact]
    public void Load_BothWildcardsExpandTogether()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["*"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.Equal(OpClasses.Names.Count, grant.OpClasses.Count);
        Assert.Equal(_vaultConfig.Vaults.Count, grant.Vaults.Count);
    }

    [Fact]
    public void Load_MissingTtlThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"] } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_InvalidTtlStringThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "forever" } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_FractionalTtlThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": 5.5 } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_NonPositiveTtlThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": 0 } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_WildcardTtlNeverExpires()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.Null(grant.ExpiresAtUtc);
        Assert.False(grant.IsExpired(DateTimeOffset.UtcNow));
        Assert.False(grant.IsExpired(DateTimeOffset.UtcNow.AddYears(100)));
    }

    [Fact]
    public void Load_NumericTtlComputesExpiryFromLoadTime()
    {
        var loadedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": 30, "scopeRoots": { "personal": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState(), new FixedTimeProvider(loadedAt));

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.Equal(loadedAt.AddMinutes(30), grant.ExpiresAtUtc);
    }

    [Fact]
    public void Load_NumericTtlAlreadyElapsedIsExpired()
    {
        // A grant "loaded" 10 minutes ago with a 1-minute ttl expired 9 minutes ago -
        // deterministic without depending on real elapsed wall-clock time in the test.
        var loadedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": 1, "scopeRoots": { "personal": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState(), new FixedTimeProvider(loadedAt));

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.True(grant.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Authenticate_ExpiredTokenReturnsNull()
    {
        // Load with a 1-minute ttl, then advance the store's own time source 10 minutes past
        // load - simulating elapsed wall-clock time deterministically. Authenticate is the
        // single source of truth for "is this grant currently usable", so an expired grant
        // must be indistinguishable from an unknown one to this method.
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": 1, "scopeRoots": { "personal": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        Assert.Null(store.Authenticate("token-a"));
    }

    [Fact]
    public void Authenticate_NotYetExpiredTokenReturnsGrant()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": 30, "scopeRoots": { "personal": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);

        timeProvider.Now = timeProvider.Now.AddMinutes(5);

        Assert.NotNull(store.Authenticate("token-a"));
    }

    [Fact]
    public void GetAuthFailureReason_ReturnsExpiredForExpiredGrant()
    {
        // The diagnostic-only path must still distinguish an expired grant from an unknown
        // token - it just must never return the grant itself, so it can't be used to authorize
        // a call.
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": 1, "scopeRoots": { "personal": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        Assert.Null(store.Authenticate("token-a"));
        Assert.Equal(AuthFailureReason.Expired, store.GetAuthFailureReason("token-a"));
    }

    [Fact]
    public void GetAuthFailureReason_ReturnsUnknownForUnrecognizedToken()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());
        Assert.Equal(AuthFailureReason.Unknown, store.GetAuthFailureReason("not-a-real-token"));
    }

    [Fact]
    public void Load_MissingScopeRootsFieldThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*" } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsMissingEntryForAVaultInVaultsThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal", "test"], "ttl": "*", "scopeRoots": { "personal": [] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyGrantsFullAccessToEveryVault()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal", "test"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.True(grant.ScopeRoots.ContainsKey("personal"));
        Assert.True(grant.ScopeRoots.ContainsKey("test"));
        Assert.Empty(grant.ScopeRoots["personal"]);
        Assert.Empty(grant.ScopeRoots["test"]);
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyMixedWithExplicitVaultEntryThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "*": ["*"], "personal": [] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyWithNonWildcardValueThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "*": ["Projects/Widget"] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsVaultWildcardMeansThatVaultIsUnscoped()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.True(grant.ScopeRoots.ContainsKey("personal"));
        Assert.Empty(grant.ScopeRoots["personal"]);
    }

    [Fact]
    public void Load_ScopeRootsCanonicalizesEntries()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": ["Projects\\Widget", "Areas/./Homelab"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Projects/Widget", "Areas/Homelab" }, grant.ScopeRoots["personal"]);
    }

    [Fact]
    public void Load_ScopeRootsRootedEntryThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": ["C:/Windows"] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsEscapingEntryThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": ["../outside"] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsEmptyEntryThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": [""] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsNotAnObjectThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": ["Projects/Widget"] } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsVaultEntryNotAnArrayThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "personal": "Projects/Widget" } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsVaultNotInGrantsVaultsThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal"], "ttl": "*", "scopeRoots": { "test": ["Projects/Widget"] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_ScopeRootsMultiVaultKeepsEachVaultsRootsSeparate()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["personal", "test"], "ttl": "*", "scopeRoots": { "personal": ["Area"], "test": [] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.True(grant.ScopeRoots.ContainsKey("personal"));
        Assert.True(grant.ScopeRoots.ContainsKey("test"));
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Area" }, grant.ScopeRoots["personal"]);
        Assert.Empty(grant.ScopeRoots["test"]);
    }

    [Fact]
    public void Mint_ReturnsATokenThatAuthenticatesToTheMintedGrant()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());

        var token = store.Mint(
            "minted-1",
            new HashSet<string> { OpClass.Read },
            new HashSet<string> { "test" },
            ScopeRoots(("test", "Projects/Foo")),
            ttlMinutes: 30);

        var grant = store.Authenticate(token);
        Assert.NotNull(grant);
        Assert.Equal("minted-1", grant.Id);
        Assert.True(grant.Allows(OpClass.Read));
        Assert.False(grant.Allows(OpClass.Write));
        Assert.True(grant.CanAccessVault("test"));
        Assert.Equal(ScopeRoots(("test", "Projects/Foo")), grant.ScopeRoots);
    }

    [Fact]
    public void Mint_DoesNotAffectFileLoadedGrants()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());
        store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);

        var admin = store.Authenticate("admin-token");
        Assert.NotNull(admin);
        Assert.NotNull(admin.ScopeRoots);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "personal" }, admin.ScopeRoots.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mint_ExpiresAfterItsTtlElapses()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);

        var token = store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 1);
        Assert.NotNull(store.Authenticate(token));

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        Assert.Null(store.Authenticate(token));
        Assert.Equal(AuthFailureReason.Expired, store.GetAuthFailureReason(token));
    }

    [Fact]
    public void Mint_UnknownTokenAfterMintingIsStillUnknown()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());
        store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);

        Assert.Null(store.Authenticate("not-a-real-token"));
        Assert.Equal(AuthFailureReason.Unknown, store.GetAuthFailureReason("not-a-real-token"));
    }

    [Fact]
    public void Mint_EachCallReturnsADistinctUnguessableToken()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());

        var tokenA = store.Mint("minted-a", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);
        var tokenB = store.Mint("minted-b", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);

        Assert.NotEqual(tokenA, tokenB);
        Assert.Equal("minted-a", store.Authenticate(tokenA)!.Id);
        Assert.Equal("minted-b", store.Authenticate(tokenB)!.Id);
    }

    [Fact]
    public void Mint_NonPositiveTtlThrows()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 0));
    }

    /// <summary>Shared assertion for every whole-file-failure case above: the load no
    /// longer throws - it records a Fatal problem from source "GrantStore" and returns an
    /// inert store nobody can authenticate against. Per-row validation failures still fail
    /// the whole file in this batch (see <see cref="GrantStore"/>'s remarks), so this covers
    /// both a structurally-broken grants.json and a single bad row within an otherwise
    /// valid one.</summary>
    private static void AssertFatalAndEmpty(ServerHealthState healthState, GrantStore store)
    {
        Assert.True(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Fatal, problem.Severity);
        Assert.Equal("GrantStore", problem.Source);
        Assert.NotEmpty(problem.Detail);

        Assert.Null(store.Authenticate("admin-token"));
        Assert.Null(store.Authenticate("readonly-token"));
    }

    /// <summary>Builds the per-vault <see cref="Grant.ScopeRoots"/> dictionary shape from (vault, subtree) pairs, grouping subtrees under their vault key.</summary>
    private static Dictionary<string, IReadOnlySet<string>> ScopeRoots(params (string Vault, string Subtree)[] entries)
    {
        var byVault = new Dictionary<string, HashSet<string>>();
        foreach (var (vault, subtree) in entries)
        {
            if (!byVault.TryGetValue(vault, out var subtrees))
                byVault[vault] = subtrees = new HashSet<string>();
            subtrees.Add(subtree);
        }
        return byVault.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value);
    }
}
