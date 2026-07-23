# Pester suite for tools/move.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring the VaultAccess module's
# own Pester setup and tools.old/Tests/rename_note.Tests.ps1's convention.
# Run with: Invoke-Pester tools/Tests/move.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside move.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: this suite covers argument validation, source resolution + dest
# construction, scope membership (earlier slices), and the file-move core-engine slice:
# the write-lock plan-validate-apply structure, the ambiguity gate, and the single-scan
# reference-rewrite engine. The folder-move path (directory move, folder-note sync, prefix
# rewrite) is the next slice and a folder source returns a deferred shape (folderMoveDeferred)
# rather than mutating - exercised here only to confirm that deferral holds.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'move.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-MoveError {
        # Runs move.ps1, expecting it to throw the shared {code, message, data?} JSON error
        # shape, and returns the parsed error object for assertions. Defined in this
        # top-level BeforeAll (rather than inline in the Describe body) so it survives into
        # Pester v5's Run phase - a bare `function` statement in a Describe body only
        # executes during Discovery and would not be visible from It blocks.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'move.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }
}

Describe 'move (validate + resolve + scope)' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-move-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $configPath = Join-Path $script:TempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
        $env:VAULT_MCP_VAULTS_JSON = $configPath
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'happy path - return shape' {
        It 'returns a well-formed Tier-2 JSON object and applies a bare-name rename' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            { $output | ConvertFrom-Json } | Should -Not -Throw

            $result = $output | ConvertFrom-Json
            $result.oldPath | Should -Be 'Resources/Gizmo-Reference.md'
            $result.newPath | Should -Be 'Resources/Renamed-Gizmo.md'
            $result.kind | Should -Be 'file'
            $result.pureMove | Should -BeFalse
            # Six occurrences of Gizmo-Reference across three notes (bare, heading, alias,
            # and path-qualified forms); the [[Widget]] bystander links are not touched.
            $result.referencesRewritten | Should -Be 6
            $result.filesRewritten.Count | Should -Be 3
            $result.PSObject.Properties.Name | Should -Not -Contain 'stub'

            # The move actually lands: source gone, destination present.
            Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeFalse
            Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
        }

        It 'accepts the optional Kind and Force parameters without erroring' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo' -Kind file -Force
            { $output | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'resolves kind as folder for a directory source' {
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget'
            $result = $output | ConvertFrom-Json
            $result.kind | Should -Be 'folder'
            $result.oldPath | Should -Be 'Projects/Widget'
            $result.newPath | Should -Be 'Projects/Renamed-Widget'
        }
    }

    Context 'argument validation' {
        It 'rejects an empty dest as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = '   ' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects kind "project" as invalid_argument (not a PowerShell binding error)' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'project' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects kind "area" as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'area' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects an unrecognized kind value as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'note' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects dest equal to source (literal match) as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Resources/Gizmo-Reference.md' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a dest inside the source folder for a folder move as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Projects/Widget'; Dest = 'Projects/Widget/Nested' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a dest basename with a link-breaking character as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Bad[Name]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'rejects a dest basename with an OS-invalid character as invalid_argument' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Bad:Name' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'reports a naming-convention violation as a warning, not a failure' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'lowercase_name'
            $result = $output | ConvertFrom-Json
            $result.namingWarnings | Should -Not -BeNullOrEmpty
        }

        It 'raises no naming warning for a convention-conformant dest' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.namingWarnings | Should -BeNullOrEmpty
        }
    }

    Context 'source resolution + dest construction' {
        It 'fails not_found for a missing source' {
            $err = Invoke-MoveError @{ Source = 'Resources/Does-Not-Exist.md'; Dest = 'Somewhere' }
            $err.code | Should -Be 'not_found'
            $err.data.source | Should -Be 'Resources/Does-Not-Exist.md'
        }

        It 'renames in place for a bare-name dest, keeping the current parent' {
            $output = & $script:ScriptPath -Source 'Projects/Widget/Inbox/Plan-Widget-Assembly.md' -Dest 'Renamed-Plan'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Projects/Widget/Inbox/Renamed-Plan.md'
        }

        It 'treats a dest containing "/" as a full vault-relative destination' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Archive/Old-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Archive/Old-Gizmo.md'
        }

        It 'normalizes a leading "/" to the vault root' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest '/Root-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Root-Gizmo.md'
        }

        It 'does not force a .md extension for a non-note file' {
            # Test-Vault.md's folder note aside, use a folder move to prove no extension is
            # forced onto a non-note dest.
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Widget-Renamed'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Projects/Widget-Renamed'
        }

        It 'leaves an explicit .md extension alone (no double extension)' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo.md'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Resources/Renamed-Gizmo.md'
        }

        It 'fails already_exists when the destination is occupied' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Projects/Widget/Tasks.md' }
            $err.code | Should -Be 'already_exists'
            $err.data.dest | Should -Be 'Projects/Widget/Tasks.md'
        }

        It 'allows a case-only rename without already_exists, and reports pureMove: false' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'gizmo-reference'
            { $output | ConvertFrom-Json } | Should -Not -Throw
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Resources/gizmo-reference.md'
            $result.pureMove | Should -BeFalse
        }

        It 'reports pureMove: true when the basename is unchanged' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Archive/Gizmo-Reference'
            $result = $output | ConvertFrom-Json
            $result.pureMove | Should -BeTrue
        }

        It 'fails kind_mismatch when Kind asserts folder for a file source' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo'; Kind = 'folder' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'file'
            $err.data.assertedKind | Should -Be 'folder'
        }

        It 'fails kind_mismatch when Kind asserts file for a folder source' {
            $err = Invoke-MoveError @{ Source = 'Projects/Widget'; Dest = 'Projects/Renamed-Widget'; Kind = 'file' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'folder'
        }
    }

    Context 'scope membership' {
        It 'passes when both source and dest are in scope' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Resources') }
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            { $output | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'fails out_of_scope naming the source when the source falls outside scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.paths | Should -Contain 'Resources/Gizmo-Reference.md'
        }

        It 'fails out_of_scope naming the dest when the dest falls outside scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Resources') }
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Projects/Escaped-Gizmo' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.paths | Should -Contain 'Projects/Escaped-Gizmo.md'
        }

        It 'passes unscoped when the grant has an empty scope-roots set for the vault' {
            $Global:VaultMcpScopeRoots = @{ scratch = @() }
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            { $output | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'rewrites references outside the grant scope (the rewrite is not scope-limited)' {
            # Source and dest are both in Resources; the rewrite must still repair the bare and
            # path-qualified links that live in Projects/ and Inbox/, outside the scope root.
            $Global:VaultMcpScopeRoots = @{ scratch = @('Resources') }
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.referencesRewritten | Should -Be 6
            (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') -Raw) | Should -Match '\[\[Renamed-Gizmo'
        }
    }

    Context 'reference-rewrite engine' {
        It 'preserves the embed marker, #heading, and |alias, rewriting only the target' {
            & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo' | Out-Null
            $plan = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Inbox/Plan-Widget-Assembly.md') -Raw
            $plan | Should -Match '\[\[Renamed-Gizmo\]\]'
            $plan | Should -Match '\[\[Renamed-Gizmo#Torque Specs\|the torque table\]\]'
            $plan | Should -Not -Match 'Gizmo-Reference'
            # The [[Widget]] bystander link is never touched.
            $plan | Should -Match '\[\[Widget\]\]'
        }

        It 'canonicalizes a path-qualified link to the bare new name when it is unique' {
            & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo' | Out-Null
            $plan = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Inbox/Plan-Widget-Assembly.md') -Raw
            # [[Resources/Gizmo-Reference]] -> bare [[Renamed-Gizmo]] (new basename unique).
            $plan | Should -Not -Match '\[\[Resources/'
        }

        It 'pureMove repairs path-qualified links but leaves bare links unchanged' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Archive/Gizmo-Reference'
            $result = $output | ConvertFrom-Json
            $result.pureMove | Should -BeTrue
            # Only the one path-qualified link ([[Resources/Gizmo-Reference]]) is repaired;
            # every bare [[Gizmo-Reference]] stays textually valid and is untouched.
            $result.referencesRewritten | Should -Be 1
            $plan = Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Inbox/Plan-Widget-Assembly.md') -Raw
            $plan | Should -Not -Match '\[\[Resources/Gizmo-Reference\]\]'
            $plan | Should -Match '\[\[Gizmo-Reference\]\]'
            Test-Path (Join-Path $script:VaultDir 'Archive/Gizmo-Reference.md') | Should -BeTrue
        }

        It 'applies a case-only rename on disk and rewrites bare links to the new case' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'gizmo-reference'
            $result = $output | ConvertFrom-Json
            $result.pureMove | Should -BeFalse
            $result.referencesRewritten | Should -Be 6
            # The on-disk name actually changed case (not a no-op). Case-sensitive membership
            # (-ccontains) - Pester's -Contain is case-insensitive and would pass vacuously.
            $onDisk = @((Get-ChildItem (Join-Path $script:VaultDir 'Resources') -Filter '*.md').Name)
            ($onDisk -ccontains 'gizmo-reference.md') | Should -BeTrue
            ($onDisk -ccontains 'Gizmo-Reference.md') | Should -BeFalse
            (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') -Raw) | Should -Match '\[\[gizmo-reference'
        }

        It 'skips a note that vanished between enumeration and read (unreadable-skip)' -Skip:(-not $IsWindows) {
            # Construct a dangling symlink: enumerated as a note, satisfies the note glob, but
            # cannot be read. Needs Developer Mode / elevation to create; skip otherwise so the
            # run reports the gap rather than passing vacuously (Testing.md convention).
            $link = Join-Path $script:VaultDir 'Inbox/Dangling.md'
            try {
                New-Item -ItemType SymbolicLink -Path $link -Target (Join-Path $script:VaultDir 'Inbox/No-Such-Target.md') -ErrorAction Stop | Out-Null
            }
            catch {
                Set-ItResult -Skipped -Because 'creating a symlink needs Developer Mode or elevation'
                return
            }
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            $result = $output | ConvertFrom-Json
            $result.filesSkippedUnreadable | Should -BeGreaterOrEqual 1
            $result.referencesRewritten | Should -Be 6
        }
    }

    Context 'ambiguity gate' {
        BeforeEach {
            # Inject duplicate basenames and referencing links into the scratch copy only -
            # never committed to test-vault (Testing.md fixture convention).
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'A') -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'B') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'A/Dupe.md') -Value "# Dupe A`n"
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'B/Dupe.md') -Value "# Dupe B`n"
            # A bare link (ambiguous) and a path-qualified link (definitive) to the A copy.
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Refs.md') -Value "See [[Dupe]] and [[A/Dupe]].`n"
        }

        It 'fails ambiguous_references unforced when the old basename is non-unique and bare links exist' {
            $err = Invoke-MoveError @{ Source = 'A/Dupe.md'; Dest = 'A/Renamed-Dupe' }
            $err.code | Should -Be 'ambiguous_references'
            $err.data.duplicatePaths | Should -Contain 'A/Dupe.md'
            $err.data.duplicatePaths | Should -Contain 'B/Dupe.md'
            # Nothing mutated - validate-stage refusal.
            Test-Path (Join-Path $script:VaultDir 'A/Dupe.md') | Should -BeTrue
        }

        It 'forced: skips the ambiguous bare link but still rewrites the definitive path-qualified one' {
            $output = & $script:ScriptPath -Source 'A/Dupe.md' -Dest 'A/Renamed-Dupe' -Force
            $result = $output | ConvertFrom-Json
            $result.referencesSkippedAmbiguous.count | Should -Be 1
            $result.referencesSkippedAmbiguous.files | Should -Contain 'Inbox/Refs.md'
            # The path-qualified [[A/Dupe]] is definitive and IS rewritten.
            $result.referencesRewritten | Should -Be 1
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Refs.md') -Raw
            $refs | Should -Match '\[\[Dupe\]\]'          # bare link left untouched
            $refs | Should -Match '\[\[Renamed-Dupe\]\]'  # qualified link canonicalized (now unique)
        }

        It 'fails basename_collision unforced when the new basename already exists elsewhere' {
            $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Dupe' }
            $err.code | Should -Be 'basename_collision'
            $err.data.collidingPaths | Should -Contain 'A/Dupe.md'
        }

        It 'forced: proceeds through a basename collision, emitting rewritten links path-qualified' {
            $output = & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Resources/Dupe' -Force
            $result = $output | ConvertFrom-Json
            $result.basenameCollisionCreated | Should -BeTrue
            # The new basename (Dupe) is non-unique, so rewrites emit the full vault path.
            (Get-Content (Join-Path $script:VaultDir 'Projects/Widget/Tasks.md') -Raw) | Should -Match '\[\[Resources/Dupe\]\]'
        }
    }

    Context 'folder-note orphan + folder-move deferral' {
        It 'warns folderNoteOrphaned when a folder note is file-renamed away from its folder name' {
            $output = & $script:ScriptPath -Source 'Projects/Widget/Widget.md' -Dest 'Renamed-Widget'
            $result = $output | ConvertFrom-Json
            $result.kind | Should -Be 'file'
            $result.folderNoteOrphaned | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget/Renamed-Widget.md') | Should -BeTrue
        }

        It 'defers a folder-source move without mutating (next slice)' {
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget'
            $result = $output | ConvertFrom-Json
            $result.kind | Should -Be 'folder'
            $result.folderMoveDeferred | Should -BeTrue
            # No mutation: the source folder is untouched.
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget') | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Projects/Renamed-Widget') | Should -BeFalse
        }
    }

    Context 'read-only pre-flight' {
        It 'fails readonly_targets when a rewrite target is read-only, mutating nothing' {
            $tasks = Join-Path $script:VaultDir 'Projects/Widget/Tasks.md'
            (Get-Item -LiteralPath $tasks).IsReadOnly = $true
            try {
                $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo' }
                $err.code | Should -Be 'readonly_targets'
                $err.data.readOnlyFiles | Should -Contain 'Projects/Widget/Tasks.md'
                # Caught pre-flight: the source is not moved.
                Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
            }
            finally {
                (Get-Item -LiteralPath $tasks).IsReadOnly = $false
            }
        }
    }
}

Describe 'move standalone invocability' {
    # Per Plan-Vault-Tool-Primitives's Tool Authoring Standards: every tool script must be
    # invocable outside the MCP server - pwsh -File against a real vault, no runspace
    # context, no ambient scope, no environment setup - and carry a unit test asserting it.
    # This spawns a genuinely separate pwsh process (rather than dot-sourcing/calling the
    # script in this Pester session) so no global left over from the host's
    # ScriptToolRunner wrapper (VaultMcpScopeRoots, VaultMcpOpClasses) or from another test
    # in this same process could be mistaken for injected host context.

    # Per-test fresh copy: the move now mutates the vault, so each test gets its own scratch
    # copy rather than sharing one across the Describe.
    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-move-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via pwsh -File with no runspace context or ambient scope and returns well-formed JSON' {
        # A fresh pwsh process has none of ScriptToolRunner's injected globals and no
        # inherited $env:VAULT_MCP_VAULTS_JSON from this session, so setting the env var
        # for just this child process's invocation is what "no ambient scope" requires -
        # the module's own default-path fallback (module-relative vaults.json) is what
        # standalone invocation against the real vault would otherwise use.
        $psExe = (Get-Process -Id $PID).Path
        $rawOutput = & $psExe -NoProfile -NonInteractive -Command "`$env:VAULT_MCP_VAULTS_JSON = '$($script:StandaloneConfigPath)'; & '$($script:ScriptPath)' -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'"
        $LASTEXITCODE | Should -Be 0

        { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
        $result = $rawOutput | ConvertFrom-Json
        $result.oldPath | Should -Be 'Resources/Gizmo-Reference.md'
        $result.newPath | Should -Be 'Resources/Renamed-Gizmo.md'
        $result.referencesRewritten | Should -Be 6
        Test-Path (Join-Path $script:StandaloneVaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
    }

    It 'still degrades open (no scope enforcement) with no injected VaultMcpScopeRoots' {
        $psExe = (Get-Process -Id $PID).Path
        $rawOutput = & $psExe -NoProfile -NonInteractive -Command "`$env:VAULT_MCP_VAULTS_JSON = '$($script:StandaloneConfigPath)'; & '$($script:ScriptPath)' -Source 'Projects/Widget/Tasks.md' -Dest 'Renamed-Tasks'"
        $LASTEXITCODE | Should -Be 0
        { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
    }
}
