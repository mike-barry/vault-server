# Tests for VaultAccess Public/FrontmatterWrite.ps1 - the validate-and-classify wrapper and the
# agentic outcome diff.
#
# The capability blocks drive the gate through the host-injected $Global:VaultMcpOpClasses, the
# same ambient variable Scope.Tests.ps1 uses, and every one of them clears it in AfterEach so the
# injected authority can never leak into another It, Describe, or file.
#
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force

    # -RelativePath is mandatory, so every call supplies one. This is the path for the cases where
    # the path itself is not what is under test: an ordinary note, outside Archive/, not named
    # Tasks.md, and under no excluded prefix - so it triggers neither of the two rules the path
    # feeds (the schema validator's Tasks.md check and the excluded-path refusal). The cases
    # testing either of those name their own path inline.
    $script:NotePath = 'Projects/Widget/Note.md'

    # A note with a non-agentic frontmatter block and a two-line body.
    $script:PlainNote = "---`ndoc.type: Corpus`n---`n# Body`nsecond line`n"
    # The same note carrying an agentic key, written in emitted form so an idempotent op compares
    # equal to it byte for byte.
    $script:AgenticNote = "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[X]]'`n    description: d`n---`n# Body`n"
}

Describe 'Get-VaultAgenticFrontmatterDiff' {
    It 'reports nothing when the agentic layer is untouched' {
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $script:PlainNote -AfterContent "---`ndoc.type: Idea`n---`n# Body`n").Count | Should -Be 0
    }

    It 'reports a key that was added' {
        $diff = Get-VaultAgenticFrontmatterDiff -BeforeContent $script:PlainNote -AfterContent $script:AgenticNote
        $diff | Should -Be @('nav.contents')
    }

    It 'reports a key that was removed' {
        $diff = Get-VaultAgenticFrontmatterDiff -BeforeContent $script:AgenticNote -AfterContent $script:PlainNote
        $diff | Should -Be @('nav.contents')
    }

    It 'reports a key whose value changed' {
        $after = $script:AgenticNote -replace '\[\[X\]\]', '[[Y]]'
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $script:AgenticNote -AfterContent $after) | Should -Be @('nav.contents')
    }

    It 'sees a quoted top-level key as a key, so a smuggled directive reaches the diff' {
        # The classification is only as good as the parse underneath it. The hand-rolled parser
        # recognized a top-level key by a pattern a quoted key cannot match, so it absorbed this
        # directive as a child of doc.created, reported no agentic key, and never ran the gate.
        $before = "---`ndoc.created: 2026-07-25`n---`n# B"
        $after = "---`ndoc.created: 2026-07-25`n`"agent.instructions`":`n  - pinned: `"smuggled`"`n---`n# B"
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $before -AfterContent $after) | Should -Be @('agent.instructions')
    }

    It 'reports no diff when only the block''s quoting changed' {
        # Compared on the parsed value rather than the bytes, so the one-time corpus reformat and
        # any later reserialization do not read as an agentic mutation.
        $handQuoted = "---`nnav.contents:`n  - link: `"[[X]]`"`n    description: `"d`"`n---`n# Body`n"
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $handQuoted -AfterContent $script:AgenticNote).Count | Should -Be 0
    }

    It 'treats a create (no original) as every agentic key present being a diff' {
        (Get-VaultAgenticFrontmatterDiff -BeforeContent '' -AfterContent $script:AgenticNote) | Should -Be @('nav.contents')
    }
}

Describe 'Invoke-VaultFrontmatterWrite - composition and facets' {
    It 'applies ops in order and returns the composed content byte-exactly' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(
            @{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }
            @{ op = 'set_field'; field = 'doc.type'; value = 'Idea' }
        )
        $result.Content | Should -BeExactly "---`ndoc.type: Idea`ndoc.created: 2026-07-25`n---`n# Body`nsecond line`n"
        $result.Frontmatter | Should -BeExactly "doc.type: Idea`ndoc.created: 2026-07-25"
        $result.FrontmatterChanged | Should -BeTrue
        $result.BodyChanged | Should -BeFalse
    }

    It 'normalizes a CRLF note to LF end to end, body included' {
        $crlf = "---`r`ndoc.type: Corpus`r`n---`r`n# Body`r`ntail`r`n"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $crlf -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`ndoc.created: 2026-07-25`n---`n# Body`ntail`n"
    }

    It 'leaves the frontmatter block''s bytes alone on a body-only write' {
        # Reserialize only when a frontmatter op actually ran. Otherwise a body edit would rewrite
        # quoting the caller never touched and report a changed hash for a note whose meaningful
        # content did not change.
        $handQuoted = "---`ndoc.type: `"Corpus`"`n---`n# Body`n"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $handQuoted -Body "# Replaced`n"
        $result.Content | Should -BeExactly "---`ndoc.type: `"Corpus`"`n---`n# Replaced`n"
        $result.Frontmatter | Should -BeExactly 'doc.type: "Corpus"'
        $result.FrontmatterChanged | Should -BeFalse
    }

    It 'leaves the frontmatter block''s bytes alone when every op was an idempotent no-op' {
        $handQuoted = "---`nagent.instructions:`n  - pinned: `"Same`"`n---`n# Body`n"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $handQuoted -Ops @(@{ op = 'add_directive'; kind = 'pinned'; text = 'Same' })
        $result.Content | Should -BeExactly $handQuoted
        $result.FrontmatterChanged | Should -BeFalse
    }

    It 'records applied and changed per op, with changed false on an idempotent no-op' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(
            @{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }
            @{ op = 'remove_field'; field = 'doc.archived' }
        )
        $result.OpsApplied.Count | Should -Be 2
        $result.OpsApplied[0].Op | Should -Be 'set_field'
        $result.OpsApplied[0].Field | Should -Be 'doc.created'
        $result.OpsApplied[0].Applied | Should -BeTrue
        $result.OpsApplied[0].Changed | Should -BeTrue
        $result.OpsApplied[1].Changed | Should -BeFalse
    }

    It 'returns @() for OpsApplied when there are no ops' {
        (Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @() -Body "# New`n").OpsApplied.Count | Should -Be 0
    }

    It 'replaces the body facet and leaves the frontmatter block untouched' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Body "# Replaced`n"
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n# Replaced`n"
        $result.FrontmatterChanged | Should -BeFalse
        $result.BodyChanged | Should -BeTrue
    }

    It 'truncates the body on an empty -Body and leaves the block intact' {
        (Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Body '').Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n"
    }

    It 'leaves the body untouched when -Body is not bound at all' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.Body | Should -BeExactly "# Body`nsecond line`n"
        $result.BodyChanged | Should -BeFalse
    }

    It 'routes remove_field to block removal by detected shape' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:AgenticNote -Ops @(@{ op = 'remove_field'; field = 'nav.contents' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n# Body`n"
    }
}

Describe 'Invoke-VaultFrontmatterWrite - schema violations are returned, never thrown' {
    It 'returns the violation list and still composes the content' {
        # The design turn lint depends on: a hard throw here would refuse lint''s provenance date
        # fix on exactly the notes that already have agent.* problems.
        $broken = "---`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body`n"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $broken -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.Violations.Count | Should -Be 1
        $result.Violations[0].Rule | Should -Be 'missing_description'
        $result.Content | Should -Match 'doc.created: 2026-07-25'
    }

    It 'returns an empty violation list for a clean note' {
        (Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })).Violations.Count | Should -Be 0
    }

    It 'runs the path-dependent Tasks.md check when RelativePath is supplied' {
        $result = Invoke-VaultFrontmatterWrite -Content "# Tasks`n" -Ops @() -Body "# Tasks`n" -RelativePath 'Projects/Widget/Tasks.md'
        $result.Violations[0].Rule | Should -Be 'tasks_missing_use_skill'
    }
}

Describe 'Invoke-VaultFrontmatterWrite - the fence-introduction guard' {
    It 'refuses a body write that mints a frontmatter block on a fence-less note' {
        # Without this the body replace would create a block no op authored, bypassing schema
        # validation and the agentic key assertion entirely.
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content "# Plain note`n" -Body "---`nnav.contents: smuggled`n---`n# Body`n" } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'frontmatter_integrity'
    }

    It 'allows a body write that opens a fence it never closes - that is prose, not a block' {
        # No block is minted, because to the standard an unterminated '---' is a thematic break
        # followed by a paragraph. The guard fires on a real block, not on a lone fence line.
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content "# Plain note`n" -Body "---`nnav.contents: smuggled`n"
        $result.FrontmatterChanged | Should -BeFalse
        $result.AgenticKeysChanged.Count | Should -Be 0
    }

    It 'refuses a frontmatter op against a note whose first line is an unclosed fence' {
        # Such a note has no frontmatter to the parser, so the refusal has moved from a fence STATE
        # to the write that would strand the user's apparent metadata under a fresh block.
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content "---`ndoc.type: Corpus`n# Body" -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'frontmatter_integrity'
        $obj.data.reason | Should -Be 'apparent_fence'
    }

    It 'refuses a supplied frontmatter block that never closes' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Frontmatter "---`ndoc.type: Corpus`n" } | Should -Throw '*frontmatter_integrity*'
    }

    It 'refuses a supplied frontmatter block carrying content past its closing fence' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Frontmatter "---`ndoc.type: Corpus`n---`n# smuggled body`n" } | Should -Throw '*frontmatter_integrity*'
    }

    It 'allows a body write that merely contains --- below the first line' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content "# Plain`n" -Body "# Heading`n---`nnot frontmatter`n"
        $result.Content | Should -BeExactly "# Heading`n---`nnot frontmatter`n"
        $result.FrontmatterChanged | Should -BeFalse
    }
}

Describe 'Invoke-VaultFrontmatterWrite - scalar hygiene' {
    It 'refuses a field name outside the key pattern with malformed_op and the op index' {
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(
                @{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }
                @{ op = 'set_field'; field = 'doc created'; value = 'x' }
            ) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.opIndex | Should -Be 1
    }

    It 'refuses a value carrying a control character' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.type'; value = "Corpus`e[31m" }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses directive text carrying a control character, which the spec''s field-only hygiene would have missed' {
        # The gate binds every op that carries caller text, not just the field ops: an
        # agent.instructions directive is the most agent-facing text in the vault.
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'add_directive'; kind = 'pinned'; text = "bad`e[31m" }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses hint text carrying a control character' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'add_hint'; text = "a`e[31m" }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses strip_directive text carrying a control character' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'strip_directive'; kind = 'pinned'; text = "bad`e[31m" }) } |
            Should -Throw '*malformed_op*'
    }

    It 'accepts a value carrying a line break and a quote, keeping it inside its own key' {
        # The two refusals that existed only because a line-splicing writer could not escape. Emit
        # goes through the tree now, so a value cannot become structure however it is written.
        # Against `scope`, whose registry pattern is any string - on a patterned key the registry
        # refuses this a step earlier, which is the test below rather than this one.
        $value = "personal `"quoted`"`nnav.contents: smuggled"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'scope'; value = $value })
        Get-VaultFrontmatterField -Content $result.Content -Field 'scope' | Should -BeExactly $value
        $result.AgenticKeysChanged.Count | Should -Be 0
    }

    It 'refuses a set_field value that fails its key''s registry pattern' {
        # Values are enforced, not just key names: a caller blocked from inventing a key must not
        # be able to put arbitrary text into a legitimate one instead.
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.type'; value = "Note`nnav.contents: smuggled" }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.opIndex | Should -Be 0
    }

    It 'refuses a set_field against a key the registry does not carry' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'obsidianUIMode'; value = 'source' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses a remove_field against a key the registry does not carry' {
        # A removal is a write. The pass-through guarantee is that a foreign key SURVIVES an edit,
        # not that a tool may delete what it cannot author.
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content "---`nobsidianUIMode: preview`ndoc.type: Corpus`n---`n# B`n" -Ops @(@{ op = 'remove_field'; field = 'obsidianUIMode' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'passes an unrecognized key already in the note through untouched' {
        # The extent split, measured: edit validates the keys an op TOUCHES, so the six real
        # Tasks/ notes carrying Obsidian's obsidianUIMode stay writable and keep the key. This is
        # the outcome that would be lost by "tightening" edit to whole-block validation.
        $note = "---`nobsidianUIMode: preview`ndoc.type: Corpus`n---`n# B`n"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $note -Ops @(@{ op = 'set_field'; field = 'doc.type'; value = 'Note' })
        Get-VaultFrontmatterField -Content $result.Content -Field 'obsidianUIMode' | Should -BeExactly 'preview'
        Get-VaultFrontmatterField -Content $result.Content -Field 'doc.type' | Should -BeExactly 'Note'
        $result.Violations.Count | Should -Be 0
    }

    It 'refuses an unknown op' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'reticulate_splines' }) } | Should -Throw '*malformed_op*'
    }

    It 'refuses an op missing a required parameter' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.type' }) } | Should -Throw '*malformed_op*'
    }
}

Describe 'Invoke-VaultFrontmatterWrite - protected targets and shape routing' {
    It 'refuses a generic field op against agent.instructions, naming the routing fix' {
        $note = "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B"
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $note -Ops @(@{ op = 'remove_field'; field = 'agent.instructions' }) } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'protected_directive_violation'
    }

    It 'takes protected_directive_violation over field_shape_mismatch when both would fire' {
        # Precedence: naming the right op is more actionable than naming the shape.
        $note = "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B"
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $note -Ops @(@{ op = 'set_field'; field = 'agent.instructions'; value = 'x' }) } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'protected_directive_violation'
    }

    It 'refuses a flat-scalar set against a detected block field with field_shape_mismatch' {
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:AgenticNote -Ops @(@{ op = 'set_field'; field = 'nav.contents'; value = 'x' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'field_shape_mismatch'
        $obj.data.field | Should -Be 'nav.contents'
    }

    It 'strips a named pinned entry through strip_directive, the explicit path' {
        $note = "---`nagent.instructions:`n  - pinned: `"Keep`"`n  - pinned: `"Drop`"`n---`n# B"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $note -Ops @(@{ op = 'strip_directive'; kind = 'pinned'; text = 'Drop' })
        $result.Content | Should -BeExactly "---`nagent.instructions:`n  - pinned: Keep`n---`n# B"
    }
}

Describe 'Invoke-VaultFrontmatterWrite - the nav entry ops' {
    BeforeAll {
        $script:FolderNote = "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n  - folder: Inbox`n    description: Working docs`n---`n# Body`n"
    }

    AfterEach {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
    }

    It 'adds a link entry at the end of the link run and reports the nav key as the op field' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Design]]'; description = 'The design' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n  - link: '[[Design]]'`n    description: The design`n  - folder: Inbox`n    description: Working docs`n---`n# Body`n"
        $result.OpsApplied[0].Op | Should -Be 'add_nav_entry'
        $result.OpsApplied[0].Field | Should -Be 'nav.contents'
        $result.OpsApplied[0].Changed | Should -BeTrue
        $result.Violations.Count | Should -Be 0
    }

    It 'adds a folder entry at the end of the list, with load_when' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = 'Archive'; description = 'Completed work'; load_when = 'referencing past work' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n  - folder: Inbox`n    description: Working docs`n  - folder: Archive`n    description: Completed work`n    load_when: referencing past work`n---`n# Body`n"
        $result.Violations.Count | Should -Be 0
    }

    It 'honours before and after against an existing entry target' {
        $before = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[First]]'; description = 'first'; before = '[[Tasks]]' })
        $before.Content | Should -Match ([regex]::Escape("nav.contents:`n  - link: '[[First]]'"))
        $after = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Last]]'; description = 'last'; after = 'Inbox' })
        $after.Content | Should -Match ([regex]::Escape("    description: Working docs`n  - link: '[[Last]]'"))
    }

    It 'creates the key when the note carries none - lint''s folder-note completeness case' {
        # The gap these ops close: set_field against a block key is field_shape_mismatch by
        # design, and a body patch is body-confined, so before this there was no way to apply it.
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Tasks]]'; description = 'Task list' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[Tasks]]'`n    description: Task list`n---`n# Body`nsecond line`n"
        $result.Violations.Count | Should -Be 0
    }

    It 'removes an entry addressed by its target and drops the key when that empties the list' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:AgenticNote -Ops @(
            @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = '[[X]]' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n# Body`n"
        $result.OpsApplied[0].Field | Should -Be 'nav.contents'
        $result.OpsApplied[0].Changed | Should -BeTrue
    }

    It 'honours order and repetition: each op sees the previous op''s result' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[A]]'; description = 'a' }
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[B]]'; description = 'b'; after = '[[A]]' }
            @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = '[[Tasks]]' }
        )
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[A]]'`n    description: a`n  - link: '[[B]]'`n    description: b`n  - folder: Inbox`n    description: Working docs`n---`n# Body`n"
        $result.OpsApplied.Count | Should -Be 3
    }

    It 'is idempotent on a duplicate add: changed false, nothing mutated, nothing asserted' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Tasks]]'; description = 'a different description' })
        $result.Content | Should -BeExactly $script:FolderNote
        $result.OpsApplied[0].Changed | Should -BeFalse
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'is idempotent on an absent-target removal: changed false, nothing asserted' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'remove_nav_entry'; key = 'nav.contents'; folder = 'Nowhere' })
        $result.Content | Should -BeExactly $script:FolderNote
        $result.OpsApplied[0].Changed | Should -BeFalse
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'refuses an ambiguous removal with malformed_op and the op index, rather than guessing' {
        $dupe = "---`nnav.contents:`n  - link: `"[[A]]`"`n    description: `"a`"`n  - link: `"[[A]]`"`n    description: `"b`"`n---`n# B"
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $dupe -Ops @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = 'Inbox'; description = 'i' }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = '[[A]]' }
            ) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.opIndex | Should -Be 1
    }

    It 'refuses a key outside the nav block-list allow-list, with the op index' {
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
                @{ op = 'add_nav_entry'; key = 'agent.instructions'; link = '[[X]]'; description = 'x' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.opIndex | Should -Be 0
    }

    It 'reports and writes the canonical key spelling whatever case the op used' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'Nav.See_Also'; link = '[[Y]]'; description = 'y' })
        $result.Content | Should -Match ([regex]::Escape('nav.see_also:'))
        $result.OpsApplied[0].Field | Should -Be 'nav.see_also'
    }

    It 'refuses link and folder together, and neither, in both ops' {
        foreach ($op in @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; folder = 'Inbox'; description = 'x' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; description = 'x' }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = '[[X]]'; folder = 'Inbox' }
                @{ op = 'remove_nav_entry'; key = 'nav.contents' }
            )) {
            { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @($op) } | Should -Throw '*malformed_op*'
        }
    }

    It 'refuses a missing or blank description' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(@{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]' }) } | Should -Throw '*malformed_op*'
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(@{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = '  ' }) } | Should -Throw '*malformed_op*'
    }

    It 'refuses before and after together' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; before = '[[Tasks]]'; after = 'Inbox' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses an anchor naming no existing entry' {
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; before = '[[Nope]]' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses a control character in any caller-text field' {
        foreach ($op in @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = "[[X]]`e[31m"; description = 'x' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = "Inbox`e[31m"; description = 'x' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = "x`e[31m" }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; load_when = "w`e[31m" }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; before = "[[Tasks]]`e[31m" }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; after = "Inbox`e[31m" }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = "[[X]]`e[31m" }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; folder = "Inbox`e[31m" }
            )) {
            { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @($op) } | Should -Throw '*malformed_op*'
        }
    }

    It 'keeps a line break in a caller-text field inside its own entry - the op takes fields, not block text' {
        # The reason the op is structured rather than taking pre-rendered block text: each field is
        # a value in the tree the emitter escapes, so no field can be shaped into a top-level key.
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.see_also'; link = '[[X]]'; description = "x`ndoc.type: smuggled" })
        Get-VaultFrontmatterField -Content $result.Content -Field 'doc.type' | Should -BeExactly 'Corpus'
        $result.AgenticKeysChanged | Should -Be @('nav.see_also')
    }

    It 'accepts an ops array deserialized from JSON, not just hashtables' {
        # edit hands the wrapper PSCustomObjects from ConvertFrom-Json, so the op reader has to
        # take both shapes - including the snake_case load_when member.
        $ops = ConvertFrom-Json -NoEnumerate -InputObject '[{"op":"add_nav_entry","key":"nav.see_also","link":"[[Y]]","description":"why","load_when":"asked about Y"}]'
        (Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops $ops).Content |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.see_also:`n  - link: '[[Y]]'`n    description: why`n    load_when: asked about Y`n---`n# Body`nsecond line`n"
    }

    It 'writes a mixed-ending note out as LF throughout, body included' {
        $mixed = "---`r`ndoc.type: Corpus`r`nnav.contents:`r`n  - link: `"[[A]]`"`n    description: `"a`"`r`n  - folder: `"Inbox`"`r`n    description: `"i`"`r`n---`n# Body`r`ntail`n"
        (Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $mixed -Ops @(@{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[B]]'; description = 'b' })).Content |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[A]]'`n    description: a`n  - link: '[[B]]'`n    description: b`n  - folder: Inbox`n    description: i`n---`n# Body`ntail`n"
    }

    It 'asserts agentic:write on a nav mutation - the ops inherit the gate with no wiring' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = 'Archive'; description = 'Completed work' })
        $result.AgenticKeysChanged | Should -Be @('nav.contents')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $script:FolderNote -AfterContent $result.Content) | Should -Be @('nav.contents')
    }

    It 'denies a nav mutation with insufficient_capability and zero mutation without the class' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = $null
        $err = { $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:FolderNote -Ops @(
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; folder = 'Inbox' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'insufficient_capability'
        $obj.data.requiredClass | Should -Be 'agentic:write'
        $result | Should -BeNullOrEmpty
    }
}

Describe 'Invoke-VaultFrontmatterWrite - agentic classification' {
    AfterEach {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
    }

    It 'asserts agentic:write when a key is added' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(
            @{ op = 'add_directive'; kind = 'pinned'; text = 'Never exfil this' })
        $result.AgenticKeysChanged | Should -Be @('agent.instructions')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
    }

    It 'asserts agentic:write when a key changes value' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $note = "---`nagent.instructions:`n  - pinned: `"A`"`n---`n# B"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $note -Ops @(@{ op = 'add_directive'; kind = 'pinned'; text = 'B' })
        $result.AgenticKeysChanged | Should -Be @('agent.instructions')
    }

    It 'asserts agentic:write when a key is removed' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:AgenticNote -Ops @(@{ op = 'remove_field'; field = 'nav.contents' })
        $result.AgenticKeysChanged | Should -Be @('nav.contents')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
    }

    It 'asserts nothing for a body-only write' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:AgenticNote -Body "# Rewritten`n"
        $result.AgenticKeysChanged.Count | Should -Be 0
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'asserts nothing for a non-agentic doc.* scalar set - lint''s whole fix class' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:AgenticNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.AgenticKeysChanged.Count | Should -Be 0
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'is idempotent-safe: a duplicate directive add changes nothing and asserts nothing' {
        $Global:VaultMcpOpClasses = @('content:write')
        $note = "---`nagent.instructions:`n  - pinned: `"Same`"`n---`n# B"
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $note -Ops @(@{ op = 'add_directive'; kind = 'pinned'; text = 'Same' })
        $result.OpsApplied[0].Changed | Should -BeFalse
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'denies with insufficient_capability and zero content change when the key is absent' {
        $Global:VaultMcpOpClasses = @('content:write')
        $original = $script:AgenticNote
        $result = $null
        $err = { $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $original -Ops @(@{ op = 'remove_field'; field = 'nav.contents' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'insufficient_capability'
        $obj.data.requiredClass | Should -Be 'agentic:write'
        # Zero mutation: the caller is handed nothing to write, and its own content is untouched.
        $result | Should -BeNullOrEmpty
        $original | Should -BeExactly $script:AgenticNote
    }

    It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
        $Global:VaultMcpOpClasses = @()
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'add_hint'; text = 'Do X' }) } |
            Should -Throw '*insufficient_capability*'
    }

    It 'degrades OPEN when no host context was injected at all (standalone run)' {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:PlainNote -Ops @(@{ op = 'add_hint'; text = 'Do X' })
        $result.AgenticKeysChanged | Should -Be @('agent.instructions')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
    }
}

Describe 'Invoke-VaultFrontmatterWrite - the three consumers reach one path' {
    AfterEach {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
    }

    It 'edit: content plus ops plus a body resolved from the body facet' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        # edit resolves its own old_string/new_string against Split-VaultNoteContent's Body first
        # and hands the wrapper the resulting body, so the fence guard still sees what a patch did.
        $split = Split-VaultNoteContent -Content $script:AgenticNote
        $patchedBody = $split.Body.Replace('# Body', '# Patched')
        $result = Invoke-VaultFrontmatterWrite -Content $script:AgenticNote -RelativePath 'Projects/Widget/Widget.md' `
            -Ops @(@{ op = 'add_directive'; kind = 'use_skill'; skill = 'vault-docs'; when = 'editing this file' }) `
            -Body $patchedBody
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: '[[X]]'`n    description: d`nagent.instructions:`n  - use_skill: vault-docs`n    when: editing this file`n---`n# Patched`n"
        $result.FrontmatterChanged | Should -BeTrue
        $result.BodyChanged | Should -BeTrue
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
        $result.Violations.Count | Should -Be 0
    }

    It 'create: no original content, a whole supplied block, and a body' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $result = Invoke-VaultFrontmatterWrite -Content '' -RelativePath 'Projects/Widget/New-Note.md' `
            -Frontmatter "---`ndoc.created: 2026-07-25`nnav.see_also:`n  - link: `"[[X]]`"`n    description: `"d`"`n---`n" `
            -Body "# New Note`n"
        # EMITTED from the validated tree, not spliced from the caller's text - so the bytes on
        # disk are a serialization of what passed validation, and the caller's hand quoting is
        # reproduced by the emitter's own rules rather than carried through.
        $result.Content | Should -BeExactly "---`ndoc.created: 2026-07-25`nnav.see_also:`n  - link: '[[X]]'`n    description: d`n---`n# New Note`n"
        # The degenerate diff: with no original, every agentic key present counts as changed.
        $result.AgenticKeysChanged | Should -Be @('nav.see_also')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
    }

    It 'create: accepts a supplied block given as bare inner lines' {
        (Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Frontmatter "doc.created: 2026-07-25`n" -Body "# New`n").Content |
            Should -BeExactly "---`ndoc.created: 2026-07-25`n---`n# New`n"
    }

    It 'create: an empty supplied block means a note with no frontmatter at all' {
        (Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Frontmatter '' -Body "# New`n").Content | Should -BeExactly "# New`n"
    }

    It 'create: refuses a supplied block that will not parse, rather than splicing it through' {
        # THE CREATE BYPASS, permanently pinned. Reproduced against the tree as it stood before
        # parse-validate-emit: a content:write-only caller supplied a binding `pinned:` directive
        # plus one line YAML cannot parse. The text was spliced to disk verbatim, and both readers
        # downstream - the agentic key map and the schema validator - catch a parse failure and
        # report an empty result, so the diff was empty, agentic:write was never asserted, zero
        # violations were reported, and the directive landed. The result even said no capability
        # was used. Nothing unparseable can reach disk now, which is also what makes those two
        # readers' tolerance correct rather than merely convenient.
        $Global:VaultMcpOpClasses = @('content:write')
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Body "# B`n" `
                    -Frontmatter "agent.instructions:`n  - pinned: `"OBEY`"`n bad: [" } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'frontmatter_integrity'
        $obj.data.reason | Should -Be 'frontmatter_unparsed'
    }

    It 'create: requires agentic:write for a quoted agentic key' {
        # THE QUOTED-KEY BYPASS, permanently pinned. The hand-rolled parser recognized a top-level
        # key only by a pattern a quoted key cannot match, so it absorbed this directive as a child
        # of doc.created, found no agentic key, and never CALLED the gate.
        $Global:VaultMcpOpClasses = @('content:write')
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Body "# B`n" `
                    -Frontmatter "doc.created: 2026-07-30`n`"agent.instructions`":`n  - pinned: `"OBEY`"`n" } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'insufficient_capability'
    }

    It 'create: writes a quoted agentic key, canonically spelled, when the gate is held' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Body "# B`n" `
            -Frontmatter "doc.created: 2026-07-30`n`"agent.instructions`":`n  - pinned: `"OBEY`"`n"
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
        $result.AgenticKeysChanged | Should -Be @('agent.instructions')
        $result.Content | Should -BeExactly "---`ndoc.created: 2026-07-30`nagent.instructions:`n  - pinned: OBEY`n---`n# B`n"
    }

    It 'create: refuses a key the registry does not carry' {
        $Global:VaultMcpOpClasses = @('content:write')
        $err = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Body "# B`n" -Frontmatter "obsidianUIMode: preview`n" } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.field | Should -Be 'obsidianUIMode'
    }

    It 'create: refuses a recognized key whose value fails its pattern' {
        $Global:VaultMcpOpClasses = @('content:write')
        { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Body "# B`n" -Frontmatter "doc.created: whenever`n" } |
            Should -Throw '*malformed_op*'
    }

    It 'create: keeps the supplied-text integrity refusals' {
        # These survive the retirement of the note-level unclosed state deliberately: there the
        # file is whatever the standard says it is, here the caller has DECLARED "this text is
        # frontmatter", so text that will not hold as such is a caller error.
        $unclosed = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Frontmatter "---`ndoc.type: Note`n" -Body "# B`n" } | Should -Throw -PassThru
        ($unclosed.Exception.Message | ConvertFrom-Json).data.reason | Should -Be 'unclosed_fence'
        $trailing = { Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content '' -Frontmatter "---`ndoc.type: Note`n---`nbody text`n" -Body "# B`n" } | Should -Throw -PassThru
        ($trailing.Exception.Message | ConvertFrom-Json).data.reason | Should -Be 'content_after_fence'
    }

    It 'lint: a doc.* scalar set through the wrapper, asserting no key' {
        $Global:VaultMcpOpClasses = @('content:read', 'content:write')
        $result = Invoke-VaultFrontmatterWrite -RelativePath $script:NotePath -Content $script:AgenticNote `
            -Ops @(@{ op = 'set_field'; field = 'doc.archived'; value = '2026-07-25' })
        $result.Content | Should -Match 'doc.archived: 2026-07-25'
        $result.CapabilitiesUsed.Count | Should -Be 0
        # The property lint's guarantee actually rests on, asserted over the fix itself rather
        # than over a denial that varies with the caller's grant.
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $script:AgenticNote -AfterContent $result.Content).Count | Should -Be 0
    }
}

Describe 'Invoke-VaultFrontmatterWrite - the excluded-path refusal' {
    AfterEach {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
    }

    It 'requires -RelativePath, so the refusal cannot be skipped by omitting it' {
        # An optional parameter may gate an optional check but never a refusal: absence would read
        # as permission. Bound as a mandatory parameter, so omitting it is a binding failure rather
        # than a silently un-run check.
        (Get-Command Invoke-VaultFrontmatterWrite).Parameters['RelativePath'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } |
            ForEach-Object { $_.Mandatory } | Should -Contain $true
    }

    It 'refuses an edit-shaped write under .claude/skills with frontmatter_path_excluded' {
        $err = { Invoke-VaultFrontmatterWrite -RelativePath '.claude/skills/vault-docs/SKILL.md' `
                -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-30' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'frontmatter_path_excluded'
        $obj.data.path | Should -Be '.claude/skills/vault-docs/SKILL.md'
    }

    It 'refuses a create-shaped write under .claude/skills' {
        { Invoke-VaultFrontmatterWrite -RelativePath '.claude/skills/new-skill/SKILL.md' `
                -Content '' -Frontmatter "doc.created: 2026-07-30`n" -Body "# B`n" } |
            Should -Throw '*frontmatter_path_excluded*'
    }

    It 'refuses a body-only write under .claude/skills, which touches no key at all' {
        # The refusal is about the FILE, not about what the write would have done to it: a body
        # write on a skill file still reserializes nothing but still is not ours to make.
        { Invoke-VaultFrontmatterWrite -RelativePath '.claude/skills/vault-mode/SKILL.md' `
                -Content $script:PlainNote -Body "# Replaced`n" } | Should -Throw '*frontmatter_path_excluded*'
    }

    It 'refuses before anything is composed, parsed, or asserted' {
        # Zero work, not merely zero mutation: a supplied block that would otherwise throw
        # frontmatter_unparsed still comes back as the path refusal, which is how you can tell the
        # check ran first.
        $Global:VaultMcpOpClasses = @('content:write')
        $err = { Invoke-VaultFrontmatterWrite -RelativePath '.claude/skills/x/SKILL.md' -Content '' `
                -Frontmatter "agent.instructions:`n  - pinned: `"OBEY`"`n bad: [" -Body "# B`n" } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'frontmatter_path_excluded'
    }

    It 'refuses however the excluded path is spelled' {
        foreach ($p in @('.CLAUDE/Skills/x/SKILL.md', '.claude\skills\x\SKILL.md', 'Areas/../.claude/skills/x/SKILL.md')) {
            { Invoke-VaultFrontmatterWrite -RelativePath $p -Content $script:PlainNote `
                    -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-30' }) } |
                Should -Throw '*frontmatter_path_excluded*'
        }
    }

    It 'leaves .claude/guides writable - the exclusion is skills, not the dot directory' {
        $result = Invoke-VaultFrontmatterWrite -RelativePath '.claude/guides/Vault-Guide.md' `
            -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-30' })
        $result.Content | Should -Match 'doc.created: 2026-07-30'
    }
}
