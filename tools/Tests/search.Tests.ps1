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
