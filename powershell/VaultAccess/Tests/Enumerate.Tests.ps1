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

Describe 'Get-VaultFilePath -Pattern (glob surface)' {
    It 'matches a bare basename anywhere via **' {
        $m = @(Get-VaultFilePath -Pattern '**/Widget.md')
        $m | Should -Be @('Projects/Widget/Widget.md')
    }

    It 'honors an anchored (location-expressing) pattern' {
        $m = @(Get-VaultFilePath -Pattern 'Projects/**/*.md')
        $m.Count | Should -Be 3
        $m | ForEach-Object { $_ | Should -Match '^Projects/' }
    }

    It 'matches the root-level manifest-style file, mirroring the spec probe' {
        # Spec's Vault Root Containment probe uses **/Vault-Manifest.md against the real vault;
        # the fixture's root file is Test-Vault.md.
        @(Get-VaultFilePath -Pattern '**/Test-Vault.md') | Should -Be @('Test-Vault.md')
    }

    It 'is case-insensitive (--glob-case-insensitive), matching native Glob on Windows' {
        @(Get-VaultFilePath -Pattern '**/test-vault.MD') | Should -Be @('Test-Vault.md')
    }

    It 'returns empty output (not an error) when nothing matches' {
        @(Get-VaultFilePath -Pattern '**/NoSuchFileXYZ.md') | Should -BeNullOrEmpty
    }

    Context 'vault-root containment - escape probes return nothing (Plan-Tool-Glob)' {
        It 'confines <_> to the vault, matching zero files' -ForEach @('../**', '../../**', '/Windows/**', 'C:/Windows/**', '**/../../*') {
            @(Get-VaultFilePath -Pattern $_) | Should -BeNullOrEmpty
        }
    }

    It 'maps a malformed pattern (rg exit 2) to invalid_pattern carrying stderr' {
        $err = $null
        try { Get-VaultFilePath -Pattern '[' | Out-Null } catch { $err = $_ }
        $err | Should -Not -BeNullOrEmpty
        $parsed = $err.Exception.Message | ConvertFrom-Json
        $parsed.code | Should -Be 'invalid_pattern'
        $parsed.data.stderr | Should -Not -BeNullOrEmpty
    }

    It 'applies the scope post-filter to matches, dropping out-of-scope files silently' {
        $m = @(Get-VaultFilePath -Pattern '**/*.md' -ScopeRoots @{ test = @('Projects/Widget') })
        $m | Should -Not -BeNullOrEmpty
        $m | ForEach-Object { $_ | Should -Match '^Projects/Widget/' }
        $m | Should -Not -Contain 'Resources/Gizmo-Reference.md'
    }
}

Describe 'Search-VaultContent (grep surface)' {
    Context 'output modes' {
        It 'content mode emits path:line:text with vault-relative forward-slash paths' {
            $m = @(Search-VaultContent -Pattern 'quorvium')
            $m | Should -Contain 'Resources/Gizmo-Reference.md:11:The gizmo is cast from quorvium alloy, a fictional material whose name appears only in this fixture vault, making it a reliable unique search token.'
            $m | ForEach-Object { $_ | Should -Match '^[^:\\]+(/[^:\\]+)*:\d+:' }
        }

        It 'accumulates every matching line in a file (no first-hit break)' {
            $m = @(Search-VaultContent -Pattern 'quorvium' | Where-Object { $_ -like 'Resources/Gizmo-Reference.md:*' })
            $m.Count | Should -Be 2
        }

        It 'files_with_matches mode emits one path per file' {
            $m = @(Search-VaultContent -Pattern 'quorvium' -Mode 'files_with_matches')
            $m | Should -Contain 'Resources/Gizmo-Reference.md'
            ($m | Where-Object { $_ -eq 'Resources/Gizmo-Reference.md' }).Count | Should -Be 1
        }

        It 'count mode emits path:count per file' {
            $m = @(Search-VaultContent -Pattern 'quorvium' -Mode 'count')
            $m | Should -Contain 'Resources/Gizmo-Reference.md:2'
        }
    }

    Context 'matching options' {
        It 'is case-sensitive by default' {
            @(Search-VaultContent -Pattern 'QUORVIUM') | Should -BeNullOrEmpty
        }

        It 'matches case-insensitively with -IgnoreCase' {
            @(Search-VaultContent -Pattern 'QUORVIUM' -IgnoreCase) | Should -Not -BeNullOrEmpty
        }

        It 'narrows the walk to -Path, output still vault-relative' {
            $m = @(Search-VaultContent -Pattern 'quorvium' -Path 'Resources')
            $m | Should -Not -BeNullOrEmpty
            $m | ForEach-Object { $_ | Should -Match '^Resources/' }
        }

        It 'restricts matches to files the -Glob selects' {
            @(Search-VaultContent -Pattern 'quorvium' -Glob 'Tasks.md' -Mode 'files_with_matches') |
                Should -Be @('Projects/Widget/Tasks.md')
        }

        It 'returns empty output (not an error) when nothing matches' {
            @(Search-VaultContent -Pattern 'zzz-token-not-in-vault-zzz') | Should -BeNullOrEmpty
        }
    }

    Context 'vault-root containment - escape probes return nothing' {
        It 'confines <_> to the vault' -ForEach @('../**', '/Windows/**') {
            # A -Glob that only matches outside the vault yields no in-vault file, hence no match.
            @(Search-VaultContent -Pattern 'quorvium' -Glob $_) | Should -BeNullOrEmpty
        }
    }

    Context 'error classification (coarse, exit-code-first)' {
        It 'maps a malformed regex (rg exit 2, regex-parse signature) to invalid_pattern' {
            $err = $null
            try { Search-VaultContent -Pattern '(' | Out-Null } catch { $err = $_ }
            $err | Should -Not -BeNullOrEmpty
            $parsed = $err.Exception.Message | ConvertFrom-Json
            $parsed.code | Should -Be 'invalid_pattern'
            $parsed.data.stderr | Should -Match 'regex parse error'
        }

        It 'aborts as ripgrep_failed on a per-file read error even when other files matched (pin)' {
            $lockedPath = Join-Path $script:TempRoot 'test/Resources/Gizmo-Reference.md'
            $handle = $null
            try { $handle = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None) }
            catch { Set-ItResult -Skipped -Because 'could not construct an unreadable file on this machine'; return }
            try {
                $err = $null
                try { Search-VaultContent -Pattern 'quorvium' | Out-Null } catch { $err = $_ }
                $err | Should -Not -BeNullOrEmpty
                $parsed = $err.Exception.Message | ConvertFrom-Json
                $parsed.code | Should -Be 'ripgrep_failed'
                $parsed.data.exitCode | Should -Be 2
                # Presence first: an ABSENT stderr reads as $null, which matches nothing and
                # passes a bare "not match".
                $parsed.data.PSObject.Properties.Name | Should -Contain 'stderr'
                $parsed.data.stderr | Should -Not -Match 'regex parse error'
            }
            finally { $handle.Dispose() }
        }

        It 'throws dependency_unavailable-shaped when ripgrep cannot be resolved' {
            { Search-VaultContent -Pattern 'quorvium' -RgPath (Join-Path $script:TempRoot 'no-such-rg.exe') } |
                Should -Throw '*dependency_unavailable*'
        }
    }

    Context 'scope post-filter (silent)' {
        It 'drops out-of-scope matches, never surfacing out_of_scope' {
            $m = @(Search-VaultContent -Pattern 'quorvium' -ScopeRoots @{ test = @('Projects/Widget') })
            $m | Should -Not -BeNullOrEmpty
            $m | ForEach-Object { $_ | Should -Match '^Projects/Widget/' }
            $m | Where-Object { $_ -like 'Resources/*' } | Should -BeNullOrEmpty
        }
    }
}
