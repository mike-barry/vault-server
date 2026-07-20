# Pester suite for tools/rename_note.ps1. Exercises the script directly (not through the
# HTTP dispatcher - see tests/VaultMcpServer.Tests/RenameNoteDispatchTests.cs for that)
# against a scratch copy of test-vault/, mutating the copy rather than the committed
# fixture, mirroring the VaultAccess module's own Pester setup.
# Run with: Invoke-Pester tools/Tests/rename_note.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside rename_note.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'rename_note.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}

Describe 'rename_note' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-rename-pester-' + [guid]::NewGuid().ToString('N'))
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

    It 'moves the file and rewrites bare, heading, display-text, and path-qualified backlinks' {
        & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Widget-Part-Reference'

        Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeFalse
        Test-Path (Join-Path $script:VaultDir 'Resources/Widget-Part-Reference.md') | Should -BeTrue

        (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') -Raw) | Should -Match '\[\[Widget-Part-Reference\]\]'

        $tasks = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') -Raw
        $tasks | Should -Match '\[\[Widget-Part-Reference\]\]'
        $tasks | Should -Match '\[\[Widget-Part-Reference#Torque Specs\]\]'

        $plan = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Inbox/Plan-Widget-Assembly.md') -Raw
        $plan | Should -Match '\[\[Widget-Part-Reference#Torque Specs\|the torque table\]\]'
        $plan | Should -Match '\[\[Resources/Widget-Part-Reference\]\]'
    }

    It 'accepts a bracketed wikilink as -Note' {
        & $script:ScriptPath -Note '[[Gizmo-Reference]]' -NewName 'Renamed-Gizmo'
        Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
    }

    It 'accepts a bare name with a .md extension as -Note' {
        & $script:ScriptPath -Note 'Gizmo-Reference.md' -NewName 'Renamed-Gizmo'
        Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
    }

    It 'accepts a path-qualified name with a .md extension as -Note' {
        & $script:ScriptPath -Note 'Resources/Gizmo-Reference.md' -NewName 'Renamed-Gizmo'
        Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
    }

    It 'accepts a full vault-relative path as -NewName and creates the destination folder' {
        & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Archive/Old-Gizmo'
        Test-Path (Join-Path $script:VaultDir 'Archive/Old-Gizmo.md') | Should -BeTrue

        $tasks = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') -Raw
        $tasks | Should -Match '\[\[Old-Gizmo\]\]'
    }

    It 'skips ambiguous bare links but rewrites path-qualified ones' {
        New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'A'), (Join-Path $script:VaultDir 'B') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'A/Dupe.md') -Value "# Dupe A`n"
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'B/Dupe.md') -Value "# Dupe B`n"
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Note-Dupe-Ref.md') -Value "See [[Dupe]] and [[A/Dupe]].`n"

        & $script:ScriptPath -Note 'A/Dupe' -NewName 'A/Dupe-Renamed'

        Test-Path (Join-Path $script:VaultDir 'A/Dupe.md') | Should -BeFalse
        Test-Path (Join-Path $script:VaultDir 'A/Dupe-Renamed.md') | Should -BeTrue

        $content = Get-Content (Join-Path $script:VaultDir 'Inbox/Note-Dupe-Ref.md') -Raw
        $content | Should -Match '\[\[Dupe\]\]'
        $content | Should -Match '\[\[A/Dupe-Renamed\]\]'
    }

    It 'moves the file without rewriting any links on a pure folder move (basename unchanged)' {
        & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Archive/Gizmo-Reference'
        Test-Path (Join-Path $script:VaultDir 'Archive/Gizmo-Reference.md') | Should -BeTrue

        $tasks = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') -Raw
        $tasks | Should -Match '\[\[Gizmo-Reference\]\]'
    }

    It 'throws when the note cannot be resolved' {
        { & $script:ScriptPath -Note 'Nonexistent' -NewName 'Whatever' } | Should -Throw
    }

    It 'throws when the destination already exists, mutating nothing' {
        # The assertion that matters is not "it throws" but "nothing was mutated": the
        # collision check also exists inside Move-VaultNote, which runs after every backlink
        # has been rewritten and so cannot prevent a torn vault on its own.
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'Resources/Existing-Collision.md') -Value "# Existing`n"
        { & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Existing-Collision' } | Should -Throw '*Destination already exists*'

        Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
        (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') -Raw) | Should -Match '\[\[Gizmo-Reference\]\]'
        (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') -Raw) | Should -Not -Match '\[\[Existing-Collision\]\]'
    }

    It 'refuses up front when a file needing rewrite is read-only, mutating nothing' {
        $backlinkFile = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
        $before = Get-Content $backlinkFile -Raw
        (Get-Item $backlinkFile).IsReadOnly = $true
        try {
            { & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Renamed-Gizmo' } |
                Should -Throw '*read-only*Nothing was modified*'

            Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeFalse
            Get-Content $backlinkFile -Raw | Should -BeExactly $before
        }
        finally {
            (Get-Item $backlinkFile).IsReadOnly = $false
        }
    }

    It 'does not let skip-missing mask a real read failure' {
        # Skip-missing must swallow ONLY a genuine disappearance. A note that is present but
        # unreadable (here: locked with no sharing) has to surface, not be quietly skipped -
        # otherwise the tool would report success while leaving that note's links stale.
        $locked = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
        $fs = [System.IO.File]::Open($locked, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        try {
            { & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Renamed-Gizmo' } | Should -Throw

            # Failed during the read-only planning phase, so nothing was written at all.
            Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeFalse
        }
        finally {
            $fs.Dispose()
        }
    }

    It 'skips an unreadable note instead of aborting the whole rename' {
        # A dangling symlink is the reliably constructible case of "enumerated as a file,
        # satisfies Test-Path -PathType Leaf, no readable content". Creating one needs
        # Developer Mode or elevation, so this skips rather than passing vacuously.
        $dangling = Join-Path $script:VaultDir 'Resources/Dangling.md'
        try {
            New-Item -ItemType SymbolicLink -Path $dangling -Target (Join-Path $script:VaultDir 'Resources/no-such-target.md') -ErrorAction Stop | Out-Null
        }
        catch {
            Set-ItResult -Skipped -Because 'creating a symbolic link requires Developer Mode or elevation on this machine'
            return
        }

        $out = & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Renamed-Gizmo'

        # The broken link is skipped and reported - and the rename still completes.
        $out | Should -Match 'Skipped 1 unreadable note'
        Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
        (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') -Raw) | Should -Match '\[\[Renamed-Gizmo\]\]'
    }

    It 'reports exactly which files were rewritten when the move itself fails' {
        # Forces a failure that pre-flight cannot predict: the destination's parent exists as
        # a FILE, so directory creation fails inside Move-VaultNote - after the backlink
        # rewrites have already landed. Partial state must be declared, not discovered.
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'Sub') -Value 'not a directory'

        $err = $null
        try { & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Sub/Moved' }
        catch { $err = $_.Exception.Message }

        $err | Should -Not -BeNullOrEmpty
        $err | Should -Match 'PARTIAL STATE'
        $err | Should -Match 'was NOT moved'
        $err | Should -Match 'Projects/Widget/Widget\.md'

        # The declared state must match reality: source still in place, backlinks rewritten.
        Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
        (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') -Raw) | Should -Match '\[\[Moved\]\]'
    }

    It 'refuses under a scoped grant for the target vault, mutating nothing' {
        $global:VaultMcpScopeRoots = @{ scratch = @('Resources') }

        { & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Renamed-Gizmo' } |
            Should -Throw -ExpectedMessage '*unscoped Write grant for vault "scratch"*'

        Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
        Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeFalse
        $tasks = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') -Raw
        $tasks | Should -Match '\[\[Gizmo-Reference\]\]'
    }

    It 'proceeds when the grant is explicitly unscoped for the target vault (empty root set)' {
        $global:VaultMcpScopeRoots = @{ scratch = @() }

        & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Renamed-Gizmo'

        Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
    }

    It 'proceeds when there is no ambient scope map at all (no grant in play)' {
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue

        & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Renamed-Gizmo'

        Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
    }

    It 'takes the per-vault write lock for the whole operation, mutating nothing when it cannot be acquired' {
        # Proves the lock is actually engaged rather than merely present: with the lock held
        # by another process the rename must fail to acquire and leave the vault untouched -
        # not the file, and not the backlinks, since the lock wraps resolution and the rewrite
        # pass too, not just the final move.
        Import-Module (Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force
        $lockName = Get-VaultWriteLockName -Vault 'scratch'
        $signal = Join-Path $script:TempRoot 'lock-held.txt'

        $holder = Start-Job -ScriptBlock {
            param($name, $signalPath)
            $m = [System.Threading.Mutex]::new($false, $name)
            $m.WaitOne() | Out-Null
            Set-Content -LiteralPath $signalPath -Value 'held'
            Start-Sleep -Seconds 120
        } -ArgumentList $lockName, $signal

        try {
            $deadline = [datetime]::UtcNow.AddSeconds(60)
            while (-not (Test-Path -LiteralPath $signal) -and [datetime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
            }
            Test-Path -LiteralPath $signal | Should -BeTrue -Because 'the lock-holder job must acquire the mutex before the rename is attempted'

            $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = '2'
            { & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Renamed-Gizmo' } |
                Should -Throw '*Timed out*vault write lock*'

            Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeFalse
            (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') -Raw) | Should -Match '\[\[Gizmo-Reference\]\]'
        }
        finally {
            Remove-Item Env:\VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
            Stop-Job $holder -ErrorAction SilentlyContinue
            Remove-Job $holder -Force -ErrorAction SilentlyContinue
        }
    }
}
