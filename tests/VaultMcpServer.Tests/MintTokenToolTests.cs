using System.Text.Json;
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
/// Covers <see cref="MintTokenTool"/>'s own logic at the unit level, in particular how it groups
/// the flat vault-prefixed <c>scope_roots</c> strings into the per-vault dictionary shape of
/// <see cref="Grant.ScopeRoots"/>. <c>MintTokenDispatchTests</c> covers the same tool over HTTP.
/// </summary>
public sealed class MintTokenToolTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-mint-tool-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private GrantStore MakeStore()
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, """
            { "admin-token": { "id": "admin", "opClasses": ["content:read", "content:write", "grants:manage"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": ["*"], "scratch": ["*"] } } }
            """);
        return GrantStore.Load(path, OpClasses, _vaultConfig, new ServerHealthState());
    }

    /// <summary>
    /// Builds a store from an arbitrary grants.json body. The attenuation tests need caller grants
    /// that are scoped, hold restricted op classes or expire, none of which the fixed admin row in
    /// the other overload covers.
    /// </summary>
    /// <param name="grantsJson">The grants.json body to load.</param>
    /// <param name="timeProvider">The clock the store uses for expiry, or null for the system clock.</param>
    /// <returns>A store loaded from that body.</returns>
    private GrantStore MakeStore(string grantsJson, TimeProvider? timeProvider = null)
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, grantsJson);
        return GrantStore.Load(path, OpClasses, _vaultConfig, new ServerHealthState(), timeProvider);
    }

    private static Dictionary<string, JsonElement> Arguments(IEnumerable<string> scopeRoots, IEnumerable<string> opClass) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new { scope_roots = scopeRoots, op_class = opClass }))!;

    private static Dictionary<string, JsonElement> Arguments(IEnumerable<string> scopeRoots, IEnumerable<string> opClass, int ttl) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new { scope_roots = scopeRoots, op_class = opClass, ttl }))!;

    /// <summary>
    /// Runs the tool's own body over bound arguments. The execute path's checks are not this class's
    /// subject, and several tests use a caller whose grant lacks the tool's floor.
    /// </summary>
    /// <param name="store">The grant store the tool mints into.</param>
    /// <param name="arguments">The bound tool arguments.</param>
    /// <param name="token">The caller's token, looked up in <paramref name="store"/>.</param>
    /// <returns>The tool's result or the refusal it raised.</returns>
    private Outcome Mint(GrantStore store, IReadOnlyDictionary<string, JsonElement> arguments, string token = "admin-token") =>
        Run(new MintTokenTool(store, OpClasses, _vaultConfig), MintTokenTool.Declaration, arguments, store.Authenticate(token)!);

    [Fact]
    public void Invoke_MultiVaultScopeRoots_ProducesSeparatePerVaultSets()
    {
        var store = MakeStore();
        var result = Mint(store, Arguments(["test/Projects/Foo", "scratch/Areas/Bar"], ["content:read"]), "admin-token");

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(result.Payload);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.NotNull(mintedGrant);
        Assert.NotNull(mintedGrant.ScopeRoots);

        Assert.True(mintedGrant.ScopeRoots!.ContainsKey("test"));
        Assert.True(mintedGrant.ScopeRoots!.ContainsKey("scratch"));

        var testRoots = Assert.IsType<VaultScope.RootedScope>(mintedGrant.ScopeRoots!["test"]).Roots;
        var scratchRoots = Assert.IsType<VaultScope.RootedScope>(mintedGrant.ScopeRoots!["scratch"]).Roots;

        Assert.Contains("Projects/Foo", testRoots);
        Assert.DoesNotContain("Areas/Bar", testRoots);

        Assert.Contains("Areas/Bar", scratchRoots);
        Assert.DoesNotContain("Projects/Foo", scratchRoots);
    }

    [Fact]
    public void Invoke_WholeVaultScopeRoot_ProducesUnscopedForThatVault()
    {
        var store = MakeStore();
        var result = Mint(store, Arguments(["test"], ["content:read"]), "admin-token");

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(result.Payload);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.NotNull(mintedGrant?.ScopeRoots);
        Assert.Equal(VaultScope.Unscoped, mintedGrant!.ScopeRoots!["test"]);
    }

    [Theory]
    [InlineData("test/../escape")]  // ".." escaping past the vault root
    [InlineData("test//rooted")]    // an empty leading segment -> a rooted remainder
    public void Invoke_ScopeRootSubtreeThatCannotBeCanonicalized_IsRejectedAsError(string scopeRoot)
    {
        var store = MakeStore();
        var result = Mint(store, Arguments([scopeRoot], ["content:read"]), "admin-token");

        // Subtrees are normalized at mint time, as GrantStore.Load does, so a malformed subtree
        // fails this call rather than producing a grant that fails on some later tool call.
        Assert.True(result.IsError);
        Assert.Equal("invalid_argument", result.Code);
        Assert.Contains("invalid subtree", result.ErrorText);
        Assert.Equal(scopeRoot, result.Data!["scope_root"]);
    }

    [Fact]
    public void Invoke_ScopeRootSubtreeWithRedundantSegments_IsCanonicalized()
    {
        var store = MakeStore();
        var result = Mint(store, Arguments(["test/Projects/./Foo"], ["content:read"]), "admin-token");

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(result.Payload);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.Contains("Projects/Foo", Assert.IsType<VaultScope.RootedScope>(mintedGrant!.ScopeRoots!["test"]).Roots);
    }

    [Fact]
    public void Invoke_WholeVaultAndSubtreeForSameVault_WholeVaultWinsProducingUnscoped()
    {
        var store = MakeStore();
        var result = Mint(store, Arguments(["test", "test/Projects/Foo"], ["content:read"]), "admin-token");

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(result.Payload);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.NotNull(mintedGrant?.ScopeRoots);
        Assert.Equal(VaultScope.Unscoped, mintedGrant!.ScopeRoots!["test"]);
    }

    // --- Attenuation-only minting ---------------------------------------------------------

    [Fact]
    public void Invoke_RequestWithinCallersAuthority_Succeeds()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read", "grants:manage"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects"] } } }
            """);
        var result = Mint(store, Arguments(["test/Projects/Foo"], ["content:read"]), "scoped-token");

        Assert.False(result.IsError == true);
    }

    [Fact]
    public void Invoke_OpClassNotHeldByCaller_IsRejectedNamingTheDimension()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read", "grants:manage"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }
            """);
        var result = Mint(store, Arguments(["test"], ["content:write"]), "scoped-token");

        Assert.True(result.IsError);
        // authority_required rather than invalid_argument: minting can only attenuate, so a request
        // beyond the caller's authority is refused rather than narrowed, and the caller's remedy is
        // elevation rather than a different argument.
        Assert.Equal("authority_required", result.Code);
        var text = result.ErrorText;
        Assert.Contains("op_class", text);
        Assert.Contains("content:write", text);
        Assert.Contains("scoped", text); // names the caller's own grant id

        // The data object names the refused argument and nothing derived from the caller's own
        // authority: not the missing classes, not the grant's class set, not its id.
        var data = result.Data!;
        Assert.Equal("op_class", data["argument"]);
        Assert.Equal(["argument"], data.Keys.ToArray());
    }

    [Fact]
    public void Invoke_ManageGrantsIsAllowedIffTheCallerHoldsIt_NoSpecialCase()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read", "grants:manage"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }
            """);
        var result = Mint(store, Arguments(["test"], ["content:read", "grants:manage"]), "scoped-token");

        Assert.False(result.IsError == true);
    }

    [Fact]
    public void Invoke_VaultNotHeldByCaller_IsRejectedNamingTheDimension()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }
            """);
        var result = Mint(store, Arguments(["scratch"], ["content:read"]), "scoped-token");

        Assert.True(result.IsError);
        // out_of_scope is the same code the dispatcher's vault-membership denial uses: one code per
        // condition, whichever layer refused.
        Assert.Equal("out_of_scope", result.Code);
        var text = result.ErrorText;
        Assert.Contains("scope_roots", text);
        Assert.Contains("scratch", text);
        Assert.Contains("scoped", text);

        // The caller's own vault set never reaches data, only the refused dimension.
        Assert.Equal(["argument"], result.Data!.Keys.ToArray());
    }

    [Fact]
    public void Invoke_ScopeRootOutsideCallersRoot_IsRejectedNamingTheDimension()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects/Foo"] } } }
            """);
        var result = Mint(store, Arguments(["test/Projects/Bar"], ["content:read"]), "scoped-token");

        Assert.True(result.IsError);
        Assert.Equal("out_of_scope", result.Code);
        var text = result.ErrorText;
        Assert.Contains("scope_roots", text);
        Assert.Contains("test/Projects/Bar", text);
        Assert.Contains("scoped", text);

        // The data object names the vault the caller itself addressed. The caller's own roots for
        // that vault stay out of it.
        var data = result.Data!;
        Assert.Equal("test", data["vault"]);
        Assert.Equal(["argument", "vault"], data.Keys.Order().ToArray());
    }

    [Fact]
    public void Invoke_WholeVaultRequestFromScopedCaller_IsRejected()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects/Foo"] } } }
            """);
        var result = Mint(store, Arguments(["test"], ["content:read"]), "scoped-token");

        Assert.True(result.IsError);
        Assert.Equal("out_of_scope", result.Code);
        Assert.Contains("unscoped access", result.ErrorText);
    }

    [Fact]
    public void Invoke_UnscopedVaultCaller_PermitsAnyRequestedRoot()
    {
        var store = MakeStore("""
            { "admin-token": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["*"] } } }
            """);
        var result = Mint(store, Arguments(["test/Anywhere/At/All"], ["content:read"]), "admin-token");

        Assert.False(result.IsError == true);
    }

    [Fact]
    public void Invoke_TtlWithinCallersRemainingLifetime_MintsTheRequestedTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new FixedTimeProvider(now);
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 60, "scopeRoots": { "test": ["*"] } } }
            """, timeProvider);
        var result = Mint(store, Arguments(["test"], ["content:read"], 10), "scoped-token");

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(result.Payload);
        Assert.False(minted.RootElement.GetProperty("ttl_capped").GetBoolean());
        var mintedGrant = store.Authenticate(minted.RootElement.GetProperty("token").GetString()!);
        Assert.Equal(now.AddMinutes(10), mintedGrant!.ExpiresAtUtc);
    }

    [Fact]
    public void Invoke_TtlExceedingCallersRemainingLifetime_IsCappedNotRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new FixedTimeProvider(now);
        // The caller's own grant expires in 5 minutes.
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 5, "scopeRoots": { "test": ["*"] } } }
            """, timeProvider);
        var callerGrant = store.Authenticate("scoped-token")!;
        Assert.Equal(now.AddMinutes(5), callerGrant.ExpiresAtUtc);

        // Requests 60 minutes - far beyond the caller's own remaining 5.
        var result = Mint(store, Arguments(["test"], ["content:read"], 60), "scoped-token");

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(result.Payload);
        Assert.True(minted.RootElement.GetProperty("ttl_capped").GetBoolean());
        Assert.Equal(60, minted.RootElement.GetProperty("ttl_minutes").GetInt32()); // requested value, reported as-is

        var mintedGrant = store.Authenticate(minted.RootElement.GetProperty("token").GetString()!);
        Assert.NotNull(mintedGrant);
        // Capped at the caller's own expiry, not the requested 60 minutes.
        Assert.Equal(callerGrant.ExpiresAtUtc, mintedGrant!.ExpiresAtUtc);
        Assert.True(mintedGrant.ExpiresAtUtc < now.AddMinutes(60));
    }

    [Fact]
    public void Invoke_InfiniteCaller_ImposesNoTtlCap()
    {
        var store = MakeStore(); // admin-token: ttl "*" (infinite)
        Assert.Null(store.Authenticate("admin-token")!.ExpiresAtUtc);

        var result = Mint(store, Arguments(["test"], ["content:read"], 1_000_000));

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(result.Payload);
        Assert.False(minted.RootElement.GetProperty("ttl_capped").GetBoolean());
    }

    // --- Coded argument refusals ----------------------------------------------------------

    [Fact]
    public void Invoke_UnknownVaultInScopeRoots_IsUnknownVault()
    {
        var store = MakeStore();
        var result = Mint(store, Arguments(["no-such-vault/Projects"], ["content:read"]), "admin-token");

        Assert.True(result.IsError);
        // unknown_vault is the code for a name that is not configured anywhere, so a caller handles
        // it the same way whichever layer saw it.
        Assert.Equal("unknown_vault", result.Code);
        var data = result.Data!;
        Assert.Equal("no-such-vault", data["vault"]);
        Assert.Equal("no-such-vault/Projects", data["scope_root"]);
    }

    [Theory]
    [InlineData("scope_roots")]
    [InlineData("op_class")]
    public void Invoke_WildcardArgument_IsInvalidArgumentNamingIt(string argument)
    {
        var store = MakeStore();
        var result = Mint(store, Arguments(argument == "scope_roots" ? ["*"] : ["test"], argument == "op_class" ? ["*"] : ["content:read"]), "admin-token");

        Assert.True(result.IsError);
        Assert.Equal("invalid_argument", result.Code);
        Assert.Equal(argument, result.Data!["argument"]);
    }

    [Fact]
    public void Invoke_UnknownOpClassName_IsInvalidArgumentNotACapabilityDenial()
    {
        var store = MakeStore();
        var result = Mint(store, Arguments(["test"], ["not:a-class"]), "admin-token");

        Assert.True(result.IsError);
        // No elevation can satisfy a class name that is not in the registry, so this must not carry
        // the capability-shortfall code the attenuation check raises.
        Assert.Equal("invalid_argument", result.Code);
        Assert.Equal("not:a-class", result.Data!["op_class"]);
    }

    [Fact]
    public void Invoke_WildcardTtl_IsInvalidArgument()
    {
        var store = MakeStore();
        var registry = new ToolRegistry().Add(MintTokenTool.Declaration, new MintTokenTool(store, OpClasses, _vaultConfig));
        var arguments = Args(new { scope_roots = new[] { "test" }, op_class = new[] { "content:read" }, ttl = "*" });

        // A "*" ttl fails argument binding rather than the tool's own range check, so this goes
        // through the execute path, which is where a binding failure becomes a coded refusal.
        var refusal = Assert.Throws<ToolRefusal>(() => new ToolExecution(_vaultConfig).Invoke(
            registry.Find("mint_token")!.Value, arguments, store.Authenticate("admin-token")!, new ClientSession("principal")));

        Assert.Equal("invalid_argument", refusal.Code);
        Assert.Equal("ttl", JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal)).RootElement.GetProperty("data").GetProperty("argument").GetString());
    }
}
