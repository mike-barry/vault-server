using VaultMcpServer.Auth;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class GrantStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-grants-tests-").FullName;

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
        var store = GrantStore.Load(WriteGrants(TwoGrants));

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
        var store = GrantStore.Load(WriteGrants(TwoGrants));
        Assert.Null(store.Authenticate("not-a-real-token"));
    }

    [Fact]
    public void Authenticate_EmptyTokenReturnsNull()
    {
        var store = GrantStore.Load(WriteGrants(TwoGrants));
        Assert.Null(store.Authenticate(""));
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => GrantStore.Load(Path.Combine(_tempDir, "absent.json")));
    }

    [Fact]
    public void Load_EmptyObjectThrows()
    {
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants("{}")));
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
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json)));
    }

    [Fact]
    public void Load_UnknownOpClassThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Delete"], "vaults": ["personal"] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json)));
    }

    [Fact]
    public void Load_EmptyOpClassesThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": [], "vaults": ["personal"] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json)));
    }

    [Fact]
    public void Load_EmptyVaultsThrows()
    {
        var json = """{ "token-a": { "id": "admin", "opClasses": ["Read"], "vaults": [] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json)));
    }

    [Fact]
    public void Load_MissingIdThrows()
    {
        var json = """{ "token-a": { "opClasses": ["Read"], "vaults": ["personal"] } }""";
        Assert.Throws<InvalidDataException>(() => GrantStore.Load(WriteGrants(json)));
    }
}
