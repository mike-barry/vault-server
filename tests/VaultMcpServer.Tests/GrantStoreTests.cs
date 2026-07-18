using VaultMcpServer.Auth;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class GrantStoreTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>, IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"));

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
          "admin-token": { "id": "admin", "opClasses": ["Read", "Write"], "vaults": ["personal"] },
          "readonly-token": { "id": "read-only", "opClasses": ["Read"], "vaults": ["personal", "test"] }
        }
        """;

    [Fact]
    public void Authenticate_KnownTokenReturnsItsGrant()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig);

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
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig);
        Assert.Null(store.Authenticate("not-a-real-token"));
    }

    [Fact]
    public void Authenticate_EmptyTokenReturnsNull()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants), OpClasses, _vaultConfig);
        Assert.Null(store.Authenticate(""));
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => GrantStore.Load(Path.Combine(_tempDir, "absent.json"), OpClasses, _vaultConfig));
    }

    [Fact]
    public void Load_EmptyObjectThrows()
    {
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants("{}"), OpClasses, _vaultConfig));
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
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig));
    }

    [Fact]
    public void Load_UnknownOpClassThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Delete"], "vaults": ["personal"] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig));
    }

    [Fact]
    public void Load_EmptyOpClassesThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": [], "vaults": ["personal"] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig));
    }

    [Fact]
    public void Load_EmptyVaultsThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": [] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig));
    }

    [Fact]
    public void Load_MissingIdThrows()
    {
        var json = """{ "token-a": { "opClasses": ["Read"], "vaults": ["personal"] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig));
    }

    [Fact]
    public void Load_OpClassesWildcardExpandsToAllRegistryClasses()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["*"], "vaults": ["test"] } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig);

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var opClass in OpClasses.Names)
            Assert.True(grant.Allows(opClass));
    }

    [Fact]
    public void Load_VaultsWildcardExpandsToAllConfiguredVaults()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": ["*"] } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig);

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        foreach (var vault in _vaultConfig.Vaults.Keys)
            Assert.True(grant.CanAccessVault(vault));
    }

    [Fact]
    public void Load_BothWildcardsExpandTogether()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["*"], "vaults": ["*"] } }""";
        var store = GrantStore.Load(WriteGrants(json), OpClasses, _vaultConfig);

        var grant = store.Authenticate("token-a");
        Assert.NotNull(grant);
        Assert.Equal(OpClasses.Names.Count, grant.OpClasses.Count);
        Assert.Equal(_vaultConfig.Vaults.Count, grant.Vaults.Count);
    }
}
