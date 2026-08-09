using System.Text.Json;
using VaultMcpServer.Health;
using VaultShared.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Tests;

namespace VaultMcpServer.Tests;

/// <summary>
/// Covers <see cref="ScriptToolLoader"/>: what it derives from a script's comment-help block, which
/// scripts it refuses, and how a refusal is reported. Each test writes its own scripts into a
/// temporary directory and loads that directory.
/// </summary>
public sealed class ScriptToolLoaderTests : IDisposable
{
    private static readonly OpClassRegistry OpClasses = OpClassRegistryLoader.Load(Path.Combine(TestVaultFixture.RepoRoot, "op-classes.json"), new ServerHealthState());

    private readonly string _tempDir = Directory.CreateTempSubdirectory("vault-mcp-tools-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    /// <summary>Writes a script into this test's temp tools directory.</summary>
    /// <param name="fileName">The file name to write, including its extension.</param>
    /// <param name="content">The script's text.</param>
    /// <returns>The full path written.</returns>
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

    private const string ValidateSetScript = """
        <#
        .SYNOPSIS
        Exercises a mode.
        .PARAMETER Mode
        Which seam to exercise.
        .PARAMETER Label
        A free-form label for the run.
        .NOTES
        OpClass: content:read
        #>
        param(
            [ValidateSet('read', 'elevated')] [string]$Mode = 'read',
            [string]$Label
        )
        Write-Output "$Mode $Label"
        """;

    private const string ValidateSetArrayScript = """
        <#
        .SYNOPSIS
        Runs a set of passes.
        .PARAMETER Passes
        Which passes to run.
        .NOTES
        OpClass: content:read
        #>
        param(
            [ValidateSet('typography', 'whitespace')] [string[]]$Passes
        )
        Write-Output "$Passes"
        """;

    /// <summary>
    /// A script exercising the <c>.SCHEMA</c> side-car. <c>frontmatter_ops</c> is a typed array of
    /// objects, which a param() block can only derive as a bare <c>{type: "array"}</c>, so the
    /// side-car carries a <c>oneOf</c> over the op vocabulary with <c>const</c> pinning each
    /// branch's <c>op</c>.
    /// </summary>
    /// <remarks>
    /// The derived and authored contracts disagree on everything here - <c>.PARAMETER</c> text, JSON
    /// type and structure - so a test can tell which one was used.
    /// </remarks>
    private const string SideCarSchemaScript = """
        <#
        .SYNOPSIS
        Applies frontmatter ops to a note.
        .PARAMETER path
        The note's vault-relative path.
        .PARAMETER frontmatter_ops
        DERIVED-DESCRIPTION-THAT-MUST-NOT-SURVIVE
        .SCHEMA frontmatter_ops
        {
          "type": "array",
          "description": "Ordered frontmatter operations, applied as one transaction.",
          "items": {
            "oneOf": [
              {
                "type": "object",
                "properties": {
                  "op": { "const": "set" },
                  "key": { "type": "string", "description": "Dotted frontmatter key." },
                  "value": { "description": "The value to set." }
                },
                "required": ["op", "key", "value"]
              },
              {
                "type": "object",
                "properties": {
                  "op": { "const": "add_nav_entry" },
                  "key": { "enum": ["nav.contents", "nav.see_also"] },
                  "description": { "type": "string" }
                },
                "required": ["op", "key"]
              }
            ]
          }
        }
        .NOTES
        OpClass: content:write
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory=$true)] [string]$path,
            [string]$frontmatter_ops
        )
        Write-Output "$path $frontmatter_ops"
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
        // grants:manage exists only in op-classes.json, not in the OpClass constants class, so
        // loading this script shows the loader validates .NOTES op classes against the registry
        // rather than against a fixed set.
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
        // The registry match is case-insensitive, but the stored class is the registry's canonical
        // casing, which Grant.Allows comparisons at dispatch time rely on.
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
        // One broken script loads zero tools from that file and is recorded as a Degraded problem
        // naming it, while every other script in the folder loads normally.
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
        // A script broken on one scan and fixed before the next must stop appearing as a problem.
        // The loader replaces its whole problem set on each scan rather than appending.
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
        // The other direction: a script valid on one scan that breaks before the next must appear
        // as a problem on that next scan.
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
        // The same rule through ScriptToolRegistry.Refresh, which is the entry point the tools/
        // watcher calls at runtime.
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
        // The property set is exactly the param() block's parameters: a schema is the tool's whole
        // advertised argument surface, so an extra property is as wrong as a missing one. Compared
        // as a set, because the names come from a Dictionary whose enumeration order is not part
        // of its contract, and JSON object member order carries no meaning either.
        // The count is what makes the three containment checks below an exact set: JSON object
        // member names cannot repeat, so three names present out of three is all three and no
        // others.
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(3, properties.Count);
        Assert.Contains("From", properties);
        Assert.Contains("To", properties);
        Assert.Contains("Force", properties);
        // Likewise exact rather than by containment, so a change that started marking every
        // parameter required would fail here instead of only losing the Force case.
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "From", "To" }, required);
    }

    [Fact]
    public void Load_ValidateSetParameter_CarriesItsMembers()
    {
        WriteScript("probe.ps1", ValidateSetScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var mode = Assert.Single(tool.Parameters, p => p.Name == "Mode");
        Assert.Equal(new[] { "read", "elevated" }, mode.AllowedValues);

        var label = Assert.Single(tool.Parameters, p => p.Name == "Label");
        Assert.Null(label.AllowedValues);
    }

    [Fact]
    public void BuildInputSchema_ValidateSetParameter_EmitsEnumOnlyWhereConstrained()
    {
        WriteScript("probe.ps1", ValidateSetScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var properties = tool.BuildInputSchema().GetProperty("properties");
        var members = properties.GetProperty("Mode").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "read", "elevated" }, members);
        // A parameter with no ValidateSet omits the key entirely, rather than carrying an empty
        // array or a null a client would have to special-case.
        Assert.False(properties.GetProperty("Label").TryGetProperty("enum", out _));
    }

    [Fact]
    public void BuildInputSchema_ValidateSetOnArrayParameter_ConstrainsItemsNotTheArray()
    {
        WriteScript("probe.ps1", ValidateSetArrayScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var passes = tool.BuildInputSchema().GetProperty("properties").GetProperty("Passes");
        Assert.Equal("array", passes.GetProperty("type").GetString());
        // A set on an array constrains each element. A property-level enum would require the array
        // itself to equal a member string, which no array does.
        Assert.False(passes.TryGetProperty("enum", out _));
        var members = passes.GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "typography", "whitespace" }, members);
    }

    [Fact]
    public void Load_SideCarSchema_ReplacesTheWholeDerivedPropertyNode()
    {
        WriteScript("edit_fm.ps1", SideCarSchemaScript);
        var healthState = new ServerHealthState();
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, healthState));
        Assert.Empty(healthState.Problems);

        var ops = Assert.Single(tool.Parameters, p => p.Name == "frontmatter_ops");
        // The param() block's own facts are still carried. They stay true about the script, and the
        // encoding signal needs both: the side-car says non-string while the .NET type is string.
        Assert.Equal("string", ops.JsonType);
        Assert.NotNull(ops.Schema);

        var property = tool.BuildInputSchema().GetProperty("properties").GetProperty("frontmatter_ops");

        // The side-car replaces the node, so the type is the side-car's rather than MapJsonType's.
        Assert.Equal("array", property.GetProperty("type").GetString());
        // The description is the side-car's own, not the .PARAMETER text.
        Assert.Equal("Ordered frontmatter operations, applied as one transaction.", property.GetProperty("description").GetString());
        Assert.DoesNotContain("DERIVED-DESCRIPTION-THAT-MUST-NOT-SURVIVE", property.GetRawText(), StringComparison.Ordinal);

        // The emitted node is JSON-equal to the side-car as written: not a re-derived node with a
        // few keys overwritten, and not double-encoded as a JSON string.
        Assert.Equal(JsonValueKind.Object, property.ValueKind);
        Assert.True(JsonElement.DeepEquals(ops.Schema!.Value, property));

        // The oneOf over the op vocabulary survives intact. MapJsonType cannot express items at
        // all, let alone a per-branch const or enum.
        var branches = property.GetProperty("items").GetProperty("oneOf").EnumerateArray().ToList();
        Assert.Equal(2, branches.Count);
        Assert.Equal("set", branches[0].GetProperty("properties").GetProperty("op").GetProperty("const").GetString());
        Assert.Equal("add_nav_entry", branches[1].GetProperty("properties").GetProperty("op").GetProperty("const").GetString());
        var navKeys = branches[1].GetProperty("properties").GetProperty("key").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "nav.contents", "nav.see_also" }, navKeys);
    }

    [Fact]
    public void Load_SideCarSchema_LeavesEveryOtherParameterDerived()
    {
        // A side-car states one parameter's node and leaves the others alone: `path` sits in the
        // same script and derives from its param() declaration as it would with no side-car
        // present. What this adds over BuildInputSchema_MatchesJsonSchemaShape and the ValidateSet
        // tests is that Schema is null on a parameter with no side-car of its own.
        WriteScript("edit_fm.ps1", SideCarSchemaScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var path = Assert.Single(tool.Parameters, p => p.Name == "path");
        Assert.Null(path.Schema);

        var property = tool.BuildInputSchema().GetProperty("properties").GetProperty("path");
        Assert.Equal("string", property.GetProperty("type").GetString());
        Assert.Equal("The note's vault-relative path.", property.GetProperty("description").GetString());
    }

    [Fact]
    public void BuildInputSchema_SideCarSchema_RequiredStillComesFromParameterMandatory()
    {
        // `required` is a keyword of the parent object schema rather than of a property node, so a
        // side-car that replaces the node cannot express it. It stays driven by the param() block,
        // which is what the PowerShell binder enforces: frontmatter_ops is optional there and so
        // optional here.
        WriteScript("edit_fm.ps1", SideCarSchemaScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var required = tool.BuildInputSchema().GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "path" }, required);
    }

    [Fact]
    public void Load_SideCarSchemaBeatsValidateSet_WithNoMergedEnum()
    {
        // A [ValidateSet] and a .SCHEMA on the same parameter do not merge. The side-car wins
        // outright and the set contributes nothing, so the advertised schema is readable from the
        // side-car alone.
        WriteScript("passes.ps1", """
            <#
            .SYNOPSIS
            Runs a set of passes.
            .PARAMETER passes
            Which passes to run.
            .SCHEMA passes
            {
              "type": "array",
              "description": "Passes to run, as objects rather than bare names.",
              "items": {
                "type": "object",
                "properties": { "name": { "type": "string" } },
                "required": ["name"]
              }
            }
            .NOTES
            OpClass: content:read
            #>
            param(
                [ValidateSet('typography', 'whitespace')] [string[]]$passes
            )
            Write-Output "$passes"
            """);

        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));
        // The set is still carried on the parameter, since it remains true of the script, but none
        // of it reaches the advertised node.
        var passes = Assert.Single(tool.Parameters);
        Assert.Equal(new[] { "typography", "whitespace" }, passes.AllowedValues);

        var property = tool.BuildInputSchema().GetProperty("properties").GetProperty("passes");
        Assert.False(property.TryGetProperty("enum", out _));
        var items = property.GetProperty("items");
        Assert.False(items.TryGetProperty("enum", out _));
        Assert.Equal("object", items.GetProperty("type").GetString());
        Assert.DoesNotContain("typography", property.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MalformedSideCarSchema_SkipsScriptAndNeverFallsBackToMapJsonType()
    {
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Applies frontmatter ops to a note.
            .PARAMETER frontmatter_ops
            Ops to apply.
            .SCHEMA frontmatter_ops
            {
              "type": "array",
              "items": { "oneOf": [
            }
            .NOTES
            OpClass: content:write
            #>
            param([string]$frontmatter_ops)
            """);

        // The script is refused rather than falling back. A fallback would re-advertise
        // frontmatter_ops as a bare {type: "string"} and, because side-car presence is also the
        // argument-encoding signal, strip its encoding, so a typo would quietly degrade the tool
        // instead of breaking it visibly.
        AssertScriptSkipped(expectedDetailFragment: ".SCHEMA");
        AssertListsAsBrokenEntry("bad");
    }

    [Fact]
    public void Load_SideCarSchemaNamingNoDeclaredParameter_SkipsScript()
    {
        // Refused like a parameter carrying no .PARAMETER entry. A misspelled name is otherwise
        // indistinguishable from an absent side-car: the parameter re-derives through MapJsonType
        // and loses its encoding, with nothing left to signal that anything went wrong.
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Applies frontmatter ops to a note.
            .PARAMETER frontmatter_ops
            Ops to apply.
            .SCHEMA frontmater_ops
            { "type": "array" }
            .NOTES
            OpClass: content:write
            #>
            param([string]$frontmatter_ops)
            """);

        AssertScriptSkipped(expectedDetailFragment: "frontmater_ops");
        AssertListsAsBrokenEntry("bad");
    }

    [Fact]
    public void Load_SideCarSchemaDoesNotSubstituteForAMissingParameterEntry()
    {
        // .PARAMETER stays mandatory for every parameter, side-car or not. A side-car carries its
        // own description, but that describes the wire shape rather than the parameter, so the
        // help entry is still required. The refusal is the ordinary .PARAMETER one.
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Applies frontmatter ops to a note.
            .SCHEMA frontmatter_ops
            { "type": "array", "description": "Ordered frontmatter operations." }
            .NOTES
            OpClass: content:write
            #>
            param([string]$frontmatter_ops)
            """);

        AssertScriptSkipped(expectedDetailFragment: ".PARAMETER");
    }

    [Fact]
    public void Load_NamelessSideCarSchema_SkipsScript()
    {
        // A nameless .SCHEMA names no parameter and is refused, rather than being dropped the way a
        // nameless .PARAMETER is. A dropped .PARAMETER is caught immediately afterwards, when the
        // parameter it was meant to document turns out to have no help entry. Nothing downstream
        // would catch a dropped .SCHEMA.
        WriteScript("bad.ps1", """
            <#
            .SYNOPSIS
            Applies frontmatter ops to a note.
            .PARAMETER frontmatter_ops
            Ops to apply.
            .SCHEMA
            { "type": "array" }
            .NOTES
            OpClass: content:write
            #>
            param([string]$frontmatter_ops)
            """);

        AssertScriptSkipped(expectedDetailFragment: ".SCHEMA");
        AssertListsAsBrokenEntry("bad");
    }

    /// <summary>
    /// Verifies that the whole comment-help block parses from a script saved with CRLF line
    /// endings.
    /// </summary>
    /// <remarks>
    /// Every <c>.ps1</c> in <c>tools/</c> is LF, but an editor can rewrite one. The section-header
    /// regex anchors each line with <c>$</c> under <c>RegexOptions.Multiline</c>, and in .NET that
    /// anchor matches only before a <c>\n</c>, so a CRLF's <c>\r</c> has to be absorbed by the
    /// trailing character class. With <c>\r</c> outside that class no header matches, a CRLF script
    /// parses zero sections, and the loader refuses it for a missing <c>.SYNOPSIS</c> it has.
    ///
    /// All four things the block carries are asserted - synopsis, op class, parameters and the
    /// <c>.SCHEMA</c> side-car - because one regex gates all of them and a partial fix could
    /// recover the simple headers while still losing the side-car.
    /// </remarks>
    [Fact]
    public void Load_ScriptWithCrlfLineEndings_ParsesTheWholeCommentHelpBlock()
    {
        var path = WriteScript("edit_fm.ps1", SideCarSchemaScript.ReplaceLineEndings("\r\n"));

        // The script has to really be CRLF on disk for this test to mean anything. The repo has no
        // .gitattributes and its sources are LF, so a checkout that started translating line
        // endings would otherwise leave this test passing while testing nothing.
        var text = File.ReadAllText(path);
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty), StringComparison.Ordinal);

        var healthState = new ServerHealthState();
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, healthState));
        Assert.Empty(healthState.Problems);

        Assert.Equal("Applies frontmatter ops to a note.", tool.Description);
        Assert.Equal(OpClass.Write, tool.OpClass);
        Assert.Equal(2, tool.Parameters.Count);

        // The .PARAMETER text must not keep a stray \r either. A header match ends before the line
        // terminator, so the section body starts on it and only the trailing Trim() removes it.
        var pathParameter = Assert.Single(tool.Parameters, p => p.Name == "path");
        Assert.Equal("The note's vault-relative path.", pathParameter.Description);
        Assert.True(pathParameter.Required);

        var ops = Assert.Single(tool.Parameters, p => p.Name == "frontmatter_ops");
        Assert.NotNull(ops.Schema);
        Assert.Equal("array", ops.Schema!.Value.GetProperty("type").GetString());
        Assert.Equal("Ordered frontmatter operations, applied as one transaction.", ops.Schema!.Value.GetProperty("description").GetString());
        // The side-car body is multi-line JSON, so it is what CRLF could still break even with the
        // header matching: the block has to terminate at .NOTES and its body has to parse.
        Assert.Equal(2, ops.Schema!.Value.GetProperty("items").GetProperty("oneOf").GetArrayLength());
        Assert.True(ops.EncodesStructuredArgument);
    }

    /// <summary>
    /// Asserts that a refused script will surface as a <c>BROKEN:</c> entry rather than vanishing
    /// from <c>tools/list</c>.
    /// </summary>
    /// <param name="expectedToolName">The tool name the broken entry should be listed under.</param>
    /// <remarks>
    /// <see cref="ScriptToolDispatcher.ListToolsAsync"/> synthesizes that entry from two things: a
    /// Degraded problem under <see cref="ScriptToolLoader.HealthSource"/>, and that problem's
    /// <see cref="DegradedProblem.SubjectPath"/>, whose base name becomes the listed tool name. This
    /// checks both. <c>HealthCheckDispatchTests</c> covers the same case over the wire.
    /// </remarks>
    private void AssertListsAsBrokenEntry(string expectedToolName)
    {
        var healthState = new ServerHealthState();
        ScriptToolLoader.Load(_tempDir, OpClasses, healthState);
        var problem = Assert.Single(healthState.Problems, p => p.Source == ScriptToolLoader.HealthSource && p.SubjectPath is not null);
        Assert.Equal(expectedToolName, Path.GetFileNameWithoutExtension(problem.SubjectPath!));
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail), "The Detail is the listed entry's description text.");
    }

    /// <summary>
    /// Asserts that loading a folder holding a single <c>bad.ps1</c> returns no tools instead of
    /// throwing, is never Fatal, and records exactly one Degraded problem from the ScriptTools
    /// source naming the script.
    /// </summary>
    /// <param name="expectedDetailFragment">Text the problem's detail must contain, or null to
    /// check only that the script was named.</param>
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
