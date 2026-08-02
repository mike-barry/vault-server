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

    // The fixture's VaultConfig configures vaults "test" (default) and "scratch" - every row
    // here names those, since GrantStore.Load now validates each row's vaults against the
    // configured set.
    private const string TwoGrants = """
        {
          "admin-token": { "id": "admin", "opClasses": ["content:read", "content:write"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } },
          "readonly-token": { "id": "read-only", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["*"], "scratch": ["*"] } }
        }
        """;

    /// <summary>A known-good row loaded alongside each bad row in the row-skip tests, proving a skip is per-row, not whole-file.</summary>
    private const string GoodRow = """ "good-token": { "id": "good", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """;

    /// <summary>Loads a grants.json built from the given rows (each a `"token": {...}` fragment) against the fixture config and a fresh health state.</summary>
    private (GrantStore Store, ServerHealthState HealthState) LoadRows(params string[] rows)
    {
        var json = "{\n" + string.Join(",\n", rows) + "\n}";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, healthState);
        return (store, healthState);
    }

    [Fact]
    public void Authenticate_KnownTokenReturnsItsGrant()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());

        var admin = store.Authenticate("admin-token");
        Assert.NotNull(admin);
        Assert.Equal("admin", admin.Id);
        Assert.True(admin.Allows(OpClass.Read));
        Assert.True(admin.Allows(OpClass.Write));
        Assert.True(admin.CanAccessVault("test"));
        Assert.False(admin.CanAccessVault("scratch"));

        var readOnly = store.Authenticate("readonly-token");
        Assert.NotNull(readOnly);
        Assert.Equal("read-only", readOnly.Id);
        Assert.True(readOnly.Allows(OpClass.Read));
        Assert.False(readOnly.Allows(OpClass.Write));
        Assert.True(readOnly.CanAccessVault("scratch"));
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
    public void Load_MissingFileIsFatal()
    {
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(Path.Combine(_tempDir, "absent.json"), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_EmptyObjectIsFatal()
    {
        // Zero configured rows is a structurally-empty grant table - still whole-file Fatal,
        // unlike a table whose rows were individually skipped.
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants("{}"), OpClasses, _vaultConfig, healthState: healthState);

        AssertFatalAndEmpty(healthState, store);
    }

    [Fact]
    public void Load_EveryRowSkipped_IsFatal()
    {
        // All rows individually invalid: each still records its own Degraded skip (the
        // per-row detail), but a grants.json that parses down to zero usable rows is
        // functionally indistinguishable from an empty grant table - nobody can
        // authenticate against it - so it now also records a Fatal problem, same as the
        // structurally-empty-file case, instead of leaving the server merely Degraded with
        // callers silently getting plain 401s.
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "a", "opClasses": ["Nope"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            """ "token-b": { "id": "b", "opClasses": ["content:read"], "vaults": ["gone"], "ttl": "*", "scopeRoots": { "gone": ["*"] } } """);

        Assert.True(healthState.IsFatallyDegraded);
        Assert.Equal(3, healthState.Problems.Count);
        Assert.Equal(2, healthState.Problems.Count(p => p.Severity == ProblemSeverity.Degraded));
        var fatal = Assert.Single(healthState.Problems, p => p.Severity == ProblemSeverity.Fatal);
        Assert.Equal("GrantStore", fatal.Source);
        Assert.Null(store.Authenticate("token-a"));
        Assert.Null(store.Authenticate("token-b"));
    }

    [Fact]
    public void Load_DuplicateId_SkipsSecondRowKeepsFirst()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            """ "token-b": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store);
        Assert.NotNull(store.Authenticate("token-a"));
        Assert.Null(store.Authenticate("token-b"));
    }

    [Fact]
    public void Load_UnknownOpClass_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["Delete"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "unknown opClass");
    }

    [Fact]
    public void Load_EmptyOpClasses_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": [], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store);
    }

    [Fact]
    public void Load_EmptyVaults_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": [], "ttl": "*", "scopeRoots": {} } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store);
    }

    [Fact]
    public void Load_UnknownVault_SkipsRow()
    {
        // The new check this batch adds: a row naming a vault absent from the configured
        // vault set (e.g. after a vault rename) skips rather than loading silently inert.
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["renamed-away"], "ttl": "*", "scopeRoots": { "renamed-away": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "unknown vault \"renamed-away\"");
    }

    [Fact]
    public void Load_RowNamingASkippedVaultStillLoads()
    {
        // A vault whose root is missing on disk is per-entry skipped by VaultConfig but is
        // still CONFIGURED - a grant referencing it stays valid (the vault being broken is
        // the vault's problem, not the grant's), so the grant works again without a grants
        // reload once the root is restored and the server restarts.
        var config = LoadConfigWithBrokenVault();

        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["broken"], "ttl": "*", "scopeRoots": { "broken": ["*"] } } }""";
        var healthState = new ServerHealthState();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, config, healthState);

        Assert.DoesNotContain(healthState.Problems, p => p.Source == "GrantStore");
        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.True(grant.CanAccessVault("broken"));
    }

    [Fact]
    public void Load_VaultsWildcardExpandsAcrossSkippedVaultsToo()
    {
        // "*" expands against the FULL configured set, skipped vaults included, so a grant's
        // effective vault set stays stable while a vault root is temporarily broken.
        var config = LoadConfigWithBrokenVault();

        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, config, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.True(grant.CanAccessVault("ok"));
        Assert.True(grant.CanAccessVault("broken"));

        // The scope wildcard derives from the expanded vault set, so delegation carries the scope
        // roots in with it: the skipped vault gets a real unscoped entry rather than being left
        // without one, which is what keeps the grant whole across a restart that fixes the root.
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["ok"]);
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["broken"]);
    }

    [Fact]
    public void Load_MissingId_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store);
    }

    [Fact]
    public void Load_OpClassesWildcardExpandsToAllRegistryClasses()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["*"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var opClass in OpClasses.Names)
            Assert.True(grant.Allows(opClass));
    }

    [Fact]
    public void Load_VaultsWildcardExpandsToAllConfiguredVaults()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var vault in _vaultConfig.ConfiguredVaultNames)
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
        Assert.Equal(_vaultConfig.ConfiguredVaultNames.Count, grant.Vaults.Count);
    }

    [Fact]
    public void Load_MissingTtl_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_InvalidTtlString_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "forever", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_FractionalTtl_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 5.5, "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_NonPositiveTtl_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 0, "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_WildcardTtlNeverExpires()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }""";
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
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 30, "scopeRoots": { "test": ["*"] } } }""";
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
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 1, "scopeRoots": { "test": ["*"] } } }""";
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
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 1, "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        Assert.Null(store.Authenticate("token-a"));
    }

    [Fact]
    public void Authenticate_NotYetExpiredTokenReturnsGrant()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 30, "scopeRoots": { "test": ["*"] } } }""";
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
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 1, "scopeRoots": { "test": ["*"] } } }""";
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
    public void Load_MissingScopeRootsField_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*" } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsMissingEntryForAVaultInVaults_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyGrantsFullAccessToEveryVault()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.True(grant.ScopeRoots.ContainsKey("test"));
        Assert.True(grant.ScopeRoots.ContainsKey("scratch"));
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["test"]);
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["scratch"]);
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyMixedWithExplicitVaultEntry_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "*": ["*"], "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyWithNonWildcardValue_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "*": ["Projects/Widget"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsVaultWildcardMeansThatVaultIsUnscoped()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.True(grant.ScopeRoots.ContainsKey("test"));
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["test"]);
    }

    [Fact]
    public void Load_ScopeRootsEmptyListForVault_SkipsRow()
    {
        // The empty list is the one spelling a mistake produces on its own - a dropped entry, a
        // list filtered to nothing - so it is rejected rather than read as whole-vault access.
        // Losing content from a grant must never widen it.
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsEmptyListIsRejectedRatherThanTreatedAsTheWildcard()
    {
        // The discriminating pair: the same row differing only in the spelling of its scope. If
        // the empty list were still read as unscoped these would agree, and this test would pass
        // while proving nothing.
        var empty = GrantStore.Load(
            WriteGrants("""{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } }"""),
            OpClasses, _vaultConfig, new ServerHealthState());
        var wildcard = GrantStore.Load(
            WriteGrants("""{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }"""),
            OpClasses, _vaultConfig, new ServerHealthState());

        Assert.Null(empty.Authenticate("token-a"));
        Assert.Equal(VaultScope.Unscoped, wildcard.Authenticate("token-a")!.ScopeRoots["test"]);
    }

    [Fact]
    public void Load_ScopeRootsCanonicalizesEntries()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects\\Widget", "Areas/./Homelab"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        AssertRoots(new[] { "Projects/Widget", "Areas/Homelab" }, grant.ScopeRoots["test"]);
    }

    [Fact]
    public void Load_ScopeRootsRootedEntry_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["C:/Windows"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsEscapingEntry_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["../outside"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsEmptyEntry_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [""] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsNotAnObject_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": ["Projects/Widget"] } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsVaultEntryNotAnArray_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": "Projects/Widget" } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsVaultNotInGrantsVaults_SkipsRow()
    {
        var (store, healthState) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "scratch": ["Projects/Widget"] } } """,
            GoodRow);

        AssertSingleRowSkipped(healthState, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsMultiVaultKeepsEachVaultsRootsSeparate()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["Area"], "scratch": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new ServerHealthState());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.True(grant.ScopeRoots.ContainsKey("test"));
        Assert.True(grant.ScopeRoots.ContainsKey("scratch"));
        AssertRoots(new[] { "Area" }, grant.ScopeRoots["test"]);
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["scratch"]);
    }

    [Fact]
    public void Mint_ReturnsATokenThatAuthenticatesToTheMintedGrant()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState());

        var (token, _, _) = store.Mint(
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
        store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "scratch" }, ScopeRoots(("scratch", "")), ttlMinutes: 30);

        var admin = store.Authenticate("admin-token");
        Assert.NotNull(admin);
        Assert.NotNull(admin.ScopeRoots);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test" }, admin.ScopeRoots.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mint_ExpiresAfterItsTtlElapses()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);

        var (token, _, _) = store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 1);
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

        var (tokenA, _, _) = store.Mint("minted-a", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);
        var (tokenB, _, _) = store.Mint("minted-b", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);

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

    /// <summary>Shared assertion for the remaining whole-file-failure cases (missing file,
    /// structurally-empty table): the load records a Fatal problem from source "GrantStore"
    /// and returns an inert store nobody can authenticate against. Per-row validation
    /// failures no longer land here - they skip just their row; see
    /// <see cref="AssertSingleRowSkipped"/>.</summary>
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

    /// <summary>Shared assertion for every row-level skip case: exactly one Degraded problem
    /// from source "GrantStore" (never Fatal - the server stays fully operational), the bad
    /// "token-a" row does not authenticate, and the known-good row loaded normally.</summary>
    private static void AssertSingleRowSkipped(ServerHealthState healthState, GrantStore store, string? expectedDetailFragment = null)
    {
        Assert.False(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal("GrantStore", problem.Source);
        Assert.NotEmpty(problem.Detail);
        if (expectedDetailFragment is not null)
            Assert.Contains(expectedDetailFragment, problem.Detail);

        Assert.NotNull(store.Authenticate("good-token"));
    }

    /// <summary>Loads a VaultConfig with a healthy vault "ok" (also the default) and a vault "broken" whose root does not exist - the per-entry-skip shape the skipped-vault grant tests need.</summary>
    private VaultConfig LoadConfigWithBrokenVault()
    {
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            default_vault = "ok",
            vaults = new Dictionary<string, string>
            {
                ["ok"] = _tempDir,
                ["broken"] = Path.Combine(_tempDir, "no-such-vault-root"),
            },
        }));
        var config = VaultConfig.Load(configPath, new ServerHealthState());
        Assert.Single(config.SkippedVaults);
        return config;
    }

    /// <summary>Builds the per-vault <see cref="Grant.ScopeRoots"/> dictionary shape from (vault, subtree) pairs, grouping subtrees under their vault key.</summary>
    private static Dictionary<string, VaultScope> ScopeRoots(params (string Vault, string Subtree)[] entries)
    {
        var byVault = new Dictionary<string, HashSet<string>>();
        foreach (var (vault, subtree) in entries)
        {
            if (!byVault.TryGetValue(vault, out var subtrees))
                byVault[vault] = subtrees = new HashSet<string>();
            subtrees.Add(subtree);
        }
        return byVault.ToDictionary(kv => kv.Key, kv => VaultScope.Of(kv.Value));
    }

    /// <summary>A per-vault map where every named vault is unscoped - the whole-vault case.</summary>
    private static Dictionary<string, VaultScope> UnscopedFor(params string[] vaults) =>
        vaults.ToDictionary(v => v, _ => VaultScope.Unscoped, StringComparer.OrdinalIgnoreCase);

    /// <summary>Asserts a vault's scope is confined to exactly <paramref name="expected"/>.</summary>
    private static void AssertRoots(IEnumerable<string> expected, VaultScope actual) =>
        Assert.Equal(
            expected.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Assert.IsType<VaultScope.RootedScope>(actual).Roots);
}
