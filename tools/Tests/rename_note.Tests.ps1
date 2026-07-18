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

    It 'throws when the destination already exists' {
        Set-Content -LiteralPath (Join-Path $script:VaultDir 'Resources/Existing-Collision.md') -Value "# Existing`n"
        { & $script:ScriptPath -Note 'Gizmo-Reference' -NewName 'Existing-Collision' } | Should -Throw
    }
}
