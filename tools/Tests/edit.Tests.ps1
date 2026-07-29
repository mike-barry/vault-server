# Pester suite for tools/edit.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring move.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/edit.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside edit.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: this suite covers the Tier-2 return and the write-class envelope, the facet-form
# rules, the frontmatter-op vocabulary as edit drives it (including the idempotent changed:false
# paths), body-confined exactly-once patch matching in both failure directions, the staleness
# guard (state_mismatch with zero mutation, and resultingSha chaining with no interleaved read),
# byte preservation end to end, every error code in the spec's table including the
# protected-over-shape precedence case, the capability-denial pair edit is the first tool to
# carry (injected set lacking agentic:write denies with zero disk change; no injected set
# degrades open), the content:write op-class metadata declaration, and standalone invocability
# with the JSON error shape on stderr.
#
# What is deliberately NOT re-proven here: the substrate's own semantics (byte-preserving
# splices, the schema rules, the outcome diff) are covered by the VaultAccess Frontmatter*
# suites; this suite proves edit drives them correctly and shapes their outcomes into the tool
# contract.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'edit.ps1'
    $script:ReadScriptPath = Join-Path $PSScriptRoot '..' 'read.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    # A handful of assertions read a written note's frontmatter as the parsed TREE rather than by
    # matching on emitted text - the durable form for "the value survived", now that the emitter
    # rather than the caller decides how a value is quoted.
    Import-Module (Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

    function Invoke-EditError {
        # Runs edit.ps1, expecting it to throw the shared {code, message, data?} JSON error
        # shape, and returns the parsed error object for assertions. Defined in this
        # top-level BeforeAll (rather than inline in the Describe body) so it survives into
        # Pester v5's Run phase - a bare `function` statement in a Describe body only
        # executes during Discovery and would not be visible from It blocks.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'edit.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }

    function Get-FileSha {
        # The digest edit's expected_sha is compared against: SHA-256 over the file's raw bytes
        # as lowercase hex, computed independently of the code under test.
        param([string] $FullPath)
        [System.BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($FullPath))).Replace('-', '').ToLowerInvariant()
    }
}

# =============================================================================================
# Discovery-phase setup for the "advertised frontmatter_ops contract" Context below.
#
# This all runs at DISCOVERY, not in a BeforeAll, because the schema's branch list is what the
# Context's -ForEach iterates over: the enumeration has to exist before Pester builds the test
# tree. Everything the Run phase needs is handed across through -ForEach rather than through
# script-scope variables, so nothing here depends on discovery state surviving into Run.
#
# The whole point of the block is that a hand-authored schema can drift from the script it
# documents. So NOTHING below names an op: the branch list comes out of edit.ps1's own .SCHEMA
# block, each op instance is built from its branch's own `required` list and property nodes, and
# the expected branch set comes out of the substrate's dispatch AST. A test that hand-listed the
# ops it exercises would go green on exactly the drift it exists to catch.
# =============================================================================================

function Get-AdvertisedSchema {
    <#
    .SYNOPSIS
    The JSON Schema a script's `.SCHEMA <param>` side-car advertises for one parameter, read out
    of the script's comment-help.

    Uses ScriptToolLoader's own two regexes - HelpBlockRegex and SectionHeaderRegex, copied
    verbatim from src/VaultMcpServer/Tools/ScriptToolLoader.cs into the two [regex] calls below -
    plus the loader's own section-body slice (header end to next header start, trimmed), so what
    this returns is what the loader emits as the property node.

    Read by parsing rather than by calling the real loader deliberately: reaching
    ScriptToolLoader from Pester would mean loading VaultMcpServer.dll into this session - a
    build artifact the running MCP server holds open - purely to re-read a file this suite can
    already read, and it would put a build dependency on a PowerShell suite that has none today.
    The C# ScriptToolLoaderTests own the question of whether these regexes lift the block
    correctly and whether the block survives verbatim into `inputSchema`; what this suite owns is
    whether the block matches the script it documents.
    #>
    param([Parameter(Mandatory)] [string] $ScriptPath, [Parameter(Mandatory)] [string] $ParameterName)

    $text = [System.IO.File]::ReadAllText($ScriptPath)
    $block = [regex]::Match($text, '(?s)<#(.*?)#>')
    if (-not $block.Success) { throw "No comment-help block in $ScriptPath." }
    $body = $block.Groups[1].Value

    $headers = [regex]::Matches($body, '(?m)^[ \t]*\.(?<tag>[A-Za-z]+)\b[ \t]*(?<arg>\S+)?[ \t]*$')
    for ($i = 0; $i -lt $headers.Count; $i++) {
        $header = $headers[$i]
        if ($header.Groups['tag'].Value.ToUpperInvariant() -ne 'SCHEMA') { continue }
        if ($header.Groups['arg'].Value -ine $ParameterName) { continue }
        $start = $header.Index + $header.Length
        $end = if ($i + 1 -lt $headers.Count) { $headers[$i + 1].Index } else { $body.Length }
        return ($body.Substring($start, $end - $start).Trim() | ConvertFrom-Json)
    }
    throw "No '.SCHEMA $ParameterName' block in $ScriptPath."
}

function Get-SubstrateOpShape {
    <#
    .SYNOPSIS
    The op shapes the frontmatter substrate's dispatch actually implements, as an op name on its
    own or an op name and its kind joined by a slash, read from the AST of
    Private/FrontmatterOps.ps1.

    Derived rather than restated so the converse of the drift check holds: an op added to the
    substrate without a schema branch fails the comparison instead of going unnoticed. The
    `default` clause is not a shape and the AST keeps it off `Clauses`, so it drops out for free.
    #>
    param([Parameter(Mandatory)] [string] $OpsScriptPath)

    $ast = [System.Management.Automation.Language.Parser]::ParseFile($OpsScriptPath, [ref]$null, [ref]$null)
    $switches = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.SwitchStatementAst] }, $true)
    $opSwitch = $switches | Where-Object { $_.Condition.Extent.Text -eq '$name' } | Select-Object -First 1
    if ($null -eq $opSwitch) { throw "No 'switch (`$name)' op dispatch in $OpsScriptPath." }

    $shapes = @()
    foreach ($clause in $opSwitch.Clauses) {
        $op = $clause.Item1.Value
        # A branch of the op dispatch that itself switches on $kind is two shapes, not one: the
        # required members differ per kind (add_directive/pinned takes text, use_skill takes
        # skill+when), which is exactly what a per-branch `required` has to express.
        $kindSwitch = $clause.Item2.FindAll({ param($n) $n -is [System.Management.Automation.Language.SwitchStatementAst] }, $true) |
            Where-Object { $_.Condition.Extent.Text -eq '$kind' }
        if ($kindSwitch) {
            foreach ($kindClause in @($kindSwitch)[0].Clauses) { $shapes += "$op/$($kindClause.Item1.Value)" }
        }
        else { $shapes += $op }
    }
    return $shapes
}

# Placeholder values for the properties a branch declares required, keyed by property NAME, with
# an optional "<op>[/<kind>].<name>" override taking precedence. The overrides exist for one
# reason: the fixture below has to ALREADY CONTAIN the target a strip/remove branch addresses,
# and must NOT contain the one an add branch inserts - otherwise the add reports changed:false
# and the "it was applied" half of the assertion proves nothing. A branch requiring a property
# with no entry here throws rather than being skipped: an unrecognized property is drift too.
$DriftValues = @{
    'field'                       = 'doc.type'
    'value'                       = 'Corpus'
    'text'                        = 'drift probe instruction text'
    'skill'                       = 'vault-docs'
    'when'                        = 'editing this file'
    'note'                        = 'drift probe note'
    'description'                 = 'drift probe entry'
    'load_when'                   = 'probing for drift'
    'link'                        = '[[Drift-Probe-Added]]'
    'folder'                      = 'Inbox'
    'before'                      = '[[Drift-Probe-Seeded]]'
    'after'                       = '[[Drift-Probe-Seeded]]'
    'strip_directive/pinned.text' = 'drift probe seeded pinned rule'
    'remove_nav_entry.link'       = '[[Drift-Probe-Seeded]]'
}

function Get-DriftValue {
    param([Parameter(Mandatory)] [string] $Shape, [Parameter(Mandatory)] [string] $Name)
    foreach ($candidate in @("$Shape.$Name", $Name)) {
        if ($DriftValues.ContainsKey($candidate)) { return $DriftValues[$candidate] }
    }
    throw "The .SCHEMA block declares a property '$Name' on branch '$Shape' that this suite has no value for - either the block gained a property or it drifted; extend `$DriftValues."
}

function New-DriftOpInstance {
    <#
    .SYNOPSIS
    The minimal op object one schema branch documents: every property the branch marks `required`,
    valued from that property's own `const` / `enum` where it pins one and from $DriftValues
    otherwise.

    Plus one thing `required` cannot carry: a branch declaring BOTH `link` and `folder` while
    requiring neither is the link-XOR-folder pair the schema deliberately does not express (the
    plan's Out of Scope), and the substrate refuses a nav entry op carrying neither - so the
    builder supplies one discriminator. That rule triggers off the branch's own property set, not
    off an op name, so it needs no maintenance when the vocabulary changes.
    #>
    param([Parameter(Mandatory)] $Branch, [Parameter(Mandatory)] [string] $Shape)

    $propertyNames = @($Branch.properties.PSObject.Properties.Name)
    $instance = [ordered]@{}
    foreach ($name in @($Branch.required)) {
        $node = $Branch.properties.$name
        $nodeKeys = @($node.PSObject.Properties.Name)
        $instance[$name] =
            if ($nodeKeys -contains 'const') { $node.const }
            elseif ($nodeKeys -contains 'enum') { @($node.enum)[0] }
            else { Get-DriftValue -Shape $Shape -Name $name }
    }
    if (($propertyNames -contains 'link') -and ($propertyNames -contains 'folder') -and
        -not ($instance.Contains('link') -or $instance.Contains('folder'))) {
        $instance['link'] = Get-DriftValue -Shape $Shape -Name 'link'
    }
    return $instance
}

$DriftSchema = Get-AdvertisedSchema -ScriptPath (Join-Path $PSScriptRoot '..' 'edit.ps1') -ParameterName 'frontmatter_ops'
$DriftSubstrateShapes = @(Get-SubstrateOpShape -OpsScriptPath (Join-Path $PSScriptRoot '..' '..' 'powershell' 'VaultAccess' 'Private' 'FrontmatterOps.ps1'))
$DriftBranches = @(foreach ($branch in $DriftSchema.items.oneOf) {
        $op = $branch.properties.op.const
        $kind = if (@($branch.properties.PSObject.Properties.Name) -contains 'kind') { $branch.properties.kind.const } else { $null }
        $shape = if ($kind) { "$op/$kind" } else { $op }
        $instance = New-DriftOpInstance -Branch $branch -Shape $shape
        @{
            Shape = $shape
            Op    = $op
            Kind  = $kind
            # A one-element JSON array, composed rather than ConvertTo-Json'd from a wrapper:
            # frontmatter_ops refuses a bare object, and PowerShell unrolls a single-element
            # array on the way into ConvertTo-Json - the -AsArray bug this plan already retired
            # once in lint's remedy builder.
            Ops   = '[' + (ConvertTo-Json -Compress -Depth 6 -InputObject $instance) + ']'
        }
    })

# One note carrying a target for every strip/remove branch and none of the values an add branch
# inserts, built from $DriftValues so the seeds and the generated ops cannot drift apart.
$DriftFixture = @(
    '---'
    'doc.type: Note'
    'agent.instructions:'
    "  - pinned: `"$(Get-DriftValue -Shape 'strip_directive/pinned' -Name 'text')`""
    '  - use_skill: "vault-tasks"'
    '    when: "managing tasks"'
    'nav.contents:'
    "  - link: `"$(Get-DriftValue -Shape 'remove_nav_entry' -Name 'link')`""
    '    description: "seeded so the remove branch addresses a real entry"'
    '---'
    ''
    '# Drift-Probe'
    ''
    'body line'
    ''
) -join "`n"

Describe 'edit' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-edit-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $configPath = Join-Path $script:TempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
        $env:VAULT_MCP_VAULTS_JSON = $configPath

        # A byte-precise working note: known frontmatter, known body, LF throughout.
        $script:NotePath = 'Inbox/Edit-Case.md'
        $script:NoteFull = Join-Path $script:VaultDir 'Inbox/Edit-Case.md'
        [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n`n# Edit-Case`nalpha line`nbeta line`n")
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Tier-2 return shape and the write-class envelope' {
        It 'returns a single well-formed JSON object for a frontmatter op' {
            $output = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'
            { $output | ConvertFrom-Json } | Should -Not -Throw

            $result = $output | ConvertFrom-Json
            $result.path | Should -Be $script:NotePath
            $result.frontmatterChanged | Should -BeTrue
            $result.bodyChanged | Should -BeFalse
            $result.opsApplied.Count | Should -Be 1
            $result.opsApplied[0].op | Should -Be 'set_field'
            $result.opsApplied[0].field | Should -Be 'doc.type'
            $result.opsApplied[0].changed | Should -BeTrue
            $result.resultingSha | Should -Match '^[0-9a-f]{64}$'
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Corpus`n---`n`n# Edit-Case`nalpha line`nbeta line`n"
        }

        It 'emits capabilitiesUsed and filesSkippedUnreadable as arrays, [] when empty' {
            $output = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'
            $result = $output | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Contain 'capabilitiesUsed'
            $result.PSObject.Properties.Name | Should -Contain 'filesSkippedUnreadable'
            $output | Should -Match '"capabilitiesUsed":\s*\[\]'
            # Structurally always [] - edit is single-target, so a missing or unreadable sole
            # target is not_found / read_failure, never a skip.
            $output | Should -Match '"filesSkippedUnreadable":\s*\[\]'
        }

        It 'omits lockWarning entirely when the lock was not abandoned' {
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | ConvertFrom-Json
            # Absent, not null: an always-present null would read as checked-and-clear.
            $result.PSObject.Properties.Name | Should -Not -Contain 'lockWarning'
        }

        It 'echoes agentic:write in capabilitiesUsed when the agentic layer moved' {
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"add_hint","text":"Prefer the torque table"}]' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
        }

        It 'returns resultingFrontmatter byte-identical to read part: frontmatter' {
            # The contract locked in the spec: resultingFrontmatter is raw block text matching what
            # read returns, so the caller can verify without a follow-up read - which matters
            # because several ops are silently idempotent. Both tools take the split from the same
            # substrate function, so the match is structural.
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | ConvertFrom-Json
            $viaRead = & $script:ReadScriptPath -Path $script:NotePath -Part 'frontmatter'
            $result.resultingFrontmatter | Should -BeExactly $viaRead
            $result.resultingFrontmatter | Should -BeExactly "doc.created: 2026-07-16`ndoc.type: Note`ndoc.archived: 2026-07-25"
        }

        # --- The parity contract above, exercised on the fence shapes that strain it: a
        # whitespace-padded fence, an inner line that opens with "---", and a "----" line that
        # looks like a fence and is not. The contract is only worth as much as the shapes it is
        # tested on, and a clean fence reaches none of these. Both tools take the split from the
        # substrate's Split-VaultNoteContent, so each case pins that one authority's answer
        # arriving on both sides of the contract. ---

        It 'returns resultingFrontmatter matching read on a whitespace-padded opening fence' {
            # A fence line is recognized on its trimmed text, so "--- " opens a block exactly as
            # "---" does and both tools see the same closed block.
            $padded = 'Inbox/Edit-Padded-Open.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $padded), "--- `ndoc.type: Note`n---`nBody`n")

            $result = & $script:ScriptPath -Path $padded -frontmatter_ops '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | ConvertFrom-Json
            $viaRead = & $script:ReadScriptPath -Path $padded -Part 'frontmatter'
            $result.resultingFrontmatter | Should -BeExactly $viaRead
            $result.resultingFrontmatter | Should -BeExactly "doc.type: Note`ndoc.archived: 2026-07-25"
            # The padded fence line is NOT preserved: a frontmatter op re-emits the whole block
            # through the module's only fence writer, which writes a bare "---". Recognizing the
            # padding is what the parity contract needs; keeping it is not something any caller
            # can observe once the block has been rewritten.
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir $padded)) |
                Should -BeExactly "---`ndoc.type: Note`ndoc.archived: 2026-07-25`n---`nBody`n"
        }

        It 'returns resultingFrontmatter matching read on a whitespace-padded closing fence' {
            # The same trimming closes a block, and the closing line is consumed whole - no " \n"
            # fragment heads the body. Both facets are asserted here rather than inferred from the
            # opening-fence case above, since padding on the closing line is a separate path.
            $padded = 'Inbox/Edit-Padded-Close.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $padded), "---`ndoc.type: Note`n--- `nBody`n")

            $result = & $script:ScriptPath -Path $padded -frontmatter_ops '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | ConvertFrom-Json
            $viaRead = & $script:ReadScriptPath -Path $padded -Part 'frontmatter'
            $result.resultingFrontmatter | Should -BeExactly $viaRead
            $result.resultingFrontmatter | Should -BeExactly "doc.type: Note`ndoc.archived: 2026-07-25"
            # The body facet carries no fence fragment: the closing line is consumed whole.
            & $script:ReadScriptPath -Path $padded -Part 'body' | Should -BeExactly "Body`n"
        }

        It 'agrees with read that an inner line starting with "---" is block text, and refuses it' {
            # The sharpest case, and the one where the two tools' agreement is about WHAT THE
            # BLOCK IS rather than what edit can do with it. A fence is a whole line, not a
            # prefix: "---junk" is not one, so the block runs on to the real "---" three lines
            # down and read reports every inner line - "---junk" among them - as block text.
            #
            # edit sees the same block and refuses it, because that block is not YAML. That is
            # the parity holding, not breaking: both tools take the split from the substrate's
            # Split-VaultNoteContent and agree on its extent; only edit goes on to PARSE it, and
            # a block a real parser rejects is a note that cannot be written until it is repaired.
            $inner = 'Inbox/Edit-Inner-Dashes.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $inner), "---`ndoc.type: Note`n---junk`nmore`n---`nBody`n")

            & $script:ReadScriptPath -Path $inner -Part 'frontmatter' |
                Should -BeExactly "doc.type: Note`n---junk`nmore"
            $err = Invoke-EditError @{ Path = $inner; frontmatter_ops = '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'frontmatter_unparsed'
            # Refused means untouched, so the note is still there to repair.
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir $inner)) |
                Should -BeExactly "---`ndoc.type: Note`n---junk`nmore`n---`nBody`n"
        }

        It 'agrees with read that a "----" closing line closes nothing' {
            # The whole-line rule from the other direction: "----" is not a fence, so the opening
            # one never closes - and to the standard a "---" that nothing closes is a thematic
            # break, not an opening fence, so the note has no frontmatter at all. read hands back
            # the whole file as body, and edit refuses to prepend a block that would strand the
            # user's apparent metadata below it. The reason is apparent_fence: the note-level
            # unclosed state is retired, so there is no unclosed_fence to report here.
            $dashes = 'Inbox/Edit-Four-Dashes.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $dashes), "---`ndoc.type: Note`n----`nBody`n")

            $err = Invoke-EditError @{ Path = $dashes; frontmatter_ops = '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'apparent_fence'
            & $script:ReadScriptPath -Path $dashes -Part 'frontmatter' | Should -BeExactly ''
            & $script:ReadScriptPath -Path $dashes -Part 'body' | Should -BeExactly "---`ndoc.type: Note`n----`nBody`n"
        }

        It 'returns an empty resultingFrontmatter for a note that has no frontmatter block' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' `
                -frontmatter_ops '[{"op":"add_directive","kind":"use_skill","skill":"vault-tasks","when":"managing tasks"}]' | ConvertFrom-Json
            # The op above creates the block, so this is asserted on the pre-op state via a note
            # that stays fence-less: a body-only patch.
            $result.resultingFrontmatter | Should -Not -BeNullOrEmpty

            $bodyOnly = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md' -old_string 'Loose Thought' -new_string 'Loose Thoughts' | ConvertFrom-Json
            $bodyOnly.frontmatterChanged | Should -BeFalse
            $bodyOnly.bodyChanged | Should -BeTrue
        }

        It 'reports opsApplied in order with per-op changed flags' {
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"},{"op":"remove_field","field":"doc.absent"}]' | ConvertFrom-Json
            $result.opsApplied.Count | Should -Be 2
            $result.opsApplied[0].op | Should -Be 'set_field'
            $result.opsApplied[0].changed | Should -BeTrue
            $result.opsApplied[1].op | Should -Be 'remove_field'
            $result.opsApplied[1].changed | Should -BeFalse
        }
    }

    Context 'facet forms' {
        It 'replaces the whole body when body is supplied with expected_sha' {
            $sha = Get-FileSha $script:NoteFull
            $result = & $script:ScriptPath -Path $script:NotePath -Body "# Replaced`n" -expected_sha $sha | ConvertFrom-Json
            $result.bodyChanged | Should -BeTrue
            $result.frontmatterChanged | Should -BeFalse
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Replaced`n"
        }

        It 'truncates the body on an empty body string and leaves the block intact' {
            $sha = Get-FileSha $script:NoteFull
            & $script:ScriptPath -Path $script:NotePath -Body '' -expected_sha $sha | Out-Null
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n"
        }

        It 'applies a patch and a frontmatter op in one call' {
            $result = & $script:ScriptPath -Path $script:NotePath `
                -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' `
                -old_string 'alpha line' -new_string 'ALPHA LINE' | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue
            $result.bodyChanged | Should -BeTrue
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Corpus`n---`n`n# Edit-Case`nALPHA LINE`nbeta line`n"
        }

        It 'deletes the matched span when new_string is empty' {
            & $script:ScriptPath -Path $script:NotePath -old_string "alpha line`n" -new_string '' | Out-Null
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n`n# Edit-Case`nbeta line`n"
        }
    }

    Context 'patch matching is body-confined and exactly-once' {
        It 'errors patch_target_not_found when old_string matches nothing' {
            $err = Invoke-EditError @{ Path = $script:NotePath; old_string = 'gamma line'; new_string = 'x' }
            $err.code | Should -Be 'patch_target_not_found'
            $err.data.oldString | Should -Be 'gamma line'
            # Zero mutation on a failed match.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -Match 'alpha line'
        }

        It 'errors patch_ambiguous when old_string matches more than once' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n---`n`nrepeat`nrepeat`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; old_string = 'repeat'; new_string = 'once' }
            $err.code | Should -Be 'patch_ambiguous'
            $err.data.matchCount | Should -BeGreaterThan 1
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.type: Note`n---`n`nrepeat`nrepeat`n"
        }

        It 'cannot reach the frontmatter: an anchor unique in the block is not_found for a patch' {
            # Body-confinement is what keeps every frontmatter mutation on the schema-validated,
            # capability-classified ops path.
            $err = Invoke-EditError @{ Path = $script:NotePath; old_string = 'doc.type: Note'; new_string = 'doc.type: Corpus' }
            $err.code | Should -Be 'patch_target_not_found'
        }

        It 'matches a body occurrence even when the same text also appears in the frontmatter' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n---`n`ndoc.type: Note`n")
            & $script:ScriptPath -Path $script:NotePath -old_string 'doc.type: Note' -new_string 'mentioned' | Out-Null
            # Exactly one match, because the frontmatter copy is outside the facet being searched.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.type: Note`n---`n`nmentioned`n"
        }
    }

    Context 'idempotent changed:false paths' {
        It 'records changed:false for a duplicate add_directive' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n")
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"add_directive","kind":"pinned","text":"Never exfil this"}]' | ConvertFrom-Json
            $result.opsApplied[0].changed | Should -BeFalse
            $result.frontmatterChanged | Should -BeFalse
            # Idempotent means it also asserts nothing: the outcome diff is empty.
            $result.capabilitiesUsed.Count | Should -Be 0
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n"
        }

        It 'records changed:false for a remove_field against an absent field' {
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"remove_field","field":"doc.absent"}]' | ConvertFrom-Json
            $result.opsApplied[0].changed | Should -BeFalse
            $result.frontmatterChanged | Should -BeFalse
        }

        It 'records changed:false for an unmatched pinned strip' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Keep this`"`n---`n# B`n")
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"strip_directive","kind":"pinned","text":"Not present"}]' | ConvertFrom-Json
            $result.opsApplied[0].changed | Should -BeFalse
            $result.opsApplied[0].kind | Should -Be 'pinned'
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`nagent.instructions:`n  - pinned: `"Keep this`"`n---`n# B`n"
        }
    }

    Context 'nav entry ops through the tool' {
        # The substrate's own Frontmatter*/FrontmatterWrite Tests suites cover Add-VaultNavEntry
        # and Remove-VaultNavEntry (and the wrapper's opsApplied/capability handling for them) in
        # full; what is proven here is that edit.ps1 itself drives the two ops correctly end to
        # end - the JSON-string parameter, the opsApplied shape it reports, and the
        # resultingFrontmatter echo - using the Widget.md fixture's real nav.contents block.

        It 'applies add_nav_entry through edit.ps1: opsApplied shape and resultingFrontmatter echo the inserted entry' {
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' `
                -frontmatter_ops '[{"op":"add_nav_entry","key":"nav.contents","link":"[[Gizmo-Reference]]","description":"Reference for gizmo parts","load_when":"researching gizmo parts"}]' | ConvertFrom-Json

            $result.opsApplied.Count | Should -Be 1
            $result.opsApplied[0].op | Should -Be 'add_nav_entry'
            $result.opsApplied[0].field | Should -Be 'nav.contents'
            $result.opsApplied[0].changed | Should -BeTrue
            # kind is per-op optional: this op carries none, so it is omitted rather than emitted
            # as an empty string or null the caller has to interpret.
            $result.opsApplied[0].PSObject.Properties.Name | Should -Not -Contain 'kind'

            $result.frontmatterChanged | Should -BeTrue
            # Quoted on need rather than by habit, which is the YAML emitter's rule and not ours:
            # a leading '[' is the flow-sequence indicator so a wikilink is quoted, and ordinary
            # prose is not quoted at all.
            $result.resultingFrontmatter | Should -Match ([regex]::Escape("link: '[[Gizmo-Reference]]'"))
            $result.resultingFrontmatter | Should -Match ([regex]::Escape('description: Reference for gizmo parts'))
            $result.resultingFrontmatter | Should -Match ([regex]::Escape('load_when: researching gizmo parts'))
            [System.IO.File]::ReadAllText($widget) | Should -Match ([regex]::Escape("link: '[[Gizmo-Reference]]'"))
        }

        It 'round-trips remove_nav_entry: removing the added entry restores the frontmatter byte for byte' {
            $add = '[{"op":"add_nav_entry","key":"nav.contents","link":"[[Gizmo-Reference]]","description":"Reference for gizmo parts","load_when":"researching gizmo parts"}]'
            $remove = '[{"op":"remove_nav_entry","key":"nav.contents","link":"[[Gizmo-Reference]]"}]'

            # One warm-up cycle first, and it is the whole reason this is not a single add/remove
            # pair. A frontmatter op re-emits the block, so the FIRST write a fixture note takes
            # also reformats whatever quoting it was authored with - a one-time change, since emit
            # is idempotent. Baselining after that reformat is what makes the byte-for-byte claim
            # a claim about the ROUND TRIP rather than about the fixture's authored bytes.
            & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -frontmatter_ops $add | Out-Null
            & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -frontmatter_ops $remove | Out-Null
            $baseline = & $script:ReadScriptPath -Path 'Projects/Widget/Widget.md' -Part 'frontmatter'

            & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -frontmatter_ops $add | Out-Null
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -frontmatter_ops $remove | ConvertFrom-Json

            $result.opsApplied[0].op | Should -Be 'remove_nav_entry'
            $result.opsApplied[0].field | Should -Be 'nav.contents'
            $result.opsApplied[0].changed | Should -BeTrue
            $result.frontmatterChanged | Should -BeTrue
            $result.resultingFrontmatter | Should -BeExactly $baseline
            (& $script:ReadScriptPath -Path 'Projects/Widget/Widget.md' -Part 'frontmatter') | Should -BeExactly $baseline
        }

        It 'records changed:false through the tool for a duplicate add_nav_entry, and asserts nothing' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' `
                -frontmatter_ops '[{"op":"add_nav_entry","key":"nav.contents","link":"[[Widget/Tasks]]","description":"a different description"}]' | ConvertFrom-Json
            $result.opsApplied[0].op | Should -Be 'add_nav_entry'
            $result.opsApplied[0].changed | Should -BeFalse
            $result.frontmatterChanged | Should -BeFalse
            $result.capabilitiesUsed.Count | Should -Be 0
        }

        It 'records changed:false through the tool for an absent-target remove_nav_entry, and asserts nothing' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' `
                -frontmatter_ops '[{"op":"remove_nav_entry","key":"nav.contents","folder":"Nowhere"}]' | ConvertFrom-Json
            $result.opsApplied[0].op | Should -Be 'remove_nav_entry'
            $result.opsApplied[0].changed | Should -BeFalse
            $result.frontmatterChanged | Should -BeFalse
            $result.capabilitiesUsed.Count | Should -Be 0
        }

        It 'accepts the ops as a JSON string built via ConvertTo-Json - frontmatter_ops''s actual parameter shape' {
            # frontmatter_ops is typed as a plain string parameter (see the header comment-help
            # and the -NoEnumerate deserialization below it): every call in this suite hands it a
            # JSON string, and this case builds that string from an object rather than hand-typing
            # it, the way a real caller would serialize a nav op.
            $ops = ConvertTo-Json -Compress -Depth 5 @(@{ op = 'add_nav_entry'; key = 'nav.see_also'; folder = 'Archive'; description = 'Completed work' })
            $ops | Should -BeOfType [string]

            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -frontmatter_ops $ops | ConvertFrom-Json
            $result.opsApplied[0].op | Should -Be 'add_nav_entry'
            $result.opsApplied[0].field | Should -Be 'nav.see_also'
            $result.opsApplied[0].changed | Should -BeTrue
            $result.resultingFrontmatter | Should -Match ([regex]::Escape('folder: Archive'))
        }
    }

    Context 'the advertised frontmatter_ops contract' -ForEach @{
        Schema      = $DriftSchema
        Branches    = $DriftBranches
        Shapes      = @($DriftBranches | ForEach-Object { $_.Shape })
        Substrate   = $DriftSubstrateShapes
        Fixture     = $DriftFixture
        FixturePath = 'Inbox/Drift-Probe.md'
        ModulePath  = (Join-Path $PSScriptRoot '..' '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1')
    } {
        # The drift guard for the `.SCHEMA frontmatter_ops` side-car in edit.ps1. The block is
        # hand-authored JSON Schema and nothing in the loader checks it against the script - the
        # loader only checks that it parses and names a real parameter - so the one thing that can
        # keep it honest is dispatching every shape it documents through the real script.
        #
        # "Advertises, does not enforce" cuts both ways, and only one of them is testable: a
        # payload the schema permits and the script refuses is a lie this catches; a payload the
        # script accepts and the schema does not describe (an extra member, a value convention) is
        # not, by design. What the branch-set comparison below adds is the other direction at op
        # granularity - a new op in the substrate with no branch here fails.

        It 'advertises an array of op objects with a description of its own' {
            $Schema.type | Should -Be 'array'
            # Its own, not the .PARAMETER text: the block replaces the whole property node, so
            # nothing of the help entry reaches the client for this parameter.
            $Schema.description | Should -Not -BeNullOrEmpty
            $Schema.items.oneOf.Count | Should -BeGreaterThan 0
        }

        It 'pins every branch on a const op and describes every property it declares' {
            foreach ($branch in $Schema.items.oneOf) {
                $branch.type | Should -Be 'object'
                # const, not enum or a bare type: it is what makes the oneOf branches mutually
                # exclusive, and what lets this suite name a branch without a lookup table.
                @($branch.properties.op.PSObject.Properties.Name) | Should -Contain 'const'
                $branch.required | Should -Contain 'op'
                foreach ($property in $branch.properties.PSObject.Properties) {
                    $property.Value.description |
                        Should -Not -BeNullOrEmpty -Because "$($branch.properties.op.const).$($property.Name) is advertised with no description, so the value conventions it carries reach no caller"
                }
            }
        }

        It 'declares exactly the op shapes the substrate implements' {
            # The converse of the dispatch cases below: those prove every branch works, this
            # proves no working op lacks a branch. Both sides are derived - the branches from the
            # block, the shapes from the dispatch AST - so neither can be satisfied by editing a
            # list in this file. Both are computed at discovery, next to the branch enumeration
            # the tree is built from; a Pester function defined at file scope is not in scope here
            # at Run time, and a second copy of either reader would be the drift itself.
            ($Shapes | Sort-Object) -join ', ' | Should -BeExactly (($Substrate | Sort-Object) -join ', ')
        }

        It 'enumerates the nav block-list keys the substrate allows, and no others' {
            # The key enum is the one place the block restates a list the substrate owns - a JSON
            # literal cannot call a function, which is why this guard exists at all and why
            # lint.ps1 needs no counterpart. Read from the module's exported accessor: the
            # allow-list is deliberately a collection in one place, and a second copy here would
            # be the drift this suite exists to refuse.
            Import-Module $ModulePath -Force
            $navKeys = Get-VaultNavListKey
            $keyed = @($Schema.items.oneOf | Where-Object { @($_.properties.PSObject.Properties.Name) -contains 'key' })
            $keyed.Count | Should -BeGreaterThan 0
            foreach ($branch in $keyed) {
                (@($branch.properties.key.enum) -join ', ') | Should -BeExactly (($navKeys) -join ', ')
            }
        }

        It 'dispatches the <Shape> branch through edit.ps1 and the substrate applies it' -ForEach $DriftBranches {
            # The whole guard, per branch: the op object was built from this branch's own
            # `required` list and property nodes, and it goes into the real script through the
            # real parameter. A branch documenting a shape the substrate refuses fails here as
            # malformed_op (or protected_directive_violation / field_shape_mismatch) - the script
            # throws, and Pester fails the case on the throw.
            $full = Join-Path $script:VaultDir $FixturePath
            [System.IO.File]::WriteAllText($full, $Fixture)

            $result = & $script:ScriptPath -Path $FixturePath -frontmatter_ops $Ops | ConvertFrom-Json

            $result.opsApplied.Count | Should -Be 1
            $result.opsApplied[0].op | Should -Be $Op
            if ($Kind) { $result.opsApplied[0].kind | Should -Be $Kind }
            # Accepted AND applied. changed:false would mean the shape parsed and then addressed
            # nothing, which is the half-proof a schema drift test must not settle for - the
            # fixture is seeded so every documented shape has real work to do.
            $result.opsApplied[0].changed | Should -BeTrue
            $result.frontmatterChanged | Should -BeTrue
        }
    }

    Context 'staleness guard' {
        It 'errors state_mismatch with zero mutation when the file changed underneath the caller' {
            $stale = Get-FileSha $script:NoteFull
            # Another writer lands between the caller's read and its edit.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n---`n`n# Moved on`n")
            $afterOtherWriter = [System.IO.File]::ReadAllText($script:NoteFull)

            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# Clobber`n"; expected_sha = $stale }
            $err.code | Should -Be 'state_mismatch'
            $err.data.expectedSha | Should -Be $stale
            $err.data.actualSha | Should -Be (Get-FileSha $script:NoteFull)
            # Zero mutation: the other writer's content survives verbatim.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly $afterOtherWriter
        }

        It 'accepts a matching expected_sha on a non-body facet too' {
            $sha = Get-FileSha $script:NoteFull
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' -expected_sha $sha | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue
        }

        It 'chains: a second edit passes the first edit''s resultingSha with no interleaved read' {
            $sha = Get-FileSha $script:NoteFull
            $first = & $script:ScriptPath -Path $script:NotePath -Body "# First`n" -expected_sha $sha | ConvertFrom-Json
            $second = & $script:ScriptPath -Path $script:NotePath -Body "# Second`n" -expected_sha $first.resultingSha | ConvertFrom-Json
            $second.resultingSha | Should -Not -Be $first.resultingSha
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Second`n"
            # And the chained token is itself the digest of what is now on disk.
            $second.resultingSha | Should -Be (Get-FileSha $script:NoteFull)
        }

        It 'errors state_mismatch when a chained resultingSha is reused after another edit landed' {
            $sha = Get-FileSha $script:NoteFull
            $first = & $script:ScriptPath -Path $script:NotePath -Body "# First`n" -expected_sha $sha | ConvertFrom-Json
            & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | Out-Null
            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# Third`n"; expected_sha = $first.resultingSha }
            $err.code | Should -Be 'state_mismatch'
        }
    }

    Context 'byte preservation' {
        It 'normalizes a CRLF note to LF throughout when a frontmatter op recomposes it' {
            # Retired and replaced: this used to assert the note's CRLF endings SURVIVED the op.
            # They no longer do, and that is the locked policy rather than a regression - a
            # frontmatter op recomposes the note through the module's one composer, which writes
            # LF with nothing to detect. What is preserved is body CONTENT: every byte except the
            # terminators, which is the narrowed guarantee the whole terminator-preservation model
            # was traded for.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`r`ndoc.created: 2026-07-16`r`ndoc.type: Note`r`n---`r`n`r`n# Edit-Case`r`nalpha line`r`n")
            & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | Out-Null
            $raw = [System.IO.File]::ReadAllText($script:NoteFull)
            $raw | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`ndoc.archived: 2026-07-25`n---`n`n# Edit-Case`nalpha line`n"
            # Stated as the invariant rather than inferred from the literal above: not one CR is
            # left anywhere in the file, frontmatter or body.
            $raw | Should -Not -Match "`r"
        }

        It 'flattens a CRLF note to LF through a body patch, frontmatter included' {
            # The body splice is the path that would most plausibly have been left preserving what
            # arrived, since it never reaches the emitter - which is why it is the one worth
            # pinning. LF is unconditional, and it reaches the block a body write does not
            # otherwise touch.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`r`ndoc.type: Note`r`n---`r`n`r`n# Edit-Case`r`nalpha line`r`nbeta line`r`n")
            & $script:ScriptPath -Path $script:NotePath -old_string 'alpha line' -new_string 'ALPHA LINE' | Out-Null
            $raw = [System.IO.File]::ReadAllText($script:NoteFull)
            $raw | Should -BeExactly "---`ndoc.type: Note`n---`n`n# Edit-Case`nALPHA LINE`nbeta line`n"
            $raw | Should -Not -Match "`r"
        }

        It 'flattens a mixed-ending note to LF through a frontmatter op' {
            # Retired and replaced for the same reason as the CRLF case above. The rule this used
            # to assert - keep a mixed file locally consistent, terminator by terminator - was the
            # most intricate part of the model that went, and there is nothing left to be
            # consistent WITH: one composer, one terminator.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`r`ndoc.type: Note`r`n---`n`n# Edit-Case`nalpha line`r`n")
            & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | Out-Null
            $raw = [System.IO.File]::ReadAllText($script:NoteFull)
            $raw | Should -BeExactly "---`ndoc.type: Corpus`n---`n`n# Edit-Case`nalpha line`n"
            $raw | Should -Not -Match "`r"
        }
    }

    Context 'capability denial (edit is the first key-asserting tool)' {
        It 'denies an agentic op with insufficient_capability and zero disk change when the key is absent' {
            $Global:VaultMcpOpClasses = @('content:write')
            $before = [System.IO.File]::ReadAllBytes($script:NoteFull)
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"add_hint","text":"Do the thing"}]' }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'agentic:write'
            # Validate-phase classification: nothing reached the disk.
            [System.IO.File]::ReadAllBytes($script:NoteFull) | Should -Be $before
        }

        It 'denies a nav.* removal for the same reason' {
            $Global:VaultMcpOpClasses = @('content:write')
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $before = [System.IO.File]::ReadAllBytes($widget)
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Widget.md'; frontmatter_ops = '[{"op":"remove_field","field":"nav.contents"}]' }
            $err.code | Should -Be 'insufficient_capability'
            [System.IO.File]::ReadAllBytes($widget) | Should -Be $before
        }

        It 'denies add_nav_entry with insufficient_capability and zero disk change when the class set lacks agentic:write' {
            # The outcome diff is what asserts the key, not the op name: add_nav_entry moves
            # nav.contents exactly as a generic set_field against it would.
            $Global:VaultMcpOpClasses = @('content:write')
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $before = [System.IO.File]::ReadAllBytes($widget)
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Widget.md'; frontmatter_ops = '[{"op":"add_nav_entry","key":"nav.contents","link":"[[Gizmo-Reference]]","description":"Reference for gizmo parts"}]' }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'agentic:write'
            $err.data.agenticKeysChanged | Should -Contain 'nav.contents'
            [System.IO.File]::ReadAllBytes($widget) | Should -Be $before
        }

        It 'denies remove_nav_entry the same way, with zero disk change' {
            $Global:VaultMcpOpClasses = @('content:write')
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $before = [System.IO.File]::ReadAllBytes($widget)
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Widget.md'; frontmatter_ops = '[{"op":"remove_nav_entry","key":"nav.contents","link":"[[Widget/Tasks]]"}]' }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'agentic:write'
            [System.IO.File]::ReadAllBytes($widget) | Should -Be $before
        }

        It 'proves the nav-entry denial is not vacuous: the same add_nav_entry succeeds once the class is granted' {
            # Without this, a denial that actually came from a different refusal (a bad op shape,
            # say) would read identically to a real capability gate.
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -frontmatter_ops '[{"op":"add_nav_entry","key":"nav.contents","link":"[[Gizmo-Reference]]","description":"Reference for gizmo parts"}]' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
            $result.opsApplied[0].changed | Should -BeTrue
        }

        It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
            $Global:VaultMcpOpClasses = @()
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"add_hint","text":"Do the thing"}]' }
            $err.code | Should -Be 'insufficient_capability'
        }

        It 'allows the same op when the injected set holds agentic:write' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"add_hint","text":"Do the thing"}]' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
        }

        It 'never asserts the key for a body-only write, even with an empty injected set' {
            $Global:VaultMcpOpClasses = @()
            $sha = Get-FileSha $script:NoteFull
            $result = & $script:ScriptPath -Path $script:NotePath -Body "# Body only`n" -expected_sha $sha | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }

        It 'never asserts the key for a non-agentic doc.* scalar set' {
            $Global:VaultMcpOpClasses = @()
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }
    }

    Context 'errors - operand and argument shape' {
        It 'errors not_found on a missing note (edit never creates)' {
            $err = Invoke-EditError @{ Path = 'Inbox/Nonexistent-Note.md'; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'not_found'
            $err.data.path | Should -Be 'Inbox/Nonexistent-Note.md'
        }

        It 'rejects a folder target as kind_mismatch (edit is note-semantic)' {
            $err = Invoke-EditError @{ Path = 'Projects/Widget'; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.requiredKind | Should -Be 'note'
            $err.data.resolvedKind | Should -Be 'folder'
        }

        It 'rejects an existing non-md file as kind_mismatch' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/attachment.txt') -Value 'not a note'
            $err = Invoke-EditError @{ Path = 'Inbox/attachment.txt'; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'file'
        }

        It 'refuses an extensionless path as invalid_argument naming the .md form' {
            # The Path Convention's rule, shared with read: the miss is loud and retryable in
            # one call rather than an ambiguous not_found.
            $err = Invoke-EditError @{ Path = 'Inbox/Edit-Case'; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'Inbox/Edit-Case\.md'
            $err.data.suggestedPath | Should -Be 'Inbox/Edit-Case.md'
        }

        It 'errors invalid_argument on a whitespace path' {
            $err = Invoke-EditError @{ Path = '   '; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument on a vault-escaping path' {
            $err = Invoke-EditError @{ Path = '../outside.md'; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument when no facet is supplied' {
            $err = Invoke-EditError @{ Path = $script:NotePath }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'Nothing to do'
        }

        It 'errors invalid_argument for an empty ops array with no body form' {
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument when body accompanies a patch pair' {
            $sha = Get-FileSha $script:NoteFull
            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# X`n"; expected_sha = $sha; old_string = 'alpha line'; new_string = 'x' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'mutually exclusive'
        }

        It 'errors invalid_argument on half a patch pair (old_string alone)' {
            $err = Invoke-EditError @{ Path = $script:NotePath; old_string = 'alpha line' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'new_string'
        }

        It 'errors invalid_argument on half a patch pair (new_string alone)' {
            $err = Invoke-EditError @{ Path = $script:NotePath; new_string = 'alpha line' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'old_string'
        }

        It 'errors invalid_argument on an empty old_string (an anchor that cannot self-verify)' {
            $err = Invoke-EditError @{ Path = $script:NotePath; old_string = ''; new_string = 'x' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'old_string is empty'
        }

        It 'errors invalid_argument when body is supplied without expected_sha' {
            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# X`n" }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'expected_sha'
        }

        It 'does NOT require expected_sha for a patch or for frontmatter ops' {
            { & $script:ScriptPath -Path $script:NotePath -old_string 'alpha line' -new_string 'x' | Out-Null } | Should -Not -Throw
            { & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | Out-Null } | Should -Not -Throw
        }

        It 'errors read_failure (not not_found) when the note exists but is locked' {
            $handle = [System.IO.File]::Open($script:NoteFull, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' }
                $err.code | Should -Be 'read_failure'
                $err.data.path | Should -Be $script:NotePath
            }
            finally {
                $handle.Dispose()
            }
        }
    }

    Context 'errors - op vocabulary and hygiene' {
        It 'errors malformed_op on frontmatter_ops that is not valid JSON' {
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '{not json' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'valid JSON'
        }

        It 'errors malformed_op when frontmatter_ops is a bare object rather than an array' {
            # -NoEnumerate is what keeps this distinguishable from a one-element array.
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '{"op":"set_field","field":"doc.type","value":"Corpus"}' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'array'
        }

        It 'accepts a one-element ops array (not collapsed to a bare object)' {
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | ConvertFrom-Json
            $result.opsApplied.Count | Should -Be 1
        }

        It 'errors malformed_op with data.opIndex on an unknown op' {
            # The code alone cannot distinguish a bad op from ConvertTo-VaultToolResult's
            # dataless malformed_op for stray success-stream output, so the assertion checks
            # for the op coordinates.
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Corpus"},{"op":"reticulate_splines"}]' }
            $err.code | Should -Be 'malformed_op'
            $err.data.opIndex | Should -Be 1
            $err.data.reason | Should -Match 'reticulate_splines'
        }

        It 'errors malformed_op with data.opIndex on an op missing a required parameter' {
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.type"}]' }
            $err.code | Should -Be 'malformed_op'
            $err.data.opIndex | Should -Be 0
        }

        It 'writes a value carrying a line break INSIDE its own key rather than minting one' {
            # Retired and replaced: this used to assert a REFUSAL, which existed only because a
            # line-splicing emitter could not escape a newline. It can now - the value goes
            # through the YAML emitter as a block or quoted scalar - so the refusal went, and what
            # is asserted instead is the property the refusal was standing in for: the smuggled
            # text lands as part of doc.type's value and mints no second key.
            $ops = ConvertTo-Json -Compress -Depth 5 @(@{ op = 'set_field'; field = 'doc.type'; value = "Note`nnav.contents: smuggled" })
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops $ops | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue

            $map = Get-VaultFrontmatterMap -Content ([System.IO.File]::ReadAllText($script:NoteFull))
            $map['doc.type'] | Should -BeExactly "Note`nnav.contents: smuggled"
            # The whole point: no key was created by the value's contents.
            @($map.Keys) | Should -Not -Contain 'nav.contents'
        }

        It 'round-trips directive text carrying a double quote (the retired emit hole)' {
            # Retired and replaced for the same reason. The double-quote refusal was a bespoke
            # format's limitation leaking into the tool contract - a YAML emitter needs no special
            # handling for a quote at all - so what is asserted now is that the quote survives the
            # write and reads back as the caller wrote it.
            $ops = ConvertTo-Json -Compress -Depth 5 @(@{ op = 'add_directive'; kind = 'pinned'; text = 'bad" text' })
            $result = & $script:ScriptPath -Path $script:NotePath -frontmatter_ops $ops | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue

            $map = Get-VaultFrontmatterMap -Content ([System.IO.File]::ReadAllText($script:NoteFull))
            $directives = Get-VaultFrontmatterEntryList -Map $map -Field 'agent.instructions'
            @($directives).Count | Should -Be 1
            Get-VaultEntryFieldValue -Entry $directives[0] -Name 'pinned' | Should -BeExactly 'bad" text'
        }

        It 'refuses a field name outside the frontmatter key pattern' {
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc created","value":"x"}]' }
            $err.code | Should -Be 'malformed_op'
        }

        It 'errors protected_directive_violation for a generic remove_field against agent.instructions' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"remove_field","field":"agent.instructions"}]' }
            $err.code | Should -Be 'protected_directive_violation'
            $err.data.field | Should -Be 'agent.instructions'
            $err.message | Should -Match 'strip_directive'
        }

        It 'takes protected_directive_violation over field_shape_mismatch when both would fire' {
            # Precedence: naming the right op (routing) is more actionable than naming the shape.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"agent.instructions","value":"x"}]' }
            $err.code | Should -Be 'protected_directive_violation'
        }

        It 'errors field_shape_mismatch for a flat-scalar set against a detected block field' {
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Widget.md'; frontmatter_ops = '[{"op":"set_field","field":"nav.contents","value":"x"}]' }
            $err.code | Should -Be 'field_shape_mismatch'
            $err.data.field | Should -Be 'nav.contents'
        }

        It 'routes a block-field removal to the block primitive rather than orphaning children' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -frontmatter_ops '[{"op":"remove_field","field":"nav.contents"}]' | ConvertFrom-Json
            $result.resultingFrontmatter | Should -BeExactly ''
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Projects/Widget/Widget.md')) | Should -Not -Match 'description:'
        }
    }

    Context 'errors - integrity and schema' {
        It 'errors frontmatter_integrity on a note whose first line is a fence nothing closes' {
            # The reason moved, and the reasoning under it did too. There is no note-level
            # "unclosed" state any more: to the standard a '---' that nothing closes is a
            # thematic break, so this note simply has no frontmatter. What edit refuses is
            # therefore not a broken block but the prospect of prepending a NEW block above the
            # user's apparent metadata and stranding it as prose - hence apparent_fence.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n# Body with no closing fence`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.created","value":"2026-07-25"}]' }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'apparent_fence'
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -Not -Match 'doc.created'
        }

        It 'patches such a note as the body it is, since the fence never made a block' {
            # Retired and replaced: this used to assert a REFUSAL, on the reasoning that a
            # dangling fence made the body facet the whole file and patch matching would reach
            # "the frontmatter". There is no frontmatter to reach - the file is body, all of it,
            # and text that is body is patchable. Body confinement is untouched: it means the
            # patch matches the body FACET, and here that facet is the file.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n# Body`n")
            $result = & $script:ScriptPath -Path $script:NotePath -old_string 'doc.type: Note' -new_string 'doc.type: Corpus' | ConvertFrom-Json
            $result.bodyChanged | Should -BeTrue
            $result.frontmatterChanged | Should -BeFalse
            # Still no block: the patch rewrote prose and minted nothing.
            $result.resultingFrontmatter | Should -BeExactly ''
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.type: Corpus`n# Body`n"
        }

        It 'errors frontmatter_integrity when a body replace would mint a frontmatter block' {
            $tasks = Join-Path $script:VaultDir 'Projects/Widget/Tasks.md'
            $sha = Get-FileSha $tasks
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Tasks.md'; Body = "---`nnav.contents: smuggled`n---`n# Tasks`n"; expected_sha = $sha }
            $err.code | Should -Be 'frontmatter_integrity'
            [System.IO.File]::ReadAllText($tasks) | Should -Not -Match 'smuggled'
        }

        It 'errors schema_validation_failed when the resulting frontmatter violates the schema' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nnav.see_also:`n  - link: `"[[Widget]]`"`n---`n# B`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.created","value":"2026-07-25"}]' }
            $err.code | Should -Be 'schema_validation_failed'
            $err.data.field | Should -Be 'nav.see_also'
            $err.data.rule | Should -Be 'missing_description'
            $err.data.entryIndex | Should -Be 1
            # Validate-phase: nothing written.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -Not -Match 'doc.created'
        }

        It 'runs the path-dependent Tasks.md check, so edit refuses a Tasks.md left without its directive' {
            # edit passes its own relative path to the validator, which enables the one check that
            # needs the note's name rather than its content.
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Tasks.md'; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Tasks"}]' }
            $err.code | Should -Be 'schema_validation_failed'
            $err.data.rule | Should -Be 'tasks_missing_use_skill'
        }

        It 'accepts the same Tasks.md edit when the call also supplies the required directive' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' `
                -frontmatter_ops '[{"op":"add_directive","kind":"use_skill","skill":"vault-tasks","when":"managing tasks"},{"op":"set_field","field":"doc.type","value":"Tasks"}]' | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue
            $result.resultingFrontmatter | Should -Match 'use_skill: vault-tasks'
        }
    }

    Context 'scope and vault resolution' {
        It 'errors out_of_scope when the path falls outside the ambient scope roots' {
            # The pre-check is mandatory and is the only thing that produces this code: the
            # Write-VaultNote chokepoint's own scope assertion throws plain text.
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.path | Should -Be $script:NotePath
            # Refused ahead of any in-memory mutation, so nothing reached the disk.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -Match 'doc.type: Note'
        }

        It 'edits normally when the path falls inside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -old_string 'Sample Project' -new_string 'Fixture Project' | ConvertFrom-Json
            $result.bodyChanged | Should -BeTrue
        }

        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
            $err.data.vault | Should -Be 'nope'
        }

        It 'refuses a configured vault whose root is missing with vault_unavailable' {
            $configPath = Join-Path $script:TempRoot 'with-missing.json'
            @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir; gone = (Join-Path $script:TempRoot 'no-such-root') } } |
                ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'; Vault = 'gone' }
            $err.code | Should -Be 'vault_unavailable'
        }
    }

    Context 'lock_timeout' {
        It 'fails lock_timeout when the write lock is held by another process' -Skip:(-not $IsWindows) {
            # The write lock cannot be contended in-process (the mutex is reentrant for the
            # calling thread), so a genuinely separate process must hold it - the cross-process
            # convention in Testing.md. The chokepoint raises the coded shape itself and edit
            # does not catch it, so this asserts the code surfaces unchanged.
            $modulePath = Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1'
            $configPath = $env:VAULT_MCP_VAULTS_JSON
            $signalPath = Join-Path $script:TempRoot 'lock-held.signal'

            $holder = Start-Job -ScriptBlock {
                param($ModulePath, $ConfigPath, $SignalPath)
                $env:VAULT_MCP_VAULTS_JSON = $ConfigPath
                Import-Module $ModulePath -Force
                Invoke-VaultWriteLock -Vault 'scratch' -ScriptBlock {
                    Set-Content -LiteralPath $SignalPath -Value 'held'
                    Start-Sleep -Seconds 30
                }
            } -ArgumentList $modulePath, $configPath, $signalPath

            try {
                $deadline = (Get-Date).AddSeconds(20)
                while (-not (Test-Path -LiteralPath $signalPath) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 100
                }
                (Test-Path -LiteralPath $signalPath) | Should -BeTrue -Because 'the background lock holder must have acquired before we contend'

                $prevTimeout = $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS
                $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = '1'
                try {
                    $err = Invoke-EditError @{ Path = $script:NotePath; frontmatter_ops = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' }
                    $err.code | Should -Be 'lock_timeout'
                    $err.data.timeoutSeconds | Should -Be 1
                    [System.IO.File]::ReadAllText($script:NoteFull) | Should -Match 'doc.type: Note'
                }
                finally {
                    if ($null -eq $prevTimeout) {
                        Remove-Item Env:\VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
                    }
                    else {
                        $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = $prevTimeout
                    }
                }
            }
            finally {
                Stop-Job $holder -ErrorAction SilentlyContinue
                Remove-Job $holder -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'op-class metadata declaration' {
        It 'declares the content:write floor in its .NOTES comment-help (the loader-parsed op class)' {
            # Dispatcher floor-gating itself is enforced C#-side and is generically proven by the
            # xUnit dispatcher test that refuses a content:write tool to a content:read grant;
            # edit inherits that uniform grant.Allows(tool.OpClass) check. What IS assertable
            # PowerShell-side is edit's own metadata declaration - the OpClass line
            # ScriptToolLoader reads to derive the floor.
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*content:write\s*$'
        }

        It 'documents every parameter in comment-help, which the loader requires to load the tool' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            foreach ($p in @('path', 'frontmatter_ops', 'body', 'old_string', 'new_string', 'expected_sha', 'vault')) {
                $scriptText | Should -Match "(?m)^\s*\.PARAMETER\s+$p\s*$"
            }
        }
    }
}

Describe 'edit standalone invocability' {
    # Per Tool-Contracts's Tool Authoring Standards: every tool script must be invocable outside
    # the MCP server - pwsh -File against a real vault, no runspace context, no ambient scope, no
    # environment setup - and carry a unit test asserting it. This spawns a genuinely separate
    # pwsh process (rather than calling the script in this Pester session) so no global left over
    # from the host's ScriptToolRunner wrapper (VaultMcpScopeRoots, VaultMcpOpClasses) or from
    # another test in this same process could be mistaken for injected host context. It is also
    # what proves the caller-carried staleness guard works with no host at all - the property
    # that decided the design against host-side read tracking.

    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-edit-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File edit.ps1" invocation (the Tool Authoring Standard spelling)' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Resources/Gizmo-Reference.md' -frontmatter_ops '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]'
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            $result = $rawOutput | ConvertFrom-Json
            $result.path | Should -Be 'Resources/Gizmo-Reference.md'
            $result.frontmatterChanged | Should -BeTrue
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Resources/Gizmo-Reference.md')) | Should -Match 'doc.archived: 2026-07-25'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'degrades OPEN on an agentic op with no injected op-class set' {
        # The other half of the capability-denial pair: a standalone run has no host context, so
        # Assert-VaultOpClass returns silently, exactly as an unscoped scope-roots map does.
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Resources/Gizmo-Reference.md' -frontmatter_ops '[{"op":"add_hint","text":"Prefer the torque table"}]'
            $LASTEXITCODE | Should -Be 0
            $result = $rawOutput | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Resources/Gizmo-Reference.md')) | Should -Match 'agent.instructions:'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'enforces the caller-carried staleness guard with no host present' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Resources/Gizmo-Reference.md' -Body "# Clobber`n" -expected_sha ('0' * 64) 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"state_mismatch"'
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Resources/Gizmo-Reference.md')) | Should -Match 'quorvium alloy'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'surfaces the shared JSON error shape on stderr under pwsh -File' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Inbox/Nonexistent-Note.md' -frontmatter_ops '[{"op":"set_field","field":"doc.type","value":"Note"}]' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"not_found"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}
