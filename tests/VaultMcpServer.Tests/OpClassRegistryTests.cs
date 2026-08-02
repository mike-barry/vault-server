using VaultMcpServer.Health;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

/// <summary>
/// Load/parse coverage for <see cref="OpClassRegistry"/>, which is backed by
/// op-classes.json. Same fail-fast rigor (and the same
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
        var registry = OpClassRegistry.Load(WriteRegistry(ThreeClasses), new ServerHealthState());

        Assert.Equal(3, registry.Names.Count);
        Assert.True(registry.IsValid("Read"));
        Assert.True(registry.IsValid("Write"));
        Assert.True(registry.IsValid("ManageGrants"));
        Assert.False(registry.IsValid("Delete"));
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        var healthState = new ServerHealthState();
        var registry = OpClassRegistry.Load(Path.Combine(_tempDir, "absent.json"), healthState);

        AssertFatalAndEmpty(healthState, registry);
    }

    [Fact]
    public void Load_NonArrayRootThrows()
    {
        var healthState = new ServerHealthState();
        var registry = OpClassRegistry.Load(WriteRegistry("""{ "name": "Read" }"""), healthState);

        AssertFatalAndEmpty(healthState, registry);
    }

    [Fact]
    public void Load_EmptyArrayThrows()
    {
        var healthState = new ServerHealthState();
        var registry = OpClassRegistry.Load(WriteRegistry("[]"), healthState);

        AssertFatalAndEmpty(healthState, registry);
    }

    [Fact]
    public void Load_RowMissingNameThrows()
    {
        var healthState = new ServerHealthState();
        var registry = OpClassRegistry.Load(WriteRegistry("""[ { "description": "no name" } ]"""), healthState);

        AssertFatalAndEmpty(healthState, registry);
    }

    [Fact]
    public void Load_DuplicateNameThrows()
    {
        var json = """[ { "name": "Read" }, { "name": "read" } ]""";
        var healthState = new ServerHealthState();
        var registry = OpClassRegistry.Load(WriteRegistry(json), healthState);

        AssertFatalAndEmpty(healthState, registry);
    }

    /// <summary>Shared assertion for every whole-file-failure case above: op-classes.json
    /// stays all-or-nothing (there is no per-row skip for it), so a whole-file failure
    /// records a single Fatal problem from source "OpClassRegistry" and returns a registry
    /// with no valid op-classes at all - never a partial one.</summary>
    private static void AssertFatalAndEmpty(ServerHealthState healthState, OpClassRegistry registry)
    {
        Assert.True(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Fatal, problem.Severity);
        Assert.Equal("OpClassRegistry", problem.Source);
        Assert.NotEmpty(problem.Detail);

        Assert.Empty(registry.Names);
        Assert.False(registry.IsValid("Read"));
    }

    [Fact]
    public void Canonicalize_ReturnsRegistryCasingForACaseInsensitiveMatch()
    {
        var registry = OpClassRegistry.Load(WriteRegistry(ThreeClasses), new ServerHealthState());

        Assert.Equal("ManageGrants", registry.Canonicalize("managegrants"));
        Assert.Equal("Read", registry.Canonicalize("READ"));
        Assert.Null(registry.Canonicalize("Delete"));
    }
}
