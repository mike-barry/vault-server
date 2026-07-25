# Pester suite for tools/grep.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring glob.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/grep.Tests.ps1
#
# Lives in tools/Tests/ rather than beside grep.ps1: ScriptToolLoader scans tools/*.ps1
# (top-directory-only) for MCP tools, so a *.Tests.ps1 in tools/ would load as a broken tool.
#
# Covers: the three output modes (content path:line:text, files_with_matches, count),
# case-sensitive-by-default vs. IgnoreCase, Path narrowing, the Glob file filter, empty output on
# no matches, the silent scope post-filter, the invalid_argument surface (empty/whitespace
# pattern, unrecognized output_mode, rooted/escaping path), the content:read op-class floor
# declaration, standalone invocability, and the two spec pinning tests: (1) rg exits 2 on a
# per-file read error even though matches were found/printed -> ripgrep_failed (whole scan
# aborts), and (2) rg's regex-parse stderr signature discriminates invalid_pattern from a
# non-pattern ripgrep_failed.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'grep.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-GrepError {
        # Runs grep.ps1, expecting the shared {code, message, data?} JSON error shape, and
        # returns the parsed error object. In the top-level BeforeAll so it survives into
        # Pester v5's Run phase.
        param([hashtable] $Params)
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'grep.ps1 was expected to throw but did not.'
        }
        catch {
            $_.Exception.Message | ConvertFrom-Json
        }
    }
}

Describe 'grep' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-grep-pester-' + [guid]::NewGuid().ToString('N'))
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

    Context 'content mode (default)' {
        It 'emits path:line:text per matching line, vault-relative forward slashes' {
            $out = @(& $script:ScriptPath -Pattern 'quorvium')
            $out | Should -Not -BeNullOrEmpty
            $out | Should -Contain 'Resources/Gizmo-Reference.md:11:The gizmo is cast from quorvium alloy, a fictional material whose name appears only in this fixture vault, making it a reliable unique search token.'
            # Every content line is path:line:text with a forward-slash, no-backslash path.
            $out | ForEach-Object { $_ | Should -Match '^[^:\\]+(/[^:\\]+)*\.md:\d+:' }
        }

        It 'accumulates every matching line in a file, not just the first hit' {
            # Gizmo-Reference.md has two "quorvium" lines (7 and 11) - the search.ps1 first-hit
            # break is gone, both must appear.
            $out = @(& $script:ScriptPath -Pattern 'quorvium' | Where-Object { $_ -like 'Resources/Gizmo-Reference.md:*' })
            $out.Count | Should -Be 2
        }

        It 'produces empty output (not an error) when nothing matches' {
            @(& $script:ScriptPath -Pattern 'zzz-token-not-in-vault-zzz') | Should -BeNullOrEmpty
        }
    }

    Context 'files_with_matches mode' {
        It 'emits one vault-relative path per matching file, no line/text' {
            $out = @(& $script:ScriptPath -Pattern 'quorvium' -OutputMode 'files_with_matches')
            $out | Should -Contain 'Resources/Gizmo-Reference.md'
            $out | ForEach-Object { $_ | Should -Match '^[^:]+\.md$' }
            # One line per file: Gizmo-Reference appears once despite two matching lines.
            ($out | Where-Object { $_ -eq 'Resources/Gizmo-Reference.md' }).Count | Should -Be 1
        }

        It 'matches output_mode case-insensitively' {
            $out = @(& $script:ScriptPath -Pattern 'quorvium' -OutputMode 'Files_With_Matches')
            $out | Should -Contain 'Resources/Gizmo-Reference.md'
        }
    }

    Context 'count mode' {
        It 'emits path:count per file with matches' {
            $out = @(& $script:ScriptPath -Pattern 'quorvium' -OutputMode 'count')
            $out | Should -Contain 'Resources/Gizmo-Reference.md:2'
            $out | ForEach-Object { $_ | Should -Match '^[^:]+\.md:\d+$' }
        }
    }

    Context 'IgnoreCase' {
        It 'is case-sensitive by default (native-Grep fidelity)' {
            @(& $script:ScriptPath -Pattern 'QUORVIUM') | Should -BeNullOrEmpty
        }

        It 'matches case-insensitively when IgnoreCase is set' {
            $out = @(& $script:ScriptPath -Pattern 'QUORVIUM' -IgnoreCase $true)
            $out | Should -Not -BeNullOrEmpty
            $out | Where-Object { $_ -like 'Resources/Gizmo-Reference.md:*' } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Path narrowing' {
        It 'searches only under the given vault-relative folder, output still vault-relative' {
            $out = @(& $script:ScriptPath -Pattern 'quorvium' -Path 'Resources')
            $out | Should -Not -BeNullOrEmpty
            $out | ForEach-Object { $_ | Should -Match '^Resources/' }
        }

        It 'folds a backslash/leading-slash path spelling' {
            $out = @(& $script:ScriptPath -Pattern 'quorvium' -Path '\Resources\')
            $out | ForEach-Object { $_ | Should -Match '^Resources/' }
        }

        It 'a subtree with no matches is empty output, not an error' {
            @(& $script:ScriptPath -Pattern 'quorvium' -Path 'Projects/Widget/Inbox' -OutputMode 'files_with_matches') |
                Should -Be @('Projects/Widget/Inbox/Plan-Widget-Assembly.md')
            @(& $script:ScriptPath -Pattern 'zzz-nope-zzz' -Path 'Resources') | Should -BeNullOrEmpty
        }
    }

    Context 'Glob file filter' {
        It 'restricts matches to files the glob selects' {
            $out = @(& $script:ScriptPath -Pattern 'quorvium' -Glob 'Tasks.md' -OutputMode 'files_with_matches')
            $out | Should -Be @('Projects/Widget/Tasks.md')
        }
    }

    Context 'scope post-filter (silent)' {
        It 'drops out-of-scope matches with no out_of_scope error' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects/Widget') }
            $out = @(& $script:ScriptPath -Pattern 'quorvium')
            $out | Should -Not -BeNullOrEmpty
            $out | ForEach-Object { $_ | Should -Match '^Projects/Widget/' }
            # Resources/Gizmo-Reference.md matches but is out of scope: silently absent.
            $out | Where-Object { $_ -like 'Resources/*' } | Should -BeNullOrEmpty
        }

        It 'an explicit out-of-scope path returns empty output, not out_of_scope' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects/Widget') }
            @(& $script:ScriptPath -Pattern 'quorvium' -Path 'Resources') | Should -BeNullOrEmpty
        }
    }

    Context 'errors - invalid_argument' {
        It 'rejects an empty pattern' {
            (Invoke-GrepError -Params @{ Pattern = '' }).code | Should -Be 'invalid_argument'
        }

        It 'rejects a whitespace-only pattern' {
            (Invoke-GrepError -Params @{ Pattern = '   ' }).code | Should -Be 'invalid_argument'
        }

        It 'rejects an unrecognized output_mode, echoing the value' {
            $err = Invoke-GrepError -Params @{ Pattern = 'quorvium'; OutputMode = 'banana' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.outputMode | Should -Be 'banana'
        }

        It 'rejects a rooted path' {
            (Invoke-GrepError -Params @{ Pattern = 'x'; Path = 'C:/Windows' }).code | Should -Be 'invalid_argument'
        }

        It 'rejects a vault-escaping path' {
            (Invoke-GrepError -Params @{ Pattern = 'x'; Path = '../outside' }).code | Should -Be 'invalid_argument'
        }
    }

    Context 'vault resolution' {
        # Both failure modes carry a structured code rather than the module's plain text
        # (Plan-Vault-Tool-Primitives: Error Convention), so the refusal is machine-routable
        # in a standalone run as well as under the dispatcher.
        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-GrepError -Params @{ Pattern = 'quorvium'; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
            $err.data.vault | Should -Be 'nope'
        }

        It 'refuses a configured vault whose root is missing with vault_unavailable' {
            $configPath = Join-Path $script:TempRoot 'with-missing.json'
            @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir; gone = (Join-Path $script:TempRoot 'no-such-root') } } |
                ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-GrepError -Params @{ Pattern = 'quorvium'; Vault = 'gone' }
            $err.code | Should -Be 'vault_unavailable'
            $err.data.reason | Should -Match 'does not exist'
        }

        It 'classifies an unconfigured vault as unknown_vault even with a path argument' {
            # The path check resolves the vault internally, so without the up-front resolution
            # this surfaced as grep's invalid_argument carrying module prose.
            $err = Invoke-GrepError -Params @{ Pattern = 'quorvium'; Path = 'Projects'; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
        }
    }

    Context 'errors - ripgrep classification (spec pinning tests)' {
        It 'maps a malformed regex to invalid_pattern carrying rg stderr (regex-parse signature)' {
            $err = Invoke-GrepError -Params @{ Pattern = '(' }
            $err.code | Should -Be 'invalid_pattern'
            $err.data.pattern | Should -Be '('
            $err.data.stderr | Should -Match 'regex parse error'
        }

        It 'aborts the whole scan as ripgrep_failed on a per-file read error, even though other files matched' {
            # PIN: rg exits 2 when a file cannot be read even after printing matches from other
            # files - the coarse posture leans on that, so a locked file must surface as
            # ripgrep_failed (exit 2, no regex-parse signature), never a silent narrowing or a
            # misfiled invalid_pattern. Windows exclusive-open is the file lock read.Tests.ps1 uses.
            $lockedPath = Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md'
            $handle = $null
            try {
                $handle = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            }
            catch {
                Set-ItResult -Skipped -Because 'could not construct an unreadable file (exclusive open failed) on this machine'
                return
            }
            try {
                $err = Invoke-GrepError -Params @{ Pattern = 'quorvium' }
                $err.code | Should -Be 'ripgrep_failed'
                $err.data.exitCode | Should -Be 2
                $err.data.stderr | Should -Not -BeNullOrEmpty
                $err.data.stderr | Should -Not -Match 'regex parse error'
            }
            finally {
                $handle.Dispose()
            }
        }
    }

    Context 'op-class metadata declaration' {
        It 'declares the content:read floor in its .NOTES comment-help (the loader-parsed op class)' {
            # What is assertable PowerShell-side without the dispatcher is grep's own metadata
            # declaration - the OpClass line ScriptToolLoader reads to derive the floor; the
            # dispatcher's floor-gating is proven generically C#-side.
            (Get-Content -LiteralPath $script:ScriptPath -Raw) | Should -Match '(?m)^\s*OpClass:\s*content:read\s*$'
        }
    }
}

Describe 'grep standalone invocability' {
    # Per Plan-Vault-Tool-Primitives's Tool Authoring Standards: every tool script must be
    # invocable outside the MCP server - pwsh -File against a real vault, no runspace context,
    # no ambient scope - and carry a unit test asserting it. This spawns a genuinely separate
    # pwsh process so no host-injected global (VaultMcpScopeRoots / VaultMcpOpClasses) exists to
    # be mistaken for injected context; ripgrep resolves off PATH (no VAULT_MCP_RG_PATH), which
    # is exactly the standalone degradation the spec describes.
    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-grep-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File grep.ps1 -Pattern <regex>" invocation and emits matching lines' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $out = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Pattern 'quorvium'
            $LASTEXITCODE | Should -Be 0
            @($out) | Where-Object { $_ -like 'Resources/Gizmo-Reference.md:*' } | Should -Not -BeNullOrEmpty
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
            $out = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Pattern 'zzz-token-not-in-vault-zzz'
            $LASTEXITCODE | Should -Be 0
            @($out) | Should -BeNullOrEmpty
        }
        finally {
            if ($null -eq $prev) { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
            else { $env:VAULT_MCP_VAULTS_JSON = $prev }
        }
    }

    It 'surfaces the shared JSON error shape on stderr under pwsh -File' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $stderr = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Pattern '(' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"invalid_pattern"'
        }
        finally {
            if ($null -eq $prev) { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
            else { $env:VAULT_MCP_VAULTS_JSON = $prev }
        }
    }
}
