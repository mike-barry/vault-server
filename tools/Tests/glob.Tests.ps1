# Pester suite for tools/glob.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring move.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/glob.Tests.ps1
#
# Lives in tools/Tests/ rather than beside glob.ps1: ScriptToolLoader scans tools/*.ps1
# (top-directory-only) for MCP tools, so a *.Tests.ps1 in tools/ would load as a broken tool.
#
# Covers: Tier-1 plain-text output (one vault-relative path per line, rg order), empty output on
# no matches, non-empty-pattern validation (invalid_argument), malformed-pattern mapping
# (invalid_pattern), case-insensitivity, vault-root containment through the pattern path, the
# content:read op-class floor declaration, and standalone invocability.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'glob.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-GlobError {
        # Runs glob.ps1, expecting the shared {code, message, data?} JSON error shape, and
        # returns the parsed error object. In the top-level BeforeAll so it survives into
        # Pester v5's Run phase.
        param([hashtable] $Params)
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'glob.ps1 was expected to throw but did not.'
        }
        catch {
            $_.Exception.Message | ConvertFrom-Json
        }
    }
}

Describe 'glob' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-glob-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $configPath = Join-Path $script:TempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
        $env:VAULT_MCP_VAULTS_JSON = $configPath
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'matching and output shape' {
        It 'returns matching vault-relative paths, one per line' {
            $out = @(& $script:ScriptPath -Pattern '**/Widget.md')
            $out | Should -Be @('Projects/Widget/Widget.md')
        }

        It 'honors a location-expressing (anchored) pattern' {
            $out = @(& $script:ScriptPath -Pattern 'Projects/**/*.md')
            $out.Count | Should -Be 3
            $out | ForEach-Object { $_ | Should -Match '^Projects/' }
        }

        It 'matches the root-level file' {
            @(& $script:ScriptPath -Pattern '**/Test-Vault.md') | Should -Be @('Test-Vault.md')
        }

        It 'matches case-insensitively like the native Glob tool' {
            @(& $script:ScriptPath -Pattern '**/widget.MD') | Should -Be @('Projects/Widget/Widget.md')
        }

        It 'produces empty output (not an error) when nothing matches' {
            @(& $script:ScriptPath -Pattern '**/NoSuchFileXYZ.md') | Should -BeNullOrEmpty
        }
    }

    Context 'vault-root containment - escape probes return nothing' {
        It 'confines <_> to the vault' -ForEach @('../**', '../../**', '/Windows/**', 'C:/Windows/**', '**/../../*') {
            @(& $script:ScriptPath -Pattern $_) | Should -BeNullOrEmpty
        }
    }

    Context 'errors' {
        It 'rejects an empty pattern with invalid_argument' {
            $err = Invoke-GlobError -Params @{ Pattern = '' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a whitespace-only pattern with invalid_argument' {
            $err = Invoke-GlobError -Params @{ Pattern = '   ' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'maps a malformed glob to invalid_pattern carrying rg stderr' {
            $err = Invoke-GlobError -Params @{ Pattern = '[' }
            $err.code | Should -Be 'invalid_pattern'
            $err.data.stderr | Should -Not -BeNullOrEmpty
        }
    }

    Context 'op-class metadata declaration' {
        It 'declares the content:read floor in its .NOTES comment-help (the loader-parsed op class)' {
            # What is assertable PowerShell-side without the dispatcher is glob's own metadata
            # declaration - the OpClass line ScriptToolLoader reads to derive the floor; the
            # dispatcher's floor-gating is proven generically C#-side.
            (Get-Content -LiteralPath $script:ScriptPath -Raw) | Should -Match '(?m)^\s*OpClass:\s*content:read\s*$'
        }
    }
}

Describe 'glob standalone invocability' {
    # Per Plan-Vault-Tool-Primitives's Tool Authoring Standards: every tool script must be
    # invocable outside the MCP server - pwsh -File against a real vault, no runspace context,
    # no ambient scope - and carry a unit test asserting it. This spawns a genuinely separate
    # pwsh process so no host-injected global (VaultMcpScopeRoots / VaultMcpOpClasses) exists to
    # be mistaken for injected context; ripgrep resolves off PATH (no VAULT_MCP_RG_PATH), which
    # is exactly the standalone degradation the spec describes.
    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-glob-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File glob.ps1" invocation and emits matching paths' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $out = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Pattern '**/Widget.md'
            $LASTEXITCODE | Should -Be 0
            @($out) | Should -Be @('Projects/Widget/Widget.md')
        }
        finally {
            if ($null -eq $prev) { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
            else { $env:VAULT_MCP_VAULTS_JSON = $prev }
        }
    }

    It 'emits no output for a no-match pattern and exits cleanly' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $out = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Pattern '**/NoSuchFileXYZ.md'
            $LASTEXITCODE | Should -Be 0
            @($out) | Should -BeNullOrEmpty
        }
        finally {
            if ($null -eq $prev) { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
            else { $env:VAULT_MCP_VAULTS_JSON = $prev }
        }
    }
}
