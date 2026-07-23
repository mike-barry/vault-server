# Pester suite for tools/read_note.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring rename_note.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/read_note.Tests.ps1

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'read_note.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}

Describe 'read_note' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-read-pester-' + [guid]::NewGuid().ToString('N'))
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

    It 'returns the full note content by default' {
        $content = & $script:ScriptPath -Note 'Widget' | Out-String
        $content | Should -Match 'nav\.contents:'
        $content | Should -Match '# Widget\.md - Sample Project for Fixture Testing'
    }

    It 'returns only the frontmatter block with -FrontmatterOnly' {
        $content = & $script:ScriptPath -Note 'Widget' -FrontmatterOnly | Out-String
        $content | Should -Match 'nav\.contents:'
        $content | Should -Not -Match '# Widget\.md'
    }

    It 'returns an empty result for -FrontmatterOnly on a note with no frontmatter' {
        $content = & $script:ScriptPath -Note 'Tasks' -FrontmatterOnly | Out-String
        $content.Trim() | Should -Be ''
    }

    It 'resolves the note the same way resolve_wikilink does (bracketed wikilink)' {
        $content = & $script:ScriptPath -Note '[[Gizmo-Reference]]' | Out-String
        $content | Should -Match 'quorvium alloy'
    }

    It 'throws when the note cannot be resolved' {
        { & $script:ScriptPath -Note 'Nonexistent-Note' } | Should -Throw '*not found*'
    }
}
