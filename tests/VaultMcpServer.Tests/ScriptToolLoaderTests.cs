using VaultMcpServer.Health;
using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class ScriptToolLoaderTests : IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-tools-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteScript(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private const string ValidScript = """
        <#
        .SYNOPSIS
        Renames a note.
        .PARAMETER From
        The note's current vault-relative path.
        .PARAMETER To
        The note's new vault-relative path.
        .PARAMETER Force
        Overwrite an existing note at the destination.
        .NOTES
        OpClass: content:write
        #>
        param(
            [Parameter(Mandatory=$true)] [string]$From,
            [Parameter(Mandatory)] [string]$To,
            [switch]$Force
        )
        Write-Output "renamed $From to $To"
        """;

    [Fact]
    public void Load_MissingDirectoryReturnsEmpty()
    {
        var healthState = new ServerHealthState();
        Assert.Empty(ScriptToolLoader.Load(Path.Combine(_tempDir, "no-such-dir"), OpClasses, healthState));
        Assert.Empty(healthState.Problems);
    }

    [Fact]
    public void Load_ValidScript_DerivesNameDescriptionOpClassAndParameters()
    {
        WriteScript("rename_note.ps1", ValidScript);
        var healthState = new ServerHealthState();
        var tools = ScriptToolLoader.Load(_tempDir, OpClasses, healthState);

        Assert.Empty(healthState.Problems);
        var tool = Assert.Single(tools);
        Assert.Equal("rename_note", tool.Name);
        Assert.Equal("Renames a note.", tool.Description);
        Assert.Equal(OpClass.Write, tool.OpClass);
        Assert.Equal(3, tool.Parameters.Count);

        var from = Assert.Single(tool.Parameters, p => p.Name == "From");
        Assert.Equal("string", from.JsonType);
        Assert.True(from.Required);
        Assert.Equal("The note's current vault-relative path.", from.Description);

        var to = Assert.Single(tool.Parameters, p => p.Name == "To");
        Assert.True(to.Required, "Mandatory shorthand (no '=$true') should still be detected.");

        var force = Assert.Single(tool.Parameters, p => p.Name == "Force");
        Assert.Equal("boolean", force.JsonType);
        Assert.False(force.Required);
    }

    [Fact]
    public void Load_MultipleScripts_ReturnsOneToolEach()
    {
        WriteScript("a.ps1", ValidScript);
        WriteScript("b.ps1", ValidScript.Replace("OpClass: content:write", "OpClass: content:read"));

        var tools = ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState());

        Assert.Equal(2, tools.Count);
        Assert.Equal(OpClass.Write, tools.Single(t => t.Name == "a").OpClass);
        Assert.Equal(OpClass.Read, tools.Single(t => t.Name == "b").OpClass);
    }

    [Fact]
    public void Load_ScriptWithNoParamBlock_HasEmptyParameters()
    {
        WriteScript("agenda.ps1", """
            <#
            .SYNOPSIS
            Summarizes today's tasks.
            .NOTES
            OpClass: content:read
            #>
            Write-Output "agenda"
            """);

        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));
        Assert.Empty(tool.Parameters);
    }

    [Fact]
    public void Load_MissingSynopsis_SkipsScript()
    {
        WriteScript("bad.ps1", """
            <#
            .NOTES
            OpClass: content:read
            #>
            param()
            """);
        AssertScriptSkipped(expectedDetailFragment: ".SYNOPSIS");
    }

    [Fact]
    public void Load_MissingOpClass_SkipsScript()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Does a thing.
            #>
            param()
            """);
        AssertScriptSkipped(expectedDetailFragment: "OpClass");
    }

    [Fact]
    public void Load_RegistryBackedNonBuiltInOpClass_ManageGrants_Loads()
    {
        // grants:manage exists only in op-classes.json, not in the OpClass constants class -
        // proving the loader validates .NOTES op classes against the registry, not a fixed
        // enum (the phase-2 change that made mint_token's class declarable at all).
        WriteScript("admin_thing.ps1", """
            <#
            .SYNOPSIS
            Does grant administration.
            .NOTES
            OpClass: grants:manage
            #>
            Write-Output "ok"
            """);

        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));
        Assert.Equal("grants:manage", tool.OpClass);
    }

    [Fact]
    public void Load_OpClassIsCanonicalizedCaseInsensitively()
    {
        // The registry match is case-insensitive but the stored class is the registry's
        // canonical casing - dispatch-time Grant.Allows comparisons rely on that.
        WriteScript("lower.ps1", """
            <#
            .SYNOPSIS
            Declares its op class in a different case.
            .NOTES
            OpClass: GRANTS:MANAGE
            #>
            Write-Output "ok"
            """);

        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));
        Assert.Equal("grants:manage", tool.OpClass);
    }

    [Fact]
    public void Load_InvalidOpClass_SkipsScript()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Does a thing.
            .NOTES
            OpClass: Delete
            #>
            param()
            """);
        AssertScriptSkipped(expectedDetailFragment: "OpClass");
    }

    [Fact]
    public void Load_ParameterMissingHelpEntry_SkipsScript()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Does a thing.
            .NOTES
            OpClass: content:read
            #>
            param([string]$Undocumented)
            """);
        AssertScriptSkipped(expectedDetailFragment: "Undocumented");
    }

    [Fact]
    public void Load_NoCommentHelpBlock_SkipsScript()
    {
        WriteScript("bad.ps1", "param([string]$X)\nWrite-Output $X\n");
        AssertScriptSkipped(expectedDetailFragment: ".SYNOPSIS");
    }

    [Fact]
    public void Load_SyntaxError_SkipsScript()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Broken.
            .NOTES
            OpClass: content:read
            #>
            param($X
            """);
        AssertScriptSkipped();
    }

    [Fact]
    public void Load_BrokenScript_SkipsOnlyThatScriptRestOfFolderLoads()
    {
        // The core per-script skip-and-report behavior: one broken script loads zero tools
        // from that file, is recorded as a Degraded problem naming it, and every other
        // script in the folder loads normally.
        WriteScript("good.ps1", ValidScript);
        WriteScript("broken.ps1", "param([string]$X)\nWrite-Output $X\n");
        var healthState = new ServerHealthState();

        var tools = ScriptToolLoader.Load(_tempDir, OpClasses, healthState);

        var tool = Assert.Single(tools);
        Assert.Equal("good", tool.Name);
        Assert.False(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal(ScriptToolLoader.HealthSource, problem.Source);
        Assert.Contains("broken.ps1", problem.Detail);
    }

    [Fact]
    public void Load_ProblemsAreReplacedPerScan_BrokenThenFixedClears()
    {
        // Staleness, direction 1: a script that was broken on the previous scan and is fixed
        // before the next one must stop appearing as a problem - the loader replaces its
        // whole problem set per scan instead of appending.
        var healthState = new ServerHealthState();
        WriteScript("tool.ps1", "param([string]$X)\nWrite-Output $X\n");

        Assert.Empty(ScriptToolLoader.Load(_tempDir, OpClasses, healthState));
        Assert.Single(healthState.Problems);

        WriteScript("tool.ps1", ValidScript);
        var tools = ScriptToolLoader.Load(_tempDir, OpClasses, healthState);

        Assert.Single(tools);
        Assert.Empty(healthState.Problems);
    }

    [Fact]
    public void Load_ProblemsAreReplacedPerScan_OkThenBrokenAppears()
    {
        // Staleness, direction 2: a script valid on the previous scan that breaks before the
        // next one must appear as a problem on that next scan.
        var healthState = new ServerHealthState();
        WriteScript("tool.ps1", ValidScript);

        Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, healthState));
        Assert.Empty(healthState.Problems);

        WriteScript("tool.ps1", "param([string]$X)\nWrite-Output $X\n");
        var tools = ScriptToolLoader.Load(_tempDir, OpClasses, healthState);

        Assert.Empty(tools);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal(ScriptToolLoader.HealthSource, problem.Source);
        Assert.Contains("tool.ps1", problem.Detail);
    }

    [Fact]
    public void Registry_Refresh_RecomputesProblemSet()
    {
        // Same staleness contract exercised through ScriptToolRegistry.Refresh - the entry
        // point the tools/ watcher actually calls at runtime.
        var healthState = new ServerHealthState();
        WriteScript("tool.ps1", "param([string]$X)\nWrite-Output $X\n");
        var registry = new ScriptToolRegistry(_tempDir, OpClasses, healthState);
        Assert.Empty(registry.Tools);
        Assert.Single(healthState.Problems);

        WriteScript("tool.ps1", ValidScript);
        registry.Refresh();

        Assert.Single(registry.Tools);
        Assert.Empty(healthState.Problems);

        WriteScript("tool.ps1", "param([string]$X)\nWrite-Output $X\n");
        registry.Refresh();

        Assert.Empty(registry.Tools);
        Assert.Single(healthState.Problems);
    }

    [Fact]
    public void BuildInputSchema_MatchesJsonSchemaShape()
    {
        WriteScript("rename_note.ps1", ValidScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var schema = tool.BuildInputSchema();
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("From", out _));
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("From", required);
        Assert.Contains("To", required);
        Assert.DoesNotContain("Force", required);
    }

    /// <summary>Shared assertion for every per-script skip case above (single bad.ps1 in the
    /// folder): the load returns zero tools instead of throwing, is never Fatal, and records
    /// exactly one Degraded problem from the ScriptTools source naming the script.</summary>
    private void AssertScriptSkipped(string? expectedDetailFragment = null)
    {
        var healthState = new ServerHealthState();
        var tools = ScriptToolLoader.Load(_tempDir, OpClasses, healthState);

        Assert.Empty(tools);
        Assert.False(healthState.IsFatallyDegraded);
        var problem = Assert.Single(healthState.Problems);
        Assert.Equal(ProblemSeverity.Degraded, problem.Severity);
        Assert.Equal(ScriptToolLoader.HealthSource, problem.Source);
        Assert.Contains("bad.ps1", problem.Detail);
        if (expectedDetailFragment is not null)
            Assert.Contains(expectedDetailFragment, problem.Detail);
    }
}
