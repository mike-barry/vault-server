using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

/// <summary>
/// Load/parse coverage for <see cref="OpClassRegistry"/> - the op-classes.json-backed
/// replacement for the old hardcoded enum. Same fail-fast rigor (and the same
/// temp-dir-per-test pattern) as <see cref="GrantStoreTests"/>/<see cref="VaultConfigTests"/>,
/// since GrantStore and ScriptToolLoader both validate op-class names against this registry.
/// </summary>
public sealed class OpClassRegistryTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-opclass-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteRegistry(string json)
    {
        var path = Path.Combine(_tempDir, "op-classes.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string ThreeClasses = """
        [
          { "name": "Read", "description": "reads" },
          { "name": "Write", "description": "writes" },
          { "name": "ManageGrants", "description": "mints" }
        ]
        """;

    [Fact]
    public void Load_ValidFile_ExposesEveryName()
    {
        var registry = OpClassRegistry.Load(WriteRegistry(ThreeClasses));

        Assert.Equal(3, registry.Names.Count);
        Assert.True(registry.IsValid("Read"));
        Assert.True(registry.IsValid("Write"));
        Assert.True(registry.IsValid("ManageGrants"));
        Assert.False(registry.IsValid("Delete"));
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        Assert.Throws<FileNotFoundException>(() => OpClassRegistry.Load(Path.Combine(_tempDir, "absent.json")));
    }

    [Fact]
    public void Load_NonArrayRootThrows()
    {
        Assert.Throws<InvalidDataException>(() => OpClassRegistry.Load(WriteRegistry("""{ "name": "Read" }""")));
    }

    [Fact]
    public void Load_EmptyArrayThrows()
    {
        Assert.Throws<InvalidDataException>(() => OpClassRegistry.Load(WriteRegistry("[]")));
    }

    [Fact]
    public void Load_RowMissingNameThrows()
    {
        Assert.Throws<InvalidDataException>(() => OpClassRegistry.Load(WriteRegistry("""[ { "description": "no name" } ]""")));
    }

    [Fact]
    public void Load_DuplicateNameThrows()
    {
        var json = """[ { "name": "Read" }, { "name": "read" } ]""";
        Assert.Throws<InvalidDataException>(() => OpClassRegistry.Load(WriteRegistry(json)));
    }

    [Fact]
    public void Canonicalize_ReturnsRegistryCasingForACaseInsensitiveMatch()
    {
        var registry = OpClassRegistry.Load(WriteRegistry(ThreeClasses));

        Assert.Equal("ManageGrants", registry.Canonicalize("managegrants"));
        Assert.Equal("Read", registry.Canonicalize("READ"));
        Assert.Null(registry.Canonicalize("Delete"));
    }
}
