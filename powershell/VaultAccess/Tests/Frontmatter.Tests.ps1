# Tests for VaultAccess Public/Frontmatter.ps1 - the in-memory frontmatter primitives.
# Pure: every function takes and returns a content string, so there is no filesystem to sandbox
# and no shared fixture is needed.
#
# Assertions are on exact strings (-BeExactly), never on a parsed round trip, because the emitted
# shape is part of the contract - the vault's list indentation and its key order both have to
# survive a mutation, and a parse-and-compare would hide either changing. What is NOT asserted
# anywhere is the frontmatter block's original bytes: a mutation reserializes the whole block, so
# quoting is the emitter's to choose. The body is the facet held byte-for-byte, modulo the line
# terminators every primitive normalizes to LF.
#
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force

    # A folder note's nav.contents in the shape every folder note in the vault carries: link
    # entries first, folder entries last, and already in emitted form so a no-op mutation compares
    # equal to it. The nav entry insertion defaults are defined against it.
    $script:NavNote = "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n  - folder: Inbox`n    description: Working docs`n---`n# Body`n"
}

Describe 'Split-VaultNoteContent' {
    It 'splits the two facets, body verbatim after the closing fence' {
        $split = Split-VaultNoteContent -Content "---`na: 1`n---`n# Body`nmore`n"
        $split.FenceState | Should -Be 'Closed'
        $split.Frontmatter | Should -BeExactly 'a: 1'
        $split.Body | Should -BeExactly "# Body`nmore`n"
    }

    It 'hands back the block text raw, never parsed - CRLF inside it included' {
        # The read side normalizes nothing: `read part: frontmatter` delegates here, and it reports
        # what is on disk. Normalization is the WRITE side's, and only when a block is reserialized.
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

    It 'treats an unclosed fence as no frontmatter at all, the whole note being body' {
        # The standard's reading, adopted over the hand-rolled substrate's third fence state: a
        # '---' with nothing closing it is a thematic break followed by a paragraph, so there is no
        # block here to report, refuse, or mutate.
        $content = "---`na: 1`n# Body"
        $split = Split-VaultNoteContent -Content $content
        $split.FenceState | Should -Be 'None'
        $split.Frontmatter | Should -BeExactly ''
        $split.Body | Should -BeExactly $content
    }

    It 'treats an empty fence pair as no frontmatter at all' {
        # Two thematic breaks to any standard parser. The substrate used to both accept this shape
        # and produce it, which is fixed at the producer: a removal that empties the block drops
        # the fence rather than leaving the pair behind.
        $split = Split-VaultNoteContent -Content "---`n---`nBody"
        $split.FenceState | Should -Be 'None'
        $split.Body | Should -BeExactly "---`n---`nBody"
    }

    It 'does not read a four-dash opener as a fence' {
        (Split-VaultNoteContent -Content "----`na: 1`n----`nBody").FenceState | Should -Be 'None'
    }

    It 'does not read a fence that starts below the first line' {
        (Split-VaultNoteContent -Content "# Heading`n---`na: 1`n---`n").FenceState | Should -Be 'None'
    }

    It 'gives a BodyOffset that splices exactly at the body' {
        $content = "---`na: 1`n---`n# Body"
        $split = Split-VaultNoteContent -Content $content
        $content.Substring(0, $split.BodyOffset) | Should -BeExactly "---`na: 1`n---`n"
    }

    It 'gives a BodyOffset past the closing fence in a CRLF note' {
        $content = "---`r`na: 1`r`n---`r`n# Body"
        $split = Split-VaultNoteContent -Content $content
        $content.Substring(0, $split.BodyOffset) | Should -BeExactly "---`r`na: 1`r`n---`r`n"
        $split.Body | Should -BeExactly '# Body'
    }
}

Describe 'Get-VaultFrontmatterField' {
    It 'reads a flat scalar, quoting resolved by the parser' {
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

    It 'reports Block for a key whose value is a sequence' {
        # The detection field_shape_mismatch depends on. Get-VaultFrontmatterField returns $null
        # here exactly as it does for an absent field, so it cannot tell a block from a miss.
        $content = "---`nnav.contents:`n  - link: `"[[X]]`"`n    description: `"d`"`n---`n# B"
        Get-VaultFrontmatterFieldShape -Content $content -Field 'nav.contents' | Should -Be 'Block'
        Get-VaultFrontmatterField -Content $content -Field 'nav.contents' | Should -BeNullOrEmpty
    }

    It 'reports Scalar for a bare key with no value at all (nothing to orphan)' {
        Get-VaultFrontmatterFieldShape -Content "---`ndoc.type:`n---`n# B" -Field 'doc.type' | Should -Be 'Scalar'
    }
}

Describe 'Set-VaultFrontmatterField' {
    It 'updates an existing field in place, preserving the note key order' {
        Set-VaultFrontmatterField -Content "---`na: 1`ndoc.type: Idea`nb: 2`n---`n# B" -Field 'doc.type' -Value 'Corpus' |
            Should -BeExactly "---`na: 1`ndoc.type: Corpus`nb: 2`n---`n# B"
    }

    It 'updates the existing key rather than minting a second one for a caller''s casing' {
        Set-VaultFrontmatterField -Content "---`ndoc.type: Idea`n---`n# B" -Field 'DOC.Type' -Value 'Corpus' |
            Should -BeExactly "---`ndoc.type: Corpus`n---`n# B"
    }

    It 'appends a new field after the last existing key' {
        Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc.type' -Value 'Corpus' |
            Should -BeExactly "---`na: 1`ndoc.type: Corpus`n---`n# B"
    }

    It 'creates a frontmatter block when the note has none' {
        Set-VaultFrontmatterField -Content '# B' -Field 'doc.type' -Value 'Corpus' |
            Should -BeExactly "---`ndoc.type: Corpus`n---`n# B"
    }

    It 'writes LF throughout a CRLF note, body included' {
        # LF unconditionally, with no detection and nothing preserved. Obsidian writes LF and the
        # vault repo normalizes every text file to it on checkout, so a CRLF note cannot survive a
        # round trip through the vault regardless of what a primitive did with it.
        $result = Set-VaultFrontmatterField -Content "---`r`na: 1`r`n---`r`n# Body`r`nsecond`r`n" -Field 'doc.type' -Value 'Corpus'
        $result | Should -BeExactly "---`na: 1`ndoc.type: Corpus`n---`n# Body`nsecond`n"
        $result | Should -Not -Match "`r"
    }

    It 'refuses to prepend a block to a note whose first line is an unclosed fence' {
        # No longer a fence STATE - the note simply has no frontmatter - but writing a fresh block
        # above the user's apparent metadata would strand it as prose, so the write refuses under
        # its own reason rather than the retired unclosed_fence.
        $err = { Set-VaultFrontmatterField -Content "---`nfoo: 1`n# Body" -Field 'doc.type' -Value 'Corpus' } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'frontmatter_integrity'
        $obj.data.reason | Should -Be 'apparent_fence'
    }

    It 'refuses a field name the registry does not carry' {
        $err = { Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc created' -Value 'x' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'refuses a well-formed key name that is simply not in the registry' {
        # The inversion the registry is for: a name passing any character class is not the test -
        # membership is. obsidianUIMode is Obsidian's own preference, carried by six real notes,
        # and it stays unwritable while those notes stay writable.
        $err = { Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'obsidianUIMode' -Value 'preview' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'refuses a recognized key whose value fails its registry pattern' {
        # The narrower smuggling channel the value patterns close: a caller blocked from inventing
        # a key putting arbitrary text into a legitimate one instead.
        $err = { Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc.created' -Value 'whenever' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'refuses a value carrying a control character, which no escaping makes safe to render' {
        $err = { Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'doc.type' -Value "Corpus`e[31m" } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'writes a line break as a block scalar that reads back as the value given' {
        # The refusal that existed only because a line-splicing writer could not escape. A value is
        # a value in the tree now, so a line break in it cannot become structure. Asserted against
        # `scope`, whose registry pattern is any string: doc.type's own pattern refuses this value
        # a step earlier, which would test the registry rather than the emitter.
        $value = "personal`ndoc.smuggled: yes"
        $result = Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'scope' -Value $value
        Get-VaultFrontmatterField -Content $result -Field 'scope' | Should -BeExactly $value
        Get-VaultFrontmatterField -Content $result -Field 'doc.smuggled' | Should -BeNullOrEmpty
    }

    It 'writes a double quote and reads it back unchanged' {
        $result = Set-VaultFrontmatterField -Content "---`na: 1`n---`n# B" -Field 'scope' -Value 'say "hi"'
        Get-VaultFrontmatterField -Content $result -Field 'scope' | Should -BeExactly 'say "hi"'
    }
}

Describe 'Remove-VaultFrontmatterField' {
    It 'removes a flat scalar and leaves the other keys and the body alone' {
        Remove-VaultFrontmatterField -Content "---`na: 1`ndoc.type: Corpus`nb: 2`n---`n# B" -Field 'doc.type' |
            Should -BeExactly "---`na: 1`nb: 2`n---`n# B"
    }

    It 'is a no-op when the field is absent, returning the caller''s bytes rather than a reserialize' {
        $content = "---`na: 1`n---`n# B"
        Remove-VaultFrontmatterField -Content $content -Field 'doc.type' | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter block' {
        Remove-VaultFrontmatterField -Content '# Body only' -Field 'doc.type' | Should -BeExactly '# Body only'
    }

    It 'removes the last field inside the block when nothing follows the closing fence' {
        # Regression for the skill-side index arithmetic: the removal built a reversed index range
        # whenever the slice ran to the end of the content, corrupting or silently dropping the
        # edit. There is no range to reverse now, and the composed note ends its fence properly.
        Remove-VaultFrontmatterField -Content "---`na: 1`nfoo: 1`n---" -Field 'foo' |
            Should -BeExactly "---`na: 1`n---`n"
    }

    It 'drops the fence entirely when the removal empties the block' {
        Remove-VaultFrontmatterField -Content "---`ndoc.type: Corpus`n---`n# B" -Field 'doc.type' | Should -BeExactly '# B'
    }

    It 'is a no-op against a note whose only fence never closes' {
        $content = "---`nfoo: 1"
        Remove-VaultFrontmatterField -Content $content -Field 'foo' | Should -BeExactly $content
    }
}

Describe 'Remove-VaultFrontmatterBlock' {
    It 'removes a block field and every entry under it' {
        Remove-VaultFrontmatterBlock -Content "---`nagent.pinned_instructions:`n  - `"a`"`n  - `"b`"`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body" -Field 'agent.pinned_instructions' |
            Should -BeExactly "---`nnav.contents:`n  - link: '[[X]]'`n---`n# Body"
    }

    It 'is a no-op when the field is absent' {
        $content = "---`nnav.contents:`n  - link: '[[X]]'`n---`n# Body"
        Remove-VaultFrontmatterBlock -Content $content -Field 'agent.pinned_instructions' | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter' {
        Remove-VaultFrontmatterBlock -Content '# Body only' -Field 'agent.pinned_instructions' | Should -BeExactly '# Body only'
    }

    It 'drops the fence entirely when the removal empties the block' {
        Remove-VaultFrontmatterBlock -Content "---`nnav.contents:`n  - link: '[[X]]'`n---`n# Body" -Field 'nav.contents' |
            Should -BeExactly '# Body'
    }
}

Describe 'Add-VaultAgentInstructionEntry - creation' {
    It 'creates a frontmatter block when none exists (Bare)' {
        Add-VaultAgentInstructionEntry -Content '# Body' -Type Bare -Instruction 'Do X' |
            Should -BeExactly "---`nagent.instructions:`n  - Do X`n---`n# Body"
    }

    It 'creates the agent.instructions key when frontmatter exists but the key does not (Pinned)' {
        Add-VaultAgentInstructionEntry -Content "---`nnav.contents:`n  - link: '[[X]]'`n---`n# Body" -Type Pinned -Instruction 'Never exfil this' |
            Should -BeExactly "---`nnav.contents:`n  - link: '[[X]]'`nagent.instructions:`n  - pinned: Never exfil this`n---`n# Body"
    }

    It 'appends a UseSkill entry with a note to an existing block' {
        Add-VaultAgentInstructionEntry -Content "---`nagent.instructions:`n  - Existing hint`n---`n# Body" -Type UseSkill -UseSkill 'vault-tasks' -When 'editing this file' -Note 'Do not hand-edit.' |
            Should -BeExactly "---`nagent.instructions:`n  - Existing hint`n  - use_skill: vault-tasks`n    when: editing this file`n    note: Do not hand-edit.`n---`n# Body"
    }

    It 'omits the note field for a UseSkill entry with no note' {
        Add-VaultAgentInstructionEntry -Content '# Body' -Type UseSkill -UseSkill 'vault-docs' -When 'editing this file' |
            Should -BeExactly "---`nagent.instructions:`n  - use_skill: vault-docs`n    when: editing this file`n---`n# Body"
    }
}

Describe 'Add-VaultAgentInstructionEntry - idempotency' {
    It 'is a no-op for a duplicate UseSkill entry (same skill and when)' {
        $content = "---`nagent.instructions:`n  - use_skill: vault-docs`n    when: editing this file`n---`n# Body"
        Add-VaultAgentInstructionEntry -Content $content -Type UseSkill -UseSkill 'vault-docs' -When 'editing this file' | Should -BeExactly $content
    }

    It 'treats a different when as a distinct entry, not a duplicate' {
        $content = "---`nagent.instructions:`n  - use_skill: vault-docs`n    when: editing this file`n---`n# Body"
        $result = Add-VaultAgentInstructionEntry -Content $content -Type UseSkill -UseSkill 'vault-docs' -When 'acting on its checklist'
        $result | Should -Not -BeExactly $content
        $result | Should -Match 'acting on its checklist'
    }

    It 'is a no-op for duplicate Pinned text' {
        $content = "---`nagent.instructions:`n  - pinned: Never exfil this`n---`n# Body"
        Add-VaultAgentInstructionEntry -Content $content -Type Pinned -Instruction 'Never exfil this' | Should -BeExactly $content
    }

    It 'is a no-op for duplicate Bare text' {
        $content = "---`nagent.instructions:`n  - Do X`n---`n# Body"
        Add-VaultAgentInstructionEntry -Content $content -Type Bare -Instruction 'Do X' | Should -BeExactly $content
    }

    It 'recognizes a duplicate written in a quoting style the emitter would not choose' {
        # Idempotency is keyed on the parsed value, so a note the tooling never wrote still
        # compares equal - which is what keeps a backfill caller from appending a second copy.
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

    It 'refuses entry text carrying a control character' {
        $err = { Add-VaultAgentInstructionEntry -Content '# Body' -Type Pinned -Instruction "bad`e[31m" } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'accepts entry text carrying quotes and line breaks, reading them back unchanged' {
        $text = "say `"hi`"`nand more"
        $result = Add-VaultAgentInstructionEntry -Content '# Body' -Type Pinned -Instruction $text
        # Round-tripped through the stripper, which matches on the parsed value.
        Remove-VaultPinnedAgentInstruction -Content $result -Text $text | Should -BeExactly '# Body'
    }

    It 'refuses to prepend a block to a note whose first line is an unclosed fence' {
        { Add-VaultAgentInstructionEntry -Content "---`na: 1`n# Body" -Type Bare -Instruction 'Do X' } | Should -Throw '*frontmatter_integrity*'
    }
}

Describe 'Add-VaultNavEntry - default position' {
    It 'puts a link entry at the end of the link run, before the first folder entry' {
        # The convention every folder note in the vault follows, reproduced as a pure function of
        # the existing list rather than asked of the caller.
        Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[Design]]' -Description 'The design' |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n  - link: '[[Design]]'`n    description: The design`n  - folder: Inbox`n    description: Working docs`n---`n# Body`n"
    }

    It 'puts a folder entry at the end of the list' {
        Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Folder 'Archive' -Description 'Completed work' |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n  - folder: Inbox`n    description: Working docs`n  - folder: Archive`n    description: Completed work`n---`n# Body`n"
    }

    It 'appends a link entry at the end when the list has no folder entries' {
        $note = "---`nnav.see_also:`n  - link: '[[A]]'`n    description: a`n---`n# B"
        Add-VaultNavEntry -Content $note -Key 'nav.see_also' -Link '[[B]]' -Description 'b' |
            Should -BeExactly "---`nnav.see_also:`n  - link: '[[A]]'`n    description: a`n  - link: '[[B]]'`n    description: b`n---`n# B"
    }

    It 'emits load_when when supplied and omits the field when not' {
        $with = Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[D]]' -Description 'd' -LoadWhen 'designing'
        $with | Should -Match ([regex]::Escape('    load_when: designing'))
        (Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[D]]' -Description 'd') | Should -Not -Match 'load_when'
    }
}

Describe 'Add-VaultNavEntry - explicit position' {
    It 'inserts before the entry named by -Before' {
        Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[First]]' -Description 'first' -Before '[[Tasks]]' |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[First]]'`n    description: first`n  - link: '[[Tasks]]'`n    description: Task list`n  - folder: Inbox`n    description: Working docs`n---`n# Body`n"
    }

    It 'inserts after the entry named by -After, which may be a folder target' {
        Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[Last]]' -Description 'last' -After 'Inbox' |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n  - folder: Inbox`n    description: Working docs`n  - link: '[[Last]]'`n    description: last`n---`n# Body`n"
    }

    It 'anchors past a whole entry, whatever fields it carries' {
        $note = "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`n    load_when: always`n---`n# B"
        Add-VaultNavEntry -Content $note -Key 'nav.contents' -Link '[[B]]' -Description 'b' -After '[[A]]' |
            Should -BeExactly "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`n    load_when: always`n  - link: '[[B]]'`n    description: b`n---`n# B"
    }

    It 'refuses -Before and -After together' {
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Description 'x' -Before '[[Tasks]]' -After 'Inbox' } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses an anchor that names no existing entry' {
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Description 'x' -Before '[[Nope]]' } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses an anchor that matches more than one entry' {
        $dupe = "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`n  - link: '[[A]]'`n    description: b`n---`n# B"
        { Add-VaultNavEntry -Content $dupe -Key 'nav.contents' -Link '[[X]]' -Description 'x' -After '[[A]]' } | Should -Throw '*malformed_op*'
    }
}

Describe 'Add-VaultNavEntry - key creation' {
    It 'creates the key when the note has frontmatter but not the key' {
        # The folder-note completeness case lint reports: no nav.contents at all.
        Add-VaultNavEntry -Content "---`ndoc.type: Corpus`n---`n# Body`n" -Key 'nav.contents' -Link '[[Tasks]]' -Description 'Task list' |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n---`n# Body`n"
    }

    It 'creates the frontmatter block when the note has none' {
        Add-VaultNavEntry -Content '# Body' -Key 'nav.see_also' -Folder 'Corpus' -Description 'Reference' |
            Should -BeExactly "---`nnav.see_also:`n  - folder: Corpus`n    description: Reference`n---`n# Body"
    }

    It 'refuses an anchor when the key does not exist yet' {
        { Add-VaultNavEntry -Content "---`ndoc.type: Corpus`n---`n# B" -Key 'nav.contents' -Link '[[X]]' -Description 'x' -After '[[Y]]' } |
            Should -Throw '*malformed_op*'
    }

    It 'leaves an unrelated existing nav key alone' {
        $note = "---`nnav.see_also:`n  - link: '[[A]]'`n    description: a`n---`n# B"
        Add-VaultNavEntry -Content $note -Key 'nav.contents' -Link '[[B]]' -Description 'b' |
            Should -BeExactly "---`nnav.see_also:`n  - link: '[[A]]'`n    description: a`nnav.contents:`n  - link: '[[B]]'`n    description: b`n---`n# B"
    }
}

Describe 'Add-VaultNavEntry - idempotency and refusals' {
    It 'is a no-op for a duplicate link target, whatever the description says' {
        Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[Tasks]]' -Description 'a different description' |
            Should -BeExactly $script:NavNote
    }

    It 'is a no-op for a duplicate folder target' {
        Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Folder 'Inbox' -Description 'other' | Should -BeExactly $script:NavNote
    }

    It 'treats the same target under a different key as a distinct entry' {
        (Add-VaultNavEntry -Content $script:NavNote -Key 'nav.see_also' -Link '[[Tasks]]' -Description 'd') | Should -Not -BeExactly $script:NavNote
    }

    It 'refuses a key outside the nav block-list allow-list' {
        { Add-VaultNavEntry -Content $script:NavNote -Key 'agent.instructions' -Link '[[X]]' -Description 'x' } | Should -Throw '*malformed_op*'
    }

    It 'writes the canonical key spelling whatever case the caller used' {
        # The allow-list check is case-insensitive, so without normalization 'Nav.Contents' would
        # pass it and then be written under the caller's own spelling.
        Add-VaultNavEntry -Content "---`ndoc.type: Corpus`n---`n# B" -Key 'NAV.Contents' -Link '[[X]]' -Description 'x' |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[X]]'`n    description: x`n---`n# B"
    }

    It 'refuses both link and folder' {
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Folder 'Inbox' -Description 'x' } | Should -Throw '*malformed_op*'
    }

    It 'refuses neither link nor folder' {
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Description 'x' } | Should -Throw '*malformed_op*'
    }

    It 'refuses a missing description' {
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' } | Should -Throw '*malformed_op*'
    }

    It 'refuses a blank description' {
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Description '   ' } | Should -Throw '*malformed_op*'
    }

    It 'refuses a control character on every caller-text field' {
        $bad = "x`e[31m"
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link $bad -Description 'd' } | Should -Throw '*malformed_op*'
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Folder $bad -Description 'd' } | Should -Throw '*malformed_op*'
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Description $bad } | Should -Throw '*malformed_op*'
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Description 'd' -LoadWhen $bad } | Should -Throw '*malformed_op*'
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Description 'd' -Before $bad } | Should -Throw '*malformed_op*'
        { Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[X]]' -Description 'd' -After $bad } | Should -Throw '*malformed_op*'
    }

    It 'keeps a description carrying a line break inside its own entry' {
        # The op takes fields rather than pre-rendered block text, so the emitter decides how a
        # line break is represented and it cannot become a top-level key however it is written.
        $result = Add-VaultNavEntry -Content $script:NavNote -Key 'nav.see_also' -Link '[[X]]' -Description "d`ndoc.type: smuggled"
        Get-VaultFrontmatterFieldShape -Content $result -Field 'doc.smuggled' | Should -Be 'Absent'
        Get-VaultFrontmatterField -Content $result -Field 'doc.type' | Should -BeExactly 'Corpus'
    }

    It 'refuses to prepend a block to a note whose first line is an unclosed fence' {
        { Add-VaultNavEntry -Content "---`nnav.contents:`n  - link: '[[A]]'" -Key 'nav.contents' -Link '[[B]]' -Description 'b' } |
            Should -Throw '*frontmatter_integrity*'
    }
}

Describe 'Remove-VaultNavEntry' {
    It 'removes the entry whose link matches, keeping the rest of the list in order' {
        Remove-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[Tasks]]' |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - folder: Inbox`n    description: Working docs`n---`n# Body`n"
    }

    It 'removes the entry whose folder matches, including its load_when' {
        $note = "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`n  - folder: Inbox`n    description: i`n    load_when: working`n---`n# B"
        Remove-VaultNavEntry -Content $note -Key 'nav.contents' -Folder 'Inbox' |
            Should -BeExactly "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`n---`n# B"
    }

    It 'drops the whole key when the removal empties the list' {
        $note = "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[A]]'`n    description: a`n---`n# B"
        Remove-VaultNavEntry -Content $note -Key 'nav.contents' -Link '[[A]]' | Should -BeExactly "---`ndoc.type: Corpus`n---`n# B"
    }

    It 'drops the fence too when the key was the note''s last' {
        $note = "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`n---`n# B"
        Remove-VaultNavEntry -Content $note -Key 'nav.contents' -Link '[[A]]' | Should -BeExactly '# B'
    }

    It 'is a no-op when no entry carries the target' {
        Remove-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[Nope]]' | Should -BeExactly $script:NavNote
    }

    It 'is a no-op when the key is absent' {
        $note = "---`ndoc.type: Corpus`n---`n# B"
        Remove-VaultNavEntry -Content $note -Key 'nav.contents' -Link '[[A]]' | Should -BeExactly $note
    }

    It 'is a no-op when there is no frontmatter at all' {
        Remove-VaultNavEntry -Content '# Body only' -Key 'nav.contents' -Folder 'Inbox' | Should -BeExactly '# Body only'
    }

    It 'does not cross keys - a target under nav.see_also is not removed from nav.contents' {
        $note = "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`nnav.see_also:`n  - link: '[[B]]'`n    description: b`n---`n# B"
        Remove-VaultNavEntry -Content $note -Key 'nav.contents' -Link '[[B]]' | Should -BeExactly $note
    }

    It 'refuses a target matching more than one entry rather than guessing' {
        # edit's patch_ambiguous stance: the note is already malformed, and picking one would be a
        # coin flip the caller cannot see.
        $dupe = "---`nnav.contents:`n  - link: '[[A]]'`n    description: a`n  - link: '[[A]]'`n    description: b`n---`n# B"
        $err = { Remove-VaultNavEntry -Content $dupe -Key 'nav.contents' -Link '[[A]]' } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'malformed_op'
    }

    It 'refuses a key outside the nav block-list allow-list' {
        { Remove-VaultNavEntry -Content $script:NavNote -Key 'agent.instructions' -Link '[[X]]' } | Should -Throw '*malformed_op*'
    }

    It 'refuses both link and folder' {
        { Remove-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[Tasks]]' -Folder 'Inbox' } | Should -Throw '*malformed_op*'
    }

    It 'refuses neither link nor folder' {
        { Remove-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' } | Should -Throw '*malformed_op*'
    }

    It 'refuses target text carrying a control character' {
        { Remove-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link "x`e[31m" } | Should -Throw '*malformed_op*'
    }

    It 'is a no-op against a note whose only fence never closes' {
        $content = "---`nnav.contents:`n  - link: '[[A]]'"
        Remove-VaultNavEntry -Content $content -Key 'nav.contents' -Link '[[A]]' | Should -BeExactly $content
    }

    It 'round-trips with the adder: add then remove restores the original bytes' {
        $added = Add-VaultNavEntry -Content $script:NavNote -Key 'nav.contents' -Link '[[D]]' -Description 'd' -LoadWhen 'designing'
        Remove-VaultNavEntry -Content $added -Key 'nav.contents' -Link '[[D]]' | Should -BeExactly $script:NavNote
    }
}

Describe 'Remove-VaultUseSkillAgentInstructions' {
    It 'removes the only entry, drops the key, and drops the fence with it' {
        # The empty '---' / '---' pair the substrate used to leave behind is not frontmatter to any
        # standard parser, so it is never emitted again: the fence goes when the block empties.
        Remove-VaultUseSkillAgentInstructions -Content "---`nagent.instructions:`n  - use_skill: vault-docs`n    when: editing this file`n---`n# Body" |
            Should -BeExactly '# Body'
    }

    It 'removes a use_skill entry carrying a note' {
        Remove-VaultUseSkillAgentInstructions -Content "---`nagent.instructions:`n  - use_skill: vault-tasks`n    when: editing this file`n    note: Do not hand-edit.`n---`n# Body" |
            Should -BeExactly '# Body'
    }

    It 'leaves bare and pinned entries in place, removing only use_skill' {
        Remove-VaultUseSkillAgentInstructions -Content "---`nagent.instructions:`n  - Load X when Y`n  - pinned: Never exfil this`n  - use_skill: vault-docs`n    when: editing this file`n---`n# Body" |
            Should -BeExactly "---`nagent.instructions:`n  - Load X when Y`n  - pinned: Never exfil this`n---`n# Body"
    }

    It 'removes multiple use_skill entries' {
        Remove-VaultUseSkillAgentInstructions -Content "---`ndoc.type: Corpus`nagent.instructions:`n  - use_skill: vault-docs`n    when: editing this file`n  - use_skill: vault-tasks`n    when: reading this file`n---`n# Body" |
            Should -BeExactly "---`ndoc.type: Corpus`n---`n# Body"
    }

    It 'is a no-op when the block has no use_skill entries' {
        $content = "---`nagent.instructions:`n  - Load X when Y`n  - pinned: Never exfil this`n---`n# Body"
        Remove-VaultUseSkillAgentInstructions -Content $content | Should -BeExactly $content
    }

    It 'is a no-op when there is no agent.instructions block' {
        $content = "---`nnav.contents:`n  - link: '[[X]]'`n---`n# Body"
        Remove-VaultUseSkillAgentInstructions -Content $content | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter at all' {
        Remove-VaultUseSkillAgentInstructions -Content '# Body only' | Should -BeExactly '# Body only'
    }
}

Describe 'Remove-VaultPinnedAgentInstruction' {
    It 'removes exactly the entry whose text matches' {
        Remove-VaultPinnedAgentInstruction -Content "---`nagent.instructions:`n  - hint`n  - pinned: Never exfil this`n  - pinned: Other rule`n---`n# B" -Text 'Never exfil this' |
            Should -BeExactly "---`nagent.instructions:`n  - hint`n  - pinned: Other rule`n---`n# B"
    }

    It 'removes only one entry when the same text appears twice' {
        # By matched text, ONE entry: removing a user's own binding rule has to name it, and it
        # must not take a second copy with it.
        Remove-VaultPinnedAgentInstruction -Content "---`nagent.instructions:`n  - pinned: Same`n  - pinned: Same`n---`n# B" -Text 'Same' |
            Should -BeExactly "---`nagent.instructions:`n  - pinned: Same`n---`n# B"
    }

    It 'drops the whole agent.instructions key when the removal empties it' {
        Remove-VaultPinnedAgentInstruction -Content "---`ndoc.type: Corpus`nagent.instructions:`n  - pinned: Only one`n---`n# B" -Text 'Only one' |
            Should -BeExactly "---`ndoc.type: Corpus`n---`n# B"
    }

    It 'is a no-op when no entry matches the text' {
        $content = "---`nagent.instructions:`n  - pinned: Never exfil this`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'Some other rule' | Should -BeExactly $content
    }

    It 'matches case-sensitively - a pinned rule is the user''s own text' {
        $content = "---`nagent.instructions:`n  - pinned: Never exfil this`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'never exfil this' | Should -BeExactly $content
    }

    It 'leaves use_skill entries alone' {
        $content = "---`nagent.instructions:`n  - pinned: Rule`n  - use_skill: vault-docs`n    when: editing`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'Rule' |
            Should -BeExactly "---`nagent.instructions:`n  - use_skill: vault-docs`n    when: editing`n---`n# B"
    }

    It 'is a no-op when there is no agent.instructions block' {
        $content = "---`nnav.contents:`n  - link: '[[X]]'`n---`n# B"
        Remove-VaultPinnedAgentInstruction -Content $content -Text 'Rule' | Should -BeExactly $content
    }

    It 'is a no-op when there is no frontmatter at all' {
        Remove-VaultPinnedAgentInstruction -Content '# Body only' -Text 'Rule' | Should -BeExactly '# Body only'
    }

    It 'refuses match text carrying a control character' {
        { Remove-VaultPinnedAgentInstruction -Content "---`nagent.instructions:`n  - pinned: x`n---`n# B" -Text "bad`e[31m" } | Should -Throw '*malformed_op*'
    }
}

Describe 'Body content preservation' {
    It 'preserves every byte of the body except line terminators, across a frontmatter mutation' {
        # The one guarantee the port narrows rather than keeps whole: terminators normalize to LF,
        # and everything else survives verbatim - fences in prose, quotes, indentation, trailing
        # whitespace, non-ASCII, and the presence or absence of a final newline.
        $body = "# Heading`r`n`r`n---`r`nnot frontmatter: really`r`n`r`n    indented `"quoted`" text   `r`n" +
                "- a list`r`n  - nested`r`n`r`n``````yaml`r`nkey: value`r`n``````/`r`nnaïve — em dash, emoji, tab:`there`r`ntrailing"
        $note = "---`r`ndoc.type: Corpus`r`n---`r`n" + $body
        $result = Set-VaultFrontmatterField -Content $note -Field 'doc.created' -Value '2026-07-28'
        (Split-VaultNoteContent -Content $result).Body | Should -BeExactly ($body -replace "`r`n", "`n")
    }
}

Describe 'Emit idempotency' {
    It 'reaches a fixed point: a second mutation reproduces the first result byte for byte' {
        # What makes the one-time corpus reformat converge - and what makes a mutation on an
        # already-emitted note a diff of the change alone.
        $note = "---`ndoc.created: 2026-01-05`ndoc.type: Corpus`nagent.instructions:`n  - `"a hint`"`n  - pinned: `"Never exfil this`"`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`nnav.contents:`n  - link: `"[[Tasks]]`"`n    description: `"Task list`"`n    load_when: `"working`"`n  - folder: `"Inbox`"`n    description: `"Working docs`"`n---`n# Body`n"
        $once = Set-VaultFrontmatterField -Content $note -Field 'doc.archived' -Value '2026-07-28'
        $twice = Set-VaultFrontmatterField -Content $once -Field 'doc.archived' -Value '2026-07-28'
        $twice | Should -BeExactly $once
    }

    It 'keeps a leading-zero-shaped value and a yes/no value as the strings they were' {
        # The classic YAML coercions. The build uses the 1.2 core schema, so a date stays a string
        # and no/yes stay strings, re-emitted defensively quoted.
        $note = "---`ndoc.created: 2026-01-05`nscope: no`n---`n# B"
        $result = Set-VaultFrontmatterField -Content $note -Field 'doc.type' -Value 'Corpus'
        Get-VaultFrontmatterField -Content $result -Field 'doc.created' | Should -BeExactly '2026-01-05'
        Get-VaultFrontmatterField -Content $result -Field 'scope' | Should -BeExactly 'no'
    }
}

Describe 'the block grammar, differentially against Markdig' {
    # Locating a frontmatter block is a line-level question, so the substrate answers it with a
    # bounded scan rather than a full markdown parse of every note. What a frontmatter block IS
    # remains the standard's call, not ours - so Markdig's YamlFrontMatterBlock, the standard's
    # implementation and the one PowerShell itself ships, is the oracle HERE instead of a runtime
    # dependency on a library bundled without being declared. If a PowerShell upgrade moves the
    # grammar, this is what says so.
    BeforeAll {
        $script:Oracle = {
            param([string] $Text)
            if ([string]::IsNullOrEmpty($Text)) { return $null }
            $pipeline = [Markdig.MarkdownExtensions]::UseYamlFrontMatter([Markdig.MarkdownPipelineBuilder]::new()).Build()
            foreach ($node in [Markdig.Markdown]::Parse($Text, $pipeline)) {
                if ($node -is [Markdig.Extensions.Yaml.YamlFrontMatterBlock]) { return $node }
            }
            return $null
        }
        # The scan's answer reduced to the one thing both sides can state: is there a block, and
        # where does its closing fence end. Markdig's span stops at the fence's last character and
        # excludes trailing whitespace, so the scan's BodyOffset is walked back over any padding
        # before the two are compared.
        $script:ScanEnd = {
            param([string] $Text)
            $span = InModuleScope VaultAccess { param($t) Get-VaultFrontmatterSpan -Content $t } -ArgumentList $Text
            if ($null -eq $span) { return $null }
            $end = $span.BodyOffset - 1
            while ($end -ge 0 -and $Text[$end] -in @("`n", "`r", ' ', "`t")) { $end-- }
            return $end
        }
    }

    $cases = @(
        @{ Name = 'basic'; Text = "---`na: 1`n---`nBody`n" }
        @{ Name = 'no trailing newline'; Text = "---`na: 1`n---" }
        @{ Name = 'four-dash opener'; Text = "----`na: 1`n---`nBody`n" }
        @{ Name = 'four-dash closer'; Text = "---`na: 1`n----`nBody`n" }
        @{ Name = 'opener trailing space'; Text = "--- `na: 1`n---`nBody`n" }
        @{ Name = 'closer trailing space'; Text = "---`na: 1`n--- `nBody`n" }
        @{ Name = 'closer trailing tab'; Text = "---`na: 1`n---`t`nBody`n" }
        @{ Name = 'opener leading space'; Text = " ---`na: 1`n---`nBody`n" }
        @{ Name = 'closer one-space indent'; Text = "---`na: 1`n ---`nBody`n" }
        @{ Name = 'closer three-space indent'; Text = "---`na: 1`n   ---`nBody`n" }
        @{ Name = 'closer four-space indent'; Text = "---`na: 1`n    ---`nBody`n" }
        @{ Name = 'dots closer'; Text = "---`na: 1`n...`nBody`n" }
        @{ Name = 'four-dot closer'; Text = "---`na: 1`n....`nBody`n" }
        @{ Name = 'unclosed fence'; Text = "---`na: 1`nBody`n" }
        @{ Name = 'blank lines inside'; Text = "---`n`na: 1`n`n---`nBody`n" }
        @{ Name = 'CRLF throughout'; Text = "---`r`na: 1`r`n---`r`nBody`r`n" }
        @{ Name = 'fence not on the first line'; Text = "`n---`na: 1`n---`nBody`n" }
        @{ Name = 'heading before the fence'; Text = "# H`n---`na: 1`n---`nBody`n" }
        @{ Name = 'fence alone'; Text = '---' }
        @{ Name = 'fence alone with terminator'; Text = "---`n" }
        @{ Name = 'empty content'; Text = '' }
        @{ Name = 'two blocks back to back'; Text = "---`na: 1`n---`n---`nb: 2`n---`nBody`n" }
        @{ Name = 'a --- line inside a block scalar'; Text = "---`na: |`n  ---`n---`nBody`n" }
    )

    It 'agrees with Markdig on <Name>' -TestCases $cases {
        param($Name, $Text)
        $block = & $script:Oracle $Text
        $scanEnd = & $script:ScanEnd $Text
        if ($null -eq $block) { $scanEnd | Should -Be $null }
        else {
            $scanEnd | Should -Not -Be $null
            $scanEnd | Should -Be $block.Span.End
        }
    }

    It 'departs from Markdig only on an empty fence pair, and only because Markdig is inconsistent there' {
        # Markdig reads '---' / '---' as two thematic breaks under LF and as a real block under
        # CRLF. The vault is LF and [[Plan-Frontmatter-YAML-Migration]] locks the LF reading, so
        # the scan applies it under both terminators rather than inheriting the split.
        (& $script:ScanEnd "---`n---`nBody`n") | Should -Be $null
        (& $script:ScanEnd "---`r`n---`r`nBody`r`n") | Should -Be $null
        (& $script:Oracle "---`n---`nBody`n") | Should -Be $null
        (& $script:Oracle "---`r`n---`r`nBody`r`n") | Should -Not -Be $null
    }

    It 'agrees with Markdig on every note in the repo fixture vault' {
        # A table of shapes proves the rules; a real corpus proves the rules are the ones the
        # vault's notes actually exercise.
        $notes = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../../../test-vault') -Recurse -Filter '*.md' -File)
        $notes.Count | Should -BeGreaterThan 0
        foreach ($note in $notes) {
            $text = [System.IO.File]::ReadAllText($note.FullName)
            $block = & $script:Oracle $text
            $expected = if ($null -eq $block) { $null } else { $block.Span.End }
            & $script:ScanEnd $text | Should -Be $expected -Because "of $($note.Name)"
        }
    }
}
