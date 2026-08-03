using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// The fail-closed matrix. Configuration that will not load fails the CLI rather than admitting it
/// unrestricted (<c>Spec-Auth-Requirements.md:Both Front-Ends Are Granted</c>), and an untested
/// fail-closed path is one that quietly becomes fail-open - which is why each case asserts that
/// nothing was answered on standard output as well as that the exit code refused.
///
/// Every case is one deviation from a configuration that works, so what fails is the deviation
/// rather than the fixture.
/// </summary>
public sealed class FailClosedTests(CliFixture fixture) : IClassFixture<CliFixture>
{
    /// <summary>The invocation the matrix deviates from: it succeeds, so every refusal below is
    /// attributable to the one thing that case changed.</summary>
    [Fact]
    public void TheFixtureConfiguration_Admits()
    {
        var run = fixture.Run(["glob", "--pattern", "Widget.md"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("Projects/Widget/Widget.md", run.Output);
    }

    [Fact]
    public void ASettingsFileThatIsNotThere_FailsTheInvocation() =>
        AssertRefused(fixture.Run(["glob", "--pattern", "*"], Path.Combine(fixture.Root, "absent.settings.json")));

    [Fact]
    public void ASettingsFileThatWillNotParse_FailsTheInvocation() =>
        AssertRefused(fixture.Run(["glob", "--pattern", "*"], fixture.WriteText("broken.settings.json", "{ not json")));

    /// <summary>
    /// The case the design names outright: absence is not unrestricted. A settings file naming no
    /// grant fails, rather than falling through to a floor or to full authority.
    /// </summary>
    [Fact]
    public void ASettingNamingNoGrant_FailsRatherThanAdmitting()
    {
        var settings = fixture.WriteSettings("no-grant.settings.json", null, fixture.VaultsPath, fixture.GrantsPath, fixture.OpClassesPath);

        var run = fixture.Run(["glob", "--pattern", "*"], settings);

        AssertRefused(run);
        Assert.Contains("not an unrestricted default", run.Envelope.GetProperty("message").GetString());
    }

    [Fact]
    public void ASettingNamingAGrantNothingDefines_FailsTheInvocation()
    {
        var settings = fixture.WriteSettings("unknown-grant.settings.json", "no-such-grant", fixture.VaultsPath, fixture.GrantsPath, fixture.OpClassesPath);

        AssertRefused(fixture.Run(["glob", "--pattern", "*"], settings));
    }

    [Fact]
    public void AGrantOverrideNamingAGrantNothingDefines_FailsTheInvocation()
    {
        var run = fixture.Run(["--grant", "no-such-grant", "glob", "--pattern", "*"]);

        AssertRefused(run);
        Assert.Equal("no-such-grant", run.Envelope.GetProperty("data").GetProperty("grant").GetString());
    }

    /// <summary>
    /// A token-bearing row is not selectable by id. Only the token-less named rows are, so a grant id
    /// can never be a second way to present a token nobody presented.
    /// </summary>
    [Fact]
    public void AGrantIdNamingATokenBearingRow_FailsTheInvocation()
    {
        var grants = fixture.Write("token-grants.json", new Dictionary<string, object>
        {
            ["a-secret-token"] = new { id = "token-row", opClasses = new[] { "*" }, vaults = new[] { "*" }, ttl = "*", scopeRoots = new Dictionary<string, string[]> { ["*"] = ["*"] } },
        });
        var settings = fixture.WriteSettings("token-grant.settings.json", "token-row", fixture.VaultsPath, grants, fixture.OpClassesPath);

        AssertRefused(fixture.Run(["glob", "--pattern", "*"], settings));
    }

    [Theory]
    [InlineData("grants")]
    [InlineData("vaults")]
    [InlineData("op-classes")]
    public void AConfigurationFileThatWillNotLoad_FailsTheInvocation(string which)
    {
        var broken = fixture.WriteText($"broken-{which}.json", "{ this is not json");
        var settings = fixture.WriteSettings(
            $"broken-{which}.settings.json",
            CliFixture.AdminGrant,
            which == "vaults" ? broken : fixture.VaultsPath,
            which == "grants" ? broken : fixture.GrantsPath,
            which == "op-classes" ? broken : fixture.OpClassesPath);

        AssertRefused(fixture.Run(["glob", "--pattern", "*"], settings));
    }

    [Theory]
    [InlineData("grants")]
    [InlineData("vaults")]
    [InlineData("op-classes")]
    public void AConfigurationFileThatIsNotThere_FailsTheInvocation(string which)
    {
        var absent = Path.Combine(fixture.Root, $"absent-{which}.json");
        var settings = fixture.WriteSettings(
            $"absent-{which}.settings.json",
            CliFixture.AdminGrant,
            which == "vaults" ? absent : fixture.VaultsPath,
            which == "grants" ? absent : fixture.GrantsPath,
            which == "op-classes" ? absent : fixture.OpClassesPath);

        AssertRefused(fixture.Run(["glob", "--pattern", "*"], settings));
    }

    /// <summary>
    /// The cascade stops at the first fatal load, so a broken op-classes.json is reported as itself
    /// rather than as every grant row failing validation against an empty registry.
    /// </summary>
    [Fact]
    public void ABrokenOpClassRegistry_IsReportedRatherThanBuriedUnderTheGrantsItInvalidates()
    {
        var broken = fixture.WriteText("empty-op-classes.json", "{ }");
        var settings = fixture.WriteSettings("cascade.settings.json", CliFixture.AdminGrant, fixture.VaultsPath, fixture.GrantsPath, broken);

        var run = fixture.Run(["glob", "--pattern", "*"], settings);

        AssertRefused(run);
        var message = run.Envelope.GetProperty("message").GetString()!;
        Assert.Contains("empty-op-classes.json", message);
        Assert.DoesNotContain(CliFixture.AdminGrant, message);
    }

    /// <summary>
    /// Fail-closed reaches the configuration itself, not just the tools: even the tool that reports
    /// on configuration health cannot run in a process whose configuration would not load. A
    /// front-end that answered it anyway would be reporting on a process it had refused to build.
    /// </summary>
    [Fact]
    public void EvenTheHealthTool_DoesNotRunUnderConfigurationThatWillNotLoad()
    {
        var settings = fixture.WriteSettings(
            "health-fail.settings.json",
            CliFixture.AdminGrant,
            fixture.WriteText("broken-vaults.json", "{ nope"),
            fixture.GrantsPath,
            fixture.OpClassesPath);

        AssertRefused(fixture.Run(["health_check"], settings));
    }

    private static void AssertRefused(CliRun run)
    {
        Assert.Equal(ExitCodes.Refused, run.Exit);
        Assert.Equal(string.Empty, run.Output);
        Assert.Equal(ToolCodes.DependencyUnavailable, run.Code);
    }
}
