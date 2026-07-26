# Tests for VaultAccess Public/Frontmatter.ps1 - the in-memory frontmatter primitives.
# Pure: every function takes and returns a content string, so there is no filesystem to sandbox
# and no shared fixture is needed.
#
# Assertions are on exact strings (-BeExactly), never on a parsed round trip: the invariant under
# test is that a mutation rewrites only the lines it targets and leaves every other byte - line
# terminators included - alone, and a parse-and-compare would hide exactly the damage that
# matters.
#
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force
}

Describe 'Get-VaultNoteFenceState' {
    It 'reports None for a note with no leading fence' {
        Get-VaultNoteFenceState -Content "# Body only" | Should -Be 'None'
    }

    It 'reports Closed for a complete block' {
        Get-VaultNoteFenceState -Content "---`na: 1`n---`n# Body" | Should -Be 'Closed'
    }

    It 'reports Unclosed for an opening fence that never closes' {
        # The distinction the skill-side substrate collapsed into $null, which is what let a
        # set-field call stack a second complete block on top of a dangling fence.
        Get-VaultNoteFenceState -Content "---`na: 1`n# Body" | Should -Be 'Unclosed'
    }
}

Describe 'Get-VaultFrontmatterLines' {
    It 'returns the lines between the fences' {
        $lines = Get-VaultFrontmatterLines -Content "---`na: 1`nb: 2`n---`n# Body"
        $lines.Count | Should -Be 2
        $lines[0] | Should -BeExactly 'a: 1'
    }

    It 'returns an empty array (not $null) for an empty block' {
        # $null and an empty array are different answers here - "no block at all" versus "a block
        # with no fields" - and every mutator branches on the distinction.
        $lines = Get-VaultFrontmatterLines -Content "---`n---`n# Body"
        ($null -eq $lines) | Should -BeFalse
        $lines -is [array] | Should -BeTrue
        $lines.Count | Should -Be 0
    }

    It 'returns $null when there is no frontmatter at all' {
        Get-VaultFrontmatterLines -Content '# Body only' | Should -BeNullOrEmpty
    }

    It 'returns $null for an unclosed fence' {
        Get-VaultFrontmatterLines -Content "---`na: 1`n# Body" | Should -BeNullOrEmpty
    }

    It 'strips CRLF terminators from the returned text lines' {
        $lines = Get-VaultFrontmatterLines -Content "---`r`na: 1`r`n---`r`n# Body"
        $lines[0] | Should -BeExactly 'a: 1'
    }
}

Describe 'Split-VaultNoteContent' {
    It 'splits the two facets, body verbatim after the closing fence' {
        $split = Split-VaultNoteContent -Content "---`na: 1`n---`n# Body`nmore`n"
        $split.FenceState | Should -Be 'Closed'
        $split.Frontmatter | Should -BeExactly 'a: 1'
        $split.Body | Should -BeExactly "# Body`nmore`n"
    }

    It 'preserves CRLF inside the raw frontmatter block' {
        $split = Split-VaultNoteContent -Content "---`r`na: 1`r`nb: 2`r`n---`r`n# Body"
        $split.Frontmatter | Should -BeExactly "a: 1`r`nb: 2"
        $split.Body | Should -BeExactly '# Body'
    }

    It 'treats a fence-less note as all body' {
        $split = Split-VaultNoteContent -Content '# Body only'
        $split.FenceState | Should -Be 'None'
        $split.Frontmatter | Should -BeExactly ''
        $split.Body | Should -BeExactly '# Body only'
    }

    It 'reports an empty body for a note that ends at the closing fence' {
        (Split-VaultNoteContent -Content "---`na: 1`n---`n").Body | Should -BeExactly ''
    }

    It 'gives a BodyOffset that splices exactly at the body' {
        $content = "---`na: 1`n---`n# Body"
        $split = Split-VaultNoteContent -Content $content
        $content.Substring(0, $split.BodyOffset) | Should -BeExactly "---`na: 1`n---`n"
    }
}

Describe 'Get-VaultFrontmatterField' {
    It 'reads a flat scalar, stripping one layer of quotes' {
        Get-VaultFrontmatterField -Content "---`ndoc.type: `"Corpus`"`n---`n# B" -Field 'doc.type' | Should -BeExactly 'Corpus'
    }

    It 'returns $null for an absent field' {
        Get-VaultFrontmatterField -Content "---`ndoc.type: Corpus`n---`n# B" -Field 'doc.created' | Should -BeNullOrEmpty
    }

    It 'returns $null when there is no frontmatter block' {
        Get-VaultFrontmatterField -Content '# B' -Field 'doc.created' | Should -BeNullOrEmpty
    }

    It 'returns $null for a present-but-empty value, so it cannot serve as a presence test' {
        Get-VaultFrontmatterField -Content "---`ndoc.type:`n---`n# B" -Field 'doc.type' | Should -BeNullOrEmpty
    }
}

Describe 'Get-VaultFrontmatterFieldShape' {
    It 'reports Absent for a missing field' {
        Get-VaultFrontmatterFieldShape -Content "---`na: 1`n---`n# B" -Field 'nav.contents' | Should -Be 'Absent'
    }

    It 'reports Absent when there is no frontmatter block' {
        Get-VaultFrontmatterFieldShape -Content '# B' -Field 'doc.created' | Should -Be 'Absent'
    }

    It 'reports Scalar for a flat field' {
        Get-VaultFrontmatterFieldShape -Content "---`ndoc.type: Corpus`n---`n# B" -Field 'doc.type' | Should -Be 'Scalar'
    }

    It 'reports Block for a key with indented children' {
        # This is the detection field_shape_mismatch depends on, and it must come from the block
        # parser: Get-VaultFrontmatterField returns $null here exactly as it does for an absent
        # field, so it cannot tell a block from a miss.
        $content = "---`nnav.contents:`n  - link: `"[[X]]`"`n    description: `"d`"`n---`n# B"
        Get-VaultFrontmatterFieldShape -Content $content -Field 'nav.contents' | Should -Be 'Block'
        Get-VaultFrontmatterField -Content $content -Field 'nav.contents' | Should -BeNullOrEmpty
    }

    It 'reports Scalar for a bare key with no value and no children (nothing to orphan)' {
        Get-VaultFrontmatterFieldShape -Content "---`ndoc.type:`n---`n# B" -Field 'doc.type' | Should -Be 'Scalar'
    }
}

Describe 'Set-VaultFrontmatterField' {
    It 'updates an existing field in place, preserving field order' {
        Set-VaultFrontmatterField -Content "---`na: 1`ndoc.type: Idea`nb: 2`n---`n# B" -Field 'doc.type' -Value 'Corpus' |
            Should -BeExactly "---`na: 1`ndoc.type: Corpus`nb: 2`n---`n# B"
    }

    It 'appends a new field just before the closing fence' {
        Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc.type' -Value 'Corpus' |
            Should -BeExactly "---`na: 1`ndoc.type: Corpus`n---`n# B"
    }

    It 'creates a frontmatter block when the note has none' {
        Set-VaultFrontmatterField -Content '# B' -Field 'doc.type' -Value 'Corpus' |
            Should -BeExactly "---`ndoc.type: Corpus`n---`n# B"
    }

    It 'preserves CRLF throughout a CRLF note, on both the block and the body' {
        # The invariant the skill-side substrate broke: it split on \r?\n and re-joined on \n,
        # rewriting every terminator in the file including in body text it never touched.
        $content = "---`r`na: 1`r`n---`r`n# Body`r`nsecond`r`nthird`r`n"
        $result = Set-VaultFrontmatterField -Content $content -Field 'doc.type' -Value 'Corpus'
        $result | Should -BeExactly "---`r`na: 1`r`ndoc.type: Corpus`r`n---`r`n# Body`r`nsecond`r`nthird`r`n"
        ([regex]::Matches($result, "(?<!`r)`n")).Count | Should -Be 0 -Because 'no bare LF may be introduced into a CRLF file'
    }

    It 'leaves a mixed-ending note mixed, touching only the line it changed' {
        $content = "---`r`na: 1`nb: 2`r`n---`n# Body`r`ntail"
        Set-VaultFrontmatterField -Content $content -Field 'b' -Value '3' |
            Should -BeExactly "---`r`na: 1`nb: 3`r`n---`n# Body`r`ntail"
    }

    It 'borrows the neighbouring terminator for an inserted line in a CRLF note' {
        Set-VaultFrontmatterField -Content "---`r`na: 1`r`n---`r`n# B" -Field 'doc.type' -Value 'Corpus' |
            Should -BeExactly "---`r`na: 1`r`ndoc.type: Corpus`r`n---`r`n# B"
    }

    It 'refuses an unclosed fence with frontmatter_integrity instead of stacking a second block' {
        $err = { Set-VaultFrontmatterField -Content "---`nfoo: 1`n# Body" -Field 'doc.type' -Value 'Corpus' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'frontmatter_integrity'
    }

    It 'refuses a field name outside the frontmatter key pattern' {
        $err = { Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc created' -Value 'x' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'refuses a value carrying a line break, which would mint a second top-level key' {
        $err = { Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc.type' -Value "Corpus`nnav.contents: smuggled" } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'refuses a value carrying a quote, which the read side would strip without unescaping' {
        { Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc.type' -Value '"Corpus"' } | Should -Throw '*malformed_op*'
    }
}

Describe 'Remove-VaultFrontmatterField' {
    It 'removes a flat scalar and leaves the surrounding bytes untouched' {
        Remove-VaultFrontmatterField -Content "---`na: 1`ndoc.type: Corpus`nb: 2`n---`n# B" -Field 'doc.type' |
            Should -BeExactly "---`na: 1`nb: 2`n---`n# B"
    }

    It 'is a no-op when the field is absent' {
        $content = "---`na: 1`n---`n# B"
        Remove-VaultFrontmatterField -Content $content -Field 'doc.type' | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter block' {
        Remove-VaultFrontmatterField -Content '# Body only' -Field 'doc.type' | Should -BeExactly '# Body only'
    }

    It 'preserves CRLF on the lines it keeps' {
        Remove-VaultFrontmatterField -Content "---`r`na: 1`r`ndoc.type: Corpus`r`n---`r`n# B" -Field 'doc.type' |
            Should -BeExactly "---`r`na: 1`r`n---`r`n# B"
    }

    It 'removes the last field inside the block when nothing follows the closing fence' {
        # Regression for the skill-side index arithmetic: the removal built a reversed index
        # range whenever the slice ran to the end of the content, corrupting or silently
        # dropping the edit instead of removing the line.
        Remove-VaultFrontmatterField -Content "---`na: 1`nfoo: 1`n---" -Field 'foo' |
            Should -BeExactly "---`na: 1`n---"
    }

    It 'refuses an unclosed fence rather than computing a reversed range over it' {
        $err = { Remove-VaultFrontmatterField -Content "---`nfoo: 1" -Field 'foo' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'frontmatter_integrity'
    }
}

Describe 'Remove-VaultFrontmatterBlock' {
    It 'removes a block field and its children' {
        Remove-VaultFrontmatterBlock -Content "---`nagent.pinned_instructions:`n  - `"a`"`n  - `"b`"`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body" -Field 'agent.pinned_instructions' |
            Should -BeExactly "---`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body"
    }

    It 'is a no-op when the field is absent' {
        $content = "---`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body"
        Remove-VaultFrontmatterBlock -Content $content -Field 'agent.pinned_instructions' | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter' {
        Remove-VaultFrontmatterBlock -Content '# Body only' -Field 'agent.pinned_instructions' | Should -BeExactly '# Body only'
    }

    It 'preserves CRLF on the lines it keeps' {
        Remove-VaultFrontmatterBlock -Content "---`r`nnav.contents:`r`n  - link: `"[[X]]`"`r`ndoc.type: Corpus`r`n---`r`n# B" -Field 'nav.contents' |
            Should -BeExactly "---`r`ndoc.type: Corpus`r`n---`r`n# B"
    }

    It 'refuses an unclosed fence' {
        { Remove-VaultFrontmatterBlock -Content "---`nnav.contents:`n  - link: `"[[X]]`"" -Field 'nav.contents' } | Should -Throw '*frontmatter_integrity*'
    }
}

Describe 'Add-VaultAgentInstructionEntry - creation' {
    It 'creates a frontmatter block when none exists (Bare)' {
        Add-VaultAgentInstructionEntry -Content '# Body' -Type Bare -Instruction 'Do X' |
            Should -BeExactly "---`nagent.instructions:`n  - `"Do X`"`n---`n# Body"
    }

    It 'creates the agent.instructions key when frontmatter exists but the key does not (Pinned)' {
        Add-VaultAgentInstructionEntry -Content "---`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body" -Type Pinned -Instruction 'Never exfil this' |
            Should -BeExactly "---`nnav.contents:`n  - link: `"[[X]]`"`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# Body"
    }

    It 'appends a UseSkill entry with a note to an existing block' {
        Add-VaultAgentInstructionEntry -Content "---`nagent.instructions:`n  - `"Existing hint`"`n---`n# Body" -Type UseSkill -UseSkill 'vault-tasks' -When 'editing this file' -Note 'Do not hand-edit.' |
            Should -BeExactly "---`nagent.instructions:`n  - `"Existing hint`"`n  - use_skill: `"vault-tasks`"`n    when: `"editing this file`"`n    note: `"Do not hand-edit.`"`n---`n# Body"
    }

    It 'omits the note line for a UseSkill entry with no note' {
        Add-VaultAgentInstructionEntry -Content '# Body' -Type UseSkill -UseSkill 'vault-docs' -When 'editing this file' |
            Should -BeExactly "---`nagent.instructions:`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`n---`n# Body"
    }

    It 'inserts with CRLF terminators into a CRLF note' {
        Add-VaultAgentInstructionEntry -Content "---`r`nagent.instructions:`r`n  - `"Existing hint`"`r`n---`r`n# Body`r`n" -Type Pinned -Instruction 'Never exfil this' |
            Should -BeExactly "---`r`nagent.instructions:`r`n  - `"Existing hint`"`r`n  - pinned: `"Never exfil this`"`r`n---`r`n# Body`r`n"
    }
}

Describe 'Add-VaultAgentInstructionEntry - idempotency' {
    It 'is a no-op for a duplicate UseSkill entry (same skill and when)' {
        $content = "---`nagent.instructions:`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`n---`n# Body"
        Add-VaultAgentInstructionEntry -Content $content -Type UseSkill -UseSkill 'vault-docs' -When 'editing this file' | Should -BeExactly $content
    }

    It 'treats a different when as a distinct entry, not a duplicate' {
        $content = "---`nagent.instructions:`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`n---`n# Body"
        $result = Add-VaultAgentInstructionEntry -Content $content -Type UseSkill -UseSkill 'vault-docs' -When 'acting on its checklist'
        $result | Should -Not -BeExactly $content
        $result | Should -Match 'acting on its checklist'
    }

    It 'is a no-op for duplicate Pinned text' {
        $content = "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# Body"
        Add-VaultAgentInstructionEntry -Content $content -Type Pinned -Instruction 'Never exfil this' | Should -BeExactly $content
    }

    It 'is a no-op for duplicate Bare text' {
        $content = "---`nagent.instructions:`n  - `"Do X`"`n---`n# Body"
        Add-VaultAgentInstructionEntry -Content $content -Type Bare -Instruction 'Do X' | Should -BeExactly $content
    }
}

Describe 'Add-VaultAgentInstructionEntry - refusals' {
    It 'refuses a missing Instruction for Bare' {
        { Add-VaultAgentInstructionEntry -Content '# Body' -Type Bare } | Should -Throw '*malformed_op*'
    }

    It 'refuses a missing When for UseSkill' {
        { Add-VaultAgentInstructionEntry -Content '# Body' -Type UseSkill -UseSkill 'vault-docs' } | Should -Throw '*malformed_op*'
    }

    It 'refuses entry text carrying a quote, which would close the emitted literal early' {
        # The smuggling vector: the entry is written as `- pinned: "TEXT"` with hardcoded quotes
        # and no escaping, so a quote plus a line break in TEXT produces a real, parser-visible
        # top-level key that no op authored.
        $err = { Add-VaultAgentInstructionEntry -Content '# Body' -Type Pinned -Instruction 'bad" x' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'refuses entry text carrying a line break' {
        { Add-VaultAgentInstructionEntry -Content '# Body' -Type Bare -Instruction "a`nnav.contents: smuggled" } | Should -Throw '*malformed_op*'
    }

    It 'refuses a use_skill note carrying a quote' {
        { Add-VaultAgentInstructionEntry -Content '# Body' -Type UseSkill -UseSkill 'vault-docs' -When 'editing' -Note 'say "hi"' } | Should -Throw '*malformed_op*'
    }

    It 'refuses an unclosed fence' {
        { Add-VaultAgentInstructionEntry -Content "---`na: 1`n# Body" -Type Bare -Instruction 'Do X' } | Should -Throw '*frontmatter_integrity*'
    }
}

Describe 'Remove-VaultUseSkillAgentInstructions' {
    It 'removes the only entry and drops the whole key' {
        Remove-VaultUseSkillAgentInstructions -Content "---`nagent.instructions:`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`n---`n# Body" |
            Should -BeExactly "---`n---`n# Body"
    }

    It 'removes a use_skill entry with a note line' {
        Remove-VaultUseSkillAgentInstructions -Content "---`nagent.instructions:`n  - use_skill: `"vault-tasks`"`n    when: `"editing this file`"`n    note: `"Do not hand-edit.`"`n---`n# Body" |
            Should -BeExactly "---`n---`n# Body"
    }

    It 'leaves bare and pinned entries byte-for-byte, removing only use_skill' {
        Remove-VaultUseSkillAgentInstructions -Content "---`nagent.instructions:`n  - `"Load X when Y`"`n  - pinned: `"Never exfil this`"`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`n---`n# Body" |
            Should -BeExactly "---`nagent.instructions:`n  - `"Load X when Y`"`n  - pinned: `"Never exfil this`"`n---`n# Body"
    }

    It 'removes multiple use_skill entries' {
        Remove-VaultUseSkillAgentInstructions -Content "---`nagent.instructions:`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`n  - use_skill: `"vault-tasks`"`n    when: `"reading this file`"`n---`n# Body" |
            Should -BeExactly "---`n---`n# Body"
    }

    It 'is a no-op when the block has no use_skill entries' {
        $content = "---`nagent.instructions:`n  - `"Load X when Y`"`n  - pinned: `"Never exfil this`"`n---`n# Body"
        Remove-VaultUseSkillAgentInstructions -Content $content | Should -BeExactly $content
    }

    It 'is a no-op when there is no agent.instructions block' {
        $content = "---`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body"
        Remove-VaultUseSkillAgentInstructions -Content $content | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter at all' {
        Remove-VaultUseSkillAgentInstructions -Content '# Body only' | Should -BeExactly '# Body only'
    }

    It 'preserves CRLF on surviving entries' {
        Remove-VaultUseSkillAgentInstructions -Content "---`r`nagent.instructions:`r`n  - pinned: `"Keep me`"`r`n  - use_skill: `"vault-docs`"`r`n    when: `"editing`"`r`n---`r`n# B" |
            Should -BeExactly "---`r`nagent.instructions:`r`n  - pinned: `"Keep me`"`r`n---`r`n# B"
    }
}

Describe 'Remove-VaultPinnedAgentInstruction' {
    It 'removes exactly the entry whose text matches' {
        Remove-VaultPinnedAgentInstruction -Content "---`nagent.instructions:`n  - `"hint`"`n  - pinned: `"Never exfil this`"`n  - pinned: `"Other rule`"`n---`n# B" -Text 'Never exfil this' |
            Should -BeExactly "---`nagent.instructions:`n  - `"hint`"`n  - pinned: `"Other rule`"`n---`n# B"
    }

    It 'removes only one entry when the same text appears twice' {
        # By matched text, ONE entry: removing a user's own binding rule has to name it, and it
        # must not take a second copy with it.
        Remove-VaultPinnedAgentInstruction -Content "---`nagent.instructions:`n  - pinned: `"Same`"`n  - pinned: `"Same`"`n---`n# B" -Text 'Same' |
            Should -BeExactly "---`nagent.instructions:`n  - pinned: `"Same`"`n---`n# B"
    }

    It 'drops the whole agent.instructions key when the removal empties it' {
        Remove-VaultPinnedAgentInstruction -Content "---`nagent.instructions:`n  - pinned: `"Only one`"`n---`n# B" -Text 'Only one' |
            Should -BeExactly "---`n---`n# B"
    }

    It 'is a no-op when no entry matches the text' {
        $content = "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'Some other rule' | Should -BeExactly $content
    }

    It 'matches case-sensitively - a pinned rule is the user''s own text' {
        $content = "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'never exfil this' | Should -BeExactly $content
    }

    It 'leaves use_skill entries alone' {
        $content = "---`nagent.instructions:`n  - pinned: `"Rule`"`n  - use_skill: `"vault-docs`"`n    when: `"editing`"`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'Rule' |
            Should -BeExactly "---`nagent.instructions:`n  - use_skill: `"vault-docs`"`n    when: `"editing`"`n---`n# B"
    }

    It 'is a no-op when there is no agent.instructions block' {
        $content = "---`nnav.contents:`n  - link: `"[[X]]`"`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'Rule' | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter at all' {
        Remove-VaultPinnedAgentInstruction -Content '# Body only' -Text 'Rule' | Should -BeExactly '# Body only'
    }

    It 'preserves CRLF on surviving entries' {
        Remove-VaultPinnedAgentInstruction -Content "---`r`nagent.instructions:`r`n  - pinned: `"Go`"`r`n  - pinned: `"Stay`"`r`n---`r`n# B" -Text 'Go' |
            Should -BeExactly "---`r`nagent.instructions:`r`n  - pinned: `"Stay`"`r`n---`r`n# B"
    }

    It 'refuses match text carrying a quote' {
        { Remove-VaultPinnedAgentInstruction -Content "---`nagent.instructions:`n  - pinned: `"x`"`n---`n# B" -Text 'bad" text' } | Should -Throw '*malformed_op*'
    }
}
