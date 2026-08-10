using VaultShared.Auth;
using VaultShared.Health;
using VaultShared.Configuration;
using VaultShared.Scope;

namespace VaultShared.Tests;

public sealed class GrantStoreTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new RecordingProblemSink());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-grants-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteGrants(string json)
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, json);
        return path;
    }

    // The fixture's VaultConfig configures vaults "test" (the default) and "scratch". Every row
    // here names one of those, because GrantStore.Load validates each row's vaults against the
    // configured set.
    private const string TwoGrants = """
        {
          "admin-token": { "id": "admin", "opClasses": ["content:read", "content:write"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } },
          "readonly-token": { "id": "read-only", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["*"], "scratch": ["*"] } }
        }
        """;

    /// <summary>
    /// A valid row loaded alongside each bad row in the row-skip tests, which shows that a skip
    /// applies to one row rather than to the whole file.
    /// </summary>
    private const string GoodRow = """ "good-token": { "id": "good", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """;

    /// <summary>
    /// Loads a grants.json built from the given rows against the fixture config and a fresh sink.
    /// </summary>
    /// <param name="rows">The rows to join into the file, each a <c>"token": { ... }</c> fragment.</param>
    /// <returns>The loaded store and the sink the load recorded into.</returns>
    private (GrantStore Store, RecordingProblemSink HealthState) LoadRows(params string[] rows)
    {
        var json = "{\n" + string.Join(",\n", rows) + "\n}";
        var sink = new RecordingProblemSink();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, sink);
        return (store, sink);
    }

    [Fact]
    public void Authenticate_KnownTokenReturnsItsGrant()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());

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
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());
        Assert.Null(store.Authenticate("not-a-real-token"));
    }

    [Fact]
    public void Authenticate_EmptyTokenReturnsNull()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());
        Assert.Null(store.Authenticate(""));
    }

    [Fact]
    public void Load_MissingFileIsFatal()
    {
        var sink = new RecordingProblemSink();
        var store = GrantStore.Load(Path.Combine(_tempDir, "absent.json"), OpClasses, _vaultConfig, sink: sink);

        AssertFatalAndEmpty(sink, store);
    }

    [Fact]
    public void Load_EmptyObjectIsFatal()
    {
        // A file with no rows at all is a whole-file failure, unlike a file whose rows were
        // individually skipped.
        var sink = new RecordingProblemSink();
        var store = GrantStore.Load(WriteGrants("{}"), OpClasses, _vaultConfig, sink: sink);

        AssertFatalAndEmpty(sink, store);
    }

    [Fact]
    public void Load_EveryRowSkipped_IsFatal()
    {
        // Every row is invalid, so each records its own degraded skip with its own detail. A file
        // that parses down to zero usable rows leaves nobody able to authenticate, so the load
        // also records a fatal problem, the same as for a file with no rows at all. Without it
        // the server would stay merely degraded while every caller received a plain 401.
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "a", "opClasses": ["Nope"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            """ "token-b": { "id": "b", "opClasses": ["content:read"], "vaults": ["gone"], "ttl": "*", "scopeRoots": { "gone": ["*"] } } """);

        Assert.True(sink.IsFatallyDegraded);
        Assert.Equal(3, sink.Problems.Count);
        Assert.Equal(2, sink.Problems.Count(p => p.Severity == ProblemSeverity.Degraded));
        var fatal = Assert.Single(sink.Problems, p => p.Severity == ProblemSeverity.Fatal);
        Assert.Equal("GrantStore", fatal.Source);
        Assert.Null(store.Authenticate("token-a"));
        Assert.Null(store.Authenticate("token-b"));
    }

    [Fact]
    public void Load_DuplicateId_SkipsSecondRowKeepsFirst()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            """ "token-b": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "duplicate grant id \"admin\"", skippedToken: "token-b");
        Assert.NotNull(store.Authenticate("token-a"));
    }

    [Fact]
    public void Load_UnknownOpClass_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["Delete"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "unknown opClass");
    }

    [Fact]
    public void Load_EmptyOpClasses_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": [], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "has no opClasses");
    }

    [Fact]
    public void Load_EmptyVaults_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": [], "ttl": "*", "scopeRoots": {} } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "has no vaults");
    }

    [Fact]
    public void Load_UnknownVault_SkipsRow()
    {
        // A row naming a vault absent from the configured vault set (e.g. after a vault rename)
        // skips rather than loading silently inert.
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["renamed-away"], "ttl": "*", "scopeRoots": { "renamed-away": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "unknown vault \"renamed-away\"");
    }

    [Fact]
    public void Load_RowNamingASkippedVaultStillLoads()
    {
        // VaultConfig skips a vault whose root is missing on disk, but the vault is still
        // configured, so a grant naming it stays valid. The grant works again after the root is
        // restored and the server restarts, with no change to grants.json.
        var config = LoadConfigWithBrokenVault();

        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["broken"], "ttl": "*", "scopeRoots": { "broken": ["*"] } } }""";
        var sink = new RecordingProblemSink();
        var store = GrantStore.Load(WriteGrants(json), OpClasses, config, sink);

        Assert.DoesNotContain(sink.Problems, p => p.Source == "GrantStore");
        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.True(grant.CanAccessVault("broken"));
    }

    [Fact]
    public void Load_VaultsWildcardExpandsAcrossSkippedVaultsToo()
    {
        // "*" expands against the whole configured set, skipped vaults included, so a grant's
        // effective vault set stays stable while a vault root is temporarily broken.
        var config = LoadConfigWithBrokenVault();

        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, config, new RecordingProblemSink());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.True(grant.CanAccessVault("ok"));
        Assert.True(grant.CanAccessVault("broken"));

        // The scope wildcard expands over the same vault set, so the skipped vault gets its own
        // unscoped entry instead of being left without one.
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["ok"]);
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["broken"]);
    }

    [Fact]
    public void Load_MissingId_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "missing a non-empty \"id\"");
    }

    [Fact]
    public void Load_OpClassesWildcardExpandsToAllRegistryClasses()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["*"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var opClass in OpClasses.Names)
            Assert.True(grant.Allows(opClass));
    }

    [Fact]
    public void Load_VaultsWildcardExpandsToAllConfiguredVaults()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var vault in _vaultConfig.ConfiguredVaultNames)
            Assert.True(grant.CanAccessVault(vault));
    }

    [Fact]
    public void Load_BothWildcardsExpandTogether()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["*"], "vaults": ["*"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.Equal(OpClasses.Names.Count, grant.OpClasses.Count);
        Assert.Equal(_vaultConfig.ConfiguredVaultNames.Count, grant.Vaults.Count);
    }

    [Fact]
    public void Load_MissingTtl_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_InvalidTtlString_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "forever", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_FractionalTtl_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 5.5, "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_NonPositiveTtl_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 0, "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "ttl");
    }

    [Fact]
    public void Load_WildcardTtlNeverExpires()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

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
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink(), new FixedTimeProvider(loadedAt));

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.Equal(loadedAt.AddMinutes(30), grant.ExpiresAtUtc);
    }

    [Fact]
    public void Load_NumericTtlAlreadyElapsedIsExpired()
    {
        // A grant loaded 10 minutes ago with a 1-minute ttl is already expired, without the test
        // depending on real elapsed wall-clock time.
        var loadedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 1, "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink(), new FixedTimeProvider(loadedAt));

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.True(grant.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Authenticate_ExpiredTokenReturnsNull()
    {
        // Authenticate is the single source of truth for whether a grant is currently usable, so
        // an expired grant must be indistinguishable from an unknown one to this method.
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 1, "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink(), timeProvider);

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        Assert.Null(store.Authenticate("token-a"));
    }

    [Fact]
    public void Authenticate_NotYetExpiredTokenReturnsGrant()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 30, "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink(), timeProvider);

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
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink(), timeProvider);

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        Assert.Null(store.Authenticate("token-a"));
        Assert.Equal(AuthFailureReason.Expired, store.GetAuthFailureReason("token-a"));
    }

    [Fact]
    public void GetAuthFailureReason_ReturnsUnknownForUnrecognizedToken()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());
        Assert.Equal(AuthFailureReason.Unknown, store.GetAuthFailureReason("not-a-real-token"));
    }

    [Fact]
    public void Load_MissingScopeRootsField_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*" } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsMissingEntryForAVaultInVaults_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyGrantsFullAccessToEveryVault()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "*": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

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
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "*": ["*"], "test": ["*"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsWildcardKeyWithNonWildcardValue_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "*": ["Projects/Widget"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsVaultWildcardMeansThatVaultIsUnscoped()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        Assert.True(grant.ScopeRoots.ContainsKey("test"));
        Assert.Equal(VaultScope.Unscoped, grant.ScopeRoots["test"]);
    }

    [Fact]
    public void Load_ScopeRootsEmptyListForVault_SkipsRow()
    {
        // An empty list is what a dropped entry or a list filtered to nothing leaves behind, so it
        // is rejected rather than read as whole-vault access. Losing content from a grant must
        // never widen it.
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsCanonicalizesEntries()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects\\Widget", "Areas/./Homelab"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.NotNull(grant.ScopeRoots);
        AssertRoots(new[] { "Projects/Widget", "Areas/Homelab" }, grant.ScopeRoots["test"]);
    }

    [Fact]
    public void Load_ScopeRootsRootedEntry_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["C:/Windows"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsEscapingEntry_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["../outside"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsEmptyEntry_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [""] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsNotAnObject_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": ["Projects/Widget"] } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsVaultEntryNotAnArray_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": "Projects/Widget" } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsVaultNotInGrantsVaults_SkipsRow()
    {
        var (store, sink) = LoadRows(
            """ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "scratch": ["Projects/Widget"] } } """,
            GoodRow);

        AssertSingleRowSkipped(sink, store, expectedDetailFragment: "scopeRoots");
    }

    [Fact]
    public void Load_ScopeRootsMultiVaultKeepsEachVaultsRootsSeparate()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["Area"], "scratch": ["*"] } } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig, new RecordingProblemSink());

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
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());

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
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());
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
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink(), timeProvider);

        var (token, _, _) = store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 1);
        Assert.NotNull(store.Authenticate(token));

        timeProvider.Now = timeProvider.Now.AddMinutes(10);

        Assert.Null(store.Authenticate(token));
        Assert.Equal(AuthFailureReason.Expired, store.GetAuthFailureReason(token));
    }

    [Fact]
    public void Mint_UnknownTokenAfterMintingIsStillUnknown()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());
        store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);

        Assert.Null(store.Authenticate("not-a-real-token"));
        Assert.Equal(AuthFailureReason.Unknown, store.GetAuthFailureReason("not-a-real-token"));
    }

    [Fact]
    public void Mint_EachCallReturnsADistinctUnguessableToken()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());

        var (tokenA, _, _) = store.Mint("minted-a", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);
        var (tokenB, _, _) = store.Mint("minted-b", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 30);

        Assert.NotEqual(tokenA, tokenB);
        Assert.Equal("minted-a", store.Authenticate(tokenA)!.Id);
        Assert.Equal("minted-b", store.Authenticate(tokenB)!.Id);
    }

    [Fact]
    public void Mint_NonPositiveTtlThrows()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig, new RecordingProblemSink());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Mint("minted-1", new HashSet<string> { OpClass.Read }, new HashSet<string> { "test" }, ScopeRoots(("test", "")), ttlMinutes: 0));
    }

    /// <summary>
    /// Asserts the outcome shared by the whole-file failure cases, a missing file and a file with
    /// no rows: the load records one fatal problem from source "GrantStore" and returns a store
    /// nobody can authenticate against. A per-row validation failure skips only its row instead;
    /// see <see cref="AssertSingleRowSkipped"/>.
    /// </summary>
    /// <param name="sink">The sink the failed load recorded into.</param>
    /// <param name="store">The store the failed load returned.</param>
    private static void AssertFatalAndEmpty(RecordingProblemSink sink, GrantStore store)
    {
        Assert.True(sink.IsFatallyDegraded);
        var problem = Assert.Single(sink.Problems);
        Assert.Equal(ProblemSeverity.Fatal, problem.Severity);
        Assert.Equal("GrantStore", problem.Source);
        Assert.NotEmpty(problem.Detail);

        Assert.Null(store.Authenticate("admin-token"));
        Assert.Null(store.Authenticate("readonly-token"));
    }

    /// <summary>
    /// Asserts the outcome shared by every row-level skip case: exactly one degraded problem from
    /// source "GrantStore", never a fatal one, the skipped row's token not authenticating, and the
    /// valid row still loaded.
    /// </summary>
    /// <param name="sink">The sink the load recorded into.</param>
    /// <param name="store">The store the load returned.</param>
    /// <param name="expectedDetailFragment">
    /// Text the recorded detail must contain, or <see langword="null"/> to accept any detail.
    /// </param>
    /// <param name="skippedToken">The token of the row that must not have loaded.</param>
    /// <remarks>
    /// The token assertion is what makes this a skip rather than a log line. A loader that recorded
    /// the problem and admitted the grant anyway would satisfy every other assertion here, and the
    /// failure would be fail-open.
    /// </remarks>
    private static void AssertSingleRowSkipped(
        RecordingProblemSink sink, GrantStore store, string? expectedDetailFragment = null, string skippedToken = "token-a")
    {
        Assert.False(sink.IsFatallyDegraded);
        var problem = Assert.Single(sink.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal("GrantStore", problem.Source);
        Assert.NotEmpty(problem.Detail);
        if (expectedDetailFragment is not null)
            Assert.Contains(expectedDetailFragment, problem.Detail);

        Assert.Null(store.Authenticate(skippedToken));
        Assert.NotNull(store.Authenticate("good-token"));
    }

    /// <summary>
    /// Loads a VaultConfig holding a healthy default vault "ok" and a vault "broken" whose root
    /// does not exist, so the load skips exactly one vault entry.
    /// </summary>
    /// <returns>The loaded configuration.</returns>
    private VaultConfig LoadConfigWithBrokenVault()
    {
        var configPath = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            default_vault = "ok",
            vaults = new Dictionary<string, object>
            {
                ["ok"] = new { path = _tempDir },
                ["broken"] = new { path = Path.Combine(_tempDir, "no-such-vault-root") },
            },
        }));
        var config = VaultConfigLoader.Load(configPath, TestOpClasses.Repo, new RecordingProblemSink());
        Assert.Single(config.SkippedVaults);
        return config;
    }

    /// <summary>
    /// Builds a <see cref="Grant.ScopeRoots"/> dictionary from vault and subtree pairs, grouping
    /// subtrees under their vault name.
    /// </summary>
    /// <param name="entries">The vault and subtree pairs to group.</param>
    /// <returns>One scope per named vault.</returns>
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

    /// <summary>Asserts that a scope is rooted at exactly the expected paths.</summary>
    /// <param name="expected">The roots the scope must hold, compared case-insensitively.</param>
    /// <param name="actual">The scope to check.</param>
    private static void AssertRoots(IEnumerable<string> expected, VaultScope actual) =>
        Assert.Equal(
            expected.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Assert.IsType<VaultScope.RootedScope>(actual).Roots);
}
