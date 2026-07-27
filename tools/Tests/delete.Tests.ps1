# Pester suite for tools/delete.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring move/create's setup. Every
# mutating case targets the temp scratch copy only - the committed test-vault/ is never written.
# Run with: Invoke-Pester tools/Tests/delete.Tests.ps1
#
# Lives in tools/Tests/ rather than beside delete.ps1: ScriptToolLoader scans tools/*.ps1
# (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting directly in tools/
# would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: argument validation and kind inference, the inert-recursive refusal, the
# filesystem-walk target enumeration (the .placeholder regression this tool's spec was amended
# for, plus dot-files in filesDeleted[]), the nested-repository refusal, git recoverability
# classification (unrecoverableFiles[] over untracked and gitignored files, in a real temp repo),
# the agentic:write capability three-way split, the report-only danglingBacklinks scan, the two
# folder-note fields, readonly pre-flight, apply:false, partial_state, cross-process lock_timeout,
# and standalone invocability.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'delete.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-DeleteError {
        # Runs delete.ps1, expecting the shared {code, message, data?} JSON error shape, and
        # returns the parsed error object. Defined in this top-level BeforeAll (rather than in a
        # Describe body) so it survives into Pester v5's Run phase.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'delete.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }
}

Describe 'delete' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-delete-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $configPath = Join-Path $script:TempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
        $env:VAULT_MCP_VAULTS_JSON = $configPath
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'single-file delete' {
        It 'returns a well-formed Tier-2 JSON object and removes the file' {
            $output = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md'
            { $output | ConvertFrom-Json } | Should -Not -Throw

            $result = $output | ConvertFrom-Json
            $result.applied | Should -BeTrue
            $result.path | Should -Be 'Inbox/Note-Loose-Thought.md'
            $result.kind | Should -Be 'file'
            $result.filesDeleted | Should -Be @('Inbox/Note-Loose-Thought.md')
            $result.folderNoteDeleted | Should -BeFalse
            $result.folderNoteOrphaned | Should -BeFalse
            $result.recoveryHint | Should -Not -BeNullOrEmpty

            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Note-Loose-Thought.md') | Should -BeFalse
        }

        It 'carries the full write-class envelope, with lockWarning omitted when it did not fire' {
            $result = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md' | ConvertFrom-Json
            $names = $result.PSObject.Properties.Name
            $names | Should -Contain 'capabilitiesUsed'
            $names | Should -Contain 'filesSkippedUnreadable'
            $names | Should -Contain 'unrecoverableFiles'
            $names | Should -Contain 'danglingBacklinks'
            $names | Should -Not -Contain 'lockWarning'
            $result.capabilitiesUsed.Count | Should -Be 0
            $result.filesSkippedUnreadable.Count | Should -Be 0
        }

        It 'accepts a matching kind assertion' {
            $result = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md' -Kind 'file' | ConvertFrom-Json
            $result.kind | Should -Be 'file'
        }

        It 'takes the path verbatim - no ".md" inference' {
            # "Inbox/Note-Loose-Thought" is a different value from the note's path, and there is
            # no file at it, so this must miss rather than quietly delete the note.
            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought' }
            $err.code | Should -Be 'not_found'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Note-Loose-Thought.md') | Should -BeTrue
        }
    }

    Context 'argument validation' {
        It 'errors not_found when nothing is at the path' {
            $err = Invoke-DeleteError @{ Path = 'Inbox/Nope.md' }
            $err.code | Should -Be 'not_found'
            $err.data.path | Should -Be 'Inbox/Nope.md'
        }

        It 'errors kind_mismatch when file is asserted against a folder' {
            $err = Invoke-DeleteError @{ Path = 'Projects/Widget'; Kind = 'file' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.assertedKind | Should -Be 'file'
            $err.data.resolvedKind | Should -Be 'folder'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget') | Should -BeTrue
        }

        It 'errors kind_mismatch when folder is asserted against a file' {
            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md'; Kind = 'folder' }
            $err.code | Should -Be 'kind_mismatch'
        }

        It 'rejects an unrecognized kind as invalid_argument (not a PowerShell binding error)' {
            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md'; Kind = 'note' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.kind | Should -Be 'note'
        }

        It 'rejects an empty path as invalid_argument' {
            $err = Invoke-DeleteError @{ Path = '   ' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a vault-escaping path as invalid_argument' {
            $err = Invoke-DeleteError @{ Path = '../outside.md' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'refuses recursive against a file target as an inert parameter' {
            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md'; Recursive = $true }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'recursive'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Note-Loose-Thought.md') | Should -BeTrue
        }

        It 'refuses recursive:false against a file too - supplied is supplied' {
            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md'; Recursive = $false }
            $err.code | Should -Be 'invalid_argument'
        }
    }

    Context 'folder enumeration and the recursive guard' {
        It 'refuses a non-empty folder with folder_not_empty, naming a child sample' {
            $err = Invoke-DeleteError @{ Path = 'Resources' }
            $err.code | Should -Be 'folder_not_empty'
            $err.data.path | Should -Be 'Resources'
            $err.data.childCount | Should -Be 1
            $err.data.sampleChildren | Should -Contain 'Resources/Gizmo-Reference.md'
            $err.data.PSObject.Properties.Name | Should -Contain 'unrecoverableFiles'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
        }

        It 'trips folder_not_empty on a folder holding only a .placeholder (the enumeration regression)' {
            # The whole reason the plan comes from a filesystem walk rather than the ripgrep
            # census: the census hides dot-files, so this folder would enumerate as EMPTY, the
            # guard would never fire, and a triad folder would delete without recursive.
            $placeholderDir = Join-Path $script:VaultDir 'Areas/Outdoors/Corpus'
            New-Item -ItemType Directory -Path $placeholderDir -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $placeholderDir '.placeholder') -Value ''

            $err = Invoke-DeleteError @{ Path = 'Areas/Outdoors/Corpus' }
            $err.code | Should -Be 'folder_not_empty'
            $err.data.childCount | Should -Be 1
            $err.data.sampleChildren | Should -Contain 'Areas/Outdoors/Corpus/.placeholder'
            Test-Path -LiteralPath (Join-Path $placeholderDir '.placeholder') | Should -BeTrue
        }

        It 'deletes a genuinely empty folder with no recursive flag' {
            $emptyDir = Join-Path $script:VaultDir 'Areas/Empty'
            New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null

            $result = & $script:ScriptPath -Path 'Areas/Empty' | ConvertFrom-Json
            $result.kind | Should -Be 'folder'
            $result.filesDeleted.Count | Should -Be 0
            Test-Path -LiteralPath $emptyDir | Should -BeFalse
        }

        It 'deletes a folder recursively and reports every file, dot-files included' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/.placeholder') -Value ''

            $result = & $script:ScriptPath -Path 'Projects/Widget' -Recursive $true | ConvertFrom-Json
            $result.applied | Should -BeTrue
            $result.kind | Should -Be 'folder'
            $result.filesDeleted | Should -Contain 'Projects/Widget/.placeholder'
            $result.filesDeleted | Should -Contain 'Projects/Widget/Widget.md'
            $result.filesDeleted | Should -Contain 'Projects/Widget/Inbox/Plan-Widget-Assembly.md'
            $result.folderNoteDeleted | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget') | Should -BeFalse
        }

        It 'refuses a folder containing a nested repository root even with recursive:true' {
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Projects/Widget/vendor/.git') -Force | Out-Null

            $err = Invoke-DeleteError @{ Path = 'Projects/Widget'; Recursive = $true }
            $err.code | Should -Be 'nested_repository'
            $err.data.nestedRepository | Should -Be 'Projects/Widget/vendor/.git'
            # No recursive override, and nothing removed.
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') | Should -BeTrue
        }

        It 'refuses a nested repository declared as a .git FILE (a submodule working tree)' {
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Projects/Widget/vendor') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/vendor/.git') -Value 'gitdir: ../../.git/modules/vendor'

            $err = Invoke-DeleteError @{ Path = 'Projects/Widget'; Recursive = $true }
            $err.code | Should -Be 'nested_repository'
        }
    }

    Context 'folder-note fields' {
        It 'reports folderNoteOrphaned when a folder note is deleted and its folder survives' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' | ConvertFrom-Json
            $result.folderNoteOrphaned | Should -BeTrue
            $result.folderNoteDeleted | Should -BeFalse
            # Warn-and-proceed: the note goes, the folder is left for lint.
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget') | Should -BeTrue
        }

        It 'does not report folderNoteOrphaned for an ordinary note' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' | ConvertFrom-Json
            $result.folderNoteOrphaned | Should -BeFalse
        }
    }

    Context 'danglingBacklinks (report-only, vault-wide, unscoped)' {
        It 'names every note whose link resolved to the deleted target' {
            $result = & $script:ScriptPath -Path 'Resources/Gizmo-Reference.md' | ConvertFrom-Json
            $files = @($result.danglingBacklinks | ForEach-Object { $_.file }) | Sort-Object -Unique
            $files | Should -Contain 'Projects/Widget/Tasks.md'
            $files | Should -Contain 'Projects/Widget/Widget.md'
            $files | Should -Contain 'Projects/Widget/Inbox/Plan-Widget-Assembly.md'
            # Both the bare and the path-qualified spellings are identified.
            @($result.danglingBacklinks | ForEach-Object { $_.link }) | Should -Contain 'Gizmo-Reference'
            @($result.danglingBacklinks | ForEach-Object { $_.link }) | Should -Contain 'Resources/Gizmo-Reference'
            # Report-only: no link is rewritten.
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Projects/Widget/Tasks.md')) |
                Should -Match '\[\[Gizmo-Reference\]\]'
        }

        It 'reports an empty list when nothing links at the deleted file' {
            $result = & $script:ScriptPath -Path 'Test-Vault.md' | ConvertFrom-Json
            $result.danglingBacklinks.Count | Should -Be 0
        }

        It 'scans outside the grant''s scope roots - the operand alone is gated' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Resources') }
            $result = & $script:ScriptPath -Path 'Resources/Gizmo-Reference.md' | ConvertFrom-Json
            @($result.danglingBacklinks | ForEach-Object { $_.file }) | Should -Contain 'Projects/Widget/Tasks.md'
        }
    }

    Context 'assess-before-mutate (apply)' {
        It 'apply:false removes nothing and returns the identical shape with applied:false' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $plan = & $script:ScriptPath -Path 'Projects/Widget' -Recursive $true -Apply:$false | ConvertFrom-Json

            $plan.applied | Should -BeFalse
            $plan.filesDeleted.Count | Should -Be 3
            $plan.folderNoteDeleted | Should -BeTrue
            $plan.PSObject.Properties.Name | Should -Contain 'capabilitiesUsed'
            $plan.PSObject.Properties.Name | Should -Contain 'filesSkippedUnreadable'
            $plan.PSObject.Properties.Name | Should -Contain 'recoveryHint'
            $plan.capabilitiesUsed | Should -Be @('agentic:write')

            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') | Should -BeTrue
        }

        It 'a plan and its result differ in `applied` alone' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $plan = & $script:ScriptPath -Path 'Projects/Widget' -Recursive $true -Apply:$false | ConvertFrom-Json
            $real = & $script:ScriptPath -Path 'Projects/Widget' -Recursive $true | ConvertFrom-Json

            $plan.PSObject.Properties.Name | Should -Be $real.PSObject.Properties.Name
            ($plan | Select-Object -ExcludeProperty applied | ConvertTo-Json -Depth 6) |
                Should -Be ($real | Select-Object -ExcludeProperty applied | ConvertTo-Json -Depth 6)
            $plan.applied | Should -BeFalse
            $real.applied | Should -BeTrue
        }

        It 'apply:false still refuses everything a real call would refuse' {
            $err = Invoke-DeleteError @{ Path = 'Resources'; Apply = $false }
            $err.code | Should -Be 'folder_not_empty'
        }
    }

    Context 'capability denial (delete asserts agentic:write over the deleted set)' {
        It 'denies deleting a note carrying nav.* keys, with zero mutation and the path named' {
            $Global:VaultMcpOpClasses = @('content:write')
            $err = Invoke-DeleteError @{ Path = 'Projects/Widget/Widget.md' }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'agentic:write'
            $err.data.paths | Should -Contain 'Projects/Widget/Widget.md'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') | Should -BeTrue
        }

        It 'denies a folder delete when ANY contained note carries the agentic layer' {
            $Global:VaultMcpOpClasses = @('content:write')
            $err = Invoke-DeleteError @{ Path = 'Projects/Widget'; Recursive = $true }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.paths | Should -Contain 'Projects/Widget/Widget.md'
            # Validate-phase: not one file was removed.
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') | Should -BeTrue
        }

        It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
            $Global:VaultMcpOpClasses = @()
            $err = Invoke-DeleteError @{ Path = 'Projects/Widget/Widget.md' }
            $err.code | Should -Be 'insufficient_capability'
        }

        It 'allows the same delete when the injected set holds agentic:write' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
        }

        It 'never asserts the key for a note carrying only doc.* keys, even with an empty set' {
            $Global:VaultMcpOpClasses = @()
            $result = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md' | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }

        It 'never asserts the key for a non-note file, even with an empty set' {
            $Global:VaultMcpOpClasses = @()
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Resources/diagram.png') -Value 'not really a png'
            $result = & $script:ScriptPath -Path 'Resources/diagram.png' | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }
    }

    Context 'git recoverability (unrecoverableFiles)' {
        BeforeEach {
            # A real temp repo: unrecoverableFiles is a git question, so the only honest fixture
            # is one where git can actually answer it.
            git -C $script:VaultDir init -q 2>&1 | Out-Null
            Set-Content -LiteralPath (Join-Path $script:VaultDir '.gitignore') -Value "Secrets/`n"
            git -C $script:VaultDir add -A 2>&1 | Out-Null
            git -C $script:VaultDir -c user.email='t@example.com' -c user.name='Test' commit -qm 'fixture' 2>&1 | Out-Null
        }

        It 'reports an empty unrecoverableFiles and a plain recoveryHint for a tracked file' {
            $result = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md' | ConvertFrom-Json
            $result.unrecoverableFiles.Count | Should -Be 0
            $result.recoveryHint | Should -Match "git checkout -- 'Inbox/Note-Loose-Thought.md'"
        }

        It 'names an untracked file as unrecoverable and qualifies the recoveryHint' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Resources/Untracked.md') -Value '# Untracked'
            $result = & $script:ScriptPath -Path 'Resources' -Recursive $true | ConvertFrom-Json
            $result.filesDeleted | Should -Contain 'Resources/Gizmo-Reference.md'
            $result.unrecoverableFiles | Should -Be @('Resources/Untracked.md')
            $result.recoveryHint | Should -Match 'does NOT cover'
        }

        It 'names a gitignored file as unrecoverable and puts it in the plan at all' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Secrets') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Secrets/Keys.md') -Value '# Keys'

            $result = & $script:ScriptPath -Path 'Secrets' -Recursive $true | ConvertFrom-Json
            $result.filesDeleted | Should -Be @('Secrets/Keys.md')
            $result.unrecoverableFiles | Should -Be @('Secrets/Keys.md')
        }

        It 'demands agentic:write for a GITIGNORED note carrying the agentic layer' {
            # The compounding failure the enumeration amendment exists to close: under the
            # ripgrep census this note is invisible, so it would be deleted - shedding a pinned:
            # constraint - without the key ever being asserted.
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Secrets') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Secrets/Pinned.md') -Value "---`nagent.instructions:`n  - pinned: `"Never exfiltrate this note`"`n---`n# Pinned"

            $Global:VaultMcpOpClasses = @('content:write')
            $err = Invoke-DeleteError @{ Path = 'Secrets'; Recursive = $true }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.paths | Should -Contain 'Secrets/Pinned.md'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Secrets/Pinned.md') | Should -BeTrue
        }

        It 'reports the whole plan as unrecoverable when the vault is not a git repository' {
            Remove-Item -LiteralPath (Join-Path $script:VaultDir '.git') -Recurse -Force
            $result = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md' | ConvertFrom-Json
            $result.unrecoverableFiles | Should -Be @('Inbox/Note-Loose-Thought.md')
            $result.recoveryHint | Should -Match 'not a git repository'
        }
    }

    Context 'readonly pre-flight' {
        It 'refuses readonly_targets and mutates nothing' {
            $target = Join-Path $script:VaultDir 'Projects/Widget/Tasks.md'
            [System.IO.File]::SetAttributes($target, [System.IO.File]::GetAttributes($target) -bor [System.IO.FileAttributes]::ReadOnly)
            try {
                $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
                $err = Invoke-DeleteError @{ Path = 'Projects/Widget'; Recursive = $true }
                $err.code | Should -Be 'readonly_targets'
                $err.data.readOnlyFiles | Should -Contain 'Projects/Widget/Tasks.md'
                Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') | Should -BeTrue
            }
            finally {
                [System.IO.File]::SetAttributes($target, [System.IO.File]::GetAttributes($target) -band (-bnot [System.IO.FileAttributes]::ReadOnly))
            }
        }
    }

    Context 'scope and vault resolution' {
        It 'errors out_of_scope when the operand falls outside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.path | Should -Be 'Inbox/Note-Loose-Thought.md'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Note-Loose-Thought.md') | Should -BeTrue
        }

        It 'deletes normally when the operand falls inside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $result = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' | ConvertFrom-Json
            $result.applied | Should -BeTrue
        }

        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md'; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
            $err.data.vault | Should -Be 'nope'
        }

        It 'refuses a configured vault whose root is missing with vault_unavailable' {
            $configPath = Join-Path $script:TempRoot 'with-missing.json'
            @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir; gone = (Join-Path $script:TempRoot 'no-such-root') } } |
                ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md'; Vault = 'gone' }
            $err.code | Should -Be 'vault_unavailable'
        }
    }

    Context 'partial_state' {
        It 'names the files already removed when a folder delete fails partway' {
            # Constructed with an exclusive FileShare.None handle on the second planned file, so
            # the first removal lands and the second cannot. Non-.md files keep the failure in
            # the APPLY phase - a locked .md would fail the validate-phase agentic read as
            # read_failure instead, which is a different (and correct) refusal.
            $dir = Join-Path $script:VaultDir 'Areas/Attachments'
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $dir 'a.bin') -Value 'a'
            Set-Content -LiteralPath (Join-Path $dir 'b.bin') -Value 'b'

            $handle = $null
            try {
                $handle = [System.IO.File]::Open((Join-Path $dir 'b.bin'), 'Open', 'Read', 'None')
            }
            catch {
                Set-ItResult -Skipped -Because "an exclusive FileShare.None open was refused on this machine: $($_.Exception.Message)"
                return
            }

            try {
                $err = Invoke-DeleteError @{ Path = 'Areas/Attachments'; Recursive = $true }
                $err.code | Should -Be 'partial_state'
                $err.data.filesDeleted | Should -Be @('Areas/Attachments/a.bin')
                $err.data.remaining | Should -Be @('Areas/Attachments/b.bin')
                $err.message | Should -Match 'git checkout'
                Test-Path -LiteralPath (Join-Path $dir 'a.bin') | Should -BeFalse
                Test-Path -LiteralPath (Join-Path $dir 'b.bin') | Should -BeTrue
            }
            finally {
                $handle.Dispose()
            }
        }
    }

    Context 'lock_timeout' {
        It 'fails lock_timeout when the write lock is held by another process' -Skip:(-not $IsWindows) {
            # The write lock cannot be contended in-process (the mutex is reentrant for the
            # calling thread), so a genuinely separate process must hold it - the cross-process
            # convention in Testing.md. The chokepoint raises the coded shape itself and delete
            # does not catch it, so this asserts the code reaches the caller unchanged.
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
                    $err = Invoke-DeleteError @{ Path = 'Inbox/Note-Loose-Thought.md' }
                    $err.code | Should -Be 'lock_timeout'
                    $err.data.timeoutSeconds | Should -Be 1
                    Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Note-Loose-Thought.md') | Should -BeTrue
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
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*content:write\s*$'
        }

        It 'documents every parameter in comment-help, which the loader requires to load the tool' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            foreach ($p in @('path', 'kind', 'recursive', 'apply', 'vault')) {
                $scriptText | Should -Match "(?m)^\s*\.PARAMETER\s+$p\s*$"
            }
        }

        It 'declares no ValidateSet on kind, so an unrecognized value is delete''s own error' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Not -Match '\[ValidateSet\('
            $scriptText | Should -Match '(?ms)\.PARAMETER kind.*"file" or "folder"'
        }

        It 'declares no force/purge parameter - git is the undo' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Not -Match '(?m)^\s*\.PARAMETER\s+(force|purge)\s*$'
        }
    }
}

Describe 'delete standalone invocability' {
    # Per Tool-Contracts's Tool Authoring Standards: every tool script must be invocable outside
    # the MCP server - pwsh -File against a real vault, no runspace context, no ambient scope, no
    # environment setup - and carry a unit test asserting it. This spawns a genuinely separate
    # pwsh process so no global left over from the host's ScriptToolRunner wrapper
    # (VaultMcpScopeRoots, VaultMcpOpClasses) or from another test in this process could be
    # mistaken for injected host context.

    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-delete-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File delete.ps1" invocation (the Tool Authoring Standard spelling)' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'delete.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Inbox/Note-Loose-Thought.md'
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            $result = $rawOutput | ConvertFrom-Json
            $result.applied | Should -BeTrue
            $result.filesDeleted | Should -Be @('Inbox/Note-Loose-Thought.md')
            Test-Path -LiteralPath (Join-Path $script:StandaloneVaultDir 'Inbox/Note-Loose-Thought.md') | Should -BeFalse
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'degrades OPEN on an agentic note with no injected op-class set' {
        # The third state of the capability split, and the reason it needs a separate process: a
        # leftover global in the test runspace would make this pass vacuously.
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'delete.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Projects/Widget/Widget.md'
            $LASTEXITCODE | Should -Be 0
            $result = $rawOutput | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
            Test-Path -LiteralPath (Join-Path $script:StandaloneVaultDir 'Projects/Widget/Widget.md') | Should -BeFalse
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
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'delete.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Inbox/Nope.md' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"not_found"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}
