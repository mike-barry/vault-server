# Pester suite for tools/search.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring rename_note.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/search.Tests.ps1

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'search.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}

Describe 'search' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-search-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $configPath = Join-Path $script:TempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
        $env:VAULT_MCP_VAULTS_JSON = $configPath
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'skips an unreadable note instead of aborting the search' {
        # search is Read-class and takes no write lock, so it stays exposed to concurrent
        # mutation the mutex cannot cover - which is why skip-missing applies to reads.
        # Creating a symlink needs Developer Mode or elevation, so this skips rather than
        # passing vacuously.
        $dangling = Join-Path $script:VaultDir 'Resources/Dangling.md'
        try {
            New-Item -ItemType SymbolicLink -Path $dangling -Target (Join-Path $script:VaultDir 'Resources/no-such-target.md') -ErrorAction Stop | Out-Null
        }
        catch {
            Set-ItResult -Skipped -Because 'creating a symbolic link requires Developer Mode or elevation on this machine'
            return
        }

        $results = & $script:ScriptPath -Query 'quorvium'
        ($results -join "`n") | Should -Match 'Resources/Gizmo-Reference\.md'
    }

    It 'does not let skip-missing mask a real read failure' {
        # A present-but-unreadable note must surface, not be silently dropped - otherwise
        # search would under-report hits while looking successful.
        $locked = Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md'
        $fs = [System.IO.File]::Open($locked, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            { & $script:ScriptPath -Query 'quorvium' } | Should -Throw
        }
        finally { $fs.Dispose() }
    }

    It 'finds every note containing the fixture-unique "quorvium" token' {
        $results = & $script:ScriptPath -Query 'quorvium'
        $results | Should -Not -BeNullOrEmpty
        ($results -join "`n") | Should -Match 'Resources/Gizmo-Reference\.md'
    }

    It 'is case-insensitive' {
        $results = & $script:ScriptPath -Query 'QUORVIUM'
        ($results -join "`n") | Should -Match 'Resources/Gizmo-Reference\.md'
    }

    It 'includes a match snippet per hit' {
        $results = & $script:ScriptPath -Query 'quorvium'
        ($results -join "`n") | Should -Match 'quorvium alloy'
    }

    It 'returns a non-throwing no-matches result for a query with no hits' {
        $results = & $script:ScriptPath -Query 'zzz-token-not-in-vault-zzz' | Out-String
        $results | Should -Match 'No matches'
    }

    It 'matches on filename as well as content' {
        $results = & $script:ScriptPath -Query 'Gizmo-Reference'
        ($results -join "`n") | Should -Match 'Resources/Gizmo-Reference\.md'
    }

    It 'throws when the query is empty' {
        { & $script:ScriptPath -Query '' } | Should -Throw
    }
}
