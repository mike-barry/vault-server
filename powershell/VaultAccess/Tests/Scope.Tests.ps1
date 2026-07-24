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

Describe 'Assert-VaultOpClass' {
    # Pins the in-script op-class floor gate - the PowerShell-only elevation check that lets a tool
    # admitted on a lower floor class still require a higher class before an elevated operation. The
    # authority it reads is the host-injected $Global:VaultMcpOpClasses (set by ScriptToolRunner's
    # WrapperScript in the same runspace right before the tool script runs - see
    # src/VaultMcpServer/Tools/ScriptToolRunner.cs InvokeAsync/WrapperScript). These tests drive the
    # gate through that exact ambient variable (not the explicit -GrantClasses param) so the default
    # binding is what's under test, and the AfterEach ALWAYS clears it so the injected authority can
    # never leak into another It, Describe, or file and poison the rest of the suite.
    AfterEach {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
    }

    It 'passes when the injected granted set holds the required op class' {
        $Global:VaultMcpOpClasses = @('content:read', 'content:write')
        { Assert-VaultOpClass -OpClass 'content:write' } | Should -Not -Throw
    }

    It 'denies with an insufficient_capability JSON error when the injected set lacks the class' {
        $Global:VaultMcpOpClasses = @('content:read')
        # The thrown value is the compact JSON error object, surfaced verbatim as the exception
        # message. Assert its { code, message } shape the way the ToolResult suite does - parse the
        # JSON and branch on the stable `code` field, not just a substring match.
        $err = { Assert-VaultOpClass -OpClass 'content:write' } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'insufficient_capability'
        $obj.message | Should -BeLike "*content:write*"
    }

    It 'degrades OPEN when no host context was injected at all (absent global)' {
        # Deliberate openness: an absent $Global:VaultMcpOpClasses means a standalone `pwsh -File`
        # run or a direct call with no host - there is no injected authority to check against, so the
        # gate no-ops, exactly as an unscoped ($null) scope-roots map is treated as "no restriction
        # in play". This mirrors ScriptToolRunner's documented contract (a null opClasses leaves the
        # global unset; see the InvokeAsync <param name="opClasses"> doc). AfterEach has already
        # removed the variable, and no It in this block should have left it set, but assert the
        # absent-context posture explicitly rather than relying on ordering.
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        { Assert-VaultOpClass -OpClass 'content:write' } | Should -Not -Throw
    }

    It 'DENIES on an injected-but-empty set (authenticated "holds nothing", distinct from absent)' {
        # The $null-vs-empty distinction is load-bearing: an empty (non-null) array is a real,
        # authenticated grant that holds no op classes (e.g. the minimum-access floor) and must deny
        # every asserted class - NOT the same as absent context above, which degrades open.
        $Global:VaultMcpOpClasses = @()
        { Assert-VaultOpClass -OpClass 'content:write' } | Should -Throw '*insufficient_capability*'
    }

    It 'matches op classes case-insensitively (by design)' {
        # Op-class names are case-insensitive everywhere - the C# OpClassRegistry canonicalizes
        # lookups case-insensitively, and this gate follows the same rule via `-contains`'
        # case-insensitive default. A change to case-sensitive matching breaks the contract and
        # must fail here first.
        $Global:VaultMcpOpClasses = @('content:write')
        { Assert-VaultOpClass -OpClass 'CONTENT:WRITE' } | Should -Not -Throw
    }
}
