# Tests for VaultAccess Public/Config.ps1 (vault config resolution).
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:TempRoot = Initialize-VaultAccessTestFixture
}

AfterAll {
    Remove-VaultAccessTestFixture -TempRoot $script:TempRoot
}

Describe 'Vault config' {
    It 'falls back to the default vault' {
        (Resolve-VaultRoot).Name | Should -Be 'test'
    }

    It 'resolves an explicit vault' {
        (Resolve-VaultRoot -Vault scratch).Name | Should -Be 'scratch'
    }

    It 'returns the canonical name for case-insensitive input' {
        (Resolve-VaultRoot -Vault 'TEST').Name | Should -BeExactly 'test'
    }

    It 'throws on an unknown vault' {
        { Resolve-VaultRoot -Vault nope } | Should -Throw '*Unknown vault*'
    }

    It 'throws on a config whose default is not a configured vault' {
        $bad = Join-Path $script:TempRoot 'bad-vaults.json'
        @{ default_vault = 'b'; vaults = @{ a = $script:TempRoot } } | ConvertTo-Json | Set-Content -LiteralPath $bad
        { Initialize-VaultAccess -ConfigPath $bad } | Should -Throw '*not a configured vault*'
        Initialize-VaultAccess -ConfigPath (Join-Path $script:TempRoot 'vaults.json')
    }
}
