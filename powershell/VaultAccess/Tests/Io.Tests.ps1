# Tests for VaultAccess Public/Io.ps1 (note read/write and missing-error classification).
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:TempRoot = Initialize-VaultAccessTestFixture
}

AfterAll {
    Remove-VaultAccessTestFixture -TempRoot $script:TempRoot
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
