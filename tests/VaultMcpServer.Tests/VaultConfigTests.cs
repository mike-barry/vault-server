using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class VaultConfigTests(TestVaultFixture fixture) : IClassFixture<TestVaultFixture>
{
    [Fact]
    public void Resolve_FallsBackToDefaultVault()
    {
        Assert.Equal("test", fixture.Config.Resolve(null).Name);
        Assert.Equal("test", fixture.Config.Resolve("").Name);
    }

    [Fact]
    public void Resolve_ExplicitVault()
    {
        Assert.Equal("scratch", fixture.Config.Resolve("scratch").Name);
    }

    [Fact]
    public void Resolve_ReturnsCanonicalNameForCaseInsensitiveInput()
    {
        Assert.Equal("test", fixture.Config.Resolve("TEST").Name);
    }

    [Fact]
    public void Resolve_UnknownVaultThrows()
    {
        Assert.Throws<ArgumentException>(() => fixture.Config.Resolve("nope"));
    }
}

public sealed class VaultConfigLoadTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-config-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_tempDir, "vaults.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => VaultConfig.Load(Path.Combine(_tempDir, "absent.json")));
    }

    [Fact]
    public void Load_MissingDefaultVaultThrows()
    {
        var path = WriteConfig($$"""{ "vaults": { "a": {{JsonPath(_tempDir)}} } }""");
        Assert.Throws<InvalidDataException>(() => VaultConfig.Load(path));
    }

    [Fact]
    public void Load_DefaultNotInVaultsThrows()
    {
        var path = WriteConfig($$"""{ "default_vault": "b", "vaults": { "a": {{JsonPath(_tempDir)}} } }""");
        Assert.Throws<InvalidDataException>(() => VaultConfig.Load(path));
    }

    [Fact]
    public void Load_EmptyVaultsThrows()
    {
        var path = WriteConfig("""{ "default_vault": "a", "vaults": {} }""");
        Assert.Throws<InvalidDataException>(() => VaultConfig.Load(path));
    }

    [Fact]
    public void Load_NonexistentRootThrows()
    {
        var path = WriteConfig($$"""{ "default_vault": "a", "vaults": { "a": {{JsonPath(Path.Combine(_tempDir, "no-such-dir"))}} } }""");
        Assert.Throws<DirectoryNotFoundException>(() => VaultConfig.Load(path));
    }

    private static string JsonPath(string path) => System.Text.Json.JsonSerializer.Serialize(path);
}
