using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// Verifies that configuration which will not load fails the invocation rather than admitting it
/// unrestricted. Each case asserts both that the exit code refused and that nothing was written to
/// standard output, because an untested fail-closed path is one that can quietly become fail-open.
/// </summary>
/// <remarks>
/// Every case changes exactly one thing about a configuration that works, so the failure is
/// attributable to that change rather than to the fixture.
/// </remarks>
public sealed class FailClosedTests(CliFixture fixture) : IClassFixture<CliFixture>
{
    /// <summary>The baseline the other cases deviate from. It succeeds, so every refusal below is
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
    /// An absent grant is not an unrestricted one. A settings file naming no grant fails the
    /// invocation rather than falling through to a default floor or to full authority.
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
    /// A grant row that carries a token cannot be selected by id. Only token-less named rows can be,
    /// so a grant id is never a way to obtain authority that a token would otherwise gate.
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
    /// Loading stops at the first fatal failure, so a broken op-classes.json is reported as itself
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
    /// Failing closed applies to the configuration load itself, not only to the tools. Even the
    /// health-check tool does not run when configuration will not load, because its answer would
    /// describe a process that was never built.
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
