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

Describe 'Read-VaultNote' {
    It 'reads a note by vault-relative path' {
        Read-VaultNote -Path 'Resources/Gizmo-Reference.md' | Should -Match 'quorvium'
    }

    It 'throws on a missing note' {
        { Read-VaultNote -Path 'Missing.md' } | Should -Throw
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
}
