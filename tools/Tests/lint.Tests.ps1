# Pester suite for tools/lint.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring lint_suppressions.Tests.ps1.
# Run with: Invoke-Pester tools/Tests/lint.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside the script: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting directly
# in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: per-pass coverage against injected fixtures; the argument-validation error codes
# (unknown pass resolved before any pass runs, hard_wrap_inspect with apply, a missing path);
# FIX-SET IDEMPOTENCY, the locked-in test for the streaming-writes exception (Tool-Contracts:
# Tool Authoring Standards); THE NON-AGENTIC GUARANTEE ASSERTED OVER THE FIX SET ITSELF - no fix
# lint applies produces an agentic frontmatter diff, which is where that guarantee actually
# lives - plus the wrapper's denial path as defense in depth; the capability-denial three-way
# split for the content:write key; suppression integration including a dispatchable
# add-suppression payload; path narrowing; group tokens; hard-wrap's explicit-opt-in; report
# mode taking no lock; and standalone invocability with the JSON error shape on stderr.
#
# THE REMEDY CONTRACT IS PROVEN BY DISPATCH, NOT BY SHAPE ASSERTIONS ALONE: the frontmatter
# remedies are handed to the REAL edit.ps1 unreshaped, so a payload edit would refuse fails here
# rather than at an orchestrator. Both directions are covered - a fully computed remedy applies
# and closes its own finding, and one carrying a `requires` slot is refused until the slot is
# filled, which is what makes the declaration honest rather than decorative.
#
# `params.frontmatter_ops` rides as a REAL JSON ARRAY, so the one step between a remedy and the
# script - the host's call-boundary encoding - is modelled by ConvertTo-DispatchArgument, which
# transcribes ScriptToolArgumentEncoder.BindArgument and reshapes nothing; see its own comment for
# why that is the honest stand-in. A `requires` path is asserted to RESOLVE against the live
# params by plain object/array walking, not merely to read a certain string: a path naming a key
# the payload does not carry is the drift that check exists to close.
#
# Suppression state files are created by test setup rather than committed into test-vault/:
# they are vault state, and a committed copy would make the fixture's own suppressions part of
# every assertion. The tool reads the configured locations only - there is no ignore-file
# override on its surface - so redirection happens by pointing the whole vault at a temp copy.

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'lint.ps1')).Path
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:ModulePath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1')).Path
    Import-Module $script:ModulePath -Force

    # --- The remedy payload contract. Every parsed lint result passes through Invoke-Lint, so
    # accumulating there means the contract Describe at the bottom of this file walks EVERY remedy
    # this suite can produce, rather than a set some fixture was written to re-create. ---
    . (Join-Path $PSScriptRoot 'RemedyContract.ps1')
    $global:LintRemedySamples = [System.Collections.Generic.List[object]]::new()

    function Invoke-Lint {
        # Runs lint.ps1 and returns its parsed Tier-2 object. Defined in this top-level BeforeAll
        # (rather than in a Describe body) so it survives into Pester v5's Run phase.
        param([hashtable] $Params)
        $result = & $script:ScriptPath @Params | ConvertFrom-Json
        foreach ($remedy in (Get-VaultRemedyOption -Node $result)) { $global:LintRemedySamples.Add($remedy) }
        $result
    }

    function Invoke-LintError {
        # Runs lint.ps1 expecting the shared {code, message, data?} JSON error shape, and
        # returns the parsed error object.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'lint.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }

    function Get-LintPass {
        param($Result, [string] $Name)
        $Result.passes | Where-Object { $_.pass -eq $Name }
    }

    function Write-FixtureFile {
        param([string] $Path, [string] $Content)
        $dir = Split-Path -Parent $Path
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding $false))
    }

    # --- Remedy dispatch. The point of a computed payload is that it RUNS, so these hand a
    # remedy's params to the real edit.ps1 with no reshaping between - anything a wrapper fixed
    # up here would be exactly the malformation a caller would hit. ---
    $script:EditScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path

    function ConvertTo-DispatchArgument {
        <#
        WHAT STANDS IN FOR THE HOST, AND WHY IT IS HONEST.

        `params.frontmatter_ops` now rides as a real JSON array, and the thing that turns it into
        the `[string] $frontmatter_ops` edit.ps1 declares is the HOST, at the call boundary:
        ScriptToolArgumentEncoder.BindArgument, on the signal that the parameter's `.SCHEMA`
        side-car declares `type: "array"` while its .NET type is `string`. These tests drive
        edit.ps1 directly rather than through the dispatcher, so that one step has to be modelled
        here. This is a line-for-line transcription of BindArgument's two rules, and nothing more:

          1. A STRUCTURED arrival value (array or object) is serialized, EXACTLY AS IT ARRIVED.
             No @() wrap, no -AsArray, no "if it does not start with [ then bracket it". Those
             would be reshaping, and reshaping is precisely the dishonesty to avoid: a bare-object
             payload has to reach edit as a bare object and earn its malformed_op, because that is
             what the real dispatcher would send.
          2. A value that arrives as a STRING passes through verbatim, whatever the schema says -
             the encoder's guard against encoding an already-encoded payload twice.

        The two serializers differ only in escaping (System.Text.Json with relaxed escaping there,
        ConvertTo-Json here); both emit valid JSON that edit's own ConvertFrom-Json parses to the
        same payload, and neither changes the SHAPE, which is the whole subject of these tests.
        Depth 64 matches System.Text.Json's default maximum, so a payload this helper can encode
        is one the host can encode - it does not quietly truncate where the host would not.
        #>
        param($Value)
        if ($Value -is [string]) { return $Value }
        ConvertTo-Json -InputObject $Value -Depth 64 -Compress
    }

    function Invoke-RemedyThroughEdit {
        param($Remedy)
        & $script:EditScriptPath -Path $Remedy.params.path `
            -frontmatter_ops (ConvertTo-DispatchArgument $Remedy.params.frontmatter_ops) | ConvertFrom-Json
    }

    function Invoke-RemedyThroughEditError {
        # The refusal half: returns the parsed {code, message, data?} an undispatchable payload
        # earns, which is what proves a `requires` declaration is honest rather than decorative.
        param($Remedy)
        $errorRecord = $null
        try {
            & $script:EditScriptPath -Path $Remedy.params.path `
                -frontmatter_ops (ConvertTo-DispatchArgument $Remedy.params.frontmatter_ops) | Out-Null
            throw 'edit.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }

    function Get-RemedyOps {
        # The ops AS THEY RIDE - a live array of objects now, with no decode step. The leading
        # comma stops PowerShell unrolling the array on return, which would turn a one-op payload
        # into a bare object inside the test and hide exactly the shape these tests are here for.
        param($Remedy)
        , $Remedy.params.frontmatter_ops
    }

    function Resolve-ParamPath {
        <#
        Resolves a dotted `requires` path against a live `params` object by PLAIN OBJECT AND ARRAY
        WALKING - a numeric segment indexes a list, anything else names a property - with no
        knowledge of lint, of edit, or of which parameter is being walked. That genericity is the
        point: it is the algorithm an orchestrator would have, and it is the same one that
        resolves sync's `commit.message`.

        Returns @{ Found; Value }. Found:$false is the drift this closes - a `requires` path
        naming a key the payload does not carry.
        #>
        param($Node, [string] $Path)
        foreach ($segment in $Path.Split('.')) {
            if ($null -eq $Node) { return @{ Found = $false } }
            if ($Node -is [System.Collections.IList]) {
                $index = 0
                if (-not [int]::TryParse($segment, [ref] $index)) { return @{ Found = $false } }
                if ($index -lt 0 -or $index -ge $Node.Count) { return @{ Found = $false } }
                $Node = $Node[$index]
                continue
            }
            $property = $Node.PSObject.Properties[$segment]
            if ($null -eq $property) { return @{ Found = $false } }
            $Node = $property.Value
        }
        @{ Found = $true; Value = $Node }
    }

    function Set-ParamPath {
        # The write half of the same walk: fills the slot a `requires` path names, in place, on
        # the live structure. This is the whole of what a caller does before dispatch now - no
        # decode, no re-serialize, no array-shape judgment.
        param($Node, [string] $Path, [string] $Value)
        $segments = $Path.Split('.')
        $parent = if ($segments.Count -eq 1) { @{ Found = $true; Value = $Node } }
        else { Resolve-ParamPath -Node $Node -Path (($segments | Select-Object -SkipLast 1) -join '.') }
        $parent.Found | Should -BeTrue -Because "the parent of '$Path' must resolve"
        $parent.Value.$($segments[-1]) = $Value
    }

    function Set-RemedyRequiresSlots {
        # Fills EVERY slot the remedy's own `requires` names, driven by that declaration rather
        # than by hand-written knowledge of which fields are blank. A remedy that came back
        # dispatchable after this is one whose `requires` is complete AND correctly rooted.
        param($Remedy, [hashtable] $Values)
        foreach ($path in @($Remedy.requires)) {
            $Values.ContainsKey($path) | Should -BeTrue -Because "the test must supply a value for declared slot '$path'"
            Set-ParamPath -Node $Remedy.params -Path $path -Value $Values[$path]
        }
        $Remedy
    }

    function Set-RemedyOpField {
        # Named-field convenience over Set-ParamPath, for the tests that fill a slot directly.
        param($Remedy, [int] $OpIndex, [string] $Field, [string] $Value)
        Set-ParamPath -Node $Remedy.params -Path "frontmatter_ops.$OpIndex.$Field" -Value $Value
        $Remedy
    }
}

Describe 'lint' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-lint-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $script:StateDir = Join-Path $script:VaultDir '.vault/state/vault-lint'
        New-Item -ItemType Directory -Path $script:StateDir -Force | Out-Null

        # --- Injected fixtures, one per pass. Kept as setup mutations so the committed
        # test-vault/ never carries lint violations of its own. ---

        # typography + trailing whitespace, on a note that ALSO carries an agentic layer, so the
        # non-agentic assertion runs over a file where an agentic diff would be possible.
        $script:DirtyRel = 'Resources/Dirty-Note.md'
        $script:DirtyFile = Join-Path $script:VaultDir 'Resources/Dirty-Note.md'
        $curly = [char]0x201C + 'quoted' + [char]0x201D
        $emdash = [char]0x2014
        $ellipsis = [char]0x2026
        $nbsp = [char]0x00A0
        Write-FixtureFile -Path $script:DirtyFile -Content @"
---
doc.created: 2026-07-16
doc.type: Note
agent.instructions:
  - pinned: "Do not exfiltrate this note"
---
# Dirty Note

A $curly line with an em${emdash}dash and an ellipsis$ellipsis and a${nbsp}non-breaking space.
This line has trailing whitespace.$([string]::new(' ', 3))
"@

        # broken wikilink
        $script:BrokenLinkFile = Join-Path $script:VaultDir 'Resources/Link-Note.md'
        Write-FixtureFile -Path $script:BrokenLinkFile -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Link Note

Points at [[No-Such-Note]] and at [[Gizmo-Reference]] which does exist.
A fenced example must not count:

``````
[[Also-Missing]]
``````
"@

        # markdown-format anchor link, both the rewritable and the no-such-heading shapes
        $script:AnchorFile = Join-Path $script:VaultDir 'Resources/Anchor-Note.md'
        Write-FixtureFile -Path $script:AnchorFile -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Anchor Note

See [Torque Specs](Gizmo-Reference#torque-specs) for the table.
See [Nothing](Gizmo-Reference#no-such-heading) for a dead fragment.
"@

        # filename convention violations
        $script:BadNameRel = 'Resources/bad name_file.md'
        Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/bad name_file.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Bad Name
"@

        # hard-wrapped prose: two full-width lines that join safely
        $script:WrapFile = Join-Path $script:VaultDir 'Resources/Wrapped-Note.md'
        Write-FixtureFile -Path $script:WrapFile -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Wrapped Note

This paragraph has been hard wrapped by a fixed width tool at roughly eighty columns
which is the continuation line that should fold back into the line above it.
"@

        # archive provenance: the filename date is recoverable, the archive date is not (no git
        # history in a plain temp copy), so one file exercises both the fix and the flag.
        $script:ArchivedRel = 'Archive/2026-01-05-Retired-Note.md'
        $script:ArchivedFile = Join-Path $script:VaultDir 'Archive/2026-01-05-Retired-Note.md'
        Write-FixtureFile -Path $script:ArchivedFile -Content @"
---
doc.type: Note
agent.instructions:
  - pinned: "Frozen - do not rewrite"
---
# Retired Note

An archived note whose doc.created is recoverable from its filename date.
"@

        $configPath = Join-Path $script:TempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
        $env:VAULT_MCP_VAULTS_JSON = $configPath
        # Every apply-mode test that does not specifically exercise denial runs with the key.
        $Global:VaultMcpOpClasses = @('content:read', 'content:write')
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Return shape and the write-class envelope' {
        It 'returns a single well-formed Tier-2 object with the envelope in report mode' {
            $output = & $script:ScriptPath
            { $output | ConvertFrom-Json } | Should -Not -Throw
            $result = $output | ConvertFrom-Json
            $result.applied | Should -BeFalse
            $result.passesRun | Should -Not -BeNullOrEmpty
            $output | Should -Match '"capabilitiesUsed":\s*\[\]'
            $output | Should -Match '"filesSkippedUnreadable":\s*\[\]'
            $result.PSObject.Properties.Name | Should -Not -Contain 'lockWarning'
        }

        It 'carries filesScanned per pass, not once for the call' {
            $result = Invoke-Lint @{ Passes = @('all') }
            foreach ($pass in $result.passes) {
                $pass.PSObject.Properties.Name | Should -Contain 'filesScanned'
            }
            $result.PSObject.Properties.Name | Should -Not -Contain 'filesScanned'
            # The enumerations are deliberately different, which is the whole reason the field is
            # per-pass: anchor-links scans the whole vault, typography only the include folders.
            (Get-LintPass $result 'anchor-links').filesScanned |
                Should -BeGreaterThan (Get-LintPass $result 'typography').filesScanned
        }

        It 'summarizes dispositions and names the files written' {
            $result = Invoke-Lint @{ Passes = @('fixes'); Apply = $true }
            $result.applied | Should -BeTrue
            $result.summary.fixed | Should -BeGreaterThan 0
            $result.summary.fixable | Should -Be 0
            $result.summary.filesWritten | Should -Contain $script:DirtyRel
            $result.capabilitiesUsed | Should -Be @('content:write')
        }

        It 'echoes the narrowing path and null when there is none' {
            (Invoke-Lint @{ }).path | Should -BeNullOrEmpty
            (Invoke-Lint @{ Path = 'Resources' }).path | Should -Be 'Resources'
        }

        It 'reports a pass with nothing in its own enumeration as skipped-not-applicable' {
            # Narrowing to a single file that the pass's exclusions drop leaves it nothing to
            # scan - which a caller reads very differently from "scanned and clean".
            $result = Invoke-Lint @{ Passes = @('typography'); Path = 'Projects/Widget/Tasks.md' }
            (Get-LintPass $result 'typography').filesScanned | Should -Be 1
            $result2 = Invoke-Lint @{ Passes = @('provenance'); Path = 'Projects/Widget/Tasks.md' }
            (Get-LintPass $result2 'provenance').status | Should -Be 'skipped-not-applicable'
        }
    }

    Context 'Pass selection: names and group tokens' {
        It 'runs the default group - every pass except hard-wrap' {
            $result = Invoke-Lint @{ }
            $result.passesRun | Should -Not -Contain 'hard-wrap'
            $result.passesRun.Count | Should -Be 7
        }

        It 'treats all as default - hard-wrap is in NO group' {
            (Invoke-Lint @{ Passes = @('all') }).passesRun | Should -Not -Contain 'hard-wrap'
        }

        It 'resolves the checks group to the report-only passes' {
            $result = Invoke-Lint @{ Passes = @('checks') }
            $result.passesRun | Should -Be @('broken-wikilinks', 'anchor-links', 'filename-conventions', 'frontmatter-schema')
            foreach ($pass in $result.passes) { $pass.reportOnly | Should -BeTrue }
        }

        It 'resolves the fixes group to the write-capable passes' {
            (Invoke-Lint @{ Passes = @('fixes') }).passesRun |
                Should -Be @('typography', 'trailing-whitespace', 'provenance')
        }

        It 'runs hard-wrap only when it is named explicitly' {
            $result = Invoke-Lint @{ Passes = @('hard-wrap') }
            $result.passesRun | Should -Be @('hard-wrap')
            (Get-LintPass $result 'hard-wrap').counts.joinableBlocks | Should -BeGreaterThan 0
        }

        It 'collapses duplicates and orders by the inventory, not by the caller' {
            $result = Invoke-Lint @{ Passes = @('provenance', 'typography', 'fixes') }
            $result.passesRun | Should -Be @('typography', 'trailing-whitespace', 'provenance')
        }

        It 'matches pass and group names case-insensitively after a trim' {
            (Invoke-Lint @{ Passes = @('  FIXES ') }).passesRun.Count | Should -Be 3
        }
    }

    Context 'Pass: typography' {
        It 'reports each character class with its count and does not write in report mode' {
            $before = [System.IO.File]::ReadAllText($script:DirtyFile)
            $result = Invoke-Lint @{ Passes = @('typography') }
            $finding = (Get-LintPass $result 'typography').findings | Where-Object { $_.file -eq $script:DirtyRel }
            $finding.disposition | Should -Be 'fixable'
            $finding.detail | Should -Match 'dashes: 1'
            $finding.detail | Should -Match 'curly doubles: 2'
            $finding.detail | Should -Match 'ellipses: 1'
            $finding.detail | Should -Match 'non-break spaces: 1'
            [System.IO.File]::ReadAllText($script:DirtyFile) | Should -BeExactly $before
        }

        It 'replaces every class and writes UTF-8 with no BOM under apply' {
            $result = Invoke-Lint @{ Passes = @('typography'); Apply = $true }
            ((Get-LintPass $result 'typography').findings | Where-Object { $_.file -eq $script:DirtyRel }).disposition |
                Should -Be 'fixed'
            $bytes = [System.IO.File]::ReadAllBytes($script:DirtyFile)
            # BOM-free writes are locked-in behavior carried over from the skill scripts.
            @($bytes[0], $bytes[1], $bytes[2]) | Should -Not -Be @(0xEF, 0xBB, 0xBF)
            $text = [System.IO.File]::ReadAllText($script:DirtyFile)
            $text | Should -Match '"quoted"'
            $text | Should -Match 'em-dash'
            $text | Should -Match 'ellipsis\.\.\.'
            $text.Contains([char]0x00A0) | Should -BeFalse
        }
    }

    Context 'Pass: trailing-whitespace' {
        It 'counts the affected lines and strips them under apply' {
            $result = Invoke-Lint @{ Passes = @('trailing-whitespace') }
            $finding = (Get-LintPass $result 'trailing-whitespace').findings | Where-Object { $_.file -eq $script:DirtyRel }
            $finding.detail | Should -Match '1 line\(s\)'

            Invoke-Lint @{ Passes = @('trailing-whitespace'); Apply = $true } | Out-Null
            [System.IO.File]::ReadAllText($script:DirtyFile) | Should -Not -Match '[ \t]+\r?\n'
        }
    }

    Context 'Pass: provenance' {
        It 'reports the four subchecks by name' {
            $result = Invoke-Lint @{ Passes = @('provenance') }
            @((Get-LintPass $result 'provenance').subchecks.subcheck) |
                Should -Be @('archive-dates', 'created', 'doc-type', 'stale-use-skill')
        }

        It 'recovers doc.created from the filename date and flags the unrecoverable archive date' {
            $result = Invoke-Lint @{ Passes = @('provenance') }
            $findings = (Get-LintPass $result 'provenance').findings | Where-Object { $_.file -eq $script:ArchivedRel }
            $repair = $findings | Where-Object { $_.issue -eq 'archive-dates' }
            $repair.disposition | Should -Be 'fixable'
            $repair.detail | Should -Match 'doc.created: 2026-01-05'
            $flag = $findings | Where-Object { $_.issue -eq 'archive-dates-unrecoverable' }
            $flag.disposition | Should -Be 'flagged'
            $flag.detail | Should -Match 'doc.archived'
        }

        It 'writes the recovered date through the shared wrapper under apply' {
            Invoke-Lint @{ Passes = @('provenance'); Apply = $true } | Out-Null
            $content = [System.IO.File]::ReadAllText($script:ArchivedFile)
            Get-VaultFrontmatterField -Content $content -Field 'doc.created' | Should -Be '2026-01-05'
            # The wrapper preserves the note's CONTENT, which the skill-side original did not:
            # the pinned directive rides across the reserialize with its text intact. Its BYTES
            # are not preserved and are not asserted - a frontmatter op re-emits the whole block,
            # so the quoting the note happened to carry is the emitter's to choose.
            $directives = Get-VaultFrontmatterEntryList -Map (Get-VaultFrontmatterMap -Content $content) -Field 'agent.instructions'
            Get-VaultEntryFieldValue -Entry $directives[0] -Name 'pinned' | Should -Be 'Frozen - do not rewrite'
        }

        It 'reports missing doc.type as remedy-only, with the value declared in requires' {
            $result = Invoke-Lint @{ Passes = @('provenance') }
            $finding = (Get-LintPass $result 'provenance').findings |
                Where-Object { $_.issue -eq 'missing-doc-type' } | Select-Object -First 1
            $finding.disposition | Should -Be 'reported'
            $remedy = $finding.remedyOptions[0]
            $remedy.action | Should -Be 'edit-frontmatter'
            # NOT dispatchable without caller judgment: the value is a judgment, which the label
            # always said and the field now says too.
            @($remedy.requires) | Should -Be @('frontmatter_ops.0.value')
            # The path RESOLVES against the live params by plain object/array walking. Asserting
            # the string alone would go green on a path naming a key the payload does not have.
            $slot = Resolve-ParamPath -Node $remedy.params -Path 'frontmatter_ops.0.value'
            $slot.Found | Should -BeTrue
            $slot.Value | Should -BeExactly ''
            # edit requires an ARRAY of ops, and a bare object - or a nested array - is refused.
            $remedy.params.frontmatter_ops -is [array] | Should -BeTrue
            $ops = Get-RemedyOps $remedy
            $ops[0].op | Should -Be 'set_field'
            $ops[0].field | Should -Be 'doc.type'
        }

        It 'never repairs doc.type or the stale use_skill entry, even under apply' {
            $staleRel = Join-Path $script:VaultDir 'Archive/2026-02-02-Stale-Directive.md'
            Write-FixtureFile -Path $staleRel -Content @"
---
doc.created: 2026-02-02
doc.archived: 2026-02-03
agent.instructions:
  - use_skill: "vault-docs"
    when: "editing this file"
---
# Stale Directive
"@
            $before = [System.IO.File]::ReadAllText($staleRel)
            $result = Invoke-Lint @{ Passes = @('provenance'); Apply = $true }
            $finding = (Get-LintPass $result 'provenance').findings |
                Where-Object { $_.issue -eq 'stale-archived-use-skill' }
            $finding.disposition | Should -Be 'reported'
            # Stripping it is an agentic-layer write, which lint categorically does not make.
            [System.IO.File]::ReadAllText($staleRel) | Should -BeExactly $before
        }
    }

    Context 'Pass: broken-wikilinks' {
        It 'reports a link with no matching note and ignores code fences and live links' {
            $result = Invoke-Lint @{ Passes = @('broken-wikilinks') }
            $findings = @((Get-LintPass $result 'broken-wikilinks').findings)
            @($findings | Where-Object { $_.detail -match 'No-Such-Note' }).Count | Should -Be 1
            # Inside a fenced block, so it is syntax shown as an example, not a live link.
            $findings | Where-Object { $_.detail -match 'Also-Missing' } | Should -BeNullOrEmpty
            $findings | Where-Object { $_.detail -match 'Gizmo-Reference' } | Should -BeNullOrEmpty
        }

        It 'offers a global and a path-scoped suppression remedy that dispatch to lint_suppressions' {
            $result = Invoke-Lint @{ Passes = @('broken-wikilinks') }
            $finding = (Get-LintPass $result 'broken-wikilinks').findings | Where-Object { $_.detail -match 'No-Such-Note' }
            $global = $finding.remedyOptions | Where-Object { $_.id -eq 'suppress-global' }
            $global.action | Should -Be 'add-suppression'
            $global.params.action | Should -Be 'add'
            $global.params.list | Should -Be 'broken-wikilinks'
            $global.params.entry | Should -Be 'No-Such-Note'
            $global.params.PSObject.Properties.Name | Should -Not -Contain 'scope_path'
            $scoped = $finding.remedyOptions | Where-Object { $_.id -eq 'suppress-scoped' }
            $scoped.params.scope_path | Should -Be 'Resources'

            # The payload is dispatchable verbatim: hand it to lint_suppressions and the finding
            # goes away on the next run.
            $suppressions = (Resolve-Path (Join-Path $PSScriptRoot '..' 'lint_suppressions.ps1')).Path
            $Global:VaultMcpOpClasses = @('content:read', 'lint:manage')
            & $suppressions -Action $global.params.action -List $global.params.list -Entry $global.params.entry | Out-Null

            $after = Invoke-Lint @{ Passes = @('broken-wikilinks') }
            @((Get-LintPass $after 'broken-wikilinks').findings) | Should -BeNullOrEmpty
            (Get-LintPass $after 'broken-wikilinks').counts.suppressed | Should -Be 1
        }

        It 'honors a path-scoped suppression only under its prefix' {
            $stateFile = Join-Path $script:StateDir 'known-broken-wikilinks.txt'
            [System.IO.File]::WriteAllText($stateFile, "[path:Projects]`nNo-Such-Note`n", (New-Object System.Text.UTF8Encoding $false))
            $result = Invoke-Lint @{ Passes = @('broken-wikilinks') }
            # The linking file is under Resources/, so a Projects-scoped entry does not reach it.
            @((Get-LintPass $result 'broken-wikilinks').findings).Count | Should -Be 1

            [System.IO.File]::WriteAllText($stateFile, "[path:Resources]`nNo-Such-Note`n", (New-Object System.Text.UTF8Encoding $false))
            @((Get-LintPass (Invoke-Lint @{ Passes = @('broken-wikilinks') }) 'broken-wikilinks').findings) | Should -BeNullOrEmpty
        }

        It 'keeps the note-existence index vault-wide even when path narrows the scan' {
            # `path` narrows which files are SCANNED, never which notes exist. Link-Note.md links
            # [[Gizmo-Reference]], which lives in the same folder, so narrow to a file whose live
            # links point OUT of the narrowing root.
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Inbox/Outbound.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Outbound

Links [[Gizmo-Reference]], which lives outside this folder entirely.
"@
            $result = Invoke-Lint @{ Passes = @('broken-wikilinks'); Path = 'Projects/Widget/Inbox/Outbound.md' }
            (Get-LintPass $result 'broken-wikilinks').filesScanned | Should -Be 1
            (Get-LintPass $result 'broken-wikilinks').findings | Should -BeNullOrEmpty
        }

        It 'degrades to no suppression when the list is absent - never an error' {
            Remove-Item -LiteralPath (Join-Path $script:StateDir 'known-broken-wikilinks.txt') -ErrorAction SilentlyContinue
            { Invoke-Lint @{ Passes = @('broken-wikilinks') } } | Should -Not -Throw
        }
    }

    Context 'Pass: anchor-links' {
        It 'carries the computed wikilink as an edit-patch payload for the resolvable case' {
            $result = Invoke-Lint @{ Passes = @('anchor-links') }
            $finding = (Get-LintPass $result 'anchor-links').findings |
                Where-Object { $_.issue -eq 'markdown-anchor-link' } | Select-Object -First 1
            $finding.line | Should -BeGreaterThan 0
            $remedy = $finding.remedyOptions[0]
            $remedy.action | Should -Be 'edit-patch'
            $remedy.params.old_string | Should -Be '[Torque Specs](Gizmo-Reference#torque-specs)'
            $remedy.params.new_string | Should -Be '[[Gizmo-Reference#Torque Specs]]'
        }

        It 'flags a fragment matching no heading for manual review rather than guessing' {
            $result = Invoke-Lint @{ Passes = @('anchor-links') }
            $finding = (Get-LintPass $result 'anchor-links').findings |
                Where-Object { $_.detail -match 'no-such-heading' }
            $finding.disposition | Should -Be 'flagged'
            $finding.remedyOptions[0].action | Should -Be 'manual'
            $finding.remedyOptions[0].params.reason | Should -Be 'no-matching-heading'
        }

        It 'offers pick-heading with the candidates when the fragment is ambiguous' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Two-Heads.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Notes
## Detail
Some text.
## Detail
More text.
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Points-At.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Points At

Go to [Detail](Two-Heads#detail).
"@
            $result = Invoke-Lint @{ Passes = @('anchor-links') }
            $finding = (Get-LintPass $result 'anchor-links').findings |
                Where-Object { $_.issue -eq 'anchor-link-ambiguous-heading' }
            $finding.remedyOptions[0].action | Should -Be 'pick-heading'
            @($finding.remedyOptions[0].params.headings).Count | Should -Be 2
        }

        It 'is never fixed, even under apply' {
            $before = [System.IO.File]::ReadAllText($script:AnchorFile)
            Invoke-Lint @{ Passes = @('anchor-links'); Apply = $true } | Out-Null
            [System.IO.File]::ReadAllText($script:AnchorFile) | Should -BeExactly $before
        }
    }

    Context 'Pass: filename-conventions' {
        It 'reports spaces, underscores, and non-Title-Case with a computed move payload' {
            $result = Invoke-Lint @{ Passes = @('filename-conventions') }
            $finding = (Get-LintPass $result 'filename-conventions').findings |
                Where-Object { $_.file -eq $script:BadNameRel }
            $finding.detail | Should -Match 'contains spaces'
            $finding.detail | Should -Match 'contains underscores'
            $move = $finding.remedyOptions | Where-Object { $_.action -eq 'move' }
            $move.params.source | Should -Be $script:BadNameRel
            $move.params.dest | Should -Be 'Resources/Bad-Name-File.md'
        }

        It 'exempts a segment recorded in filename-exceptions.txt' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Sync-iCloud-Notes.md') -Content "# iCloud`n"
            $before = Invoke-Lint @{ Passes = @('filename-conventions') }
            @((Get-LintPass $before 'filename-conventions').findings | Where-Object { $_.file -match 'iCloud' }).Count |
                Should -Be 1

            [System.IO.File]::WriteAllText((Join-Path $script:StateDir 'filename-exceptions.txt'), "iCloud`n", (New-Object System.Text.UTF8Encoding $false))
            $after = Invoke-Lint @{ Passes = @('filename-conventions') }
            (Get-LintPass $after 'filename-conventions').findings | Where-Object { $_.file -match 'iCloud' } |
                Should -BeNullOrEmpty
        }

        It 'accepts an all-caps name and a minor word after the first segment' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/README.md') -Content "# Readme`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Road-to-Hana-Planning.md') -Content "# Road`n"
            $result = Invoke-Lint @{ Passes = @('filename-conventions') }
            $files = @((Get-LintPass $result 'filename-conventions').findings.file)
            $files | Should -Not -Contain 'Resources/README.md'
            $files | Should -Not -Contain 'Resources/Road-to-Hana-Planning.md'
        }
    }

    Context 'Pass: frontmatter-schema' {
        It 'renders the shared validator violations as findings keyed by rule' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Bad-Schema.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
agent.unknown_key:
  - "something"
nav.contents:
  - link: "[[Gizmo-Reference]]"
    folder: "Resources"
    description: "both discriminators"
---
# Bad Schema
"@
            $result = Invoke-Lint @{ Passes = @('frontmatter-schema') }
            $findings = @((Get-LintPass $result 'frontmatter-schema').findings | Where-Object { $_.file -eq 'Resources/Bad-Schema.md' })
            @($findings.issue) | Should -Contain 'schema:unknown_key'
            @($findings.issue) | Should -Contain 'schema:link_folder_both'
        }

        It 'reports a declared folder: path that no longer resolves - lint owns the filesystem half' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Ghost/Ghost.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
nav.contents:
  - folder: "Resources/Ghost/Gone"
    description: "a folder that is not there"
    load_when: "never"
---
# Ghost
"@
            $result = Invoke-Lint @{ Passes = @('frontmatter-schema') }
            $finding = (Get-LintPass $result 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'folder-entry-unresolved' }
            $finding.detail | Should -Match "Resources/Ghost/Gone"
        }

        It 'offers repoint-then-remove for an unresolved folder: entry, repair option first' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Ghost/Ghost.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
nav.contents:
  - folder: "Resources/Ghost/Gone"
    description: "a folder that is not there"
    load_when: "never"
---
# Ghost
"@
            $result = Invoke-Lint @{ Passes = @('frontmatter-schema') }
            $finding = (Get-LintPass $result 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'folder-entry-unresolved' }
            @($finding.remedyOptions.id) | Should -Be @('repoint-nav-entry', 'remove-nav-entry')

            # The repair: remove + add in one ordered call, with the entry's own prose carried
            # forward so the new path is the only thing left for the caller.
            $repoint = $finding.remedyOptions[0]
            $repoint.action | Should -Be 'edit-frontmatter'
            @($repoint.requires) | Should -Be @('frontmatter_ops.1.folder')
            # The path resolves by walking - and the numeric segment really indexes the SECOND op,
            # which is the half of the syntax a string assertion cannot check at all.
            $slot = Resolve-ParamPath -Node $repoint.params -Path 'frontmatter_ops.1.folder'
            $slot.Found | Should -BeTrue
            $slot.Value | Should -BeExactly ''
            $repoint.params.frontmatter_ops -is [array] | Should -BeTrue
            $ops = Get-RemedyOps $repoint
            $ops.Count | Should -Be 2
            $ops[0].op | Should -Be 'remove_nav_entry'
            $ops[0].folder | Should -Be 'Resources/Ghost/Gone'
            $ops[1].op | Should -Be 'add_nav_entry'
            $ops[1].folder | Should -BeExactly ''
            $ops[1].description | Should -Be 'a folder that is not there'
            $ops[1].load_when | Should -Be 'never'

            $remove = $finding.remedyOptions[1]
            @($remove.requires).Count | Should -Be 0
            $removeOps = Get-RemedyOps $remove
            $removeOps.Count | Should -Be 1
            $removeOps[0].op | Should -Be 'remove_nav_entry'
            $removeOps[0].key | Should -Be 'nav.contents'
        }

        It 'dispatches the folder-entry-unresolved removal verbatim through edit' {
            $ghost = Join-Path $script:VaultDir 'Resources/Ghost/Ghost.md'
            Write-FixtureFile -Path $ghost -Content @"
---
doc.created: 2026-07-16
doc.type: Note
nav.contents:
  - link: "[[Gizmo-Reference]]"
    description: "part specs"
    load_when: "specs needed"
  - folder: "Resources/Ghost/Gone"
    description: "a folder that is not there"
    load_when: "never"
---
# Ghost
"@
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'folder-entry-unresolved' }

            # nav.* is the agentic layer, so edit asserts agentic:write on top of content:write.
            $Global:VaultMcpOpClasses = @('content:read', 'content:write', 'agentic:write')
            $edit = Invoke-RemedyThroughEdit $finding.remedyOptions[1]
            $edit.frontmatterChanged | Should -BeTrue
            @($edit.opsApplied)[0].op | Should -Be 'remove_nav_entry'

            $after = [System.IO.File]::ReadAllText($ghost)
            $after | Should -Not -Match 'Resources/Ghost/Gone'
            $after | Should -Match "link: '\[\[Gizmo-Reference\]\]'"
            (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'folder-entry-unresolved' } | Should -BeNullOrEmpty
        }

        It 'dispatches the two-op repoint through edit once its declared slot is filled' {
            # THE STRONGEST FORM OF THE CONTRACT, on the multi-op payload: fill ONLY what
            # `requires` names - walking the declared path itself, not a hand-written field list -
            # and the remaining payload dispatches. Nothing about it needs caller judgment beyond
            # the slot, and the encoding is the dispatcher's mechanical step.
            $ghost = Join-Path $script:VaultDir 'Resources/Ghost/Ghost.md'
            Write-FixtureFile -Path $ghost -Content @"
---
doc.created: 2026-07-16
doc.type: Note
nav.contents:
  - link: "[[Gizmo-Reference]]"
    description: "part specs"
    load_when: "specs needed"
  - folder: "Resources/Ghost/Gone"
    description: "a folder that is not there"
    load_when: "never"
---
# Ghost
"@
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Resources/Ghost/Moved') -Force | Out-Null
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'folder-entry-unresolved' }
            $repoint = $finding.remedyOptions[0]
            $repoint.id | Should -Be 'repoint-nav-entry'

            $repoint = Set-RemedyRequiresSlots -Remedy $repoint -Values @{
                'frontmatter_ops.1.folder' = 'Resources/Ghost/Moved'
            }
            $Global:VaultMcpOpClasses = @('content:read', 'content:write', 'agentic:write')
            $edit = Invoke-RemedyThroughEdit $repoint
            $edit.frontmatterChanged | Should -BeTrue
            @($edit.opsApplied).Count | Should -Be 2
            @($edit.opsApplied)[0].op | Should -Be 'remove_nav_entry'
            @($edit.opsApplied)[1].op | Should -Be 'add_nav_entry'

            $after = [System.IO.File]::ReadAllText($ghost)
            $after | Should -Not -Match 'Resources/Ghost/Gone'
            $after | Should -Match 'folder: Resources/Ghost/Moved'
            # The entry's own prose rode across untouched - that is the deterministic half the
            # payload exists to carry, and it never passed through the caller's hands.
            $after | Should -Match 'a folder that is not there'
            $after | Should -Match 'never'
            (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'folder-entry-unresolved' } | Should -BeNullOrEmpty
        }

        It 'carries a fully computed add_nav_entry op for a missing standard subfolder' {
            $result = Invoke-Lint @{ Passes = @('frontmatter-schema') }
            $finding = (Get-LintPass $result 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'missing-standard-folder-entry' -and $_.file -eq 'Projects/Widget/Widget.md' }
            $finding.detail | Should -Match "'Inbox'"
            $remedy = $finding.remedyOptions[0]
            $remedy.action | Should -Be 'edit-frontmatter'
            $remedy.params.path | Should -Be 'Projects/Widget/Widget.md'
            # THE ONE-OP CASE. A single op still rides as an ARRAY - and as a FLAT one. Both
            # failure directions have bitten this file: ConvertTo-Json unrolls a one-element array
            # on PIPELINE input (bare object, refused "missing 'op' member"), and -AsArray on top
            # of -InputObject double-wraps it (nested array, refused the same way).
            $remedy.params.frontmatter_ops -is [array] | Should -BeTrue
            $remedy.params.frontmatter_ops.Count | Should -Be 1
            # ...and it survives the host's encoding step as an array of one op, not as either.
            ConvertTo-DispatchArgument $remedy.params.frontmatter_ops | Should -Match '^\[\{"op"'
            # The boilerplate table supplies both prose fields, so nothing is left to judgment.
            @($remedy.requires).Count | Should -Be 0
            $ops = Get-RemedyOps $remedy
            $ops.Count | Should -Be 1
            $ops[0].op | Should -Be 'add_nav_entry'
            $ops[0].key | Should -Be 'nav.contents'
            $ops[0].folder | Should -Be 'Projects/Widget/Inbox'
            $ops[0].description | Should -Match 'Active working docs'
            $ops[0].load_when | Should -Not -BeNullOrEmpty
        }

        It 'dispatches that remedy verbatim through edit and closes its own finding' {
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'missing-standard-folder-entry' -and $_.file -eq 'Projects/Widget/Widget.md' }

            $Global:VaultMcpOpClasses = @('content:read', 'content:write', 'agentic:write')
            $edit = Invoke-RemedyThroughEdit $finding.remedyOptions[0]
            $edit.frontmatterChanged | Should -BeTrue
            @($edit.opsApplied)[0].op | Should -Be 'add_nav_entry'

            [System.IO.File]::ReadAllText($widget) | Should -Match 'folder: Projects/Widget/Inbox'
            # The end-to-end assertion: the finding the payload came from is gone, and the write
            # introduced no schema violation of its own.
            $after = @((Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                    Where-Object { $_.file -eq 'Projects/Widget/Widget.md' })
            $after | Where-Object { $_.issue -eq 'missing-standard-folder-entry' } | Should -BeNullOrEmpty
            $after | Where-Object { $_.issue -like 'schema:*' } | Should -BeNullOrEmpty
        }

        It 'declares the description and load_when slots for an unlinked corpus member' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Corpus/Widget-Design.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Widget Design`n"
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unlinked-corpus-member' }
            $remedy = $finding.remedyOptions[0]
            $remedy.action | Should -Be 'edit-frontmatter'
            @($remedy.requires) | Should -Be @('frontmatter_ops.0.description', 'frontmatter_ops.0.load_when')
            # Both declared paths resolve against this remedy's own params, by walking.
            foreach ($path in @($remedy.requires)) {
                $slot = Resolve-ParamPath -Node $remedy.params -Path $path
                $slot.Found | Should -BeTrue -Because "'$path' must name a key the payload carries"
                $slot.Value | Should -BeExactly ''
            }
            $remedy.params.frontmatter_ops -is [array] | Should -BeTrue
            $ops = Get-RemedyOps $remedy
            $ops[0].op | Should -Be 'add_nav_entry'
            $ops[0].link | Should -Be '[[Widget-Design]]'
            $ops[0].description | Should -BeExactly ''
        }

        It 'is refused by edit when dispatched verbatim - the requires declaration is honest' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Corpus/Widget-Design.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Widget Design`n"
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $before = [System.IO.File]::ReadAllText($widget)
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unlinked-corpus-member' }

            $Global:VaultMcpOpClasses = @('content:read', 'content:write', 'agentic:write')
            $err = Invoke-RemedyThroughEditError $finding.remedyOptions[0]
            $err.code | Should -Be 'malformed_op'
            $err.data.opIndex | Should -Be 0
            $err.message | Should -Match 'description'
            [System.IO.File]::ReadAllText($widget) | Should -BeExactly $before
        }

        It 'applies once the caller fills the declared slots, landing at the end of the link run' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Corpus/Widget-Design.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Widget Design`n"
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unlinked-corpus-member' }

            $remedy = Set-RemedyOpField $finding.remedyOptions[0] 0 'description' 'How the widget is put together'
            $remedy = Set-RemedyOpField $remedy 0 'load_when' 'widget design details needed'
            $Global:VaultMcpOpClasses = @('content:read', 'content:write', 'agentic:write')
            (Invoke-RemedyThroughEdit $remedy).frontmatterChanged | Should -BeTrue

            $after = [System.IO.File]::ReadAllText($widget)
            $after | Should -Match "link: '\[\[Widget-Design\]\]'"
            $after | Should -Match 'How the widget is put together'
            (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unlinked-corpus-member' } | Should -BeNullOrEmpty
        }

        It 'declares the same slots for an unreferenced subfolder, suppression still second' {
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch') -Force | Out-Null
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch/Keep.md') -Content "# Keep`n"
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unreferenced-subfolder' }
            @($finding.remedyOptions.id) | Should -Be @('add-nav-entry', 'ignore-subfolder')
            $remedy = $finding.remedyOptions[0]
            @($remedy.requires) | Should -Be @('frontmatter_ops.0.description', 'frontmatter_ops.0.load_when')
            foreach ($path in @($remedy.requires)) {
                $slot = Resolve-ParamPath -Node $remedy.params -Path $path
                $slot.Found | Should -BeTrue -Because "'$path' must name a key the payload carries"
                $slot.Value | Should -BeExactly ''
            }
            $remedy.params.frontmatter_ops -is [array] | Should -BeTrue
            $ops = Get-RemedyOps $remedy
            $ops[0].op | Should -Be 'add_nav_entry'
            $ops[0].folder | Should -Be 'Projects/Widget/Scratch'
            $ops[0].description | Should -BeExactly ''
            @($finding.remedyOptions[1].requires).Count | Should -Be 0
        }

        It 'suppresses an unreferenced subfolder recorded in known-ignored-subfolders.txt' {
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch') -Force | Out-Null
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch/Keep.md') -Content "# Keep`n"
            $before = Invoke-Lint @{ Passes = @('frontmatter-schema') }
            @((Get-LintPass $before 'frontmatter-schema').findings | Where-Object { $_.issue -eq 'unreferenced-subfolder' }).Count |
                Should -Be 1

            [System.IO.File]::WriteAllText((Join-Path $script:StateDir 'known-ignored-subfolders.txt'),
                "# intentionally unreferenced`nProjects/Widget/Scratch`n", (New-Object System.Text.UTF8Encoding $false))
            $after = Invoke-Lint @{ Passes = @('frontmatter-schema') }
            (Get-LintPass $after 'frontmatter-schema').findings | Where-Object { $_.issue -eq 'unreferenced-subfolder' } |
                Should -BeNullOrEmpty
        }

        It 'is never fixed, even under apply - the agentic layer is categorically remedy-only' {
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $before = [System.IO.File]::ReadAllText($widget)
            Invoke-Lint @{ Passes = @('frontmatter-schema'); Apply = $true } | Out-Null
            [System.IO.File]::ReadAllText($widget) | Should -BeExactly $before
        }
    }

    Context 'Folder-note completeness across every nav-link-set arity' {
        # Get-LintNavLinkBasename and Expand-LintCorpusReach each build an OrdinalIgnoreCase
        # HashSet[string] and hand it to a caller that calls .Contains() on it. PowerShell
        # ENUMERATES a collection on output, so returning one has to suppress that or the set never
        # arrives: at 0 members the caller gets $null, at 1 a bare [string], at 2+ an [object[]].
        # Each breaks .Contains() differently - a throw that aborts the pass, silent SUBSTRING
        # matching, and matching that is case-SENSITIVE despite the comparer the set was built
        # with - so the arity is the axis these cases vary, and all three are covered for both
        # functions. The committed test-vault's only folder note declares two links, which is why
        # the existing coverage never reached the other two.
        #
        # Every fixture here is a folder note of its own (Projects/Gadget/Gadget.md) rather than an
        # edit to Widget.md, so each case controls its link count exactly and none of them perturbs
        # the Widget-based cases above.

        It 'reports an unreferenced subfolder when the folder note declares NO nav links' {
            # The repro, and an entirely ordinary note: a folder note whose frontmatter carries no
            # nav block at all. The empty set arrives as $null and .Contains() throws, aborting the
            # whole frontmatter-schema pass rather than reporting anything.
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Gadget.md - a folder note that routes nowhere yet
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Scratch/Keep.md') -Content "# Keep`n"

            $pass = Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema'
            # The pass ran to completion at all - the crash took every later file with it, so this
            # is the assertion the fix is really about.
            $pass.status | Should -Be 'findings'
            @($pass.findings | Where-Object {
                    $_.issue -eq 'unreferenced-subfolder' -and $_.file -eq 'Projects/Gadget/Gadget.md'
                }).Count | Should -Be 1
        }

        It 'does not count a subfolder as referenced because its name is a SUBSTRING of a declared link' {
            # Exactly one link entry, so the set arrives as a bare [string] and .Contains() becomes
            # String.Contains - substring matching. "Scratchpad-Overview".Contains("Scratch") is
            # true, so the Scratch subfolder is silently treated as declared.
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget.md') -Content @"
---
nav.contents:
  - link: "[[Gadget-Scratchpad-Overview]]"
    description: "How the scratchpad is organized"
    load_when: "looking for in-flight gadget notes"
---
# Gadget.md - a folder note declaring exactly one link
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget-Scratchpad-Overview.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Gadget Scratchpad Overview`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget-Scratchpad/Keep.md') -Content "# Keep`n"

            $findings = @((Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                    Where-Object { $_.issue -eq 'unreferenced-subfolder' -and $_.file -eq 'Projects/Gadget/Gadget.md' })
            $findings.Count | Should -Be 1
            $findings[0].detail | Should -Match 'Projects/Gadget/Gadget-Scratchpad'
        }

        It 'still matches a declared link against a subfolder case-insensitively at two entries' {
            # Two entries, so the set arrives as an [object[]] and .Contains() becomes IList.Contains
            # - Object.Equals, and so ordinal case-SENSITIVE, losing the OrdinalIgnoreCase comparer
            # the set was constructed with. A link whose casing drifts from the folder's then reads
            # as undeclared and the folder note is reported for a subfolder it does route to.
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget.md') -Content @"
---
nav.contents:
  - link: "[[Gadget-Alpha]]"
    description: "The alpha notes"
    load_when: "reviewing alpha work"
  - link: "[[scratch]]"
    description: "Scratch working area"
    load_when: "looking for in-flight gadget notes"
---
# Gadget.md - a folder note declaring two links
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget-Alpha.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Gadget Alpha`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Scratch/Scratch.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Scratch`n"

            (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unreferenced-subfolder' -and $_.file -eq 'Projects/Gadget/Gadget.md' } |
                Should -BeNullOrEmpty
        }

        It 'reports an unlinked corpus member when the folder note declares NO nav links' {
            # Expand-LintCorpusReach carries the identical defect, and its caller reaches it first:
            # the Corpus branch runs before the plain-subfolder branch, so this is the arity-zero
            # crash on the reach set rather than on the declared-link set.
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Gadget.md - a folder note that routes nowhere yet
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Corpus/Gadget-Design.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Gadget Design`n"

            $pass = Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema'
            $pass.status | Should -Be 'findings'
            $finding = $pass.findings | Where-Object {
                $_.issue -eq 'unlinked-corpus-member' -and $_.file -eq 'Projects/Gadget/Gadget.md'
            }
            @($finding).Count | Should -Be 1
            $finding.detail | Should -Match 'Projects/Gadget/Corpus/Gadget-Design.md'
        }

        It 'does not count a corpus member as reachable because its name is a SUBSTRING of the reach set' {
            # One declared link and no hop, so the reach set is a single-element one and arrives as
            # a bare [string]: "Gadget-Design-Overview".Contains("Gadget-Design") is true and the
            # unreachable member is passed over.
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget.md') -Content @"
---
nav.contents:
  - link: "[[Gadget-Design-Overview]]"
    description: "Where the design notes live"
    load_when: "orienting in the gadget design"
---
# Gadget.md - a folder note declaring exactly one link
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget-Design-Overview.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Gadget Design Overview`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Corpus/Gadget-Design.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Gadget Design`n"

            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unlinked-corpus-member' -and $_.file -eq 'Projects/Gadget/Gadget.md' }
            @($finding).Count | Should -Be 1
            $finding.detail | Should -Match 'Projects/Gadget/Corpus/Gadget-Design.md'
        }

        It 'reaches a corpus member through an intermediate folder note, case-insensitively' {
            # The two-element reach set, and the hop that produces it: Gadget.md links the Corpus
            # subfolder's own folder note, whose nav.contents routes on to the member. That second
            # hop is the OTHER call site of Get-LintNavLinkBasename - the foreach inside
            # Expand-LintCorpusReach - so this covers the one consumer that was never broken and
            # must not become broken.
            #
            # The hop's link is cased differently from the member's filename, which is what turns
            # the [object[]] arity into a false positive: IList.Contains is ordinal.
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Gadget.md') -Content @"
---
nav.contents:
  - link: "[[Gadget-Hub]]"
    description: "The corpus hub, which routes on to the design notes"
    load_when: "orienting in the gadget corpus"
---
# Gadget.md - a folder note routing through a corpus hub
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Corpus/Gadget-Hub/Gadget-Hub.md') -Content @"
---
nav.contents:
  - link: "[[gadget-deep]]"
    description: "The deep design notes"
    load_when: "gadget design details needed"
---
# Gadget-Hub.md - the corpus hub
"@
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Gadget/Corpus/Gadget-Hub/Gadget-Deep.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Gadget Deep`n"

            (Get-LintPass (Invoke-Lint @{ Passes = @('frontmatter-schema') }) 'frontmatter-schema').findings |
                Where-Object { $_.issue -eq 'unlinked-corpus-member' -and $_.file -eq 'Projects/Gadget/Gadget.md' } |
                Should -BeNullOrEmpty
        }
    }

    Context 'The RemedyOption contract' {
        It 'names every empty op slot in requires, across every pass that emits one' {
            # The invariant New-LintRemedy states: a params field is either COMPUTED or named in
            # `requires`, and an empty string is the slot form of the latter. A field that is
            # neither is a pre-rendered hint wearing a payload's clothes - the exact shape the
            # entryBlock params had - so this walks the whole default run rather than one pass.
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch') -Force | Out-Null
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch/Keep.md') -Content "# Keep`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Corpus/Widget-Design.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Widget Design`n"

            $checked = 0
            $unnamed = @()
            foreach ($pass in (Invoke-Lint @{ }).passes) {
                foreach ($finding in @($pass.findings)) {
                    if ($null -eq $finding.PSObject.Properties['remedyOptions']) { continue }
                    foreach ($remedy in @($finding.remedyOptions)) {
                        if ($remedy.action -ne 'edit-frontmatter') { continue }
                        $checked++
                        $ops = Get-RemedyOps $remedy
                        for ($i = 0; $i -lt $ops.Count; $i++) {
                            foreach ($prop in $ops[$i].PSObject.Properties) {
                                if ("$($prop.Value)" -ne '') { continue }
                                $path = "frontmatter_ops.$i.$($prop.Name)"
                                if (@($remedy.requires) -notcontains $path) { $unnamed += "$($remedy.id): $path" }
                            }
                        }
                    }
                }
            }
            $checked | Should -BeGreaterThan 3
            $unnamed | Should -BeNullOrEmpty
        }

        It 'names the doc.type value it cannot choose - the label always said so' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/No-Type.md') `
                -Content "---`ndoc.created: 2026-07-16`n---`n# No Type`n"
            $finding = (Get-LintPass (Invoke-Lint @{ Passes = @('provenance') }) 'provenance').findings |
                Where-Object { $_.issue -eq 'missing-doc-type' -and $_.file -eq 'Resources/No-Type.md' }
            @($finding.remedyOptions[0].requires) | Should -Be @('frontmatter_ops.0.value')
        }

        It 'every declared requires path RESOLVES against its own params, across the whole run' {
            # The converse of the walk above, and the drift the structured payload closes: that
            # test proves no blank slot is unnamed; this one proves no NAME is blank-slotless.
            # A `requires` entry pointing at a key the payload does not carry - a stale root, a
            # renamed field, an index that moved when an op was added - is undetectable from the
            # string alone, and it is exactly what a consumer would break on. The resolver walks
            # plain objects and arrays with no per-tool knowledge, which is the point: it is the
            # algorithm an orchestrator has, and the same one sync's commit.message needs.
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch') -Force | Out-Null
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Scratch/Keep.md') -Content "# Keep`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Projects/Widget/Corpus/Widget-Design.md') `
                -Content "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Widget Design`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/No-Type.md') `
                -Content "---`ndoc.created: 2026-07-16`n---`n# No Type`n"
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Ghost/Ghost.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
nav.contents:
  - folder: "Resources/Ghost/Gone"
    description: "a folder that is not there"
    load_when: "never"
---
# Ghost
"@

            $withRequires = @()
            $unresolved = @()
            foreach ($pass in (Invoke-Lint @{ }).passes) {
                foreach ($finding in @($pass.findings)) {
                    if ($null -eq $finding.PSObject.Properties['remedyOptions']) { continue }
                    foreach ($remedy in @($finding.remedyOptions)) {
                        foreach ($path in @($remedy.requires)) {
                            $withRequires += "$($remedy.id):$path"
                            $slot = Resolve-ParamPath -Node $remedy.params -Path $path
                            if (-not $slot.Found) { $unresolved += "$($remedy.id): $path" }
                        }
                    }
                }
            }
            $unresolved | Should -BeNullOrEmpty
            # All four remedies the design names as needing caller-authored text are in the run,
            # so this is not passing by finding nothing to check.
            @($withRequires | ForEach-Object { $_.Split(':')[0] } | Sort-Object -Unique) |
                Should -Be @('add-nav-entry', 'repoint-nav-entry', 'set-doc-type')
            @($withRequires | Where-Object { $_ -like 'add-nav-entry:*' }).Count | Should -BeGreaterOrEqual 4
        }
    }

    Context 'Pass: hard-wrap' {
        It 'reports a safe join as an edit-patch remedy and never applies it' {
            $before = [System.IO.File]::ReadAllText($script:WrapFile)
            $result = Invoke-Lint @{ Passes = @('hard-wrap'); Apply = $true }
            $pass = Get-LintPass $result 'hard-wrap'
            $pass.reportOnly | Should -BeTrue
            $finding = $pass.findings | Where-Object { $_.issue -eq 'hard-wrapped-block' -and $_.file -eq 'Resources/Wrapped-Note.md' }
            $finding.remedyOptions[0].action | Should -Be 'edit-patch'
            $finding.remedyOptions[0].params.new_string | Should -Match 'eighty columns which is the continuation'
            [System.IO.File]::ReadAllText($script:WrapFile) | Should -BeExactly $before
        }

        It 'flags an unbalanced code span rather than joining it - corruption safety' {
            Write-FixtureFile -Path (Join-Path $script:VaultDir 'Resources/Risky-Wrap.md') -Content @"
---
doc.created: 2026-07-16
doc.type: Note
---
# Risky Wrap

This paragraph opens a code span with a backtick like ``this one here and keeps going on
for another line so the pair of lines is a genuine join candidate for the checker.
"@
            $result = Invoke-Lint @{ Passes = @('hard-wrap') }
            $finding = (Get-LintPass $result 'hard-wrap').findings |
                Where-Object { $_.file -eq 'Resources/Risky-Wrap.md' }
            $finding.disposition | Should -Be 'flagged'
            $finding.detail | Should -Match 'unbalanced code span'
        }

        It 'adds per-block before/after detail under hard_wrap_inspect' {
            $result = Invoke-Lint @{ Passes = @('hard-wrap'); hard_wrap_inspect = $true }
            $finding = (Get-LintPass $result 'hard-wrap').findings |
                Where-Object { $_.issue -eq 'hard-wrapped-block' -and $_.file -eq 'Resources/Wrapped-Note.md' }
            @($finding.inspect.before).Count | Should -Be 2
            $finding.inspect.after | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Path narrowing' {
        It 'narrows to a folder' {
            $result = Invoke-Lint @{ Passes = @('typography'); Path = 'Projects' }
            (Get-LintPass $result 'typography').findings | Should -BeNullOrEmpty
            (Get-LintPass $result 'typography').filesScanned | Should -BeGreaterThan 0
        }

        It 'narrows to a single file, post-filtered from the pass enumeration' {
            $result = Invoke-Lint @{ Passes = @('typography'); Path = $script:DirtyRel }
            (Get-LintPass $result 'typography').filesScanned | Should -Be 1
            @((Get-LintPass $result 'typography').findings).Count | Should -Be 1
        }

        It 'applies the fix to the narrowed file alone' {
            $other = Join-Path $script:VaultDir 'Resources/Other-Dirty.md'
            Write-FixtureFile -Path $other -Content ("# Other" + [char]0x2014 + "Dash`n")
            $result = Invoke-Lint @{ Passes = @('typography'); Path = $script:DirtyRel; Apply = $true }
            $result.summary.filesWritten | Should -Be @($script:DirtyRel)
            [System.IO.File]::ReadAllText($other) | Should -Match ([regex]::Escape([string][char]0x2014))
        }
    }

    Context 'Errors' {
        It 'refuses an unknown pass name with invalid_argument' {
            $err = Invoke-LintError @{ Passes = @('typo-graphy') }
            $err.code | Should -Be 'invalid_argument'
            $err.data.pass | Should -Be 'typo-graphy'
        }

        It 'refuses an unknown group token with invalid_argument' {
            (Invoke-LintError @{ Passes = @('everything') }).code | Should -Be 'invalid_argument'
        }

        It 'validates the FULL pass set before running any pass' {
            # The first token is a real fixable pass; the second is nonsense. Nothing may be
            # written, which is what "validate the full set before running any pass" means.
            $before = [System.IO.File]::ReadAllText($script:DirtyFile)
            $err = Invoke-LintError @{ Passes = @('typography', 'nonsense'); Apply = $true }
            $err.code | Should -Be 'invalid_argument'
            [System.IO.File]::ReadAllText($script:DirtyFile) | Should -BeExactly $before
        }

        It 'refuses hard_wrap_inspect with apply' {
            $err = Invoke-LintError @{ Passes = @('hard-wrap'); hard_wrap_inspect = $true; Apply = $true }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'hard_wrap_inspect'
        }

        It 'refuses a path that does not exist with not_found' {
            $err = Invoke-LintError @{ Path = 'Resources/Nope.md' }
            $err.code | Should -Be 'not_found'
            $err.data.path | Should -Be 'Resources/Nope.md'
        }

        It 'refuses an unconfigured vault with unknown_vault' {
            (Invoke-LintError @{ Vault = 'nope' }).code | Should -Be 'unknown_vault'
        }

        It 'refuses a path outside the grant scope with out_of_scope' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            (Invoke-LintError @{ Path = 'Resources' }).code | Should -Be 'out_of_scope'
        }

        It 'silently drops out-of-scope files from enumeration rather than erroring' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $result = Invoke-Lint @{ Passes = @('typography') }
            (Get-LintPass $result 'typography').findings | Should -BeNullOrEmpty
        }

        # An INJECTED BUT EMPTY entry is the production shape of an unscoped grant - both
        # grants.json rows resolve to it, since the whole-grant wildcard {"*": ["*"]} expands at
        # load time into an empty set per vault. It is distinct from an ABSENT global (the
        # standalone case the degrade-open tests cover), and it is the case that regressed: the
        # resolved roots were re-wrapped in @(), which defeated the comma idiom that carries an
        # empty array out of Resolve-VaultScopeRoots and left a one-element array holding it. The
        # Count -gt 0 guards then read unscoped as scoped, and Test-VaultPathInScope's [string[]]
        # coercion turned the inner empty array into an empty-string root - so every call died in
        # ConvertTo-VaultScopeSegments with a raw "Scope path is empty." before any pass ran.
        It 'treats an injected but EMPTY scope-roots entry as unscoped, not as a scoped grant' {
            $Global:VaultMcpScopeRoots = @{ scratch = @() }
            $result = Invoke-Lint @{ Passes = @('typography') }
            # Scanned for real, and the fixture's known violations are still seen - an empty
            # entry must not narrow enumeration the way a real root does.
            (Get-LintPass $result 'typography').filesScanned | Should -BeGreaterThan 0
            (Get-LintPass $result 'typography').findings | Should -Not -BeNullOrEmpty
        }

        It 'accepts a path operand under an injected but EMPTY scope-roots entry' {
            # The other side of the same guard: the path-narrowing scope check at validate time.
            # Under the regression this refused every path, including one plainly inside the vault.
            $Global:VaultMcpScopeRoots = @{ scratch = @() }
            $result = Invoke-Lint @{ Passes = @('typography'); Path = 'Resources' }
            $result.path | Should -Be 'Resources'
        }
    }

    Context 'Fix-set idempotency - the locked-in test for the streaming-writes exception' {
        It 'converges on a double run: the second pass writes nothing' {
            $first = Invoke-Lint @{ Passes = @('fixes'); Apply = $true }
            @($first.summary.filesWritten).Count | Should -BeGreaterThan 0
            $afterFirst = @{}
            foreach ($rel in $first.summary.filesWritten) {
                $afterFirst[$rel] = [System.IO.File]::ReadAllText((Join-Path $script:VaultDir $rel))
            }

            $second = Invoke-Lint @{ Passes = @('fixes'); Apply = $true }
            @($second.summary.filesWritten) | Should -BeNullOrEmpty
            $second.summary.fixed | Should -Be 0
            foreach ($rel in $afterFirst.Keys) {
                # Byte-identical: a rerun recomputes the same result, which is what makes a
                # crash mid-stream safe to recover from by rerunning.
                [System.IO.File]::ReadAllText((Join-Path $script:VaultDir $rel)) | Should -BeExactly $afterFirst[$rel]
            }
        }

        It 'reports zero fixable findings once the fix set has been applied' {
            Invoke-Lint @{ Passes = @('fixes'); Apply = $true } | Out-Null
            (Invoke-Lint @{ Passes = @('fixes') }).summary.fixable | Should -Be 0
        }
    }

    Context 'The non-agentic guarantee, asserted over the fix set itself' {
        It 'produces no agentic frontmatter diff on any file the fix set writes' {
            # This is where the guarantee LIVES: every fix is a body-text transform or a doc.*
            # scalar, so no lint write can change an agent.*/nav.* key - and that holds for every
            # caller, including one holding agentic:write. The wrapper's denial is defense in
            # depth on top of it, covered separately below.
            $before = @{}
            foreach ($file in (Get-ChildItem -LiteralPath $script:VaultDir -Recurse -Filter '*.md' -File)) {
                $rel = [System.IO.Path]::GetRelativePath($script:VaultDir, $file.FullName).Replace('\', '/')
                $before[$rel] = [System.IO.File]::ReadAllText($file.FullName)
            }

            # Granted agentic:write deliberately: the property must hold when the wrapper's
            # assertion would sail through, which is exactly the case a denial-based test misses.
            $Global:VaultMcpOpClasses = @('content:read', 'content:write', 'agentic:write')
            $result = Invoke-Lint @{ Passes = @('fixes'); Apply = $true }
            @($result.summary.filesWritten).Count | Should -BeGreaterThan 0
            $result.capabilitiesUsed | Should -Not -Contain 'agentic:write'

            foreach ($rel in $result.summary.filesWritten) {
                $after = [System.IO.File]::ReadAllText((Join-Path $script:VaultDir $rel))
                $diff = Get-VaultAgenticFrontmatterDiff -BeforeContent $before[$rel] -AfterContent $after
                @($diff) | Should -BeNullOrEmpty -Because "lint's fix set must never change an agent.*/nav.* key in $rel"
            }
        }

        It 'still applies the doc.* fix for a caller who does NOT hold agentic:write' {
            # The corollary: because the diff is empty, the wrapper asserts nothing, so a plain
            # content:write caller can repair a provenance date on a note that carries a pinned:
            # entry.
            $Global:VaultMcpOpClasses = @('content:read', 'content:write')
            Invoke-Lint @{ Passes = @('provenance'); Apply = $true } | Out-Null
            $content = [System.IO.File]::ReadAllText($script:ArchivedFile)
            Get-VaultFrontmatterField -Content $content -Field 'doc.created' | Should -Be '2026-01-05'
        }

        It 'defense in depth: the shared wrapper denies an agentic write for the same class set' {
            # Not a lint path - a direct probe of the layer lint writes through, proving a fix
            # class that ever DID drift into the agentic layer would fail rather than write, for
            # any caller lacking the key.
            $Global:VaultMcpOpClasses = @('content:read', 'content:write')
            $content = [System.IO.File]::ReadAllText($script:ArchivedFile)
            $thrown = $null
            try {
                Invoke-VaultFrontmatterWrite -Content $content -Ops @(@{ op = 'add_hint'; text = 'a hint' }) | Out-Null
            }
            catch { $thrown = $_ }
            $thrown | Should -Not -BeNullOrEmpty
            ($thrown.Exception.Message | ConvertFrom-Json).code | Should -Be 'insufficient_capability'
        }
    }

    Context 'Capability denial - the content:write key on apply' {
        It 'denies an injected set lacking content:write, with zero mutation' {
            $Global:VaultMcpOpClasses = @('content:read')
            $before = [System.IO.File]::ReadAllText($script:DirtyFile)
            $err = Invoke-LintError @{ Passes = @('fixes'); Apply = $true }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'content:write'
            [System.IO.File]::ReadAllText($script:DirtyFile) | Should -BeExactly $before
        }

        It 'denies an injected but EMPTY set - not the same as absent' {
            $Global:VaultMcpOpClasses = @()
            (Invoke-LintError @{ Passes = @('fixes'); Apply = $true }).code | Should -Be 'insufficient_capability'
        }

        It 'asserts nothing in report mode, whatever the class set' {
            $Global:VaultMcpOpClasses = @()
            $result = Invoke-Lint @{ Passes = @('fixes') }
            $result.capabilitiesUsed | Should -BeNullOrEmpty
            $result.summary.fixable | Should -BeGreaterThan 0
        }

        It 'degrades open with NO injected set at all - in a separate process' {
            # Meaningful only cross-process: a leftover global in this runspace would make it pass
            # vacuously, which is why this spawns a real pwsh rather than clearing a variable.
            $psExe = (Get-Process -Id $PID).Path
            $output = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Passes 'fixes' -Apply:$true
            $LASTEXITCODE | Should -Be 0
            $result = $output | ConvertFrom-Json
            $result.applied | Should -BeTrue
            @($result.summary.filesWritten).Count | Should -BeGreaterThan 0
        }
    }

    Context 'Locking' {
        It 'report mode takes no lock - a held lock does not block a read' -Skip:(-not $IsWindows) {
            $signalPath = Join-Path $script:TempRoot 'lock-held-report.signal'
            $holder = Start-Job -ScriptBlock {
                param($ModulePath, $ConfigPath, $SignalPath)
                $env:VAULT_MCP_VAULTS_JSON = $ConfigPath
                Import-Module $ModulePath -Force
                Invoke-VaultWriteLock -Vault 'scratch' -ScriptBlock {
                    Set-Content -LiteralPath $SignalPath -Value 'held'
                    Start-Sleep -Seconds 30
                }
            } -ArgumentList $script:ModulePath, $env:VAULT_MCP_VAULTS_JSON, $signalPath

            try {
                $deadline = (Get-Date).AddSeconds(20)
                while (-not (Test-Path -LiteralPath $signalPath) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 100
                }
                (Test-Path -LiteralPath $signalPath) | Should -BeTrue -Because 'the background lock holder must have acquired before we contend'

                $prevTimeout = $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS
                $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = '1'
                try {
                    $result = Invoke-Lint @{ Passes = @('typography') }
                    $result.applied | Should -BeFalse
                    @((Get-LintPass $result 'typography').findings).Count | Should -Be 1

                    # ...and fix mode does contend, which is what makes the read's exemption a
                    # decision rather than an accident.
                    (Invoke-LintError @{ Passes = @('typography'); Apply = $true }).code | Should -Be 'lock_timeout'
                }
                finally {
                    if ($null -eq $prevTimeout) { Remove-Item Env:\VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS -ErrorAction SilentlyContinue }
                    else { $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = $prevTimeout }
                }
            }
            finally {
                Stop-Job $holder -ErrorAction SilentlyContinue
                Remove-Job $holder -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

Describe 'lint standalone invocability' {
    # Every tool must be invocable outside the server (Tool-Contracts: Standalone Invocability):
    # pwsh -File against a real vault, no runspace context, no ambient scope, no environment
    # setup beyond the vault config.
    BeforeAll {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-lint-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse
        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterAll {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs a report from pwsh -File and returns parseable JSON on stdout' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Passes 'checks'
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            ($rawOutput | ConvertFrom-Json).passesRun.Count | Should -Be 4
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'lands the coded JSON error on stderr for a standalone refusal' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $stderrPath = Join-Path $script:StandaloneTempRoot 'stderr.txt'
            $process = Start-Process -FilePath $psExe -PassThru -Wait -NoNewWindow -RedirectStandardError $stderrPath `
                -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $script:ScriptPath, '-Passes', 'not-a-pass')
            $process.ExitCode | Should -Not -Be 0
            $stderr = [System.IO.File]::ReadAllText($stderrPath)
            $stderr | Should -Match '"code"\s*:\s*"invalid_argument"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}

Describe 'lint remedy payload contract' {
    # LAST IN THE FILE ON PURPOSE. Pester runs Describes in file order, so by the time this one
    # starts, $global:LintRemedySamples holds every remedy every Invoke-Lint above produced. The
    # enumeration is therefore what lint EMITTED, not a list somebody wrote down - which is exactly
    # the drift this closes. See RemedyContract.ps1 for the walker and the Add-SyncAction exclusion.
    BeforeAll {
        $script:ToolsDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

        # The SPEC half. `action` is the semantic verb and does not name a tool: edit-frontmatter
        # and edit-patch are different facets of `edit` with different params shapes, and
        # add-suppression dispatches to lint_suppressions. $null is "advisory, dispatches nothing" -
        # a real classification, not a missing one.
        $script:LintToolContract = @{
            'edit-patch'       = 'edit'
            'edit-frontmatter' = 'edit'
            'add-suppression'  = 'lint_suppressions'
            'move'             = 'move'
            'manual'           = $null
            'pick-heading'     = $null
        }
        $script:LintSites = @(Get-VaultRemedyConstructionSite -Path $script:ScriptPath -Builder 'New-LintRemedy')
    }

    It 'passes -Tool at every New-LintRemedy construction site' {
        # Static, so it sees the sites no fixture reaches. -Tool is mandatory, so a missed site is
        # already a hard bind error at runtime - this names it at the source line instead.
        $script:LintSites.Count | Should -BeGreaterThan 0
        @($script:LintSites | Where-Object { -not $_.HasTool } | ForEach-Object { "lint.ps1:$($_.Line)" }) |
            Should -BeNullOrEmpty -Because 'every remedy construction site must state its dispatch target'
    }

    It 'classifies every action lint can construct' {
        $constructible = @($script:LintSites | ForEach-Object { $_.Action } | Where-Object { $_ } | Sort-Object -Unique)
        ($constructible -join ',') | Should -Be ((@($script:LintToolContract.Keys) | Sort-Object) -join ',') `
            -Because 'a new remedy action must be classified as dispatching or advisory, not silently unclassified'
    }

    It 'emits every action it can construct, so the walk is not partial' {
        # The anti-vacuity guard. A walker that quietly found three remedies would satisfy every
        # per-remedy check below; it cannot satisfy this one.
        $emitted = @($global:LintRemedySamples | ForEach-Object { $_.action } | Sort-Object -Unique)
        $constructible = @($script:LintSites | ForEach-Object { $_.Action } | Where-Object { $_ } | Sort-Object -Unique)
        ($emitted -join ',') | Should -Be ($constructible -join ',')
    }

    It 'holds the RemedyOption contract on every remedy the suite emitted' {
        $samples = @($global:LintRemedySamples)
        $samples.Count | Should -BeGreaterThan 0
        Write-Host "  lint remedy resolutions validated: $($samples.Count) across $((@($samples | ForEach-Object { $_.action } | Sort-Object -Unique)).Count) actions"
        $violations = @($samples | ForEach-Object {
                Test-VaultRemedyContract -Remedy $_ -ToolContract $script:LintToolContract -ToolsDir $script:ToolsDir
            })
        $violations | Should -BeNullOrEmpty
    }

    It 'reads the payload `action` key and the remedy `action` field as two different things' {
        # add-suppression's params carries lint_suppressions' own `action` wire parameter, valued
        # 'add', inside a remedy whose top-level action is 'add-suppression'. A walker that
        # flattened the remedy would conflate them; this asserts both readings survive intact.
        $suppression = @($global:LintRemedySamples | Where-Object { $_.action -eq 'add-suppression' })
        $suppression.Count | Should -BeGreaterThan 0
        foreach ($remedy in $suppression) {
            $remedy.action | Should -Be 'add-suppression'
            $remedy.params.action | Should -Be 'add'
        }
    }
}
