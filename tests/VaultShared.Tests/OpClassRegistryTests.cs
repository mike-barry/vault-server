using VaultShared.Health;
using VaultShared.Configuration;

namespace VaultShared.Tests;

/// <summary>
/// Tests loading and parsing <see cref="OpClassRegistry"/> from an op-classes.json file written
/// into a temp directory per test.
/// </summary>
/// <remarks>
/// GrantStore and ScriptToolLoader both validate op-class names against this registry, so these
/// tests hold it to the same fail-fast standard as <see cref="GrantStoreTests"/> and
/// <see cref="VaultConfigTests"/>.
/// </remarks>
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
        var registry = OpClassRegistryLoader.Load(WriteRegistry(ThreeClasses), new RecordingProblemSink());

        Assert.Equal(3, registry.Names.Count);
        Assert.True(registry.IsValid("Read"));
        Assert.True(registry.IsValid("Write"));
        Assert.True(registry.IsValid("ManageGrants"));
        Assert.False(registry.IsValid("Delete"));
    }

    [Fact]
    public void Load_MissingFileThrows()
    {
        var sink = new RecordingProblemSink();
        var registry = OpClassRegistryLoader.Load(Path.Combine(_tempDir, "absent.json"), sink);

        AssertFatalAndEmpty(sink, registry);
    }

    [Fact]
    public void Load_NonArrayRootThrows()
    {
        var sink = new RecordingProblemSink();
        var registry = OpClassRegistryLoader.Load(WriteRegistry("""{ "name": "Read" }"""), sink);

        AssertFatalAndEmpty(sink, registry);
    }

    [Fact]
    public void Load_EmptyArrayThrows()
    {
        var sink = new RecordingProblemSink();
        var registry = OpClassRegistryLoader.Load(WriteRegistry("[]"), sink);

        AssertFatalAndEmpty(sink, registry);
    }

    [Fact]
    public void Load_RowMissingNameThrows()
    {
        var sink = new RecordingProblemSink();
        var registry = OpClassRegistryLoader.Load(WriteRegistry("""[ { "description": "no name" } ]"""), sink);

        AssertFatalAndEmpty(sink, registry);
    }

    [Fact]
    public void Load_DuplicateNameThrows()
    {
        var json = """[ { "name": "Read" }, { "name": "read" } ]""";
        var sink = new RecordingProblemSink();
        var registry = OpClassRegistryLoader.Load(WriteRegistry(json), sink);

        AssertFatalAndEmpty(sink, registry);
    }

    /// <summary>
    /// Asserts the outcome shared by every whole-file failure case above. Loading op-classes.json
    /// is all-or-nothing, with no per-row skip, so a failure records exactly one fatal problem
    /// from source "OpClassRegistry" and returns an empty registry rather than a partial one.
    /// </summary>
    /// <param name="sink">The sink the failed load recorded into.</param>
    /// <param name="registry">The registry the failed load returned.</param>
    private static void AssertFatalAndEmpty(RecordingProblemSink sink, OpClassRegistry registry)
    {
        Assert.True(sink.IsFatallyDegraded);
        var problem = Assert.Single(sink.Problems);
        Assert.Equal(ProblemSeverity.Fatal, problem.Severity);
        Assert.Equal("OpClassRegistry", problem.Source);
        Assert.NotEmpty(problem.Detail);

        Assert.Empty(registry.Names);
        Assert.False(registry.IsValid("Read"));
    }

    [Fact]
    public void Canonicalize_ReturnsRegistryCasingForACaseInsensitiveMatch()
    {
        var registry = OpClassRegistryLoader.Load(WriteRegistry(ThreeClasses), new RecordingProblemSink());

        Assert.Equal("ManageGrants", registry.Canonicalize("managegrants"));
        Assert.Equal("Read", registry.Canonicalize("READ"));
        Assert.Null(registry.Canonicalize("Delete"));
    }
}
