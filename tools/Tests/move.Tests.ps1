# Pester suite for tools/move.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring the VaultAccess module's
# own Pester setup and tools.old/Tests/rename_note.Tests.ps1's convention.
# Run with: Invoke-Pester tools/Tests/move.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside move.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: move.ps1 is currently a scaffold (see its .SYNOPSIS) - argument validation,
# source resolution, dest construction, the ambiguity gate, and the reference-rewrite
# engine are later implementation slices. This suite covers only what the scaffold slice
# promises: the tool loads, dispatches through the write lock, and returns a well-formed
# Tier-2 JSON result - plus the standalone-invocability property every tool script must
# hold and carry a unit test for (Plan-Vault-Tool-Primitives's Tool Authoring Standards).
# The full behavioral Pester suite (file rename + rewrite, folder moves, ambiguity gate,
# every error code, dispatcher floor-gating) lands with the later slices that implement
# the behavior it exercises.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'move.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}

Describe 'move (scaffold)' {
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

    It 'loads, dispatches through the write lock, and returns a well-formed Tier-2 JSON object' {
        $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
        { $output | ConvertFrom-Json } | Should -Not -Throw

        $result = $output | ConvertFrom-Json
        $result.oldPath | Should -Be 'Resources/Gizmo-Reference.md'
        $result.newPath | Should -Be 'Renamed-Gizmo'
        $result.referencesRewritten | Should -Be 0
        $result.filesRewritten | Should -BeNullOrEmpty
        $result.stub | Should -BeTrue

        # The scaffold's stub body performs no mutation - proves the write lock wraps the
        # (currently inert) operation without side effects landing on the vault.
        Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
    }

    It 'accepts the optional Kind and Force parameters without erroring' {
        $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo' -Kind file -Force
        { $output | ConvertFrom-Json } | Should -Not -Throw
    }

    It 'rejects a Kind other than file or folder (ValidateSet, AST-derived MCP schema surface)' {
        { & $script:ScriptPath -Source 'A' -Dest 'B' -Kind 'project' } | Should -Throw
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
        $result.stub | Should -BeTrue
    }
}
