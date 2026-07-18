using VaultMcpServer.Tools;
using VaultMcpServer.Vault;

namespace VaultMcpServer.Tests;

public sealed class ScriptToolLoaderTests : IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistry.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"));

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
        OpClass: Write
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
        Assert.Empty(ScriptToolLoader.Load(Path.Combine(_tempDir, "no-such-dir"), OpClasses));
    }

    [Fact]
    public void Load_ValidScript_DerivesNameDescriptionOpClassAndParameters()
    {
        WriteScript("rename_note.ps1", ValidScript);
        var tools = ScriptToolLoader.Load(_tempDir, OpClasses);

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
        WriteScript("b.ps1", ValidScript.Replace("OpClass: Write", "OpClass: Read"));

        var tools = ScriptToolLoader.Load(_tempDir, OpClasses);

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
            OpClass: Read
            #>
            Write-Output "agenda"
            """);

        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses));
        Assert.Empty(tool.Parameters);
    }

    [Fact]
    public void Load_MissingSynopsisThrows()
    {
        WriteScript("bad.ps1", """
            <#
            .NOTES
            OpClass: Read
            #>
            param()
            """);
        Assert.Throws<InvalidDataException>(() => ScriptToolLoader.Load(_tempDir, OpClasses));
    }

    [Fact]
    public void Load_MissingOpClassThrows()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Does a thing.
            #>
            param()
            """);
        Assert.Throws<InvalidDataException>(() => ScriptToolLoader.Load(_tempDir, OpClasses));
    }

    [Fact]
    public void Load_InvalidOpClassThrows()
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
        Assert.Throws<InvalidDataException>(() => ScriptToolLoader.Load(_tempDir, OpClasses));
    }

    [Fact]
    public void Load_ParameterMissingHelpEntryThrows()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Does a thing.
            .NOTES
            OpClass: Read
            #>
            param([string]$Undocumented)
            """);
        Assert.Throws<InvalidDataException>(() => ScriptToolLoader.Load(_tempDir, OpClasses));
    }

    [Fact]
    public void Load_NoCommentHelpBlockThrows()
    {
        WriteScript("bad.ps1", "param([string]$X)\nWrite-Output $X\n");
        Assert.Throws<InvalidDataException>(() => ScriptToolLoader.Load(_tempDir, OpClasses));
    }

    [Fact]
    public void Load_SyntaxErrorThrows()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Broken.
            .NOTES
            OpClass: Read
            #>
            param($X
            """);
        Assert.Throws<InvalidDataException>(() => ScriptToolLoader.Load(_tempDir, OpClasses));
    }

    [Fact]
    public void BuildInputSchema_MatchesJsonSchemaShape()
    {
        WriteScript("rename_note.ps1", ValidScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses));

        var schema = tool.BuildInputSchema();
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("From", out _));
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("From", required);
        Assert.Contains("To", required);
        Assert.DoesNotContain("Force", required);
    }
}
