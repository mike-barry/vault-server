# Tests for VaultAccess Public/Enumerate.ps1 (note/file enumeration and scoped resolution).
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:TempRoot = Initialize-VaultAccessTestFixture
}

AfterAll {
    Remove-VaultAccessTestFixture -TempRoot $script:TempRoot
}

Describe 'Get-VaultNotePath' {
    It 'enumerates fixture notes plus injected dupes' {
        @(Get-VaultNotePath).Count | Should -Be 8
    }

    It 'prunes dot-directories' {
        @(Get-VaultNotePath) -like '*.obsidian*' | Should -BeNullOrEmpty
    }

    It 'returns forward-slash relative paths' {
        @(Get-VaultNotePath) | Should -Contain 'Projects/Widget/Widget.md'
    }
}

Describe 'Get-VaultNotePath scoping' {
    It 'filters to notes under the scope root' {
        $notes = @(Get-VaultNotePath -ScopeRoots @{ test = @('Projects/Widget') })
        $notes | Should -Contain 'Projects/Widget/Widget.md'
        $notes | Where-Object { $_ -notlike 'Projects/Widget/*' } | Should -BeNullOrEmpty
    }

    It 'drops out-of-scope notes rather than erroring' {
        $notes = @(Get-VaultNotePath -ScopeRoots @{ test = @('Projects/Widget') })
        $notes | Should -Not -Contain 'Resources/Gizmo-Reference.md'
    }

    It 'an unscoped call (no ScopeRoots) still returns everything' {
        @(Get-VaultNotePath).Count | Should -Be 8
    }

    It 'a scope root configured for a different vault does not leak into this vault' {
        # "test" (the vault being enumerated) has an explicit empty entry - unscoped -
        # while "scratch" is scoped; the scratch entry must not leak into test.
        $notes = @(Get-VaultNotePath -Vault test -ScopeRoots @{ test = @(); scratch = @('Projects/Widget') })
        $notes | Should -Contain 'Resources/Gizmo-Reference.md'
    }
}

Describe 'Resolve-WikiLink scoping' {
    It 'does not resolve an out-of-scope note' {
        Resolve-WikiLink -Link 'Gizmo-Reference' -ScopeRoots @{ test = @('Projects/Widget') } | Should -BeNullOrEmpty
    }

    It 'resolves an in-scope note' {
        Resolve-WikiLink -Link 'Widget' -ScopeRoots @{ test = @('Projects/Widget') } | Should -Be 'Projects/Widget/Widget.md'
    }

    It 'a scope root configured for a different vault does not restrict this vault' {
        Resolve-WikiLink -Vault scratch -Link 'Gizmo-Reference' -ScopeRoots @{ scratch = @(); test = @('Projects/Widget') } | Should -Be 'Resources/Gizmo-Reference.md'
    }
}

Describe 'Get-VaultFilePath' {
    It 'enumerates every fixture file, including non-note files if present' {
        # The 'test' vault has 6 real fixture notes plus the 2 injected dupes = 8; the
        # injected .obsidian/junk.md stays invisible (ripgrep hides dot-directories by
        # default), matching Get-VaultNotePath's own count.
        @(Get-VaultFilePath).Count | Should -Be 8
    }

    It 'prunes dot-directories, matching Get-VaultNotePath' {
        @(Get-VaultFilePath) -like '*.obsidian*' | Should -BeNullOrEmpty
    }

    It 'returns forward-slash vault-relative paths' {
        @(Get-VaultFilePath) | Should -Contain 'Projects/Widget/Widget.md'
    }

    It 'agrees with Get-VaultNotePath on the note set, so move and glob cannot drift' {
        (@(Get-VaultFilePath) | Sort-Object) | Should -Be (@(Get-VaultNotePath) | Sort-Object)
    }

    It 'filters to notes under the scope root, mirroring Get-VaultNotePath scoping' {
        $files = @(Get-VaultFilePath -ScopeRoots @{ test = @('Projects/Widget') })
        $files | Should -Not -BeNullOrEmpty
        $files | ForEach-Object { $_ | Should -Match '^Projects/Widget/' }
    }

    It 'throws dependency_unavailable-shaped when ripgrep cannot be resolved' {
        { Get-VaultFilePath -RgPath (Join-Path $script:TempRoot 'no-such-rg.exe') } | Should -Throw '*dependency_unavailable*'
    }
}
