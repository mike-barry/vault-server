# Tests for VaultAccess Public/ToolResult.ps1 (tool error/result shaping and name convention).
# Pure - no vault fixture needed, just the imported module.
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force
}

Describe 'New-VaultToolError' {
    It 'builds the {code, message} shape' {
        $json = New-VaultToolError -Code 'not_found' -Message 'Nothing at that path.'
        $obj = $json | ConvertFrom-Json
        $obj.code | Should -Be 'not_found'
        $obj.message | Should -Be 'Nothing at that path.'
        $obj.PSObject.Properties.Name | Should -Not -Contain 'data'
    }

    It 'includes data only when supplied' {
        $json = New-VaultToolError -Code 'out_of_scope' -Message 'Outside scope.' -Data @{ path = 'Foo/Bar.md' }
        $obj = $json | ConvertFrom-Json
        $obj.data.path | Should -Be 'Foo/Bar.md'
    }

    It 'is throwable directly, surfacing the JSON as the exception message' {
        { throw (New-VaultToolError -Code 'kind_mismatch' -Message 'Expected a file.') } |
            Should -Throw '*kind_mismatch*'
    }
}

Describe 'ConvertTo-VaultToolResult' {
    It 'serializes a single hashtable result as a JSON object' {
        $json = [ordered]@{ oldPath = 'A.md'; newPath = 'B.md'; referencesRewritten = 0 } | ConvertTo-VaultToolResult
        $obj = $json | ConvertFrom-Json
        $obj.oldPath | Should -Be 'A.md'
        $obj.referencesRewritten | Should -Be 0
    }

    It 'preserves array-valued fields nested inside the single result object' {
        $json = [ordered]@{ filesRewritten = @('A.md', 'B.md') } | ConvertTo-VaultToolResult
        $obj = $json | ConvertFrom-Json
        @($obj.filesRewritten).Count | Should -Be 2
    }

    It 'throws malformed_op when the result is an array (stray success-stream output arrayified the return value)' {
        { ConvertTo-VaultToolResult -Result @([ordered]@{ a = 1 }, [ordered]@{ b = 2 }) } | Should -Throw '*malformed_op*'
    }
}

Describe 'Test-VaultNameConvention' {
    It '<name>' -TestCases @(
        @{ name = 'Foo-Bar'; valid = $true }
        @{ name = 'Foo'; valid = $true }
        @{ name = 'Vault-MCP-Server'; valid = $true }
        @{ name = 'Maui-Trip-2026'; valid = $true }
        @{ name = 'Foo-Bar.md'; valid = $true }
        @{ name = 'Foo-Bar.MD'; valid = $true }
        @{ name = 'foo-bar'; valid = $false }
        @{ name = 'Foo bar'; valid = $false }
        @{ name = 'Foo_Bar'; valid = $false }
        @{ name = '-Foo-Bar'; valid = $false }
        @{ name = 'Foo-Bar-'; valid = $false }
        @{ name = 'Foo--Bar'; valid = $false }
        @{ name = ''; valid = $false }
        @{ name = '   '; valid = $false }
    ) {
        Test-VaultNameConvention -Name $name | Should -Be $valid
    }
}
