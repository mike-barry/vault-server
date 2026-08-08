using VaultShared.Access;
using VaultShared.Health;

namespace VaultShared.Tests;

/// <summary>
/// Tests how a host's configured search-engine location is interpreted: the fallback when nothing is
/// configured, the directory a relative location resolves against, and what an explicitly configured
/// executable that is not there records on the health surface.
/// </summary>
/// <remarks>
/// Both front-ends read the location from their own configuration and then come through here, so
/// these cases are what keeps the fallback, the resolution base and the existence check from being
/// decided twice and differently.
/// </remarks>
public sealed class SearchEngineConfigurationTests : IDisposable
{
    private readonly string _configDirectory = Directory.CreateTempSubdirectory("vault-engine-config-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes a stand-in executable and returns its full path.</summary>
    /// <param name="relativePath">Where to write it, relative to the configuration directory.</param>
    /// <returns>The file's full path.</returns>
    private string WriteExecutable(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_configDirectory, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "not really an executable");
        return full;
    }

    /// <summary>
    /// An absent setting and an empty one both mean the engine is found on <c>PATH</c>, and neither is
    /// a problem worth recording. A host that configures nothing is in its shipped state.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingConfiguredMeansTheEngineOnPathAndRecordsNoProblem(string? configured)
    {
        var sink = new RecordingProblemSink();

        var engine = SearchEngines.Load(configured, _configDirectory, sink);

        Assert.Equal(SearchEngine.OnPath, engine);
        Assert.Null(engine.Executable);
        Assert.Empty(sink.Problems);
    }

    /// <summary>
    /// A relative location resolves against the directory the caller supplied, never the process's
    /// working directory, so the engine a command-line front-end runs does not depend on where the
    /// operator happened to be standing.
    /// </summary>
    [Fact]
    public void ARelativeLocationResolvesAgainstTheSuppliedBaseDirectory()
    {
        var expected = WriteExecutable(Path.Combine("bin", "rg.exe"));
        var sink = new RecordingProblemSink();

        var engine = SearchEngines.Load("bin/rg.exe", _configDirectory, sink);

        Assert.Equal(expected, engine.Executable);
        Assert.Empty(sink.Problems);
    }

    [Fact]
    public void AnExplicitLocationThatIsThereRecordsNothing()
    {
        var expected = WriteExecutable("rg.exe");
        var sink = new RecordingProblemSink();

        var engine = SearchEngines.Load(expected, _configDirectory, sink);

        Assert.Equal(expected, engine.Executable);
        Assert.Empty(sink.Problems);
    }

    /// <summary>
    /// A configured executable that is not there is named once at startup, as degraded rather than
    /// fatal: only search and enumeration go through the engine, so taking the host down would refuse
    /// the reads, writes, moves and removals the failure does not touch.
    /// </summary>
    [Fact]
    public void AnExplicitLocationThatIsNotThereRecordsOneDegradedProblemAndDoesNotFailTheHost()
    {
        var missing = Path.Combine(_configDirectory, "no-such-engine.exe");
        var sink = new RecordingProblemSink();

        SearchEngines.Load(missing, _configDirectory, sink);

        var problem = Assert.Single(sink.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Contains(missing, problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(sink.IsFatallyDegraded);
    }

    /// <summary>
    /// The configured location is still handed to the layer when it is not there, so search refuses
    /// instead of silently running whatever executable <c>PATH</c> happens to offer under that name.
    /// </summary>
    [Fact]
    public void AnExplicitLocationThatIsNotThereIsStillUsedRatherThanFallingBackToPath()
    {
        var missing = Path.Combine(_configDirectory, "no-such-engine.exe");

        var engine = SearchEngines.Load(missing, _configDirectory, new RecordingProblemSink());

        Assert.Equal(missing, engine.Executable);
    }
}
