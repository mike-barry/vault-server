# Tests for VaultAccess Private/RipgrepHelpers.ps1 (shared ripgrep resolution and invocation).
# These functions are private (not exported), so they are exercised via InModuleScope.
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    Import-VaultAccessModule

    # A self-contained scratch tree with two files so Invoke-VaultRipgrep has something to walk.
    $script:RgTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-rg-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:RgTempRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $script:RgTempRoot 'Alpha.md') -Value '# Alpha'
    New-Item -ItemType Directory -Path (Join-Path $script:RgTempRoot 'Sub') | Out-Null
    Set-Content -LiteralPath (Join-Path $script:RgTempRoot 'Sub/Beta.md') -Value '# Beta'

    $script:RealRg = (Get-Command -Name 'rg' -CommandType Application).Source
}

AfterAll {
    Remove-Item -LiteralPath $script:RgTempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Resolve-VaultRgPath' {
    It 'returns an explicit -RgPath that exists' {
        $real = $script:RealRg
        InModuleScope VaultAccess -Parameters @{ Real = $real } {
            param($Real)
            Resolve-VaultRgPath -RgPath $Real | Should -Be $Real
        }
    }

    It 'throws dependency_unavailable for an explicit -RgPath that does not exist' {
        $bad = Join-Path $script:RgTempRoot 'no-such-rg.exe'
        InModuleScope VaultAccess -Parameters @{ Bad = $bad } {
            param($Bad)
            { Resolve-VaultRgPath -RgPath $Bad } | Should -Throw '*dependency_unavailable*'
        }
    }

    It 'prefers the VAULT_MCP_RG_PATH configured setting when no explicit path is given' {
        $real = $script:RealRg
        $prev = $env:VAULT_MCP_RG_PATH
        $env:VAULT_MCP_RG_PATH = $real
        try {
            InModuleScope VaultAccess -Parameters @{ Real = $real } {
                param($Real)
                Resolve-VaultRgPath | Should -Be $Real
            }
        }
        finally {
            if ($null -eq $prev) { Remove-Item Env:\VAULT_MCP_RG_PATH -ErrorAction SilentlyContinue } else { $env:VAULT_MCP_RG_PATH = $prev }
        }
    }

    It 'throws dependency_unavailable when the configured VAULT_MCP_RG_PATH does not exist' {
        $bad = Join-Path $script:RgTempRoot 'configured-missing-rg.exe'
        $prev = $env:VAULT_MCP_RG_PATH
        $env:VAULT_MCP_RG_PATH = $bad
        try {
            InModuleScope VaultAccess {
                { Resolve-VaultRgPath } | Should -Throw '*dependency_unavailable*'
            }
        }
        finally {
            if ($null -eq $prev) { Remove-Item Env:\VAULT_MCP_RG_PATH -ErrorAction SilentlyContinue } else { $env:VAULT_MCP_RG_PATH = $prev }
        }
    }

    It 'falls back to PATH when nothing is configured' {
        $prev = $env:VAULT_MCP_RG_PATH
        Remove-Item Env:\VAULT_MCP_RG_PATH -ErrorAction SilentlyContinue
        try {
            InModuleScope VaultAccess {
                (Resolve-VaultRgPath) | Should -Match 'rg(\.exe)?$'
            }
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_RG_PATH = $prev }
        }
    }
}

Describe 'Invoke-VaultRipgrep' {
    It 'returns exit 0 and stdout lines when files match' {
        $rg = $script:RealRg
        $root = $script:RgTempRoot
        $r = InModuleScope VaultAccess -Parameters @{ Rg = $rg; Root = $root } {
            param($Rg, $Root)
            Invoke-VaultRipgrep -RgPath $Rg -Arguments @('--files', '--', '.') -WorkingDirectory $Root
        }
        $r.ExitCode | Should -Be 0
        @($r.Stdout).Count | Should -Be 2
        $r.Stderr | Should -BeNullOrEmpty
    }

    It 'returns exit 1 and no stdout when a glob matches nothing' {
        $rg = $script:RealRg
        $root = $script:RgTempRoot
        $r = InModuleScope VaultAccess -Parameters @{ Rg = $rg; Root = $root } {
            param($Rg, $Root)
            Invoke-VaultRipgrep -RgPath $Rg -Arguments @('--files', '--glob-case-insensitive', '-g', '**/zzz-none.md', '--', '.') -WorkingDirectory $Root
        }
        $r.ExitCode | Should -Be 1
        @($r.Stdout) | Should -BeNullOrEmpty
    }

    It 'returns exit 2 and stderr for a malformed glob, never throwing' {
        $rg = $script:RealRg
        $root = $script:RgTempRoot
        $r = InModuleScope VaultAccess -Parameters @{ Rg = $rg; Root = $root } {
            param($Rg, $Root)
            Invoke-VaultRipgrep -RgPath $Rg -Arguments @('--files', '--glob-case-insensitive', '-g', '[', '--', '.') -WorkingDirectory $Root
        }
        $r.ExitCode | Should -Be 2
        $r.Stderr | Should -Match 'glob'
    }
}
