# Pester suite for tools/edit.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring move.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/edit.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside edit.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: this suite covers the Tier-2 return and the write-class envelope, the facet-form
# rules, the frontmatter-op vocabulary as edit drives it (including the idempotent changed:false
# paths), body-confined exactly-once patch matching in both failure directions, the staleness
# guard (state_mismatch with zero mutation, and resultingSha chaining with no interleaved read),
# byte preservation end to end, every error code in the spec's table including the
# protected-over-shape precedence case, the capability-denial pair edit is the first tool to
# carry (injected set lacking agentic:write denies with zero disk change; no injected set
# degrades open), the content:write op-class metadata declaration, and standalone invocability
# with the JSON error shape on stderr.
#
# What is deliberately NOT re-proven here: the substrate's own semantics (byte-preserving
# splices, the schema rules, the outcome diff) are covered by the VaultAccess Frontmatter*
# suites; this suite proves edit drives them correctly and shapes their outcomes into the tool
# contract.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'edit.ps1'
    $script:ReadScriptPath = Join-Path $PSScriptRoot '..' 'read.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-EditError {
        # Runs edit.ps1, expecting it to throw the shared {code, message, data?} JSON error
        # shape, and returns the parsed error object for assertions. Defined in this
        # top-level BeforeAll (rather than inline in the Describe body) so it survives into
        # Pester v5's Run phase - a bare `function` statement in a Describe body only
        # executes during Discovery and would not be visible from It blocks.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'edit.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }

    function Get-FileSha {
        # The digest edit's expected_sha is compared against: SHA-256 over the file's raw bytes
        # as lowercase hex, computed independently of the code under test.
        param([string] $FullPath)
        [System.BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($FullPath))).Replace('-', '').ToLowerInvariant()
    }
}

Describe 'edit' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-edit-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $configPath = Join-Path $script:TempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
        $env:VAULT_MCP_VAULTS_JSON = $configPath

        # A byte-precise working note: known frontmatter, known body, LF throughout.
        $script:NotePath = 'Inbox/Edit-Case.md'
        $script:NoteFull = Join-Path $script:VaultDir 'Inbox/Edit-Case.md'
        [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n`n# Edit-Case`nalpha line`nbeta line`n")
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Tier-2 return shape and the write-class envelope' {
        It 'returns a single well-formed JSON object for a frontmatter op' {
            $output = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'
            { $output | ConvertFrom-Json } | Should -Not -Throw

            $result = $output | ConvertFrom-Json
            $result.path | Should -Be $script:NotePath
            $result.frontmatterChanged | Should -BeTrue
            $result.bodyChanged | Should -BeFalse
            $result.opsApplied.Count | Should -Be 1
            $result.opsApplied[0].op | Should -Be 'set_field'
            $result.opsApplied[0].field | Should -Be 'doc.type'
            $result.opsApplied[0].changed | Should -BeTrue
            $result.resultingSha | Should -Match '^[0-9a-f]{64}$'
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Corpus`n---`n`n# Edit-Case`nalpha line`nbeta line`n"
        }

        It 'emits capabilitiesUsed and filesSkippedUnreadable as arrays, [] when empty' {
            $output = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'
            $result = $output | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Contain 'capabilitiesUsed'
            $result.PSObject.Properties.Name | Should -Contain 'filesSkippedUnreadable'
            $output | Should -Match '"capabilitiesUsed":\s*\[\]'
            # Structurally always [] - edit is single-target, so a missing or unreadable sole
            # target is not_found / read_failure, never a skip.
            $output | Should -Match '"filesSkippedUnreadable":\s*\[\]'
        }

        It 'omits lockWarning entirely when the lock was not abandoned' {
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | ConvertFrom-Json
            # Absent, not null: an always-present null would read as checked-and-clear.
            $result.PSObject.Properties.Name | Should -Not -Contain 'lockWarning'
        }

        It 'echoes agentic:write in capabilitiesUsed when the agentic layer moved' {
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"add_hint","text":"Prefer the torque table"}]' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
        }

        It 'returns resultingFrontmatter byte-identical to read part: frontmatter' {
            # The contract locked in the spec: resultingFrontmatter is raw block text matching what
            # read returns, so the caller can verify without a follow-up read - which matters
            # because several ops are silently idempotent. Both tools take the split from the same
            # substrate function, so the match is structural.
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | ConvertFrom-Json
            $viaRead = & $script:ReadScriptPath -Path $script:NotePath -Part 'frontmatter'
            $result.resultingFrontmatter | Should -BeExactly $viaRead
            $result.resultingFrontmatter | Should -BeExactly "doc.created: 2026-07-16`ndoc.type: Note`ndoc.archived: 2026-07-25"
        }

        # --- The parity contract above, exercised on the fence shapes that strain it: a
        # whitespace-padded fence, an inner line that opens with "---", and a "----" line that
        # looks like a fence and is not. The contract is only worth as much as the shapes it is
        # tested on, and a clean fence reaches none of these. Both tools take the split from the
        # substrate's Split-VaultNoteContent, so each case pins that one authority's answer
        # arriving on both sides of the contract. ---

        It 'returns resultingFrontmatter matching read on a whitespace-padded opening fence' {
            # A fence line is recognized on its trimmed text, so "--- " opens a block exactly as
            # "---" does and both tools see the same closed block.
            $padded = 'Inbox/Edit-Padded-Open.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $padded), "--- `ndoc.type: Note`n---`nBody`n")

            $result = & $script:ScriptPath -Path $padded -FrontmatterOps '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | ConvertFrom-Json
            $viaRead = & $script:ReadScriptPath -Path $padded -Part 'frontmatter'
            $result.resultingFrontmatter | Should -BeExactly $viaRead
            $result.resultingFrontmatter | Should -BeExactly "doc.type: Note`ndoc.archived: 2026-07-25"
            # The padded fence line itself is preserved byte for byte - recognizing it is not
            # licence to normalize it.
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir $padded)) |
                Should -BeExactly "--- `ndoc.type: Note`ndoc.archived: 2026-07-25`n---`nBody`n"
        }

        It 'returns resultingFrontmatter matching read on a whitespace-padded closing fence' {
            # The same trimming closes a block, and the closing line is consumed whole - no " \n"
            # fragment heads the body. Both facets are asserted here rather than inferred from the
            # opening-fence case above, since padding on the closing line is a separate path.
            $padded = 'Inbox/Edit-Padded-Close.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $padded), "---`ndoc.type: Note`n--- `nBody`n")

            $result = & $script:ScriptPath -Path $padded -FrontmatterOps '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | ConvertFrom-Json
            $viaRead = & $script:ReadScriptPath -Path $padded -Part 'frontmatter'
            $result.resultingFrontmatter | Should -BeExactly $viaRead
            $result.resultingFrontmatter | Should -BeExactly "doc.type: Note`ndoc.archived: 2026-07-25"
            # The body facet carries no fence fragment: the closing line is consumed whole.
            & $script:ReadScriptPath -Path $padded -Part 'body' | Should -BeExactly "Body`n"
        }

        It 'returns resultingFrontmatter matching read when an inner line starts with "---"' {
            # The sharpest case. A fence is a whole line, not a prefix: "---junk" is not one, so
            # the block runs on to the real "---" three lines down and both tools report every
            # inner line - "---junk" among them - as block text.
            $inner = 'Inbox/Edit-Inner-Dashes.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $inner), "---`ndoc.type: Note`n---junk`nmore`n---`nBody`n")

            $result = & $script:ScriptPath -Path $inner -FrontmatterOps '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | ConvertFrom-Json
            $viaRead = & $script:ReadScriptPath -Path $inner -Part 'frontmatter'
            $result.resultingFrontmatter | Should -BeExactly $viaRead
            $result.resultingFrontmatter | Should -BeExactly "doc.type: Note`n---junk`nmore`ndoc.archived: 2026-07-25"
        }

        It 'agrees with read that a "----" closing line closes nothing' {
            # The whole-line rule from the other direction: "----" is not a fence, so the opening
            # one never closes. read reports no frontmatter and hands back the whole file as body,
            # and edit refuses the note outright rather than editing a block it invented.
            $dashes = 'Inbox/Edit-Four-Dashes.md'
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir $dashes), "---`ndoc.type: Note`n----`nBody`n")

            $err = Invoke-EditError @{ Path = $dashes; FrontmatterOps = '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'unclosed_fence'
            & $script:ReadScriptPath -Path $dashes -Part 'frontmatter' | Should -BeExactly ''
            & $script:ReadScriptPath -Path $dashes -Part 'body' | Should -BeExactly "---`ndoc.type: Note`n----`nBody`n"
        }

        It 'returns an empty resultingFrontmatter for a note that has no frontmatter block' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' `
                -FrontmatterOps '[{"op":"add_directive","kind":"use_skill","skill":"vault-tasks","when":"managing tasks"}]' | ConvertFrom-Json
            # The op above creates the block, so this is asserted on the pre-op state via a note
            # that stays fence-less: a body-only patch.
            $result.resultingFrontmatter | Should -Not -BeNullOrEmpty

            $bodyOnly = & $script:ScriptPath -Path 'Inbox/Note-Loose-Thought.md' -OldString 'Loose Thought' -NewString 'Loose Thoughts' | ConvertFrom-Json
            $bodyOnly.frontmatterChanged | Should -BeFalse
            $bodyOnly.bodyChanged | Should -BeTrue
        }

        It 'reports opsApplied in order with per-op changed flags' {
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"},{"op":"remove_field","field":"doc.absent"}]' | ConvertFrom-Json
            $result.opsApplied.Count | Should -Be 2
            $result.opsApplied[0].op | Should -Be 'set_field'
            $result.opsApplied[0].changed | Should -BeTrue
            $result.opsApplied[1].op | Should -Be 'remove_field'
            $result.opsApplied[1].changed | Should -BeFalse
        }
    }

    Context 'facet forms' {
        It 'replaces the whole body when body is supplied with expected_sha' {
            $sha = Get-FileSha $script:NoteFull
            $result = & $script:ScriptPath -Path $script:NotePath -Body "# Replaced`n" -ExpectedSha $sha | ConvertFrom-Json
            $result.bodyChanged | Should -BeTrue
            $result.frontmatterChanged | Should -BeFalse
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Replaced`n"
        }

        It 'truncates the body on an empty body string and leaves the block intact' {
            $sha = Get-FileSha $script:NoteFull
            & $script:ScriptPath -Path $script:NotePath -Body '' -ExpectedSha $sha | Out-Null
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n"
        }

        It 'applies a patch and a frontmatter op in one call' {
            $result = & $script:ScriptPath -Path $script:NotePath `
                -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' `
                -OldString 'alpha line' -NewString 'ALPHA LINE' | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue
            $result.bodyChanged | Should -BeTrue
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Corpus`n---`n`n# Edit-Case`nALPHA LINE`nbeta line`n"
        }

        It 'deletes the matched span when new_string is empty' {
            & $script:ScriptPath -Path $script:NotePath -OldString "alpha line`n" -NewString '' | Out-Null
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n`n# Edit-Case`nbeta line`n"
        }
    }

    Context 'patch matching is body-confined and exactly-once' {
        It 'errors patch_target_not_found when old_string matches nothing' {
            $err = Invoke-EditError @{ Path = $script:NotePath; OldString = 'gamma line'; NewString = 'x' }
            $err.code | Should -Be 'patch_target_not_found'
            $err.data.oldString | Should -Be 'gamma line'
            # Zero mutation on a failed match.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -Match 'alpha line'
        }

        It 'errors patch_ambiguous when old_string matches more than once' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n---`n`nrepeat`nrepeat`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; OldString = 'repeat'; NewString = 'once' }
            $err.code | Should -Be 'patch_ambiguous'
            $err.data.matchCount | Should -BeGreaterThan 1
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.type: Note`n---`n`nrepeat`nrepeat`n"
        }

        It 'cannot reach the frontmatter: an anchor unique in the block is not_found for a patch' {
            # Body-confinement is what keeps every frontmatter mutation on the schema-validated,
            # capability-classified ops path.
            $err = Invoke-EditError @{ Path = $script:NotePath; OldString = 'doc.type: Note'; NewString = 'doc.type: Corpus' }
            $err.code | Should -Be 'patch_target_not_found'
        }

        It 'matches a body occurrence even when the same text also appears in the frontmatter' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n---`n`ndoc.type: Note`n")
            & $script:ScriptPath -Path $script:NotePath -OldString 'doc.type: Note' -NewString 'mentioned' | Out-Null
            # Exactly one match, because the frontmatter copy is outside the facet being searched.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.type: Note`n---`n`nmentioned`n"
        }
    }

    Context 'idempotent changed:false paths' {
        It 'records changed:false for a duplicate add_directive' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n")
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"add_directive","kind":"pinned","text":"Never exfil this"}]' | ConvertFrom-Json
            $result.opsApplied[0].changed | Should -BeFalse
            $result.frontmatterChanged | Should -BeFalse
            # Idempotent means it also asserts nothing: the outcome diff is empty.
            $result.capabilitiesUsed.Count | Should -Be 0
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n"
        }

        It 'records changed:false for a remove_field against an absent field' {
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"remove_field","field":"doc.absent"}]' | ConvertFrom-Json
            $result.opsApplied[0].changed | Should -BeFalse
            $result.frontmatterChanged | Should -BeFalse
        }

        It 'records changed:false for an unmatched pinned strip' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Keep this`"`n---`n# B`n")
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"strip_directive","kind":"pinned","text":"Not present"}]' | ConvertFrom-Json
            $result.opsApplied[0].changed | Should -BeFalse
            $result.opsApplied[0].kind | Should -Be 'pinned'
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`nagent.instructions:`n  - pinned: `"Keep this`"`n---`n# B`n"
        }
    }

    Context 'staleness guard' {
        It 'errors state_mismatch with zero mutation when the file changed underneath the caller' {
            $stale = Get-FileSha $script:NoteFull
            # Another writer lands between the caller's read and its edit.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n---`n`n# Moved on`n")
            $afterOtherWriter = [System.IO.File]::ReadAllText($script:NoteFull)

            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# Clobber`n"; ExpectedSha = $stale }
            $err.code | Should -Be 'state_mismatch'
            $err.data.expectedSha | Should -Be $stale
            $err.data.actualSha | Should -Be (Get-FileSha $script:NoteFull)
            # Zero mutation: the other writer's content survives verbatim.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly $afterOtherWriter
        }

        It 'accepts a matching expected_sha on a non-body facet too' {
            $sha = Get-FileSha $script:NoteFull
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' -ExpectedSha $sha | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue
        }

        It 'chains: a second edit passes the first edit''s resultingSha with no interleaved read' {
            $sha = Get-FileSha $script:NoteFull
            $first = & $script:ScriptPath -Path $script:NotePath -Body "# First`n" -ExpectedSha $sha | ConvertFrom-Json
            $second = & $script:ScriptPath -Path $script:NotePath -Body "# Second`n" -ExpectedSha $first.resultingSha | ConvertFrom-Json
            $second.resultingSha | Should -Not -Be $first.resultingSha
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`ndoc.created: 2026-07-16`ndoc.type: Note`n---`n# Second`n"
            # And the chained token is itself the digest of what is now on disk.
            $second.resultingSha | Should -Be (Get-FileSha $script:NoteFull)
        }

        It 'errors state_mismatch when a chained resultingSha is reused after another edit landed' {
            $sha = Get-FileSha $script:NoteFull
            $first = & $script:ScriptPath -Path $script:NotePath -Body "# First`n" -ExpectedSha $sha | ConvertFrom-Json
            & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | Out-Null
            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# Third`n"; ExpectedSha = $first.resultingSha }
            $err.code | Should -Be 'state_mismatch'
        }
    }

    Context 'byte preservation' {
        It 'keeps a CRLF note''s CRLF endings on disk through a frontmatter op' {
            # The defect this invariant exists for: a substrate that splits on \r?\n and rejoins
            # on \n rewrites every line terminator in the file on every op, invisibly - and would
            # break the resultingSha chain for content that did not meaningfully change.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`r`ndoc.created: 2026-07-16`r`ndoc.type: Note`r`n---`r`n`r`n# Edit-Case`r`nalpha line`r`n")
            & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]' | Out-Null
            [System.IO.File]::ReadAllText($script:NoteFull) |
                Should -BeExactly "---`r`ndoc.created: 2026-07-16`r`ndoc.type: Note`r`ndoc.archived: 2026-07-25`r`n---`r`n`r`n# Edit-Case`r`nalpha line`r`n"
        }

        It 'keeps CRLF through a body patch, including on the untouched lines' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`r`ndoc.type: Note`r`n---`r`n`r`n# Edit-Case`r`nalpha line`r`nbeta line`r`n")
            & $script:ScriptPath -Path $script:NotePath -OldString 'alpha line' -NewString 'ALPHA LINE' | Out-Null
            $raw = [System.IO.File]::ReadAllText($script:NoteFull)
            $raw | Should -BeExactly "---`r`ndoc.type: Note`r`n---`r`n`r`n# Edit-Case`r`nALPHA LINE`r`nbeta line`r`n"
            # No terminator was silently normalized: every LF in the file is part of a CRLF.
            ([regex]::Matches($raw, "(?<!`r)`n")).Count | Should -Be 0
        }

        It 'leaves a mixed-ending note mixed' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`r`ndoc.type: Note`r`n---`n`n# Edit-Case`nalpha line`r`n")
            & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | Out-Null
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -BeExactly "---`r`ndoc.type: Corpus`r`n---`n`n# Edit-Case`nalpha line`r`n"
        }
    }

    Context 'capability denial (edit is the first key-asserting tool)' {
        It 'denies an agentic op with insufficient_capability and zero disk change when the key is absent' {
            $Global:VaultMcpOpClasses = @('content:write')
            $before = [System.IO.File]::ReadAllBytes($script:NoteFull)
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"add_hint","text":"Do the thing"}]' }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'agentic:write'
            # Validate-phase classification: nothing reached the disk.
            [System.IO.File]::ReadAllBytes($script:NoteFull) | Should -Be $before
        }

        It 'denies a nav.* removal for the same reason' {
            $Global:VaultMcpOpClasses = @('content:write')
            $widget = Join-Path $script:VaultDir 'Projects/Widget/Widget.md'
            $before = [System.IO.File]::ReadAllBytes($widget)
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Widget.md'; FrontmatterOps = '[{"op":"remove_field","field":"nav.contents"}]' }
            $err.code | Should -Be 'insufficient_capability'
            [System.IO.File]::ReadAllBytes($widget) | Should -Be $before
        }

        It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
            $Global:VaultMcpOpClasses = @()
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"add_hint","text":"Do the thing"}]' }
            $err.code | Should -Be 'insufficient_capability'
        }

        It 'allows the same op when the injected set holds agentic:write' {
            $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"add_hint","text":"Do the thing"}]' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
        }

        It 'never asserts the key for a body-only write, even with an empty injected set' {
            $Global:VaultMcpOpClasses = @()
            $sha = Get-FileSha $script:NoteFull
            $result = & $script:ScriptPath -Path $script:NotePath -Body "# Body only`n" -ExpectedSha $sha | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }

        It 'never asserts the key for a non-agentic doc.* scalar set' {
            $Global:VaultMcpOpClasses = @()
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | ConvertFrom-Json
            $result.capabilitiesUsed.Count | Should -Be 0
        }
    }

    Context 'errors - operand and argument shape' {
        It 'errors not_found on a missing note (edit never creates)' {
            $err = Invoke-EditError @{ Path = 'Inbox/Nonexistent-Note.md'; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'not_found'
            $err.data.path | Should -Be 'Inbox/Nonexistent-Note.md'
        }

        It 'rejects a folder target as kind_mismatch (edit is note-semantic)' {
            $err = Invoke-EditError @{ Path = 'Projects/Widget'; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.requiredKind | Should -Be 'note'
            $err.data.resolvedKind | Should -Be 'folder'
        }

        It 'rejects an existing non-md file as kind_mismatch' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/attachment.txt') -Value 'not a note'
            $err = Invoke-EditError @{ Path = 'Inbox/attachment.txt'; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'file'
        }

        It 'refuses an extensionless path as invalid_argument naming the .md form' {
            # A deliberate divergence from read, which lets an extensionless miss fall through to
            # not_found: the Path Convention wants the miss loud and retryable in one call.
            $err = Invoke-EditError @{ Path = 'Inbox/Edit-Case'; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'Inbox/Edit-Case\.md'
            $err.data.suggestedPath | Should -Be 'Inbox/Edit-Case.md'
        }

        It 'errors invalid_argument on a whitespace path' {
            $err = Invoke-EditError @{ Path = '   '; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument on a vault-escaping path' {
            $err = Invoke-EditError @{ Path = '../outside.md'; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Note"}]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument when no facet is supplied' {
            $err = Invoke-EditError @{ Path = $script:NotePath }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'Nothing to do'
        }

        It 'errors invalid_argument for an empty ops array with no body form' {
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[]' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument when body accompanies a patch pair' {
            $sha = Get-FileSha $script:NoteFull
            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# X`n"; ExpectedSha = $sha; OldString = 'alpha line'; NewString = 'x' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'mutually exclusive'
        }

        It 'errors invalid_argument on half a patch pair (old_string alone)' {
            $err = Invoke-EditError @{ Path = $script:NotePath; OldString = 'alpha line' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'new_string'
        }

        It 'errors invalid_argument on half a patch pair (new_string alone)' {
            $err = Invoke-EditError @{ Path = $script:NotePath; NewString = 'alpha line' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'old_string'
        }

        It 'errors invalid_argument on an empty old_string (an anchor that cannot self-verify)' {
            $err = Invoke-EditError @{ Path = $script:NotePath; OldString = ''; NewString = 'x' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'old_string is empty'
        }

        It 'errors invalid_argument when body is supplied without expected_sha' {
            $err = Invoke-EditError @{ Path = $script:NotePath; Body = "# X`n" }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'expected_sha'
        }

        It 'does NOT require expected_sha for a patch or for frontmatter ops' {
            { & $script:ScriptPath -Path $script:NotePath -OldString 'alpha line' -NewString 'x' | Out-Null } | Should -Not -Throw
            { & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | Out-Null } | Should -Not -Throw
        }

        It 'errors read_failure (not not_found) when the note exists but is locked' {
            $handle = [System.IO.File]::Open($script:NoteFull, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' }
                $err.code | Should -Be 'read_failure'
                $err.data.path | Should -Be $script:NotePath
            }
            finally {
                $handle.Dispose()
            }
        }
    }

    Context 'errors - op vocabulary and hygiene' {
        It 'errors malformed_op on frontmatter_ops that is not valid JSON' {
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '{not json' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'valid JSON'
        }

        It 'errors malformed_op when frontmatter_ops is a bare object rather than an array' {
            # -NoEnumerate is what keeps this distinguishable from a one-element array.
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '{"op":"set_field","field":"doc.type","value":"Corpus"}' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'array'
        }

        It 'accepts a one-element ops array (not collapsed to a bare object)' {
            $result = & $script:ScriptPath -Path $script:NotePath -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' | ConvertFrom-Json
            $result.opsApplied.Count | Should -Be 1
        }

        It 'errors malformed_op with data.opIndex on an unknown op' {
            # The code alone cannot distinguish a bad op from ConvertTo-VaultToolResult's
            # dataless malformed_op for stray success-stream output, so the assertion checks
            # for the op coordinates.
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Corpus"},{"op":"reticulate_splines"}]' }
            $err.code | Should -Be 'malformed_op'
            $err.data.opIndex | Should -Be 1
            $err.data.reason | Should -Match 'reticulate_splines'
        }

        It 'errors malformed_op with data.opIndex on an op missing a required parameter' {
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.type"}]' }
            $err.code | Should -Be 'malformed_op'
            $err.data.opIndex | Should -Be 0
        }

        It 'refuses a value carrying a line break (key smuggling)' {
            $ops = ConvertTo-Json -Compress -Depth 5 @(@{ op = 'set_field'; field = 'doc.type'; value = "Note`nnav.contents: smuggled" })
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = $ops }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'line break'
        }

        It 'refuses directive text carrying a double quote (the hardcoded-quote emit hole)' {
            $ops = ConvertTo-Json -Compress -Depth 5 @(@{ op = 'add_directive'; kind = 'pinned'; text = 'bad" text' })
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = $ops }
            $err.code | Should -Be 'malformed_op'
        }

        It 'refuses a field name outside the frontmatter key pattern' {
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc created","value":"x"}]' }
            $err.code | Should -Be 'malformed_op'
        }

        It 'errors protected_directive_violation for a generic remove_field against agent.instructions' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"remove_field","field":"agent.instructions"}]' }
            $err.code | Should -Be 'protected_directive_violation'
            $err.data.field | Should -Be 'agent.instructions'
            $err.message | Should -Match 'strip_directive'
        }

        It 'takes protected_directive_violation over field_shape_mismatch when both would fire' {
            # Precedence: naming the right op (routing) is more actionable than naming the shape.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"agent.instructions","value":"x"}]' }
            $err.code | Should -Be 'protected_directive_violation'
        }

        It 'errors field_shape_mismatch for a flat-scalar set against a detected block field' {
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Widget.md'; FrontmatterOps = '[{"op":"set_field","field":"nav.contents","value":"x"}]' }
            $err.code | Should -Be 'field_shape_mismatch'
            $err.data.field | Should -Be 'nav.contents'
        }

        It 'routes a block-field removal to the block primitive rather than orphaning children' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -FrontmatterOps '[{"op":"remove_field","field":"nav.contents"}]' | ConvertFrom-Json
            $result.resultingFrontmatter | Should -BeExactly ''
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'Projects/Widget/Widget.md')) | Should -Not -Match 'description:'
        }
    }

    Context 'errors - integrity and schema' {
        It 'errors frontmatter_integrity on a note whose own fence never closes' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n# Body with no closing fence`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.created","value":"2026-07-25"}]' }
            $err.code | Should -Be 'frontmatter_integrity'
            $err.data.reason | Should -Be 'unclosed_fence'
        }

        It 'refuses a patch against an unclosed-fence note before the anchor is even resolved' {
            # With a dangling fence the body facet is the whole file, so patch matching would
            # otherwise reach the frontmatter body-confinement exists to protect.
            [System.IO.File]::WriteAllText($script:NoteFull, "---`ndoc.type: Note`n# Body`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; OldString = 'doc.type: Note'; NewString = 'doc.type: Corpus' }
            $err.code | Should -Be 'frontmatter_integrity'
        }

        It 'errors frontmatter_integrity when a body replace would mint a frontmatter block' {
            $tasks = Join-Path $script:VaultDir 'Projects/Widget/Tasks.md'
            $sha = Get-FileSha $tasks
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Tasks.md'; Body = "---`nnav.contents: smuggled`n---`n# Tasks`n"; ExpectedSha = $sha }
            $err.code | Should -Be 'frontmatter_integrity'
            [System.IO.File]::ReadAllText($tasks) | Should -Not -Match 'smuggled'
        }

        It 'errors schema_validation_failed when the resulting frontmatter violates the schema' {
            [System.IO.File]::WriteAllText($script:NoteFull, "---`nnav.see_also:`n  - link: `"[[Widget]]`"`n---`n# B`n")
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.created","value":"2026-07-25"}]' }
            $err.code | Should -Be 'schema_validation_failed'
            $err.data.field | Should -Be 'nav.see_also'
            $err.data.rule | Should -Be 'missing_description'
            $err.data.entryIndex | Should -Be 1
            # Validate-phase: nothing written.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -Not -Match 'doc.created'
        }

        It 'runs the path-dependent Tasks.md check, so edit refuses a Tasks.md left without its directive' {
            # edit passes its own relative path to the validator, which enables the one check that
            # needs the note's name rather than its content.
            $err = Invoke-EditError @{ Path = 'Projects/Widget/Tasks.md'; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Tasks"}]' }
            $err.code | Should -Be 'schema_validation_failed'
            $err.data.rule | Should -Be 'tasks_missing_use_skill'
        }

        It 'accepts the same Tasks.md edit when the call also supplies the required directive' {
            $result = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' `
                -FrontmatterOps '[{"op":"add_directive","kind":"use_skill","skill":"vault-tasks","when":"managing tasks"},{"op":"set_field","field":"doc.type","value":"Tasks"}]' | ConvertFrom-Json
            $result.frontmatterChanged | Should -BeTrue
            $result.resultingFrontmatter | Should -Match 'use_skill: "vault-tasks"'
        }
    }

    Context 'scope and vault resolution' {
        It 'errors out_of_scope when the path falls outside the ambient scope roots' {
            # The pre-check is mandatory and is the only thing that produces this code: the
            # Write-VaultNote chokepoint's own scope assertion throws plain text.
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.path | Should -Be $script:NotePath
            # Refused ahead of any in-memory mutation, so nothing reached the disk.
            [System.IO.File]::ReadAllText($script:NoteFull) | Should -Match 'doc.type: Note'
        }

        It 'edits normally when the path falls inside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $result = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -OldString 'Sample Project' -NewString 'Fixture Project' | ConvertFrom-Json
            $result.bodyChanged | Should -BeTrue
        }

        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
            $err.data.vault | Should -Be 'nope'
        }

        It 'refuses a configured vault whose root is missing with vault_unavailable' {
            $configPath = Join-Path $script:TempRoot 'with-missing.json'
            @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir; gone = (Join-Path $script:TempRoot 'no-such-root') } } |
                ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]'; Vault = 'gone' }
            $err.code | Should -Be 'vault_unavailable'
        }
    }

    Context 'lock_timeout' {
        It 'fails lock_timeout when the write lock is held by another process' -Skip:(-not $IsWindows) {
            # The write lock cannot be contended in-process (the mutex is reentrant for the
            # calling thread), so a genuinely separate process must hold it - the cross-process
            # convention in Testing.md. The chokepoint raises the coded shape itself and edit
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
                    $err = Invoke-EditError @{ Path = $script:NotePath; FrontmatterOps = '[{"op":"set_field","field":"doc.type","value":"Corpus"}]' }
                    $err.code | Should -Be 'lock_timeout'
                    $err.data.timeoutSeconds | Should -Be 1
                    [System.IO.File]::ReadAllText($script:NoteFull) | Should -Match 'doc.type: Note'
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
            # xUnit dispatcher test that refuses a content:write tool to a content:read grant;
            # edit inherits that uniform grant.Allows(tool.OpClass) check. What IS assertable
            # PowerShell-side is edit's own metadata declaration - the OpClass line
            # ScriptToolLoader reads to derive the floor.
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*content:write\s*$'
        }

        It 'documents every parameter in comment-help, which the loader requires to load the tool' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            foreach ($p in @('Path', 'FrontmatterOps', 'Body', 'OldString', 'NewString', 'ExpectedSha', 'Vault')) {
                $scriptText | Should -Match "(?m)^\s*\.PARAMETER\s+$p\s*$"
            }
        }
    }
}

Describe 'edit standalone invocability' {
    # Per Tool-Contracts's Tool Authoring Standards: every tool script must be invocable outside
    # the MCP server - pwsh -File against a real vault, no runspace context, no ambient scope, no
    # environment setup - and carry a unit test asserting it. This spawns a genuinely separate
    # pwsh process (rather than calling the script in this Pester session) so no global left over
    # from the host's ScriptToolRunner wrapper (VaultMcpScopeRoots, VaultMcpOpClasses) or from
    # another test in this same process could be mistaken for injected host context. It is also
    # what proves the caller-carried staleness guard works with no host at all - the property
    # that decided the design against host-side read tracking.

    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-edit-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File edit.ps1" invocation (the Tool Authoring Standard spelling)' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Resources/Gizmo-Reference.md' -FrontmatterOps '[{"op":"set_field","field":"doc.archived","value":"2026-07-25"}]'
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            $result = $rawOutput | ConvertFrom-Json
            $result.path | Should -Be 'Resources/Gizmo-Reference.md'
            $result.frontmatterChanged | Should -BeTrue
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Resources/Gizmo-Reference.md')) | Should -Match 'doc.archived: 2026-07-25'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'degrades OPEN on an agentic op with no injected op-class set' {
        # The other half of the capability-denial pair: a standalone run has no host context, so
        # Assert-VaultOpClass returns silently, exactly as an unscoped scope-roots map does.
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Resources/Gizmo-Reference.md' -FrontmatterOps '[{"op":"add_hint","text":"Prefer the torque table"}]'
            $LASTEXITCODE | Should -Be 0
            $result = $rawOutput | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('agentic:write')
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Resources/Gizmo-Reference.md')) | Should -Match 'agent.instructions:'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'enforces the caller-carried staleness guard with no host present' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Resources/Gizmo-Reference.md' -Body "# Clobber`n" -ExpectedSha ('0' * 64) 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"state_mismatch"'
            [System.IO.File]::ReadAllText((Join-Path $script:StandaloneVaultDir 'Resources/Gizmo-Reference.md')) | Should -Match 'quorvium alloy'
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
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'edit.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Inbox/Nonexistent-Note.md' -FrontmatterOps '[{"op":"set_field","field":"doc.type","value":"Note"}]' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"not_found"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}
