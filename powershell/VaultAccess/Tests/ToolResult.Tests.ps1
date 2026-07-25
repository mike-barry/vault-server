# Tests for VaultAccess Public/ToolResult.ps1 (tool error/result shaping, structured vault
# resolution, and the name convention). Mostly pure - only Resolve-VaultToolRoot needs vault
# config, and it builds its own two-entry config rather than the shared fixture, since the
# condition under test is a vault whose root is missing.
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

Describe 'Resolve-VaultToolRoot' {
    BeforeAll {
        # Two entries: "present" resolves, "gone" is configured with a root that does not
        # exist, so Initialize-VaultAccess skips it and it resolves as unavailable rather
        # than unknown - the exact pair the two structured codes discriminate.
        $script:ResolveTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-toolroot-' + [guid]::NewGuid().ToString('N'))
        $script:PresentRoot = Join-Path $script:ResolveTempRoot 'present'
        $script:GoneRoot = Join-Path $script:ResolveTempRoot 'gone'
        New-Item -ItemType Directory -Path $script:PresentRoot -Force | Out-Null

        $script:ResolveConfigPath = Join-Path $script:ResolveTempRoot 'vaults.json'
        @{ default_vault = 'present'; vaults = @{ present = $script:PresentRoot; gone = $script:GoneRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:ResolveConfigPath
        Initialize-VaultAccess -ConfigPath $script:ResolveConfigPath
    }

    AfterAll {
        Remove-Item -LiteralPath $script:ResolveTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'returns the same Name/Root object Resolve-VaultRoot returns on success' {
        $resolved = Resolve-VaultToolRoot -Vault present
        $resolved.Name | Should -BeExactly 'present'
        $resolved.Root | Should -Be $script:PresentRoot
    }

    It 'resolves the default vault when no name is supplied' {
        (Resolve-VaultToolRoot).Name | Should -BeExactly 'present'
    }

    It 'refuses an unconfigured name with unknown_vault' {
        $err = $null
        try { Resolve-VaultToolRoot -Vault 'nope' } catch { $err = $_.Exception.Message | ConvertFrom-Json }
        $err.code | Should -Be 'unknown_vault'
        $err.data.vault | Should -Be 'nope'
        $err.data.configuredVaults | Should -Contain 'present'
        # The mirrored-core wording is carried through, not replaced.
        $err.message | Should -Match 'Unknown vault'
    }

    It 'refuses a configured-but-skipped vault with vault_unavailable, carrying the skip reason' {
        $err = $null
        try { Resolve-VaultToolRoot -Vault 'gone' } catch { $err = $_.Exception.Message | ConvertFrom-Json }
        $err.code | Should -Be 'vault_unavailable'
        $err.data.vault | Should -Be 'gone'
        $err.data.reason | Should -Match 'does not exist'
        $err.message | Should -Match 'currently unavailable'
    }

    It 'classifies the default vault too when the default itself is skipped' {
        $configPath = Join-Path $script:ResolveTempRoot 'default-gone.json'
        @{ default_vault = 'gone'; vaults = @{ present = $script:PresentRoot; gone = $script:GoneRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath
        Initialize-VaultAccess -ConfigPath $configPath
        try {
            $err = $null
            try { Resolve-VaultToolRoot } catch { $err = $_.Exception.Message | ConvertFrom-Json }
            $err.code | Should -Be 'vault_unavailable'
            $err.data.vault | Should -Be 'gone'
        }
        finally {
            Initialize-VaultAccess -ConfigPath $script:ResolveConfigPath
        }
    }

    It 'leaves Resolve-VaultRoot itself throwing the mirrored plain text' {
        # The C# side (VaultConfig.Resolve) raises the same wording with no shared case file,
        # so the mirror holds only while the core function keeps its prose.
        $message = $null
        try { Resolve-VaultRoot -Vault 'nope' } catch { $message = $_.Exception.Message }
        $message | Should -Match 'Unknown vault'
        $message | Should -Not -Match 'unknown_vault'
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
