using VaultShared.Health;
using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// Verifies what the CLI does with a <c>RipgrepPath</c> setting that names an executable which is not
/// there: it records the condition once at load, keeps serving every tool that does not search, and
/// refuses the two that do with a code no caller retries.
/// </summary>
/// <remarks>
/// The setting is resolved against the directory holding the settings file, so a case that supplies a
/// relative value asserts where the resolution base is by reading the resolved path back off the
/// recorded problem.
/// </remarks>
public sealed class SearchEngineSettingTests(CliFixture fixture) : IClassFixture<CliFixture>
{
    /// <summary>Writes a settings file of the fixture's working shape with one engine location added.</summary>
    /// <param name="name">The settings file name to write within the fixture root.</param>
    /// <param name="ripgrepPath">The value to record for <c>RipgrepPath</c>.</param>
    /// <returns>The full path of the settings file written.</returns>
    private string WriteSettingsWithEngine(string name, string ripgrepPath) =>
        fixture.Write(name, new Dictionary<string, string>
        {
            ["GrantId"] = CliFixture.AdminGrant,
            ["OpClassesConfigPath"] = fixture.OpClassesPath,
            ["VaultsConfigPath"] = fixture.VaultsPath,
            ["GrantsConfigPath"] = fixture.GrantsPath,
            ["RipgrepPath"] = ripgrepPath,
        });

    [Fact]
    public void AnEngineThatIsNotThere_RefusesASearchAsAnUnavailableDependency()
    {
        var settings = WriteSettingsWithEngine("missing-engine.settings.json", "no-such-engine.exe");

        var run = fixture.Run(["glob", "--pattern", "*"], settings);

        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(ToolCodes.DependencyUnavailable, run.Code);
        Assert.Empty(run.Output);
    }

    /// <summary>
    /// The same invocation that cannot search still reads. A missing engine is recorded as degraded
    /// rather than fatal precisely so it refuses the two tools that search and nothing else.
    /// </summary>
    [Fact]
    public void AnEngineThatIsNotThere_LeavesEveryToolThatDoesNotSearchWorking()
    {
        var settings = WriteSettingsWithEngine("missing-engine-read.settings.json", "no-such-engine.exe");

        var run = fixture.Run(["read", "--path", "Projects/Widget/Widget.md"], settings);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.NotEmpty(run.Output);
    }

    /// <summary>
    /// A relative setting resolves against the directory holding the settings file, never the
    /// process's working directory, which is wherever the operator ran the command from.
    /// </summary>
    [Fact]
    public void ARelativeEngineLocation_ResolvesAgainstTheSettingsFilesDirectory()
    {
        var settings = WriteSettingsWithEngine("relative-engine.settings.json", "bin/no-such-engine.exe");

        var host = CliHost.Load(CliSettings.Load(settings), TextWriter.Null);

        var problem = Assert.Single(host.Health.Problems, p => p.Source == "SearchEngine");
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Contains(
            Path.GetFullPath(Path.Combine(fixture.Root, "bin", "no-such-engine.exe")),
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(host.Health.IsFatallyDegraded);
    }

    /// <summary>
    /// The setting is carried as the settings file spells it, because resolving it and checking it are
    /// the shared layer's, not this front-end's.
    /// </summary>
    [Fact]
    public void TheSettingIsCarriedExactlyAsConfigured()
    {
        var settings = WriteSettingsWithEngine("raw-engine.settings.json", "bin/no-such-engine.exe");

        Assert.Equal("bin/no-such-engine.exe", CliSettings.Load(settings).RipgrepPath);
    }
}
