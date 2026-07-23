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
# construction, scope membership, the file-move core-engine slice (write-lock
# plan-validate-apply, the ambiguity gate, the single-scan reference-rewrite engine), and
# the folder-move slice: the directory move, the folder-note invariant (Foo/Foo.md ->
# Bar/Bar.md), the old-folder-path prefix substitution repairing contained-file links, the
# two-component emit for a qualified link to the folder note itself, the folder-note bare
# rewrite and its ambiguity gate, pure relocations, missing-folder-note warn-and-proceed,
# and dest-folder-exists merge refusal. The final slice adds: embed-marker preservation on a
# file move, the moved-away folder-note-orphan variant, a depth-changing folder move (same-length
# suffix emit), an embed of a contained non-.md file repointed on a folder move, apply-phase
# partial_state (held-write-handle seam), cross-process lock_timeout, and the content:write
# op-class metadata declaration (dispatcher floor-gating itself is proven generically C#-side).

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

        It 'preserves the embed marker on an ![[...]] embed of the moved file' {
            # The fixture links Gizmo-Reference only as a wikilink; inject an embed so the
            # embed-marker-preservation path is exercised for a file move, not only asserted on
            # a plain wikilink. A bare embed and a path-qualified embed, both with the "!" kept.
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Embed.md') -Value "Bare ![[Gizmo-Reference]] and qualified ![[Resources/Gizmo-Reference]].`n"
            & $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo' | Out-Null
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Embed.md') -Raw
            # Both embeds canonicalize to the bare new name (unique) with the "!" marker intact;
            # the qualified one collapses to bare too, so two "![[Renamed-Gizmo]]" occurrences.
            ([regex]::Matches($refs, '!\[\[Renamed-Gizmo\]\]')).Count | Should -Be 2
            $refs | Should -Not -Match 'Gizmo-Reference'
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

    Context 'folder-note orphan (file move of a folder note)' {
        It 'warns folderNoteOrphaned when a folder note is file-renamed away from its folder name' {
            $output = & $script:ScriptPath -Source 'Projects/Widget/Widget.md' -Dest 'Renamed-Widget'
            $result = $output | ConvertFrom-Json
            $result.kind | Should -Be 'file'
            $result.folderNoteOrphaned | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget/Renamed-Widget.md') | Should -BeTrue
        }

        It 'warns folderNoteOrphaned when a folder note is file-moved to another folder, basename unchanged' {
            # The moved-away variant (distinct from the rename-away case above): the folder note
            # keeps its basename but leaves its folder, so it is no longer Foo/Foo.md. A pure
            # relocation (basename unchanged) still reports the broken invariant.
            $output = & $script:ScriptPath -Source 'Projects/Widget/Widget.md' -Dest 'Inbox/Widget'
            $result = $output | ConvertFrom-Json
            $result.kind | Should -Be 'file'
            $result.pureMove | Should -BeTrue
            $result.folderNoteOrphaned | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Inbox/Widget.md') | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') | Should -BeFalse
        }
    }

    Context 'folder move' {
        It 'renames the folder, fires the folder-note invariant, and reports the full shape' {
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget'
            $result = $output | ConvertFrom-Json

            $result.kind | Should -Be 'folder'
            $result.oldPath | Should -Be 'Projects/Widget'
            $result.newPath | Should -Be 'Projects/Renamed-Widget'
            $result.pureMove | Should -BeFalse
            # No deferral remnants.
            $result.PSObject.Properties.Name | Should -Not -Contain 'folderMoveDeferred'
            # The folder note Foo/Foo.md -> Bar/Bar.md invariant.
            $result.folderNoteRenamed.oldPath | Should -Be 'Projects/Widget/Widget.md'
            $result.folderNoteRenamed.newPath | Should -Be 'Projects/Renamed-Widget/Renamed-Widget.md'
            # Every contained file is carried, reported at its post-move path.
            $result.containedFilesMoved | Should -Contain 'Projects/Renamed-Widget/Renamed-Widget.md'
            $result.containedFilesMoved | Should -Contain 'Projects/Renamed-Widget/Tasks.md'
            $result.containedFilesMoved | Should -Contain 'Projects/Renamed-Widget/Inbox/Plan-Widget-Assembly.md'

            # Directory moved on disk.
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget') | Should -BeFalse
            Test-Path (Join-Path $script:VaultDir 'Projects/Renamed-Widget/Renamed-Widget.md') | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Projects/Renamed-Widget/Tasks.md') | Should -BeTrue
            Test-Path (Join-Path $script:VaultDir 'Projects/Renamed-Widget/Inbox/Plan-Widget-Assembly.md') | Should -BeTrue
        }

        It 'rewrites the folder note bare link across the vault' {
            & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget' | Out-Null
            # [[Widget]] -> [[Renamed-Widget]] in the notes that referenced the folder note.
            (Get-Content (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') -Raw) | Should -Match '\[\[Renamed-Widget\]\]'
            (Get-Content (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') -Raw) | Should -Not -Match '\[\[Widget\]\]'
        }

        It 'repairs a path-qualified link to a contained file by prefix substitution' {
            # Inject a qualified link to a contained file (the folder note carries [[Widget/Tasks]]
            # in its nav frontmatter already).
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Contained.md') -Value "See [[Widget/Tasks]] and [[Projects/Widget/Tasks]].`n"
            & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget' | Out-Null
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Contained.md') -Raw
            # Prefix swapped, contained tail (Tasks) preserved; link keeps its original depth.
            $refs | Should -Match '\[\[Renamed-Widget/Tasks\]\]'
            $refs | Should -Match '\[\[Projects/Renamed-Widget/Tasks\]\]'
            $refs | Should -Not -Match '\[\[Widget/Tasks\]\]'
            $refs | Should -Not -Match '\[\[Projects/Widget/Tasks\]\]'
        }

        It 'changes both components of a qualified link to the folder note itself' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Note.md') -Value "Project [[Projects/Widget/Widget]] and [[Widget/Widget]].`n"
            & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget' | Out-Null
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Note.md') -Raw
            # Prefix AND leaf both change (the folder note's basename tracks the folder's).
            $refs | Should -Match '\[\[Projects/Renamed-Widget/Renamed-Widget\]\]'
            $refs | Should -Match '\[\[Renamed-Widget/Renamed-Widget\]\]'
            $refs | Should -Not -Match 'Widget/Widget'
        }

        It 'leaves a bare link to a contained file untouched (contained basenames do not change)' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Bare-Contained.md') -Value "See [[Tasks]] and [[Plan-Widget-Assembly]].`n"
            & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget' | Out-Null
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Bare-Contained.md') -Raw
            $refs | Should -Match '\[\[Tasks\]\]'
            $refs | Should -Match '\[\[Plan-Widget-Assembly\]\]'
        }

        It 'remaps a contained note''s own sibling qualified link and writes it to the new location' {
            # A contained note links to a sibling by qualified path; after the move the rewrite
            # must land in the note's NEW location.
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/Sibling.md') -Value "Points at [[Projects/Widget/Tasks]].`n"
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget'
            $result = $output | ConvertFrom-Json
            $result.filesRewritten | Should -Contain 'Projects/Renamed-Widget/Sibling.md'
            # Old location gone; new location carries the repaired link.
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget/Sibling.md') | Should -BeFalse
            (Get-Content (Join-Path $script:VaultDir 'Projects/Renamed-Widget/Sibling.md') -Raw) | Should -Match '\[\[Projects/Renamed-Widget/Tasks\]\]'
        }

        It 'pure relocation keeps the basename: folderNoteRenamed null, prefix repair still runs' {
            # A full-path qualified link breaks on relocation and must be repaired even though the
            # basename (and every contained name) is unchanged.
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Reloc.md') -Value "Full [[Projects/Widget/Tasks]] and short [[Widget/Tasks]].`n"
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Archive/Widget'
            $result = $output | ConvertFrom-Json
            $result.pureMove | Should -BeTrue
            $result.folderNoteRenamed | Should -BeNullOrEmpty
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Reloc.md') -Raw
            # Full path repaired to the new location; the short suffix still resolves and is kept.
            $refs | Should -Match '\[\[Archive/Widget/Tasks\]\]'
            $refs | Should -Match '\[\[Widget/Tasks\]\]'
            Test-Path (Join-Path $script:VaultDir 'Archive/Widget/Widget.md') | Should -BeTrue
        }

        It 'warns and proceeds when the folder note is absent (folderNoteRenamed null)' {
            Remove-Item -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/Widget.md')
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget'
            $result = $output | ConvertFrom-Json
            $result.folderNoteRenamed | Should -BeNullOrEmpty
            # The directory still moves and contained files are carried.
            Test-Path (Join-Path $script:VaultDir 'Projects/Renamed-Widget/Tasks.md') | Should -BeTrue
            $result.containedFilesMoved | Should -Contain 'Projects/Renamed-Widget/Tasks.md'
        }

        It 'refuses a pre-existing destination folder as already_exists (a merge, not a move)' {
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Projects/Renamed-Widget') | Out-Null
            $err = Invoke-MoveError @{ Source = 'Projects/Widget'; Dest = 'Projects/Renamed-Widget' }
            $err.code | Should -Be 'already_exists'
            # Nothing mutated.
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget/Widget.md') | Should -BeTrue
        }

        It 'emits the same-length new-path suffix when the folder move changes component depth' {
            # Projects/Widget (2 components) -> Archive/2026/Widget (3): a qualified link's leading
            # folder-suffix is swapped for the SAME-LENGTH suffix of the new folder path, not the
            # whole new path. So a 1-component suffix ([[Widget/Tasks]]) takes the new path's last
            # 1 component (Widget, unchanged), while a 2-component suffix ([[Projects/Widget/Tasks]])
            # takes the new path's last 2 (2026/Widget) - proving depth is preserved per link.
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Depth.md') -Value "Short [[Widget/Tasks]] and full [[Projects/Widget/Tasks]].`n"
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Archive/2026/Widget'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Archive/2026/Widget'
            $result.pureMove | Should -BeTrue
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Depth.md') -Raw
            $refs | Should -Match '\[\[Widget/Tasks\]\]'         # 1-component suffix: last 1 of new path (unchanged)
            $refs | Should -Match '\[\[2026/Widget/Tasks\]\]'    # 2-component suffix: last 2 of new path
            $refs | Should -Not -Match '\[\[Projects/Widget/Tasks\]\]'
            # Destination parents (Archive/2026) autocreated; the folder landed there.
            Test-Path (Join-Path $script:VaultDir 'Archive/2026/Widget/Widget.md') | Should -BeTrue
        }

        It 'repoints an embed of a contained non-.md file (![[Widget/diagram.png]]) on a folder move' {
            # The rewrite grammar covers embeds of any contained file, not only note-to-note
            # links: an image inside the moved folder must have its embeds repaired by the same
            # prefix substitution. The contained-file census (Get-VaultFilePath) is not .md-only,
            # so the png is enumerated and its embed key matched.
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Projects/Widget/diagram.png') -Value 'PNGDATA'
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Embed-Png.md') -Value "See ![[Widget/diagram.png]] and ![[Projects/Widget/diagram.png]].`n"
            & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget' | Out-Null
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Embed-Png.md') -Raw
            $refs | Should -Match '!\[\[Renamed-Widget/diagram.png\]\]'
            $refs | Should -Match '!\[\[Projects/Renamed-Widget/diagram.png\]\]'
            $refs | Should -Not -Match '!\[\[Widget/diagram\.png\]\]' -Because 'the old bare-folder-path form must not survive'
            $refs | Should -Not -Match '!\[\[Projects/Widget/diagram\.png\]\]' -Because 'the old full-folder-path form must not survive'
            # The png itself is carried into the moved folder.
            Test-Path (Join-Path $script:VaultDir 'Projects/Renamed-Widget/diagram.png') | Should -BeTrue
        }

        It 'applies a case-only folder rename, bouncing the folder note through the case change' {
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'widget'
            $result = $output | ConvertFrom-Json
            $result.newPath | Should -Be 'Projects/widget'
            $result.pureMove | Should -BeFalse
            $result.folderNoteRenamed.newPath | Should -Be 'Projects/widget/widget.md'
            $onDisk = @((Get-ChildItem (Join-Path $script:VaultDir 'Projects') -Directory).Name)
            ($onDisk -ccontains 'widget') | Should -BeTrue
            ($onDisk -ccontains 'Widget') | Should -BeFalse
        }
    }

    Context 'folder move - ambiguity gate on the folder-note rename' {
        BeforeEach {
            # A second Widget.md elsewhere makes the folder-note basename non-unique; the bare
            # [[Widget]] links in the fixture then become ambiguous.
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Other') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Other/Widget.md') -Value "# Another Widget`n"
        }

        It 'fails ambiguous_references unforced when the folder-note basename is non-unique and bare links exist' {
            $err = Invoke-MoveError @{ Source = 'Projects/Widget'; Dest = 'Projects/Renamed-Widget' }
            $err.code | Should -Be 'ambiguous_references'
            $err.data.duplicatePaths | Should -Contain 'Projects/Widget/Widget.md'
            $err.data.duplicatePaths | Should -Contain 'Other/Widget.md'
            # Validate-stage refusal - nothing moved.
            Test-Path (Join-Path $script:VaultDir 'Projects/Widget') | Should -BeTrue
        }

        It 'forced: skips the ambiguous bare links but still rewrites definitive qualified links' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Ref-Q.md') -Value "Bare [[Widget]] and qualified [[Projects/Widget/Widget]].`n"
            $output = & $script:ScriptPath -Source 'Projects/Widget' -Dest 'Projects/Renamed-Widget' -Force
            $result = $output | ConvertFrom-Json
            $result.referencesSkippedAmbiguous.count | Should -BeGreaterThan 0
            $refs = Get-Content (Join-Path $script:VaultDir 'Inbox/Ref-Q.md') -Raw
            $refs | Should -Match '\[\[Widget\]\]'                                   # bare skipped
            $refs | Should -Match '\[\[Projects/Renamed-Widget/Renamed-Widget\]\]'   # qualified rewritten
        }

        It 'fails basename_collision unforced when the new folder-note basename already exists' {
            # Rename Projects/Widget -> Projects/Gizmo-Reference: Gizmo-Reference.md already exists.
            $err = Invoke-MoveError @{ Source = 'Projects/Widget'; Dest = 'Projects/Gizmo-Reference' }
            $err.code | Should -Be 'basename_collision'
            $err.data.collidingPaths | Should -Contain 'Resources/Gizmo-Reference.md'
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

    Context 'partial_state (apply-phase failure)' {
        It 'reports partial_state naming the moved item and already-rewritten files when a rewrite write fails mid-apply' {
            # The read-only pre-flight (readonly_targets) catches an unwritable target BEFORE apply,
            # so it cannot produce a torn state. A held exclusive-write handle is the seam that
            # survives the pre-flight: the file is not marked read-only and is still readable during
            # the scan (FileShare.Read), so validate passes clean; only the apply-phase WriteAllText
            # fails, after the source has already moved - exactly the mid-apply torn state.
            $tasks = Join-Path $script:VaultDir 'Projects/Widget/Tasks.md'
            $handle = [System.IO.File]::Open($tasks, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            try {
                $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo' }
                $err.code | Should -Be 'partial_state'
                # The move landed before the rewrite failed - the torn state partial_state names.
                $err.data.directoryMoved | Should -BeTrue
                Test-Path (Join-Path $script:VaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
                # filesRewritten is present (an array) and never includes the file that could not be
                # written - the one that produced the failure.
                $err.data.PSObject.Properties.Name | Should -Contain 'filesRewritten'
                $err.data.filesRewritten | Should -Not -Contain 'Projects/Widget/Tasks.md'
                # The recovery hint names git checkout.
                $err.message | Should -Match 'git checkout'
            }
            finally {
                $handle.Close(); $handle.Dispose()
            }
        }
    }

    Context 'lock_timeout' {
        It 'fails lock_timeout when the write lock is held by another process' -Skip:(-not $IsWindows) {
            # The write lock cannot be contended in-process (the mutex is reentrant for the calling
            # thread), so a genuinely separate process must hold it - the cross-process convention
            # in Testing.md. A background job acquires the per-vault write lock via
            # Invoke-VaultWriteLock, signals a handshake file once it holds it, then sleeps; the
            # foreground move (with the acquire timeout squeezed to 1s via the env override
            # Invoke-VaultWriteLock reads for its default) then cannot acquire and must time out.
            $modulePath = Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1'
            $configPath = $env:VAULT_MCP_VAULTS_JSON
            $signalPath = Join-Path $script:TempRoot 'lock-held.signal'

            $holder = Start-Job -ScriptBlock {
                param($ModulePath, $ConfigPath, $SignalPath)
                $env:VAULT_MCP_VAULTS_JSON = $ConfigPath
                Import-Module $ModulePath -Force
                Invoke-VaultWriteLock -Vault 'scratch' -ScriptBlock {
                    Set-Content -LiteralPath $SignalPath -Value 'held'
                    Start-Sleep -Seconds 30
                }
            } -ArgumentList $modulePath, $configPath, $signalPath

            try {
                # Wait for the holder to actually own the lock before contending.
                $deadline = (Get-Date).AddSeconds(20)
                while (-not (Test-Path -LiteralPath $signalPath) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 100
                }
                (Test-Path -LiteralPath $signalPath) | Should -BeTrue -Because 'the background lock holder must have acquired before we contend'

                $prevTimeout = $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS
                $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = '1'
                try {
                    $err = Invoke-MoveError @{ Source = 'Resources/Gizmo-Reference.md'; Dest = 'Renamed-Gizmo' }
                    $err.code | Should -Be 'lock_timeout'
                    # Contended out before any mutation - the source is untouched.
                    Test-Path (Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md') | Should -BeTrue
                }
                finally {
                    if ($null -eq $prevTimeout) {
                        Remove-Item Env:\VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
                    }
                    else {
                        $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = $prevTimeout
                    }
                }
            }
            finally {
                Stop-Job $holder -ErrorAction SilentlyContinue
                Remove-Job $holder -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'op-class metadata declaration' {
        It 'declares the content:write floor in its .NOTES comment-help (the loader-parsed op class)' {
            # Dispatcher floor-gating itself is enforced C#-side and is generically proven by the
            # xUnit dispatcher test that refuses a content:write tool to a content:read grant
            # (ScriptToolDispatcherTests: ReadOnlyGrantCallingWriteTool_IsRejectedAsToolError); move
            # inherits that uniform grant.Allows(tool.OpClass) check, declaring no move-specific
            # dispatcher test. What IS assertable PowerShell-side without the dispatcher is move's
            # own metadata *declaration* - the OpClass line ScriptToolLoader reads to derive the
            # floor. Asserting it here pins the input the C# gate keys on.
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*content:write\s*$'
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

    It 'runs via a literal "pwsh -File move.ps1" invocation (the Tool Authoring Standard spelling)' {
        # The two tests above use -Command so the vault-config env var can be set inside the child
        # string; this one exercises the exact "pwsh -File" spelling the authoring standard names,
        # passing the config through the child's inherited environment instead. Still a genuinely
        # separate process, so no ScriptToolRunner-injected global (VaultMcpScopeRoots /
        # VaultMcpOpClasses) exists to be mistaken for host context.
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $script:ScriptPath -Source 'Resources/Gizmo-Reference.md' -Dest 'Renamed-Gizmo'
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            $result = $rawOutput | ConvertFrom-Json
            $result.newPath | Should -Be 'Resources/Renamed-Gizmo.md'
            $result.referencesRewritten | Should -Be 6
            Test-Path (Join-Path $script:StandaloneVaultDir 'Resources/Renamed-Gizmo.md') | Should -BeTrue
        }
        finally {
            if ($null -eq $prev) { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
            else { $env:VAULT_MCP_VAULTS_JSON = $prev }
        }
    }
}
