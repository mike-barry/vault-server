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
# Suppression state files are created by test setup rather than committed into test-vault/:
# they are vault state, and a committed copy would make the fixture's own suppressions part of
# every assertion. The tool reads the configured locations only - there is no ignore-file
# override on its surface - so redirection happens by pointing the whole vault at a temp copy.

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'lint.ps1')).Path
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:ModulePath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1')).Path
    Import-Module $script:ModulePath -Force

    function Invoke-Lint {
        # Runs lint.ps1 and returns its parsed Tier-2 object. Defined in this top-level BeforeAll
        # (rather than in a Describe body) so it survives into Pester v5's Run phase.
        param([hashtable] $Params)
        & $script:ScriptPath @Params | ConvertFrom-Json
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

    function Invoke-RemedyThroughEdit {
        param($Remedy)
        & $script:EditScriptPath -Path $Remedy.params.path -FrontmatterOps $Remedy.params.frontmatterOps | ConvertFrom-Json
    }

    function Invoke-RemedyThroughEditError {
        # The refusal half: returns the parsed {code, message, data?} an undispatchable payload
        # earns, which is what proves a `requires` declaration is honest rather than decorative.
        param($Remedy)
        $errorRecord = $null
        try {
            & $script:EditScriptPath -Path $Remedy.params.path -FrontmatterOps $Remedy.params.frontmatterOps | Out-Null
            throw 'edit.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }

    function Get-RemedyOps {
        # -NoEnumerate, exactly as edit.ps1 parses the parameter: a plain ConvertFrom-Json
        # unrolls the outer array and would silently flatten a DOUBLE-wrapped payload back into
        # the shape the test expected, hiding the malformation edit would refuse.
        param($Remedy)
        @($Remedy.params.frontmatterOps | ConvertFrom-Json -NoEnumerate)
    }

    function Set-RemedyOpField {
        # Fills a `requires` slot the way a caller must: decode the JSON-string param, set the
        # named field, re-serialize. -InputObject over an array already emits a JSON array, so
        # -AsArray here would double-wrap it - the same trap lint's own serializer fell into.
        param($Remedy, [int] $OpIndex, [string] $Field, [string] $Value)
        $ops = @($Remedy.params.frontmatterOps | ConvertFrom-Json)
        $ops[$OpIndex].$Field = $Value
        $Remedy.params.frontmatterOps = (ConvertTo-Json -InputObject $ops -Depth 6 -Compress)
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
            # The wrapper's byte preservation, which the skill-side original did not have: the
            # pinned entry and the body are untouched.
            $content | Should -Match 'pinned: "Frozen - do not rewrite"'
        }

        It 'reports missing doc.type as remedy-only, with the value declared in requires' {
            $result = Invoke-Lint @{ Passes = @('provenance') }
            $finding = (Get-LintPass $result 'provenance').findings |
                Where-Object { $_.issue -eq 'missing-doc-type' } | Select-Object -First 1
            $finding.disposition | Should -Be 'reported'
            $remedy = $finding.remedyOptions[0]
            $remedy.action | Should -Be 'edit-frontmatter'
            # NOT dispatchable verbatim: the value is a judgment, which the label always said and
            # the field now says too.
            @($remedy.requires) | Should -Be @('frontmatterOps.0.value')
            # edit requires an array of ops, and a bare object - or a nested array - is refused.
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
            $global.params.PSObject.Properties.Name | Should -Not -Contain 'scopePath'
            $scoped = $finding.remedyOptions | Where-Object { $_.id -eq 'suppress-scoped' }
            $scoped.params.scopePath | Should -Be 'Resources'

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
            $remedy.params.oldString | Should -Be '[Torque Specs](Gizmo-Reference#torque-specs)'
            $remedy.params.newString | Should -Be '[[Gizmo-Reference#Torque Specs]]'
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
            @($repoint.requires) | Should -Be @('frontmatterOps.1.folder')
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
            $after | Should -Match 'link: "\[\[Gizmo-Reference\]\]"'
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
            # A single op serializes as a JSON ARRAY - and as a FLAT one. A double-wrapped
            # payload is what edit refuses with malformed_op "missing 'op' member".
            $remedy.params.frontmatterOps | Should -Match '^\[\{'
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

            [System.IO.File]::ReadAllText($widget) | Should -Match 'folder: "Projects/Widget/Inbox"'
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
            @($remedy.requires) | Should -Be @('frontmatterOps.0.description', 'frontmatterOps.0.load_when')
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
            $after | Should -Match 'link: "\[\[Widget-Design\]\]"'
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
            @($remedy.requires) | Should -Be @('frontmatterOps.0.description', 'frontmatterOps.0.load_when')
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
                                $path = "frontmatterOps.$i.$($prop.Name)"
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
            @($finding.remedyOptions[0].requires) | Should -Be @('frontmatterOps.0.value')
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
            $finding.remedyOptions[0].params.newString | Should -Match 'eighty columns which is the continuation'
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
            $result = Invoke-Lint @{ Passes = @('hard-wrap'); HardWrapInspect = $true }
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
            $err = Invoke-LintError @{ Passes = @('hard-wrap'); HardWrapInspect = $true; Apply = $true }
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
