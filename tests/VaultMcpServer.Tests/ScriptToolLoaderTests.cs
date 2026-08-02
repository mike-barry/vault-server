using System.Text.Json;
using VaultShared.Health;
using VaultMcpServer.Tools;
using VaultShared.Configuration;
using VaultShared.Tests;

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
    /// The shape the `.SCHEMA` side-car exists for: `frontmatter_ops` is a typed array of
    /// objects, which the param() block can only ever derive as a bare `{type: "array"}`, so
    /// the block carries a real `oneOf` over the op vocabulary with `const` pinning each
    /// branch's `op`. Also the case where the derived and authored contracts disagree on
    /// everything - `.PARAMETER` text, JSON type, and structure - so a test can tell which won.
    /// </summary>
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
        // A parameter with no ValidateSet omits the key entirely, rather than carrying an
        // empty array or a null that a client would have to special-case.
        Assert.False(properties.GetProperty("Label").TryGetProperty("enum", out _));
    }

    [Fact]
    public void BuildInputSchema_ValidateSetOnArrayParameter_ConstrainsItemsNotTheArray()
    {
        WriteScript("probe.ps1", ValidateSetArrayScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var passes = tool.BuildInputSchema().GetProperty("properties").GetProperty("Passes");
        Assert.Equal("array", passes.GetProperty("type").GetString());
        // A set on an array constrains each element. A property-level enum would demand the
        // array itself equal a member string, which no array satisfies.
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
        // The param() block's own facts are still carried - they remain true about the script,
        // and the encoding item needs the "side-car says non-string, .NET says string" pair.
        Assert.Equal("string", ops.JsonType);
        Assert.NotNull(ops.Schema);

        var property = tool.BuildInputSchema().GetProperty("properties").GetProperty("frontmatter_ops");

        // The block replaces the node, so the node's type is the block's, not MapJsonType's...
        Assert.Equal("array", property.GetProperty("type").GetString());
        // ...and its description is the block's own, not the .PARAMETER text.
        Assert.Equal("Ordered frontmatter operations, applied as one transaction.", property.GetProperty("description").GetString());
        Assert.DoesNotContain("DERIVED-DESCRIPTION-THAT-MUST-NOT-SURVIVE", property.GetRawText(), StringComparison.Ordinal);

        // Verbatim: the emitted node is JSON-equal to the block as authored, not a re-derived
        // node with a couple of keys overwritten - and not double-encoded as a JSON string.
        Assert.Equal(JsonValueKind.Object, property.ValueKind);
        Assert.True(JsonElement.DeepEquals(ops.Schema!.Value, property));

        // The oneOf-over-an-op-vocabulary shape survives intact, since that is the whole point
        // of the mechanism: MapJsonType cannot express items, let alone per-branch const/enum.
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
        // The counterpart to the case above, and the "without a block" case at parameter
        // granularity: `path` sits in the same script and still derives exactly as before.
        // The unchanged-shape coverage for a script with no block at all is already carried by
        // BuildInputSchema_MatchesJsonSchemaShape and the two ValidateSet schema tests above,
        // and by the byte-identical schemas the real tools/ folder still derives - so this
        // adds only what is new: that Schema is null where no block was authored.
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
        // `required` is a keyword of the parent object schema, not of a property node, so a
        // block that replaces the node cannot express it. It stays driven by the param() block,
        // which is also what the PowerShell binder enforces: frontmatter_ops is optional there
        // and so optional here, despite its block being the more elaborate contract.
        WriteScript("edit_fm.ps1", SideCarSchemaScript);
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, new ServerHealthState()));

        var required = tool.BuildInputSchema().GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "path" }, required);
    }

    [Fact]
    public void Load_SideCarSchemaBeatsValidateSet_WithNoMergedEnum()
    {
        // A [ValidateSet] plus a .SCHEMA on one parameter is a conflict, not a merge: the block
        // wins outright and the set contributes nothing, so the effective schema is readable
        // from the block alone rather than from the block plus the param() block.
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
        // The set is still carried on the parameter - it is a true fact about the script - but
        // nothing of it reaches the advertised node.
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

        // Refusal, not fallback. A fallback would re-advertise frontmatter_ops as a bare
        // {type: "string"} and - since side-car presence is also the argument-encoding signal -
        // silently strip its encoding, so a typo would quietly degrade the tool instead of
        // breaking it visibly.
        AssertScriptSkipped(expectedDetailFragment: ".SCHEMA");
        AssertListsAsBrokenEntry("bad");
    }

    [Fact]
    public void Load_SideCarSchemaNamingNoDeclaredParameter_SkipsScript()
    {
        // Mirrors the refusal of a parameter carrying no .PARAMETER entry. A misspelled name is
        // otherwise indistinguishable from an absent block: the parameter re-derives through
        // MapJsonType and loses its encoding with nothing left to read as a signal.
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
        // .PARAMETER stays mandatory for every parameter regardless of a side-car. A block
        // carries its own description, which makes it tempting to read as documenting the
        // parameter - it does not; it documents the wire shape, and the help entry is still
        // required. The refusal is the existing .PARAMETER one, not a .SCHEMA-flavored variant.
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
        // A nameless block names no parameter, so it takes the same refusal - deliberately NOT
        // the silent drop a nameless .PARAMETER gets. Dropping a .PARAMETER is still caught a
        // line later, when the parameter it was meant to document turns out to have no help
        // entry; nothing downstream catches a dropped .SCHEMA.
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
    /// The whole comment-help surface against a script saved with CRLF endings. Every `.ps1` in
    /// `tools/` is LF today, so this is about what an editor can do to one on the way past: the
    /// section-header regex anchors each line with `$` under `RegexOptions.Multiline`, and in .NET
    /// that anchor matches only *before* a `\n`, leaving a CRLF's `\r` for the trailing character
    /// class to absorb. With `\r` outside that class the header does not match, a CRLF script
    /// parses zero sections, and the loader refuses it for a missing `.SYNOPSIS` it plainly has.
    ///
    /// Asserted across all four things the block carries - synopsis, op class, parameters, and the
    /// `.SCHEMA` side-car - because one regex gates all of them, so a partial fix that recovered
    /// only the simple headers would still lose the side-car. The side-car matters most: it is the
    /// largest hand-authored block in any tool script and so the likeliest to be pasted through an
    /// editor that rewrites line endings.
    /// </summary>
    [Fact]
    public void Load_ScriptWithCrlfLineEndings_ParsesTheWholeCommentHelpBlock()
    {
        var path = WriteScript("edit_fm.ps1", SideCarSchemaScript.ReplaceLineEndings("\r\n"));

        // The fixture is only a fixture if it really is CRLF on disk. This repo has no
        // `.gitattributes` and its sources are LF, so a checkout that started translating endings
        // would otherwise turn this test green by making it test nothing.
        var text = File.ReadAllText(path);
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty), StringComparison.Ordinal);

        var healthState = new ServerHealthState();
        var tool = Assert.Single(ScriptToolLoader.Load(_tempDir, OpClasses, healthState));
        Assert.Empty(healthState.Problems);

        Assert.Equal("Applies frontmatter ops to a note.", tool.Description);
        Assert.Equal(OpClass.Write, tool.OpClass);
        Assert.Equal(2, tool.Parameters.Count);

        // The `.PARAMETER` text must not keep a stray `\r` either: a header match ends before the
        // terminator, so the section body starts on it and only the trailing Trim() removes it.
        var pathParameter = Assert.Single(tool.Parameters, p => p.Name == "path");
        Assert.Equal("The note's vault-relative path.", pathParameter.Description);
        Assert.True(pathParameter.Required);

        var ops = Assert.Single(tool.Parameters, p => p.Name == "frontmatter_ops");
        Assert.NotNull(ops.Schema);
        Assert.Equal("array", ops.Schema!.Value.GetProperty("type").GetString());
        Assert.Equal("Ordered frontmatter operations, applied as one transaction.", ops.Schema!.Value.GetProperty("description").GetString());
        // The side-car body is multi-line JSON, so this is the part CRLF could still break past a
        // recovered header match: the block terminates at `.NOTES` and its body parses as JSON.
        Assert.Equal(2, ops.Schema!.Value.GetProperty("items").GetProperty("oneOf").GetArrayLength());
        Assert.True(ops.EncodesStructuredArgument);
    }

    /// <summary>Asserts the refused script surfaces as a <c>BROKEN:</c> entry rather than
    /// vanishing from `tools/list`. <see cref="ScriptToolDispatcher.ListToolsAsync"/> synthesizes
    /// that entry from exactly two things - a Degraded problem under
    /// <see cref="ScriptToolLoader.HealthSource"/>, and its <see cref="DegradedProblem.SubjectPath"/>,
    /// whose base name becomes the listed tool name - so this asserts the entry by asserting
    /// both. <c>HealthCheckDispatchTests</c> carries the same case end-to-end over the wire.</summary>
    private void AssertListsAsBrokenEntry(string expectedToolName)
    {
        var healthState = new ServerHealthState();
        ScriptToolLoader.Load(_tempDir, OpClasses, healthState);
        var problem = Assert.Single(healthState.Problems, p => p.Source == ScriptToolLoader.HealthSource && p.SubjectPath is not null);
        Assert.Equal(expectedToolName, Path.GetFileNameWithoutExtension(problem.SubjectPath!));
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail), "The Detail is the listed entry's description text.");
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
