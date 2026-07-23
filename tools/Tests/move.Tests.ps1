# Pester suite for tools/move.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring the VaultAccess module's
# own Pester setup and tools.old/Tests/rename_note.Tests.ps1's convention.
# Run with: Invoke-Pester tools/Tests/move.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside move.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: this suite covers argument validation, source resolution + dest
# construction, and scope membership - the three checklist items implemented in this
# slice (see Plan-Tool-Move, vault-primary). The write-lock/plan-validate-apply structure,
# the ambiguity gate, the reference-rewrite engine, and the folder-move path are later
# slices and are not yet exercised here beyond confirming the return shape's placeholder
# fields (referencesRewritten: 0, stub: true, etc.).

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'move.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-MoveError {
        # Runs move.ps1, expecting it to throw the shared {code, message, data?} JSON error
        # shape, and returns the parsed error object for assertions. Defined in this
        # top-level BeforeAll (rather than inline in the Describe body) so it survives into
        # Pester v5's Run phase - a bare `function` statement in a Describe body only
        # executes during Discovery and would not be visible from It blocks.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'move.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }
}

Describe 'move (validate + resolve + scope)' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-move-pester-' + [guid]::NewGuid().ToString('N'))
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

    Context 'happy path - return shape' {
        It 'returns a well-formed Tier-2 JSON object for a bare-name rename' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            { $output | ConvertFrom-Json } | Should -Not -Throw

            $result = $output | ConvertFrom-Json
            $result.oldPath | Should -Be 'Resources/Gizmo-Reference.md'
            $result.newPath | Should -Be 'Resources/Renamed-Gizmo.md'
            $result.kind | Should -Be 'file'
            $result.pureMove | Should -BeFalse
            $result.referencesRewritten | Should -Be 0
            $result.filesRewritten | Should -BeNullOrEmpty
            $result.stub | Should -BeTrue

            # This slice's stub body performs no mutation - the write lock wraps validation
            # only, no side effects land on the vault yet.
            Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
        }

        It 'accepts the optional Kind and Force parameters without erroring' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo' -Kind file -Force
            { $output | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'resolves kind as folder for a directory source' {
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget'
            $result = $output | ConvertFrom-Json
            $result.kind | Should -Be 'folder'
            $result.oldPath | Should -Be 'Projects/Widget'
            $result.newPath | Should -Be 'Projects/Renamed-Widget'
        }
    }

    Context 'argument validation' {
        It 'rejects an empty dest as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = '   ' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects kind "project" as invalid_argument (not a PowerShell binding error)' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'project' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects kind "area" as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'area' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects an unrecognized kind value as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'note' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects dest equal to source (literal match) as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Resources/Gizmo-Reference.md' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a dest inside the source folder for a folder move as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Projects/Widget'; Dest = 'Projects/Widget/Nested' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a dest basename with a link-breaking character as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Bad[Name]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a dest basename with an OS-invalid character as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Bad:Name' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'reports a naming-convention violation as a warning, not a failure' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'lowercase_name'
            $result = $output | ConvertFrom-Json
            $result.namingWarnings | Should -Not -BeNullOrEmpty
        }

        It 'raises no naming warning for a convention-conformant dest' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.namingWarnings | Should -BeNullOrEmpty
        }
    }

    Context 'source resolution + dest construction' {
        It 'fails not_found for a missing source' {
            $err = Invoke-MoveError @{ Source = 'Resources/Does-Not-Exist.md'; Dest = 'Somewhere' }
            $err.code | Should -Be 'not_found'
            $err.data.source | Should -Be 'Resources/Does-Not-Exist.md'
        }

        It 'renames in place for a bare-name dest, keeping the current parent' {
            $output = & $script:ScriptPath -Source 'Projects/Widget/Inbox/Plan-Widget-Assembly.md' -Dest 'Renamed-Plan'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Projects/Widget/Inbox/Renamed-Plan.md'
        }

        It 'treats a dest containing "/" as a full vault-relative destination' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Archive/Old-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Archive/Old-Gizmo.md'
        }

        It 'normalizes a leading "/" to the vault root' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest '/Root-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Root-Gizmo.md'
        }

        It 'does not force a .md extension for a non-note file' {
            # Test-Vault.md's folder note aside, use a folder move to prove no extension is
            # forced onto a non-note dest.
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Widget-Renamed'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Projects/Widget-Renamed'
        }

        It 'leaves an explicit .md extension alone (no double extension)' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo.md'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Resources/Renamed-Gizmo.md'
        }

        It 'fails already_exists when the destination is occupied' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Projects/Widget/Tasks.md' }
            $err.code | Should -Be 'already_exists'
            $err.data.dest | Should -Be 'Projects/Widget/Tasks.md'
        }

        It 'allows a case-only rename without already_exists, and reports pureMove: false' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'gizmo-reference'
            { $output | ConvertFrom-Json } | Should -Not -Throw
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Resources/gizmo-reference.md'
            $result.pureMove | Should -BeFalse
        }

        It 'reports pureMove: true when the basename is unchanged' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Archive/Gizmo-Reference'
            $result = $output | ConvertFrom-Json
            $result.pureMove | Should -BeTrue
        }

        It 'fails kind_mismatch when Kind asserts folder for a file source' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'folder' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'file'
            $err.data.assertedKind | Should -Be 'folder'
        }

        It 'fails kind_mismatch when Kind asserts file for a folder source' {
            $err = Invoke-MoveError @{ Source = 'Projects/Widget'; Dest = 'Projects/Renamed-Widget'; Kind = 'file' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'folder'
        }
    }

    Context 'scope membership' {
        It 'passes when both source and dest are in scope' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Resources') }
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            { $output | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'fails out_of_scope naming the source when the source falls outside scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.paths | Should -Contain 'Resources/Gizmo-Reference.md'
        }

        It 'fails out_of_scope naming the dest when the dest falls outside scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Resources') }
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Projects/Escaped-Gizmo' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.paths | Should -Contain 'Projects/Escaped-Gizmo.md'
        }

        It 'passes unscoped when the grant has an empty scope-roots set for the vault' {
            $Global:VaultMcpScopeRoots = @{ scratch = @() }
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            { $output | ConvertFrom-Json } | Should -Not -Throw
        }
    }
}

Describe 'move standalone invocability' {
    # Per Plan-Vault-Tool-Primitives's Tool Authoring Standards: every tool script must be
    # invocable outside the MCP server - pwsh -File against a real vault, no runspace
    # context, no ambient scope, no environment setup - and carry a unit test asserting it.
    # This spawns a genuinely separate pwsh process (rather than dot-sourcing/calling the
    # script in this Pester session) so no global left over from the host's
    # ScriptToolRunner wrapper (VaultMcpScopeRoots, VaultMcpOpClasses) or from another test
    # in this same process could be mistaken for injected host context.

    BeforeAll {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-move-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterAll {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via pwsh -File with no runspace context or ambient scope and returns well-formed JSON' {
        # A fresh pwsh process has none of ScriptToolRunner's injected globals and no
        # inherited $env:VAULT_MCP_VAULTS_JSON from this session, so setting the env var
        # for just this child process's invocation is what "no ambient scope" requires -
        # the module's own default-path fallback (module-relative vaults.json) is what
        # standalone invocation against the real vault would otherwise use.
        $psExe = (Get-Process -Id $PID).Path
        $rawOutput = & $psExe -NoProfile -NonInteractive -Command "`$env:VAULT_MCP_VAULTS_JSON = '$($script:StandaloneConfigPath)'; & '$($script:ScriptPath)' -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'"
        $LASTEXITCODE | Should -Be 0

        { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
        $result = $rawOutput | ConvertFrom-Json
        $result.oldPath | Should -Be 'Resources/Gizmo-Reference.md'
        $result.newPath | Should -Be 'Resources/Renamed-Gizmo.md'
        $result.stub | Should -BeTrue
    }

    It 'still degrades open (no scope enforcement) with no injected VaultMcpScopeRoots' {
        $psExe = (Get-Process -Id $PID).Path
        $rawOutput = & $psExe -NoProfile -NonInteractive -Command "`$env:VAULT_MCP_VAULTS_JSON = '$($script:StandaloneConfigPath)'; & '$($script:ScriptPath)' -Source 'Projects/Widget/Tasks.md' -Dest 'Renamed-Tasks'"
        $LASTEXITCODE | Should -Be 0
        { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
    }
}
