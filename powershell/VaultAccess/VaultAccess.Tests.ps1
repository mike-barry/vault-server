# Pester suite for the VaultAccess module. The wikilink and path cases load from
# tests/cases/*.json, shared with the xUnit suite (tests/VaultMcpServer.Tests) so the
# mirrored chokepoint semantics are exercised identically on both sides.
# Run with: Invoke-Pester powershell/VaultAccess/VaultAccess.Tests.ps1

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'VaultAccess.psd1') -Force

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-pester-' + [guid]::NewGuid().ToString('N'))
    $source = Join-Path $repoRoot 'test-vault'
    $test = Join-Path $script:TempRoot 'test'
    $scratch = Join-Path $script:TempRoot 'scratch'
    Copy-Item -LiteralPath $source -Destination $test -Recurse
    Copy-Item -LiteralPath $source -Destination $scratch -Recurse

    New-Item -ItemType Directory -Path (Join-Path $test '.obsidian'), (Join-Path $test 'A'), (Join-Path $test 'B') | Out-Null
    Set-Content -LiteralPath (Join-Path $test '.obsidian/junk.md') -Value '# Hidden'
    Set-Content -LiteralPath (Join-Path $test 'A/Dupe.md') -Value '# Dupe'
    Set-Content -LiteralPath (Join-Path $test 'B/Dupe.md') -Value '# Dupe'

    $configPath = Join-Path $script:TempRoot 'vaults.json'
    @{ default_vault = 'test'; vaults = @{ test = $test; scratch = $scratch } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
    Initialize-VaultAccess -ConfigPath $configPath
}

AfterAll {
    Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
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

Describe 'Vault config skip semantics' {
    # A configured vault whose root directory does not exist is skipped per-entry rather
    # than failing the whole load - mirrors the C# side's VaultConfig.Load/SkippedVaults
    # (tests/VaultMcpServer.Tests/VaultConfigTests.cs, VaultConfigLoadTests). These tests
    # swap $script:VaultConfig via Initialize-VaultAccess -ConfigPath, so each restores the
    # suite's main config afterward for the Describe blocks that follow.

    BeforeAll {
        $script:SkipGoodRoot = Join-Path $script:TempRoot 'skip-good'
        New-Item -ItemType Directory -Path $script:SkipGoodRoot -Force | Out-Null
        $script:SkipMissingRoot = Join-Path $script:TempRoot 'skip-missing'
        $script:SkipMissingRootA = Join-Path $script:TempRoot 'skip-missing-a'
        $script:SkipMissingRootB = Join-Path $script:TempRoot 'skip-missing-b'
    }

    AfterEach {
        Initialize-VaultAccess -ConfigPath (Join-Path $script:TempRoot 'vaults.json')
    }

    It 'skips a configured vault whose root does not exist, without failing the load' {
        $configPath = Join-Path $script:TempRoot 'skip-one.json'
        @{ default_vault = 'good'; vaults = @{ good = $script:SkipGoodRoot; bad = $script:SkipMissingRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath

        { Initialize-VaultAccess -ConfigPath $configPath } | Should -Not -Throw

        $config = Get-VaultAccessConfig
        $config.Vaults.ContainsKey('good') | Should -BeTrue
        $config.Vaults.ContainsKey('bad') | Should -BeFalse
        $config.SkippedVaults.ContainsKey('bad') | Should -BeTrue
        $config.SkippedVaults['bad'] | Should -Match 'does not exist'
        (Resolve-VaultRoot -Vault good).Name | Should -Be 'good'
    }

    It 'fails resolving a skipped vault as "currently unavailable", distinct from unknown' {
        $configPath = Join-Path $script:TempRoot 'skip-two.json'
        @{ default_vault = 'good'; vaults = @{ good = $script:SkipGoodRoot; bad = $script:SkipMissingRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath
        Initialize-VaultAccess -ConfigPath $configPath

        { Resolve-VaultRoot -Vault bad } | Should -Throw '*currently unavailable*'
        { Resolve-VaultRoot -Vault bad } | Should -Throw '*does not exist*'

        $unavailableMessage = $null
        try { Resolve-VaultRoot -Vault bad } catch { $unavailableMessage = $_.Exception.Message }
        $unavailableMessage | Should -Match 'currently unavailable'
        $unavailableMessage | Should -Not -Match 'Unknown vault'

        $unknownMessage = $null
        try { Resolve-VaultRoot -Vault nope } catch { $unknownMessage = $_.Exception.Message }
        $unknownMessage | Should -Match 'Unknown vault'
        $unknownMessage | Should -Not -Match 'currently unavailable'
    }

    It 'loads successfully when default_vault''s root is missing, but resolving the default fails as unavailable' {
        $configPath = Join-Path $script:TempRoot 'skip-default.json'
        @{ default_vault = 'bad'; vaults = @{ good = $script:SkipGoodRoot; bad = $script:SkipMissingRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath

        { Initialize-VaultAccess -ConfigPath $configPath } | Should -Not -Throw
        { Resolve-VaultRoot } | Should -Throw '*currently unavailable*'
    }

    It 'loads successfully even when every vault root is missing' {
        $configPath = Join-Path $script:TempRoot 'skip-all.json'
        @{ default_vault = 'a'; vaults = @{ a = $script:SkipMissingRootA; b = $script:SkipMissingRootB } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath

        { Initialize-VaultAccess -ConfigPath $configPath } | Should -Not -Throw
        $config = Get-VaultAccessConfig
        $config.Vaults.Count | Should -Be 0
        $config.SkippedVaults.Count | Should -Be 2
        { Resolve-VaultRoot } | Should -Throw '*currently unavailable*'
    }
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

Describe 'Resolve-WikiLink' {
    It '<name>' -TestCases @(
        Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../tests/cases/wikilink-cases.json') -Raw |
            ConvertFrom-Json | ForEach-Object { @{ name = $_.name; link = $_.link; outcome = $_.outcome; path = $_.path } }
    ) {
        switch ($outcome) {
            'resolves' { Resolve-WikiLink -Link $link | Should -Be $path }
            'none' { Resolve-WikiLink -Link $link | Should -BeNullOrEmpty }
            'ambiguous' { { Resolve-WikiLink -Link $link } | Should -Throw '*Ambiguous*' }
            'invalid' { { Resolve-WikiLink -Link $link } | Should -Throw }
            default { throw "unknown outcome: $outcome" }
        }
    }
}

Describe 'Resolve-VaultPath' {
    It '<name>' -TestCases @(
        Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../tests/cases/path-cases.json') -Raw |
            ConvertFrom-Json | ForEach-Object { @{ name = $_.name; path = $_.path; valid = $_.valid } }
    ) {
        if ($valid) {
            [System.IO.Path]::IsPathRooted((Resolve-VaultPath -Path $path)) | Should -BeTrue
        }
        else {
            { Resolve-VaultPath -Path $path } | Should -Throw
        }
    }
}

Describe 'Test-VaultPathInScope' {
    It '<name>' -TestCases @(
        Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../tests/cases/scope-cases.json') -Raw |
            ConvertFrom-Json | ForEach-Object { @{ name = $_.name; vault = $_.vault; path = $_.path; scopeRoots = $_.scopeRoots; outcome = $_.outcome } }
    ) {
        # scopeRoots is the grant's per-vault map (a PSCustomObject from ConvertFrom-Json,
        # keyed by vault name); convert to a hashtable so Resolve-VaultScopeRoots - which
        # mirrors the C# side's dictionary-keyed lookup - can pick out $vault's own entry,
        # proving a root configured for a different vault never leaks into this one.
        $scopeRootsTable = @{}
        $scopeRoots.PSObject.Properties | ForEach-Object { $scopeRootsTable[$_.Name] = $_.Value }
        $flatScopeRoots = Resolve-VaultScopeRoots -Vault $vault -ScopeRoots $scopeRootsTable

        switch ($outcome) {
            'inScope' { Test-VaultPathInScope -Path $path -ScopeRoots $flatScopeRoots | Should -BeTrue }
            'outOfScope' { Test-VaultPathInScope -Path $path -ScopeRoots $flatScopeRoots | Should -BeFalse }
            'invalid' { { Test-VaultPathInScope -Path $path -ScopeRoots $flatScopeRoots } | Should -Throw }
            default { throw "unknown outcome: $outcome" }
        }
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

Describe 'Read-VaultNote' {
    It 'reads a note by vault-relative path' {
        Read-VaultNote -Path 'Resources/Gizmo-Reference.md' | Should -Match 'quorvium'
    }

    It 'throws on a missing note' {
        { Read-VaultNote -Path 'Missing.md' } | Should -Throw
    }

    It 'reads an in-scope note' {
        Read-VaultNote -Path 'Projects/Widget/Tasks.md' -ScopeRoots @{ test = @('Projects/Widget') } | Should -Match 'quorvium'
    }

    It 'throws on an out-of-scope note' {
        { Read-VaultNote -Path 'Resources/Gizmo-Reference.md' -ScopeRoots @{ test = @('Projects/Widget') } } | Should -Throw
    }

    It 'a scope root configured for a different vault does not restrict this vault' {
        Read-VaultNote -Vault scratch -Path 'Resources/Gizmo-Reference.md' -ScopeRoots @{ scratch = @(); test = @('Projects/Widget') } | Should -Match 'quorvium'
    }
}

Describe 'Write-VaultNote' {
    It 'writes UTF-8 without a BOM' {
        Write-VaultNote -Vault scratch -Path 'Inbox/Note-Pester-Written.md' -Content "# Written`n`nquorvium check.`n"
        $bytes = [System.IO.File]::ReadAllBytes((Resolve-VaultPath -Vault scratch -Path 'Inbox/Note-Pester-Written.md'))
        $bytes.Length | Should -BeGreaterThan 3
        ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should -BeFalse
    }

    It 'creates parent directories' {
        Write-VaultNote -Vault scratch -Path 'New-Folder/Nested/Note-Deep.md' -Content "# Deep`n"
        Test-Path -LiteralPath (Resolve-VaultPath -Vault scratch -Path 'New-Folder/Nested/Note-Deep.md') | Should -BeTrue
    }
}

Describe 'Move-VaultNote' {
    It 'moves a note to a new path' {
        Write-VaultNote -Vault scratch -Path 'Inbox/Note-Move-Source.md' -Content "# Source`n`nquorvium check.`n"
        Move-VaultNote -Vault scratch -Path 'Inbox/Note-Move-Source.md' -NewPath 'Inbox/Note-Move-Dest.md'

        Test-Path -LiteralPath (Resolve-VaultPath -Vault scratch -Path 'Inbox/Note-Move-Source.md') | Should -BeFalse
        Test-Path -LiteralPath (Resolve-VaultPath -Vault scratch -Path 'Inbox/Note-Move-Dest.md') | Should -BeTrue
        Read-VaultNote -Vault scratch -Path 'Inbox/Note-Move-Dest.md' | Should -Match 'quorvium'
    }

    It 'creates parent directories' {
        Write-VaultNote -Vault scratch -Path 'Note-Move-Nested-Source.md' -Content "# Source`n"
        Move-VaultNote -Vault scratch -Path 'Note-Move-Nested-Source.md' -NewPath 'New-Folder/Nested/Note-Move-Nested-Dest.md'

        Test-Path -LiteralPath (Resolve-VaultPath -Vault scratch -Path 'New-Folder/Nested/Note-Move-Nested-Dest.md') | Should -BeTrue
    }

    It 'throws on a missing source' {
        { Move-VaultNote -Vault scratch -Path 'Missing-Source.md' -NewPath 'Missing-Dest.md' } | Should -Throw
    }

    It 'throws when the destination already exists' {
        Write-VaultNote -Vault scratch -Path 'Note-Move-Collide-Source.md' -Content "# Source`n"
        Write-VaultNote -Vault scratch -Path 'Note-Move-Collide-Dest.md' -Content "# Dest`n"
        { Move-VaultNote -Vault scratch -Path 'Note-Move-Collide-Source.md' -NewPath 'Note-Move-Collide-Dest.md' } | Should -Throw
    }

    It 'throws when the destination is out of scope' {
        Write-VaultNote -Vault scratch -Path 'Projects/Widget/Note-Scoped-Move-Source.md' -Content "# Source`n"
        { Move-VaultNote -Vault scratch -Path 'Projects/Widget/Note-Scoped-Move-Source.md' -NewPath 'Areas/Note-Scoped-Move-Dest.md' -ScopeRoots @{ scratch = @('Projects/Widget') } } | Should -Throw
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

Describe 'Test-VaultNoteMissingError' {
    # Exercised against real error records rather than hand-built ones: what is being pinned
    # is how PowerShell and .NET actually surface these failures. Needs no elevation, unlike
    # the end-to-end dangling-symlink cases in the tool suites.

    BeforeAll {
        function Get-ErrorRecord {
            param([scriptblock] $Trigger)
            try { & $Trigger; return $null } catch { return $_ }
        }
    }

    It 'treats a file missing at read time as nothing-to-read' {
        $rec = Get-ErrorRecord { Get-Content -LiteralPath (Join-Path $script:TempRoot 'no-such-note.md') -Raw -ErrorAction Stop }
        Test-VaultNoteMissingError -ErrorRecord $rec | Should -BeTrue
    }

    It 'treats a missing parent directory as nothing-to-read' {
        $rec = Get-ErrorRecord { Get-Content -LiteralPath (Join-Path $script:TempRoot 'no-such-dir/note.md') -Raw -ErrorAction Stop }
        Test-VaultNoteMissingError -ErrorRecord $rec | Should -BeTrue
    }

    It 'unwraps a not-found raised from a .NET call, not just a cmdlet' {
        # MethodInvocationException wrapping FileNotFoundException. An outer-type-only check
        # returns false here and would hard-fail the whole operation.
        $rec = Get-ErrorRecord { [System.IO.File]::ReadAllText((Join-Path $script:TempRoot 'no-such-dotnet.md')) }
        $rec.Exception | Should -BeOfType [System.Management.Automation.MethodInvocationException]
        Test-VaultNoteMissingError -ErrorRecord $rec | Should -BeTrue
    }

    It 'does NOT treat a sharing violation as nothing-to-read' {
        $locked = Join-Path $script:TempRoot 'locked-note.md'
        Set-Content -LiteralPath $locked -Value '# locked'
        $fs = [System.IO.File]::Open($locked, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            $rec = Get-ErrorRecord { Get-Content -LiteralPath $locked -Raw -ErrorAction Stop }
            Test-VaultNoteMissingError -ErrorRecord $rec | Should -BeFalse
        }
        finally { $fs.Dispose() }
    }

    It 'does NOT treat access-denied as nothing-to-read, even though it is also wrapped' {
        # Guards against the unwrapping above over-broadening: this is a MethodInvocationException
        # too, and must still surface as a real failure.
        $rec = Get-ErrorRecord { [System.IO.File]::ReadAllText($script:TempRoot) }
        Test-VaultNoteMissingError -ErrorRecord $rec | Should -BeFalse
    }
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

Describe 'Resolve-VaultBareLink' {
    # Ranks same-basename candidates by the empirically-mapped Obsidian resolution rule
    # (Resources/Software/Obsidian-Link-Behavior.md): shortest relative path from the linking
    # file (character count), ties broken alphabetically (ordinal, case-insensitive). These
    # fixtures pin the empirical scenarios the model was mapped against.

    It 'returns the sole candidate unchanged' {
        Resolve-VaultBareLink -LinkingFile 'X/Y.md' -Candidates @('Only/Dup.md') | Should -Be 'Only/Dup.md'
    }

    It 'returns null for an empty candidate set' {
        Resolve-VaultBareLink -LinkingFile 'X/Y.md' -Candidates @() | Should -BeNullOrEmpty
    }

    It 'Left-vs-Right: the shorter relative path wins' {
        # From the vault root, "Left/Dup.md" (11 chars) beats "Right/Dup.md" (12 chars).
        Resolve-VaultBareLink -LinkingFile 'Root-Link.md' -Candidates @('Right/Dup.md', 'Left/Dup.md') |
            Should -Be 'Left/Dup.md'
    }

    It 'breaks an exact-length tie alphabetically (ordinal, case-insensitive)' {
        # "../A/Dupe.md" and "../B/Dupe.md" are both 12 chars from Inbox/Refs.md; A wins.
        Resolve-VaultBareLink -LinkingFile 'Inbox/Refs.md' -Candidates @('B/Dupe.md', 'A/Dupe.md') |
            Should -Be 'A/Dupe.md'
    }

    It 'is order-independent (same winner regardless of candidate order)' {
        $a = Resolve-VaultBareLink -LinkingFile 'Inbox/Refs.md' -Candidates @('A/Dupe.md', 'B/Dupe.md')
        $b = Resolve-VaultBareLink -LinkingFile 'Inbox/Refs.md' -Candidates @('B/Dupe.md', 'A/Dupe.md')
        $a | Should -Be $b
        $a | Should -Be 'A/Dupe.md'
    }

    It 'a candidate inside the linking file''s own subtree beats a globally-shorter path elsewhere (T3 Links-B)' {
        # From Tests/T3/Deep/T3-Links-B.md: the in-subtree candidate "Down/T3-Dup.md" (14 chars)
        # beats the sibling-branch "../North/T3-Dup.md" (18 chars), even though the latter's
        # vault-absolute path is no longer. Resolution is relative, not vault-global.
        Resolve-VaultBareLink -LinkingFile 'Tests/T3/Deep/T3-Links-B.md' `
            -Candidates @('Tests/T3/North/T3-Dup.md', 'Tests/T3/Deep/Down/T3-Dup.md') |
            Should -Be 'Tests/T3/Deep/Down/T3-Dup.md'
    }

    It 'measures distance from the linking file, not the vault root (moving closer changes the winner)' {
        # A/B/Link.md resolves [[Dup]] to A/Dupe.md ("../Dupe.md", 10) over Far/Dupe.md; once a
        # same-basename file sits at A/B/Dupe.md ("Dupe.md", 7) it becomes the winner.
        Resolve-VaultBareLink -LinkingFile 'A/B/Link.md' -Candidates @('A/Dupe.md', 'Far/Dupe.md') |
            Should -Be 'A/Dupe.md'
        Resolve-VaultBareLink -LinkingFile 'A/B/Link.md' -Candidates @('A/Dupe.md', 'A/B/Dupe.md') |
            Should -Be 'A/B/Dupe.md'
    }
}

Describe 'Vault write lock name' {
    # The name is the cross-process contract with VaultWatcher - these tests pin the
    # derivation rule (canonicalized root path, not vault name), so a change that would
    # silently stop the two processes contending for the same lock fails here first.

    It 'is deterministic for the same vault' {
        (Get-VaultWriteLockName -Vault test) | Should -BeExactly (Get-VaultWriteLockName -Vault test)
    }

    It 'differs between vaults with different roots' {
        (Get-VaultWriteLockName -Vault test) | Should -Not -Be (Get-VaultWriteLockName -Vault scratch)
    }

    It 'derives from the root path, not the vault name' {
        # Two vault names pointing at one directory must contend for the same lock.
        $aliased = Join-Path $script:TempRoot 'aliased-vaults.json'
        $root = (Resolve-VaultRoot -Vault test).Root
        @{ default_vault = 'test'; vaults = @{ test = $root; twin = $root } } | ConvertTo-Json | Set-Content -LiteralPath $aliased
        Initialize-VaultAccess -ConfigPath $aliased
        try {
            (Get-VaultWriteLockName -Vault test) | Should -BeExactly (Get-VaultWriteLockName -Vault twin)
        }
        finally {
            Initialize-VaultAccess -ConfigPath (Join-Path $script:TempRoot 'vaults.json')
        }
    }

    It 'is a legal mutex name - no backslash outside the reserved prefix, within length limit' {
        $name = Get-VaultWriteLockName -Vault test
        $name.Length | Should -BeLessThan 260
        $name.Substring($name.IndexOf('\') + 1) | Should -Not -Match '\\'
    }
}

Describe 'Vault write lock' {
    It 'runs the scriptblock and passes its output through' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'result' } | Should -Be 'result'
    }

    It 'releases the lock after the block completes' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'first' } | Out-Null
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'second' } | Should -Be 'second'
    }

    It 'releases the lock when the block throws' {
        # The whole point of the scriptblock-wrapper API: no tool can leak the lock on an
        # error path, because the tool never writes the finally.
        { Invoke-VaultWriteLock -Vault test -ScriptBlock { throw 'boom' } } | Should -Throw '*boom*'
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'after' } | Should -Be 'after'
    }

    It 'is reentrant on the same thread' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock {
            Invoke-VaultWriteLock -Vault test -ScriptBlock { 'nested' }
        } | Should -Be 'nested'
    }

    It 'does not serialize against a different vault' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock {
            Invoke-VaultWriteLock -Vault scratch -TimeoutSeconds 5 -ScriptBlock { 'other-vault' }
        } | Should -Be 'other-vault'
    }

    It 'reports not-abandoned on a clean acquisition' {
        $abandoned = $false
        Invoke-VaultWriteLock -Vault test -Abandoned ([ref]$abandoned) -ScriptBlock { } | Out-Null
        $abandoned | Should -BeFalse
    }
}

Describe 'Vault write lock cross-process behavior' {
    # These take the lock from a real second process, since the failures being covered
    # (contention, owner death) cannot be reproduced in-process: the mutex is reentrant for
    # the calling thread, so a same-thread attempt would just succeed.

    BeforeAll {
        $script:HoldScript = {
            param($name, $signalPath)
            $m = [System.Threading.Mutex]::new($false, $name)
            $m.WaitOne() | Out-Null
            Set-Content -LiteralPath $signalPath -Value 'held'
            Start-Sleep -Seconds 120
        }

        # Each test gets its own vault root, hence its own mutex. "Abandoned" is a one-shot
        # signal the OS clears once a waiter consumes it, so tests sharing a mutex leak that
        # state into each other and pass or fail on ordering.
        function New-IsolatedVault {
            param($Label)
            $dir = Join-Path $script:TempRoot "lockvault-$Label"
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $cfg = Join-Path $script:TempRoot "vaults-$Label.json"
            @{ default_vault = 'iso'; vaults = @{ iso = $dir } } | ConvertTo-Json | Set-Content -LiteralPath $cfg
            Initialize-VaultAccess -ConfigPath $cfg
        }

        function Restore-DefaultVaultConfig {
            Initialize-VaultAccess -ConfigPath (Join-Path $script:TempRoot 'vaults.json')
        }

        function Start-LockHolder {
            param($Name, $SignalPath)
            Remove-Item -LiteralPath $SignalPath -Force -ErrorAction SilentlyContinue
            $job = Start-Job -ScriptBlock $script:HoldScript -ArgumentList $Name, $SignalPath
            $deadline = [datetime]::UtcNow.AddSeconds(60)
            while (-not (Test-Path -LiteralPath $SignalPath) -and [datetime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
            }
            if (-not (Test-Path -LiteralPath $SignalPath)) {
                Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
                throw 'Lock-holder job never acquired the mutex.'
            }
            $job
        }
    }

    It 'times out when another process holds the lock' {
        New-IsolatedVault -Label 'timeout'
        try {
            $name = Get-VaultWriteLockName -Vault iso
            $signal = Join-Path $script:TempRoot 'held-timeout.txt'
            $job = Start-LockHolder -Name $name -SignalPath $signal
            try {
                { Invoke-VaultWriteLock -Vault iso -TimeoutSeconds 2 -ScriptBlock { 'never' } } |
                    Should -Throw '*Timed out*waiting for the vault write lock*'
            }
            finally {
                Stop-Job $job -ErrorAction SilentlyContinue
                Remove-Job $job -Force -ErrorAction SilentlyContinue
            }
        }
        finally { Restore-DefaultVaultConfig }
    }

    It 'acquires and reports an abandoned lock when the holding process dies, and stays usable after' -Skip:(-not $IsWindows) {
        # Killing the holder leaves the mutex abandoned; the next waiter must ACQUIRE it (not
        # fail) and be told the previous owner died. This is the self-healing that replaces a
        # release timeout. Runs on its own mutex so the one-shot abandoned signal cannot have
        # been consumed by another test.
        New-IsolatedVault -Label 'abandon'
        $keepAlive = $null
        try {
            $name = Get-VaultWriteLockName -Vault iso
            $signal = Join-Path $script:TempRoot 'held-abandon.txt'
            $job = Start-LockHolder -Name $name -SignalPath $signal

            # Hold an independent handle to the same kernel mutex across the child's death.
            # Abandonment is a property of the mutex OBJECT, and Windows destroys that object
            # the instant its last handle closes: without this handle the object can vanish
            # between the killed child releasing it and the parent's WaitOne below, so the
            # parent would open a fresh, never-abandoned mutex and the recovery signal would be
            # lost. This makes the test verify the abandoned-owner semantics deterministically
            # instead of racing the child's process teardown.
            $keepAlive = [System.Threading.Mutex]::new($false, $name)

            Stop-Job $job -ErrorAction SilentlyContinue
            Remove-Job $job -Force -ErrorAction SilentlyContinue

            $abandoned = $false
            $result = Invoke-VaultWriteLock -Vault iso -TimeoutSeconds 30 -Abandoned ([ref]$abandoned) -ScriptBlock { 'recovered' } -WarningAction SilentlyContinue
            $result | Should -Be 'recovered'
            $abandoned | Should -BeTrue

            # The abandoned state is consumed, not sticky: the very next acquisition is clean.
            $stillAbandoned = $false
            Invoke-VaultWriteLock -Vault iso -TimeoutSeconds 30 -Abandoned ([ref]$stillAbandoned) -ScriptBlock { 'clean' } | Should -Be 'clean'
            $stillAbandoned | Should -BeFalse
        }
        finally {
            if ($keepAlive) { $keepAlive.Dispose() }
            Restore-DefaultVaultConfig
        }
    }
}
