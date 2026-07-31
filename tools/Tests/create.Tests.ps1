# Pester suite for tools/create.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring edit.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/create.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside create.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: this suite covers the Tier-2 return and the write-class envelope for both kinds,
# the kind discriminator with its conditional requiredness and inert-parameter refusals, the
# path-construction rules (separator normalization, the required ".md", link-breaking and
# OS-invalid characters, naming warnings), the existence guard, the structural parent_not_found
# surfaced from Write-VaultNote, folder mkdir-p with intermediates, the sha a caller chains into
# edit's expected_sha, the capability-denial three-way split for the degenerate create-time
# agentic diff, and standalone invocability with the JSON error shape on stderr.
#
# What is deliberately NOT re-proven here: the substrate's own semantics (block normalization,
# the schema rules, the outcome diff) are covered by the VaultAccess Frontmatter* suites; this
# suite proves create drives them correctly and shapes their outcomes into the tool contract.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'create.ps1'
    $script:EditScriptPath = Join-Path $PSScriptRoot '..' 'edit.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-CreateError {
        # Runs create.ps1, expecting it to throw the shared {code, message, data?} JSON error
        # shape, and returns the parsed error object for assertions. Defined in this top-level
        # BeforeAll (rather than inline in the Describe body) so it survives into Pester v5's Run
        # phase - a bare `function` statement in a Describe body only executes during Discovery
        # and would not be visible from It blocks.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'create.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }

    function Get-FileSha {
        # The digest create returns and edit's expected_sha is compared against: SHA-256 over the
        # file's raw bytes as lowercase hex, computed independently of the code under test.
        param([string] $FullPath)
        [System.BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($FullPath))).Replace('-', '').ToLowerInvariant()
    }
}

Describe 'create' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-create-pester-' + [guid]::NewGuid().ToString('N'))
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

    Context 'Tier-2 return shape and the write-class envelope' {
        It 'returns a single well-formed JSON object for a note create' {
            $output = & $script:ScriptPath -Kind 'note' -Path 'Inbox/New-Note.md' `
                -Frontmatter "doc.created: 2026-07-25`ndoc.type: Note" -Body "# New Note`nalpha`n"
            { $output | ConvertFrom-Json } | Should -Not -Throw

            $result = $output | ConvertFrom-Json
            $result.kind | Should -Be 'note'
            $result.path | Should -Be 'Inbox/New-Note.md'
            $result.created | Should -Be @('Inbox/New-Note.md')
            $result.sha | Should -Match '^[0-9a-f]{64}$'
            $result.namingWarnings.Count | Should -Be 0
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Inbox/New-Note.md')) |
                Should -BeExactly "---`ndoc.created: 2026-07-25`ndoc.type: Note`n---`n# New Note`nalpha`n"
        }

        It 'writes UTF-8 with no BOM' {
            & $script:ScriptPath -Kind 'note' -Path 'Inbox/Unicode-Note.md' -Frontmatter '' -Body "caf`u{e9}`n" | Out-Null
            $bytes = [System.IO.File]::ReadAllBytes((Join-Path $script:VaultDir 'Inbox/Unicode-Note.md'))
            # No EF BB BF preamble, and the non-ASCII character is UTF-8 encoded (C3 A9).
            $bytes[0] | Should -Be 0x63
            [System.Text.Encoding]::UTF8.GetString($bytes) | Should -BeExactly "caf`u{e9}`n"
        }

        It 'accepts an empty frontmatter for a note deliberately carrying no block' {
            $result = & $script:ScriptPath -Kind 'note' -Path 'Inbox/Bare-Note.md' -Frontmatter '' -Body "# Bare`n" | ConvertFrom-Json
            $result.path | Should -Be 'Inbox/Bare-Note.md'
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Inbox/Bare-Note.md')) | Should -BeExactly "# Bare`n"
        }

        It 'defaults the body to empty when it is not supplied' {
            & $script:ScriptPath -Kind 'note' -Path 'Inbox/Empty-Body.md' -Frontmatter 'doc.type: Note' | Out-Null
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Inbox/Empty-Body.md')) | Should -BeExactly "---`ndoc.type: Note`n---`n"
        }

        It 'accepts an already-fenced frontmatter block as well as bare inner lines' {
            & $script:ScriptPath -Kind 'note' -Path 'Inbox/Fenced.md' -Frontmatter "---`ndoc.type: Note`n---" -Body "# F`n" | Out-Null
            & $script:ScriptPath -Kind 'note' -Path 'Inbox/Unfenced.md' -Frontmatter 'doc.type: Note' -Body "# F`n" | Out-Null
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Inbox/Fenced.md')) |
                Should -BeExactly ([System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Inbox/Unfenced.md')))
        }

        It 'normalizes separators and a leading slash in the path' {
            $result = & $script:ScriptPath -Kind 'note' -Path '/Inbox\Normalized.md' -Frontmatter '' | ConvertFrom-Json
            $result.path | Should -Be 'Inbox/Normalized.md'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Normalized.md') | Should -BeTrue
        }

        It 'emits capabilitiesUsed and filesSkippedUnreadable as arrays, [] when empty' {
            $output = & $script:ScriptPath -Kind 'note' -Path 'Inbox/Envelope.md' -Frontmatter 'doc.type: Note'
            $result = $output | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Contain 'capabilitiesUsed'
            $result.PSObject.Properties.Name | Should -Contain 'filesSkippedUnreadable'
            $output | Should -Match '"capabilitiesUsed":\s*\[\]'
            # Structurally always [] - create is single-target and reads nothing it could skip.
            $output | Should -Match '"filesSkippedUnreadable":\s*\[\]'
        }

        It 'omits lockWarning entirely when the lock was not abandoned' {
            $result = & $script:ScriptPath -Kind 'note' -Path 'Inbox/No-Warning.md' -Frontmatter '' | ConvertFrom-Json
            # Absent, not null: an always-present null would read as checked-and-clear.
            $result.PSObject.Properties.Name | Should -Not -Contain 'lockWarning'
        }

        It 'returns a sha that is the digest of the file as written' {
            $result = & $script:ScriptPath -Kind 'note' -Path 'Inbox/Digest.md' -Frontmatter 'doc.type: Note' -Body "# D`n" | ConvertFrom-Json
            $result.sha | Should -Be (Get-FileSha (Join-Path $script:VaultDir 'Inbox/Digest.md'))
        }

        It 'chains straight into an edit whole-body replace with no interleaved read' {
            # The reason create returns a sha at all: the caller hands it back as expected_sha.
            $created = & $script:ScriptPath -Kind 'note' -Path 'Inbox/Chained.md' -Frontmatter 'doc.type: Note' -Body "# First`n" | ConvertFrom-Json
            $edited = & $script:EditScriptPath -Path 'Inbox/Chained.md' -Body "# Second`n" -expected_sha $created.sha | ConvertFrom-Json
            $edited.bodyChanged | Should -BeTrue
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Inbox/Chained.md')) |
                Should -BeExactly "---`ndoc.type: Note`n---`n# Second`n"
        }

        It 'reports a naming-convention violation as a warning without failing' {
            $result = & $script:ScriptPath -Kind 'note' -Path 'Inbox/loose note.md' -Frontmatter '' | ConvertFrom-Json
            $result.namingWarnings.Count | Should -Be 1
            $result.namingWarnings[0] | Should -Match 'Title-Case-With-Hyphens'
            # Warn, never fail (move's posture): the note is created.
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/loose note.md') | Should -BeTrue
        }
    }

    Context 'kind folder' {
        It 'creates a bare directory with no folder note and no placeholder' {
            $result = & $script:ScriptPath -Kind 'folder' -Path 'Projects/Alpha' | ConvertFrom-Json
            $result.kind | Should -Be 'folder'
            $result.path | Should -Be 'Projects/Alpha'
            $result.created | Should -Be @('Projects/Alpha')
            # Bare: create deliberately seeds nothing, and deliberately does not warn about the
            # missing folder note - a folder is expected to be unpopulated at creation.
            @(Get-ChildItem -LiteralPath (Join-Path $script:VaultDir 'Projects/Alpha') -Force).Count | Should -Be 0
            $result.namingWarnings.Count | Should -Be 0
        }

        It 'creates intermediates (mkdir-p) and reports every created path' {
            $result = & $script:ScriptPath -Kind 'folder' -Path 'Projects/Alpha/Corpus/Deep' | ConvertFrom-Json
            $result.created | Should -Be @('Projects/Alpha', 'Projects/Alpha/Corpus', 'Projects/Alpha/Corpus/Deep')
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Alpha/Corpus/Deep') -PathType Container | Should -BeTrue
        }

        It 'reports only the paths it actually brought into existence' {
            # Projects/Widget already exists in the fixture, so it is not a created path.
            $result = & $script:ScriptPath -Kind 'folder' -Path 'Projects/Widget/Corpus' | ConvertFrom-Json
            $result.created | Should -Be @('Projects/Widget/Corpus')
        }

        It 'returns a null sha (a folder has no content to digest)' {
            $output = & $script:ScriptPath -Kind 'folder' -Path 'Projects/Alpha'
            $output | Should -Match '"sha":\s*null'
            ($output | ConvertFrom-Json).sha | Should -BeNullOrEmpty
        }

        It 'emits the write-class envelope for a folder create too' {
            $output = & $script:ScriptPath -Kind 'folder' -Path 'Projects/Alpha'
            $output | Should -Match '"capabilitiesUsed":\s*\[\]'
            $output | Should -Match '"filesSkippedUnreadable":\s*\[\]'
            ($output | ConvertFrom-Json).PSObject.Properties.Name | Should -Not -Contain 'lockWarning'
        }
    }

    Context 'errors - the existence guard' {
        It 'errors already_exists on an existing note' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Resources/Gizmo-Reference.md'; Frontmatter = '' }
            $err.code | Should -Be 'already_exists'
            $err.data.path | Should -Be 'Resources/Gizmo-Reference.md'
            # Zero mutation: the existing note survives verbatim.
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md')) | Should -Match 'quorvium'
        }

        It 'errors already_exists on an existing folder' {
            $err = Invoke-CreateError @{ Kind = 'folder'; Path = 'Projects/Widget' }
            $err.code | Should -Be 'already_exists'
            $err.data.path | Should -Be 'Projects/Widget'
        }

        It 'errors already_exists when a folder occupies the note path' {
            New-Item -ItemType Directory -Path (Join-Path $script:VaultDir 'Inbox/Occupied.md') | Out-Null
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Occupied.md'; Frontmatter = '' }
            $err.code | Should -Be 'already_exists'
        }
    }

    Context 'errors - parent_not_found' {
        It 'refuses a note under a missing parent directory rather than creating it' {
            # Structural: Write-VaultNote never brings a directory into existence and raises the
            # coded error itself, so the refusal does not depend on create pre-checking.
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Projects/Nonexistent/Note.md'; Frontmatter = '' }
            $err.code | Should -Be 'parent_not_found'
            $err.data.parent | Should -Be 'Projects/Nonexistent'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Nonexistent') | Should -BeFalse
        }

        It 'refuses a note whose parent path component is a file' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Resources/Gizmo-Reference.md/Child.md'; Frontmatter = '' }
            $err.code | Should -Be 'parent_not_found'
        }

        It 'refuses a folder whose path component is a file, before creating any intermediate' {
            $err = Invoke-CreateError @{ Kind = 'folder'; Path = 'Resources/Gizmo-Reference.md/Deep/Deeper' }
            $err.code | Should -Be 'parent_not_found'
            $err.data.parent | Should -Be 'Resources/Gizmo-Reference.md'
        }
    }

    Context 'errors - kind, conditional requiredness, and inert parameters' {
        It 'errors invalid_argument on an unrecognized kind' {
            $err = Invoke-CreateError @{ Kind = 'widget'; Path = 'Inbox/X.md' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.kind | Should -Be 'widget'
        }

        It 'errors invalid_argument on the retired project/area kinds' {
            # Dropped 2026-07-24: scaffolding is skill-composed from folder and note creates.
            foreach ($retired in @('project', 'area')) {
                $err = Invoke-CreateError @{ Kind = $retired; Path = 'Projects/Alpha' }
                $err.code | Should -Be 'invalid_argument'
            }
        }

        It 'matches kind case-insensitively after a trim' {
            $result = & $script:ScriptPath -Kind '  NOTE  ' -Path 'Inbox/Case-Kind.md' -Frontmatter '' | ConvertFrom-Json
            $result.kind | Should -Be 'note'
        }

        It 'requires frontmatter for kind note' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/No-Frontmatter.md' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.kind | Should -Be 'note'
            $err.data.param | Should -Be 'frontmatter'
        }

        It 'refuses frontmatter as inert for kind folder' {
            $err = Invoke-CreateError @{ Kind = 'folder'; Path = 'Projects/Alpha'; Frontmatter = 'doc.type: Note' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'frontmatter'
            # Fail-fast, not silently ignored: nothing was created.
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Projects/Alpha') | Should -BeFalse
        }

        It 'refuses body as inert for kind folder' {
            $err = Invoke-CreateError @{ Kind = 'folder'; Path = 'Projects/Alpha'; Body = '# Nope' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'body'
        }
    }

    Context 'errors - path construction' {
        It 'errors invalid_argument on a whitespace path' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = '   '; Frontmatter = '' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument on a vault-escaping path' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = '../outside.md'; Frontmatter = '' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'refuses an extensionless note path as invalid_argument naming the .md form' {
            # ".md" is required and never inferred, so create and a following edit can never
            # disagree about what the operand was.
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Extensionless'; Frontmatter = '' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'Inbox/Extensionless\.md'
            $err.data.suggestedPath | Should -Be 'Inbox/Extensionless.md'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Extensionless') | Should -BeFalse
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Extensionless.md') | Should -BeFalse
        }

        It 'refuses a non-.md note path (create is note-semantic and never converts)' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Notes.txt'; Frontmatter = '' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.suggestedPath | Should -Be 'Inbox/Notes.md'
        }

        It 'accepts a .MD spelling as the caller wrote it' {
            $result = & $script:ScriptPath -Kind 'note' -Path 'Inbox/Upper-Case.MD' -Frontmatter '' | ConvertFrom-Json
            $result.path | Should -Be 'Inbox/Upper-Case.MD'
        }

        It 'refuses link-breaking characters in the basename' {
            foreach ($bad in @('Inbox/Bad#Name.md', 'Inbox/Bad|Name.md', 'Inbox/Bad^Name.md', 'Inbox/Bad[Name].md')) {
                $err = Invoke-CreateError @{ Kind = 'note'; Path = $bad; Frontmatter = '' }
                $err.code | Should -Be 'invalid_argument'
                $err.message | Should -Match 'link-breaking or OS-invalid'
            }
        }

        It 'refuses an OS-invalid basename for a folder too' {
            $err = Invoke-CreateError @{ Kind = 'folder'; Path = 'Projects/Bad*Name' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'refuses a basename ending in a dot (an invalid trailing character on Windows)' {
            $err = Invoke-CreateError @{ Kind = 'folder'; Path = 'Projects/Trailing.' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'trims surrounding whitespace off the whole path rather than refusing it' {
            # The trim happens before the basename check, so a trailing space at the end of the
            # path is normalized away rather than being an invalid trailing character.
            $result = & $script:ScriptPath -Kind 'folder' -Path '  Projects/Alpha  ' | ConvertFrom-Json
            $result.path | Should -Be 'Projects/Alpha'
        }
    }

    Context 'errors - frontmatter integrity and schema' {
        It 'errors frontmatter_integrity on a supplied block whose fence never closes' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Unclosed.md'; Frontmatter = "---`ndoc.type: Note" }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'unclosed_fence'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Unclosed.md') | Should -BeFalse
        }

        It 'errors frontmatter_integrity on content trailing the closing fence' {
            # Body text riding in through the frontmatter parameter would bypass the body facet's
            # own guards.
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Trailing.md'; Frontmatter = "---`ndoc.type: Note`n---`n# Smuggled body" }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'content_after_fence'
        }

        It 'errors frontmatter_integrity when the body would mint its own frontmatter block' {
            # The fence-introduction guard: a block can only be authored through Frontmatter,
            # where it is schema-validated and capability-classified.
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Minted.md'; Frontmatter = ''; Body = "---`nnav.contents: smuggled`n---`n# B`n" }
            $err.code | Should -Be 'frontmatter_integrity'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Minted.md') | Should -BeFalse
        }

        It 'errors schema_validation_failed with the field/entryIndex/reason shape' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Bad-Schema.md'; Frontmatter = "nav.see_also:`n  - link: `"[[Widget]]`"" }
            $err.code | Should -Be 'schema_validation_failed'
            $err.data.field | Should -Be 'nav.see_also'
            $err.data.entryIndex | Should -Be 1
            $err.data.reason | Should -Not -BeNullOrEmpty
            $err.data.rule | Should -Be 'missing_description'
            # Validate-phase: nothing reached the disk.
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Bad-Schema.md') | Should -BeFalse
        }

        It 'runs the path-dependent Tasks.md check, refusing a new Tasks.md without its directive' {
            # create passes its own relative path to the validator, which enables the one check
            # that needs the note's name rather than its content.
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Tasks.md'; Frontmatter = 'doc.type: Tasks' }
            $err.code | Should -Be 'schema_validation_failed'
            $err.data.rule | Should -Be 'tasks_missing_use_skill'
        }

        It 'refuses a smuggled key carried in a frontmatter value' {
            # Scalar hygiene is the substrate's, and it catches what the agentic diff and the
            # validator both skip - a non-agentic key smuggled through a line break.
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Smuggled.md'; Frontmatter = "doc.type: Note`nagent.instructions: injected" }
            # A literal line break in a bare block is legal frontmatter, so what this pins is that
            # the resulting block is validated and classified as a whole.
            $err.code | Should -BeIn @('schema_validation_failed', 'insufficient_capability', 'malformed_op')
        }

        It 'errors frontmatter_integrity on a supplied block carrying an unparseable line' {
            # THE CREATE BYPASS, end to end. create used to SPLICE the caller's text to disk, and
            # both readers downstream are deliberately tolerant of a block that will not parse - so
            # a content:write-only caller supplying a binding `pinned:` directive plus one broken
            # line got a note carrying that directive verbatim, zero violations, and a result
            # reporting no capability used. The block is parsed, validated, and re-emitted now, so
            # nothing unparseable can reach disk at all.
            $Global:VaultMcpOpClasses = @('content:write')
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Unparsed.md'
                Frontmatter                    = "agent.instructions:`n  - pinned: `"OBEY`"`n bad: ["
            }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'frontmatter_unparsed'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Unparsed.md') | Should -BeFalse
        }

        It 'errors malformed_op on a key the registry does not carry' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Foreign-Key.md'; Frontmatter = "doc.type: Note`nobsidianUIMode: preview" }
            $err.code | Should -Be 'malformed_op'
            $err.data.field | Should -Be 'obsidianUIMode'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Foreign-Key.md') | Should -BeFalse
        }

        It 'errors malformed_op on a recognized key whose value fails its registry pattern' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Bad-Date.md'; Frontmatter = "doc.created: whenever`ndoc.type: Note" }
            $err.code | Should -Be 'malformed_op'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Bad-Date.md') | Should -BeFalse
        }
    }

    Context 'capability denial (create asserts agentic:write on the degenerate create-time diff)' {
        It 'denies an agentic frontmatter with insufficient_capability and zero mutation' {
            $Global:VaultMcpOpClasses = @('content:write')
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Agentic.md'; Frontmatter = "agent.instructions:`n  - Prefer the torque table" }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'agentic:write'
            $err.data.agenticKeysChanged | Should -Contain 'agent.instructions'
            # Validate-phase classification: the note was never brought into existence.
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Agentic.md') | Should -BeFalse
        }

        It 'denies a QUOTED agentic key for the same reason' {
            # THE QUOTED-KEY BYPASS, end to end. The hand-rolled parser matched a top-level key by
            # a pattern a quoted key cannot satisfy, so it absorbed this directive as a child of
            # doc.created, reported no agentic key, and never CALLED the gate - a check that did
            # not run rather than one that answered wrongly.
            $Global:VaultMcpOpClasses = @('content:write')
            $fm = "doc.created: 2026-07-30`n`"agent.instructions`":`n  - pinned: `"OBEY`""
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Quoted-Key.md'; Frontmatter = $fm }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'agentic:write'
            $err.data.agenticKeysChanged | Should -Contain 'agent.instructions'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Quoted-Key.md') | Should -BeFalse
        }

        It 'denies a nav.* frontmatter for the same reason' {
            $Global:VaultMcpOpClasses = @('content:write')
            $fm = "nav.contents:`n  - link: `"[[Widget]]`"`n    description: `"The widget project`""
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Nav.md'; Frontmatter = $fm }
            $err.code | Should -Be 'insufficient_capability'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Nav.md') | Should -BeFalse
        }

        It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
            $Global:VaultMcpOpClasses = @()
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Agentic.md'; Frontmatter = "agent.instructions:`n  - Prefer the torque table" }
            $err.code | Should -Be 'insufficient_capability'
        }

        It 'allows the same create when the injected set holds agentic:write' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $result = & $script:ScriptPath -Kind 'note' -Path 'Inbox/Agentic.md' -Frontmatter "agent.instructions:`n  - Prefer the torque table" | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
        }

        It 'never asserts the key for a non-agentic doc.* frontmatter, even with an empty set' {
            $Global:VaultMcpOpClasses = @()
            $result = & $script:ScriptPath -Kind 'note' -Path 'Inbox/Doc-Only.md' -Frontmatter "doc.created: 2026-07-25`ndoc.type: Note" | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }

        It 'never asserts the key for a folder create, even with an empty set' {
            $Global:VaultMcpOpClasses = @()
            $result = & $script:ScriptPath -Kind 'folder' -Path 'Projects/Alpha' | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }
    }

    Context 'scope and vault resolution' {
        It 'errors out_of_scope when a note path falls outside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Out.md'; Frontmatter = '' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.path | Should -Be 'Inbox/Out.md'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Out.md') | Should -BeFalse
        }

        It 'errors out_of_scope for a folder create too (the only scope check it reaches)' {
            # kind folder never touches Write-VaultNote, so the validate-phase pre-check is the
            # whole of scope enforcement for it.
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-CreateError @{ Kind = 'folder'; Path = 'Areas/Outdoors' }
            $err.code | Should -Be 'out_of_scope'
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Areas') | Should -BeFalse
        }

        It 'creates normally when the path falls inside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $result = & $script:ScriptPath -Kind 'folder' -Path 'Projects/Alpha' | ConvertFrom-Json
            $result.created | Should -Be @('Projects/Alpha')
        }

        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/X.md'; Frontmatter = ''; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
            $err.data.vault | Should -Be 'nope'
        }

        It 'refuses a configured vault whose root is missing with vault_unavailable' {
            $configPath = Join-Path $script:TempRoot 'with-missing.json'
            @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir; gone = (Join-Path $script:TempRoot 'no-such-root') } } |
                ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/X.md'; Frontmatter = ''; Vault = 'gone' }
            $err.code | Should -Be 'vault_unavailable'
        }
    }

    Context 'lock_timeout' {
        It 'fails lock_timeout when the write lock is held by another process' -Skip:(-not $IsWindows) {
            # The write lock cannot be contended in-process (the mutex is reentrant for the
            # calling thread), so a genuinely separate process must hold it - the cross-process
            # convention in Testing.md. The chokepoint raises the coded shape itself and create
            # does not catch it, so this asserts the code surfaces unchanged.
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
                $deadline = (Get-Date).AddSeconds(20)
                while (-not (Test-Path -LiteralPath $signalPath) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 100
                }
                (Test-Path -LiteralPath $signalPath) | Should -BeTrue -Because 'the background lock holder must have acquired before we contend'

                $prevTimeout = $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS
                $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = '1'
                try {
                    $err = Invoke-CreateError @{ Kind = 'note'; Path = 'Inbox/Locked.md'; Frontmatter = '' }
                    $err.code | Should -Be 'lock_timeout'
                    $err.data.timeoutSeconds | Should -Be 1
                    Test-Path -LiteralPath (Join-Path $script:VaultDir 'Inbox/Locked.md') | Should -BeFalse
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
            # xUnit dispatcher test that refuses a content:write tool to a content:read grant.
            # What IS assertable PowerShell-side is create's own metadata declaration - the
            # OpClass line ScriptToolLoader reads to derive the floor.
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*content:write\s*$'
        }

        It 'documents every parameter in comment-help, which the loader requires to load the tool' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            foreach ($p in @('kind', 'path', 'frontmatter', 'body', 'vault')) {
                $scriptText | Should -Match "(?m)^\s*\.PARAMETER\s+$p\s*$"
            }
        }

        It 'declares no ValidateSet on kind, so an unrecognized value is create''s own error' {
            # The locked decision: a binding-level rejection is a PowerShell error rather than a
            # tool result, so the members are advertised in the .PARAMETER prose instead.
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Not -Match '\[ValidateSet\('
            $scriptText | Should -Match '(?ms).PARAMETER kind.*\bnote\b.*\bfolder\b'
        }

        It 'declares no apply parameter (create takes caller-supplied content)' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Not -Match '(?m)^\s*\.PARAMETER\s+apply\s*$'
        }
    }

    Context 'the excluded frontmatter paths' {
        It 'refuses a note under .claude/skills, and writes nothing' {
            # Claude Code's frontmatter schema, not the vault's - and its folded description
            # scalars do not survive a reserialization with their shape intact. create routes
            # through the same chokepoint edit does, so it inherits the refusal with no wiring.
            $err = Invoke-CreateError @{ Kind = 'note'; Path = '.claude/skills/temp-skill/New.md'
                Frontmatter = 'doc.created: 2026-07-30'; Body = "# N`n"
            }
            $err.code | Should -Be 'frontmatter_path_excluded'
            Test-Path -LiteralPath (Join-Path $script:VaultDir '.claude/skills/temp-skill/New.md') | Should -BeFalse
        }

        It 'refuses however the excluded path is spelled' {
            foreach ($p in @('.CLAUDE/Skills/temp/New.md', '.claude\skills\temp\New.md', 'Inbox/../.claude/skills/temp/New.md')) {
                (Invoke-CreateError @{ Kind = 'note'; Path = $p; Frontmatter = 'doc.created: 2026-07-30'; Body = "# N`n" }).code |
                    Should -Be 'frontmatter_path_excluded'
            }
        }

        It 'leaves .claude/guides creatable - the exclusion is skills, not the dot directory' {
            # create requires an existing parent, so the tier has to exist before the note can.
            New-Item -ItemType Directory -Force -Path (Join-Path $script:VaultDir '.claude/guides') | Out-Null
            $result = & $script:ScriptPath -Kind 'note' -Path '.claude/guides/New-Guide.md' `
                -Frontmatter 'doc.created: 2026-07-30' -Body "# N`n" | ConvertFrom-Json
            $result.created | Should -Be @('.claude/guides/New-Guide.md')
        }
    }
}

Describe 'create standalone invocability' {
    # Per Tool-Contracts's Tool Authoring Standards: every tool script must be invocable outside
    # the MCP server - pwsh -File against a real vault, no runspace context, no ambient scope, no
    # environment setup - and carry a unit test asserting it. This spawns a genuinely separate
    # pwsh process (rather than calling the script in this Pester session) so no global left over
    # from the host's ScriptToolRunner wrapper (VaultMcpScopeRoots, VaultMcpOpClasses) or from
    # another test in this same process could be mistaken for injected host context.

    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-create-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File create.ps1" invocation (the Tool Authoring Standard spelling)' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'create.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Kind 'note' -Path 'Inbox/Standalone.md' -Frontmatter 'doc.type: Note' -Body "# Standalone`n"
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            $result = $rawOutput | ConvertFrom-Json
            $result.path | Should -Be 'Inbox/Standalone.md'
            $result.sha | Should -Match '^[0-9a-f]{64}$'
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Inbox/Standalone.md')) |
                Should -BeExactly "---`ndoc.type: Note`n---`n# Standalone`n"
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'degrades OPEN on an agentic frontmatter with no injected op-class set' {
        # The third state of the capability split, and the reason it needs a separate process: a
        # leftover global in the test runspace would make this pass vacuously.
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'create.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Kind 'note' -Path 'Inbox/Standalone-Agentic.md' -Frontmatter "agent.instructions:`n  - Prefer the torque table"
            $LASTEXITCODE | Should -Be 0
            $result = $rawOutput | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Inbox/Standalone-Agentic.md')) | Should -Match 'agent.instructions:'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'surfaces the shared JSON error shape on stderr under pwsh -File' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'create.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Kind 'note' -Path 'Resources/Gizmo-Reference.md' -Frontmatter '' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"already_exists"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}
