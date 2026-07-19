using System.Text.Json;
using VaultMcpServer.Auth;
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
    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"));

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-mint-tool-tests-").FullName;
    private readonly VaultConfig _vaultConfig = fixture.Config;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private GrantStore MakeStore()
    {
        var path = Path.Combine(_tempDir, "grants.json");
        File.WriteAllText(path, """
            { "admin-token": { "id": "admin", "opClasses": ["Read", "Write", "ManageGrants"], "vaults": ["test", "scratch"], "ttl": "*", "scopeRoots": { "test": [], "scratch": [] } } }
            """);
        return GrantStore.Load(path, OpClasses, _vaultConfig);
    }

    private static Dictionary<string, JsonElement> Arguments(IEnumerable<string> scopeRoots, IEnumerable<string> opClass) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new { scope_roots = scopeRoots, op_class = opClass }))!;

    [Fact]
    public async Task InvokeAsync_MultiVaultScopeRoots_ProducesSeparatePerVaultSets()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test/Projects/Foo", "scratch/Areas/Bar"], ["Read"]),
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
            Arguments(["test"], ["Read"]),
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

    [Fact]
    public async Task InvokeAsync_WholeVaultAndSubtreeForSameVault_WholeVaultWinsProducingEmptySet()
    {
        var store = MakeStore();
        var tool = new MintTokenTool(store, OpClasses, _vaultConfig);
        var callerGrant = store.Authenticate("admin-token")!;

        var result = await tool.InvokeAsync(
            Arguments(["test", "test/Projects/Foo"], ["Read"]),
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
}
