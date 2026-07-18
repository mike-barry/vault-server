# Pester suite for tools/resolve_wikilink.ps1. Exercises the script directly (not through
# the HTTP dispatcher) against a scratch copy of test-vault/, mirroring rename_note.Tests.ps1's
# setup.
# Run with: Invoke-Pester tools/Tests/resolve_wikilink.Tests.ps1

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'resolve_wikilink.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}

Describe 'resolve_wikilink' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-resolve-pester-' + [guid]::NewGuid().ToString('N'))
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

    It 'resolves a bare name to its vault-relative path' {
        & $script:ScriptPath -Link 'Gizmo-Reference' | Should -Be 'Resources/Gizmo-Reference.md'
    }

    It 'resolves a bracketed wikilink' {
        & $script:ScriptPath -Link '[[Gizmo-Reference]]' | Should -Be 'Resources/Gizmo-Reference.md'
    }

    It 'resolves a path-qualified target' {
        & $script:ScriptPath -Link 'Resources/Gizmo-Reference' | Should -Be 'Resources/Gizmo-Reference.md'
    }

    It 'reports a non-throwing not-found result rather than an error' {
        & $script:ScriptPath -Link 'Nonexistent-Note' | Should -Match 'Not found'
    }

    It 'throws listing candidates when the link is ambiguous' {
        New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'A'), (Join-Path $script:VaultDir 'B') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'A/Dupe.md') -Value "# Dupe A`n"
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'B/Dupe.md') -Value "# Dupe B`n"

        { & $script:ScriptPath -Link 'Dupe' } | Should -Throw '*Ambiguous wikilink*'
    }
}
