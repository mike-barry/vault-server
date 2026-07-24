# Tests for VaultAccess Public/Scope.ps1 (scope-root resolution and operation gating).
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:TempRoot = Initialize-VaultAccessTestFixture
}

AfterAll {
    Remove-VaultAccessTestFixture -TempRoot $script:TempRoot
}

Describe 'Resolve-VaultScopeRoots' {
    It 'returns an empty array when ScopeRoots is null' {
        @(Resolve-VaultScopeRoots -Vault test -ScopeRoots $null).Count | Should -Be 0
    }

    It 'throws when a non-null map has no entry for the vault' {
        { Resolve-VaultScopeRoots -Vault test -ScopeRoots @{ scratch = @('Projects/Widget') } } | Should -Throw '*Grant configuration invariant violated*'
    }

    It 'returns the flat root list for the matching vault, matched case-insensitively' {
        @(Resolve-VaultScopeRoots -Vault TEST -ScopeRoots @{ test = @('Projects/Widget') }) | Should -Be @('Projects/Widget')
    }

    It 'does not leak a different vault''s roots into the resolved vault' {
        $roots = @(Resolve-VaultScopeRoots -Vault test -ScopeRoots @{ test = @('Projects/Widget'); scratch = @('Areas/Homelab') })
        $roots | Should -Be @('Projects/Widget')
        $roots | Should -Not -Contain 'Areas/Homelab'
    }
}

Describe 'Assert-VaultOperationAllowed scoping' {
    It 'allows an in-scope path' {
        { Assert-VaultOperationAllowed -Vault test -RelativePath 'Projects/Widget/Widget.md' -ScopeRoots @('Projects/Widget') } | Should -Not -Throw
    }

    It 'throws on an out-of-scope path' {
        { Assert-VaultOperationAllowed -Vault test -RelativePath 'Resources/Gizmo-Reference.md' -ScopeRoots @('Projects/Widget') } | Should -Throw "*outside the grant's scope*"
    }

    It 'always allows the "." enumeration-gate path, even when scoped' {
        { Assert-VaultOperationAllowed -Vault test -RelativePath '.' -ScopeRoots @('Projects/Widget') } | Should -Not -Throw
    }

    It 'allows anything when ScopeRoots is empty (unscoped)' {
        { Assert-VaultOperationAllowed -Vault test -RelativePath 'Resources/Gizmo-Reference.md' -ScopeRoots @() } | Should -Not -Throw
    }
}
