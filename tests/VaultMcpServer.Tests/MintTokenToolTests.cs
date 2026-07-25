using System.Text.Json;
using VaultMcpServer.Auth;
using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

/// <summary>
/// Unit-level coverage for <see cref="MintTokenTool"/>'s own logic - how it groups the
/// flat, vault-prefixed <c>scope_roots</c> strings into <see cref="Grant.ScopeRoots"/>'s
/// per-vault dictionary shape - as distinct from <c>MintTokenDispatchTests</c>' HTTP-level,
/// end-to-end coverage of the same tool.
/// </summary>
public sealed class MintTokenToolTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-mint-tool-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private GrantStore MakeStore()
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, """
            { "admin-token": { "id": "admin", "opClasses": ["content:read", "content:write", "grants:manage"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": [], "scratch": [] } } }
            """);
        return GrantStore.Load(path, OpClasses, _vaultConfig, new ServerHealthState());
    }

    /// <summary>Builds a store from an arbitrary grants.json body - used by the attenuation tests
    /// below, whose caller grants need shapes (scoped, restricted op classes, finite ttl) that
    /// <see cref="MakeStore"/>'s fixed unscoped/infinite admin row doesn't cover.</summary>
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

    private static string ErrorText(ModelContextProtocol.Protocol.CallToolResult result) =>
        ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;

    /// <summary>The <c>code</c> of a refusal's <c>{code, message, data?}</c> JSON - the field callers
    /// branch on, so every denial test pins it rather than the prose beside it.</summary>
    private static string ErrorCode(ModelContextProtocol.Protocol.CallToolResult result)
    {
        using var doc = JsonDocument.Parse(ErrorText(result));
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>A refusal's <c>data</c> object, as a name-to-raw-JSON map, or null when the refusal
    /// carries none.</summary>
    private static Dictionary<string, string>? ErrorData(ModelContextProtocol.Protocol.CallToolResult result)
    {
        using var doc = JsonDocument.Parse(ErrorText(result));
        return doc.RootElement.TryGetProperty("data", out var data)
            ? data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString())
            : null;
    }

    [Fact]
    public async Task InvokeAsync_MultiVaultScopeRoots_ProducesSeparatePerVaultSets()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test/Projects/Foo", "scratch/Areas/Bar"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;
        using var minted = JsonDocument.Parse(text);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.NotNull(mintedGrant);
        Assert.NotNull(mintedGrant.ScopeRoots);

        Assert.True(mintedGrant.ScopeRoots!.ContainsKey("test"));
        Assert.True(mintedGrant.ScopeRoots!.ContainsKey("scratch"));

        var testRoots = mintedGrant.ScopeRoots!["test"];
        var scratchRoots = mintedGrant.ScopeRoots!["scratch"];

        Assert.Contains("Projects/Foo", testRoots);
        Assert.DoesNotContain("Areas/Bar", testRoots);

        Assert.Contains("Areas/Bar", scratchRoots);
        Assert.DoesNotContain("Projects/Foo", scratchRoots);
    }

    [Fact]
    public async Task InvokeAsync_WholeVaultScopeRoot_ProducesEmptySetForThatVault()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;
        using var minted = JsonDocument.Parse(text);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.NotNull(mintedGrant?.ScopeRoots);
        Assert.Empty(mintedGrant!.ScopeRoots!["test"]);
    }

    [Theory]
    [InlineData("test/../escape")]  // ".." escaping past the vault root
    [InlineData("test//rooted")]    // an empty leading segment -> a rooted remainder
    public async Task InvokeAsync_ScopeRootSubtreeThatCannotBeCanonicalized_IsRejectedAsError(string scopeRoot)
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments([scopeRoot], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        // Normalized at mint time (matching GrantStore.Load), so a malformed subtree fails the
        // mint call cleanly instead of producing an unusable grant that only fails - confusingly -
        // on some later, unrelated tool call.
        Assert.True(result.IsError);
        Assert.Equal("invalid_argument", ErrorCode(result));
        Assert.Contains("invalid subtree", ErrorText(result));
        Assert.Equal(scopeRoot, ErrorData(result)!["scopeRoot"]);
    }

    [Fact]
    public async Task InvokeAsync_ScopeRootSubtreeWithRedundantSegments_IsCanonicalized()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test/Projects/./Foo"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;
        using var minted = JsonDocument.Parse(text);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.Contains("Projects/Foo", mintedGrant!.ScopeRoots!["test"]);
    }

    [Fact]
    public async Task InvokeAsync_WholeVaultAndSubtreeForSameVault_WholeVaultWinsProducingEmptySet()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test", "test/Projects/Foo"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
        var text = ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text;
        using var minted = JsonDocument.Parse(text);
        var token = minted.RootElement.GetProperty("token").GetString()!;

        var mintedGrant = store.Authenticate(token);
        Assert.NotNull(mintedGrant?.ScopeRoots);
        Assert.Empty(mintedGrant!.ScopeRoots!["test"]);
    }

    // --- Attenuation-only minting ---------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_RequestWithinCallersAuthority_Succeeds()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read", "grants:manage"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects"] } } }
            """);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test/Projects/Foo"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
    }

    [Fact]
    public async Task InvokeAsync_OpClassNotHeldByCaller_IsRejectedNamingTheDimension()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read", "grants:manage"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } }
            """);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test"], ["content:write"]),
            callerGrant,
            CancellationToken.None);

        Assert.True(result.IsError);
        // insufficient_capability, not invalid_argument: minting is attenuation-only, so a request
        // exceeding the caller's authority is refused rather than intersected, and the fix is
        // elevation rather than a different argument.
        Assert.Equal("insufficient_capability", ErrorCode(result));
        var text = ErrorText(result);
        Assert.Contains("op_class", text);
        Assert.Contains("content:write", text);
        Assert.Contains("scoped", text); // names the caller's own grant id

        // No-oracle: data names the refused dimension and nothing computed from the caller's held
        // authority - not the missing subset, not the grant's class set, not its id.
        var data = ErrorData(result)!;
        Assert.Equal("op_class", data["argument"]);
        Assert.Equal(["argument"], data.Keys.ToArray());
    }

    [Fact]
    public async Task InvokeAsync_ManageGrantsIsAllowedIffTheCallerHoldsIt_NoSpecialCase()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read", "grants:manage"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } }
            """);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test"], ["content:read", "grants:manage"]),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
    }

    [Fact]
    public async Task InvokeAsync_VaultNotHeldByCaller_IsRejectedNamingTheDimension()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } }
            """);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["scratch"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.True(result.IsError);
        // out_of_scope, the same code the dispatcher's vault-membership denial uses - one code per
        // condition regardless of which layer refused.
        Assert.Equal("out_of_scope", ErrorCode(result));
        var text = ErrorText(result);
        Assert.Contains("scope_roots", text);
        Assert.Contains("scratch", text);
        Assert.Contains("scoped", text);

        // The caller's own vault set never reaches data, only the refused dimension.
        Assert.Equal(["argument"], ErrorData(result)!.Keys.ToArray());
    }

    [Fact]
    public async Task InvokeAsync_ScopeRootOutsideCallersRoot_IsRejectedNamingTheDimension()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects/Foo"] } } }
            """);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test/Projects/Bar"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("out_of_scope", ErrorCode(result));
        var text = ErrorText(result);
        Assert.Contains("scope_roots", text);
        Assert.Contains("test/Projects/Bar", text);
        Assert.Contains("scoped", text);

        // data names the vault the caller itself addressed; the caller's roots for it stay out.
        var data = ErrorData(result)!;
        Assert.Equal("test", data["vault"]);
        Assert.Equal(["argument", "vault"], data.Keys.Order().ToArray());
    }

    [Fact]
    public async Task InvokeAsync_WholeVaultRequestFromScopedCaller_IsRejected()
    {
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": ["Projects/Foo"] } } }
            """);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("out_of_scope", ErrorCode(result));
        Assert.Contains("unscoped access", ErrorText(result));
    }

    [Fact]
    public async Task InvokeAsync_UnscopedVaultCaller_PermitsAnyRequestedRoot()
    {
        var store = MakeStore("""
            { "admin-token": { "id": "admin", "opClasses": ["content:read"], "vaults": ["test"], "ttl": "*", "scopeRoots": { "test": [] } } }
            """);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test/Anywhere/At/All"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
    }

    [Fact]
    public async Task InvokeAsync_TtlWithinCallersRemainingLifetime_MintsTheRequestedTtl()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new FixedTimeProvider(now);
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 60, "scopeRoots": { "test": [] } } }
            """, timeProvider);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test"], ["content:read"], 10),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(ErrorText(result));
        Assert.False(minted.RootElement.GetProperty("ttlCapped").GetBoolean());
        var mintedGrant = store.Authenticate(minted.RootElement.GetProperty("token").GetString()!);
        Assert.Equal(now.AddMinutes(10), mintedGrant!.ExpiresAtUtc);
    }

    [Fact]
    public async Task InvokeAsync_TtlExceedingCallersRemainingLifetime_IsCappedNotRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new FixedTimeProvider(now);
        // The caller's own grant expires in 5 minutes.
        var store = MakeStore("""
            { "scoped-token": { "id": "scoped", "opClasses": ["content:read"], "vaults": ["test"], "ttl": 5, "scopeRoots": { "test": [] } } }
            """, timeProvider);
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("scoped-token")!;
        Assert.Equal(now.AddMinutes(5), callerGrant.ExpiresAtUtc);

        // Requests 60 minutes - far beyond the caller's own remaining 5.
        var result = await tool.InvokeAsync(
            Arguments(["test"], ["content:read"], 60),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(ErrorText(result));
        Assert.True(minted.RootElement.GetProperty("ttlCapped").GetBoolean());
        Assert.Equal(60, minted.RootElement.GetProperty("ttlMinutes").GetInt32()); // requested value, reported as-is

        var mintedGrant = store.Authenticate(minted.RootElement.GetProperty("token").GetString()!);
        Assert.NotNull(mintedGrant);
        // Capped at the caller's own expiry, not the requested 60 minutes.
        Assert.Equal(callerGrant.ExpiresAtUtc, mintedGrant!.ExpiresAtUtc);
        Assert.True(mintedGrant.ExpiresAtUtc < now.AddMinutes(60));
    }

    [Fact]
    public async Task InvokeAsync_InfiniteCaller_ImposesNoTtlCap()
    {
        var store = MakeStore(); // admin-token: ttl "*" (infinite)
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;
        Assert.Null(callerGrant.ExpiresAtUtc);

        var result = await tool.InvokeAsync(
            Arguments(["test"], ["content:read"], 1_000_000),
            callerGrant,
            CancellationToken.None);

        Assert.False(result.IsError == true);
        using var minted = JsonDocument.Parse(ErrorText(result));
        Assert.False(minted.RootElement.GetProperty("ttlCapped").GetBoolean());
    }

    // --- Coded argument refusals ----------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_UnknownVaultInScopeRoots_IsUnknownVault()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["no-such-vault/Projects"], ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.True(result.IsError);
        // The same code Resolve-VaultToolRoot raises PowerShell-side for a name in no configured
        // map, so a caller routes an unknown vault identically whichever tier saw it.
        Assert.Equal("unknown_vault", ErrorCode(result));
        var data = ErrorData(result)!;
        Assert.Equal("no-such-vault", data["vault"]);
        Assert.Equal("no-such-vault/Projects", data["scopeRoot"]);
    }

    [Theory]
    [InlineData("scope_roots")]
    [InlineData("op_class")]
    public async Task InvokeAsync_WildcardArgument_IsInvalidArgumentNamingIt(string argument)
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(argument == "scope_roots" ? ["*"] : ["test"], argument == "op_class" ? ["*"] : ["content:read"]),
            callerGrant,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("invalid_argument", ErrorCode(result));
        Assert.Equal(argument, ErrorData(result)!["argument"]);
    }

    [Fact]
    public async Task InvokeAsync_UnknownOpClassName_IsInvalidArgumentNotACapabilityDenial()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test"], ["not:a-class"]),
            callerGrant,
            CancellationToken.None);

        Assert.True(result.IsError);
        // No elevation could ever satisfy a name that is not in the registry, so this must not be
        // coded as the capability shortfall the attenuation check raises.
        Assert.Equal("invalid_argument", ErrorCode(result));
        Assert.Equal("not:a-class", ErrorData(result)!["opClass"]);
    }

    [Fact]
    public async Task InvokeAsync_WildcardTtl_IsInvalidArgument()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new { scope_roots = new[] { "test" }, op_class = new[] { "content:read" }, ttl = "*" }))!;
        var result = await tool.InvokeAsync(arguments, callerGrant, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("invalid_argument", ErrorCode(result));
        Assert.Equal("ttl", ErrorData(result)!["argument"]);
    }
}
