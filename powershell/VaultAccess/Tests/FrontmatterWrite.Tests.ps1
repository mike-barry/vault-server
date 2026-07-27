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

    # A note with a non-agentic frontmatter block and a two-line body.
    $script:PlainNote = "---`ndoc.type: Corpus`n---`n# Body`nsecond line`n"
    # The same note carrying an agentic key.
    $script:AgenticNote = "---`ndoc.type: Corpus`nnav.contents:`n  - link: `"[[X]]`"`n    description: `"d`"`n---`n# Body`n"
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

    It 'treats a create (no original) as every agentic key present being a diff' {
        (Get-VaultAgenticFrontmatterDiff -BeforeContent '' -AfterContent $script:AgenticNote) | Should -Be @('nav.contents')
    }
}

Describe 'Invoke-VaultFrontmatterWrite - composition and facets' {
    It 'applies ops in order and returns the composed content byte-exactly' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(
            @{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }
            @{ op = 'set_field'; field = 'doc.type'; value = 'Idea' }
        )
        $result.Content | Should -BeExactly "---`ndoc.type: Idea`ndoc.created: 2026-07-25`n---`n# Body`nsecond line`n"
        $result.Frontmatter | Should -BeExactly "doc.type: Idea`ndoc.created: 2026-07-25"
        $result.FrontmatterChanged | Should -BeTrue
        $result.BodyChanged | Should -BeFalse
    }

    It 'preserves CRLF end to end, body included' {
        $crlf = "---`r`ndoc.type: Corpus`r`n---`r`n# Body`r`ntail`r`n"
        $result = Invoke-VaultFrontmatterWrite -Content $crlf -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.Content | Should -BeExactly "---`r`ndoc.type: Corpus`r`ndoc.created: 2026-07-25`r`n---`r`n# Body`r`ntail`r`n"
    }

    It 'records applied and changed per op, with changed false on an idempotent no-op' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(
            @{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }
            @{ op = 'remove_field'; field = 'doc.nothing' }
        )
        $result.OpsApplied.Count | Should -Be 2
        $result.OpsApplied[0].Op | Should -Be 'set_field'
        $result.OpsApplied[0].Field | Should -Be 'doc.created'
        $result.OpsApplied[0].Applied | Should -BeTrue
        $result.OpsApplied[0].Changed | Should -BeTrue
        $result.OpsApplied[1].Changed | Should -BeFalse
    }

    It 'returns @() for OpsApplied when there are no ops' {
        (Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @() -Body "# New`n").OpsApplied.Count | Should -Be 0
    }

    It 'replaces the body facet and leaves the frontmatter block untouched' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Body "# Replaced`n"
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n# Replaced`n"
        $result.FrontmatterChanged | Should -BeFalse
        $result.BodyChanged | Should -BeTrue
    }

    It 'truncates the body on an empty -Body and leaves the block intact' {
        (Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Body '').Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n"
    }

    It 'leaves the body untouched when -Body is not bound at all' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.Body | Should -BeExactly "# Body`nsecond line`n"
        $result.BodyChanged | Should -BeFalse
    }

    It 'routes remove_field to block removal by detected shape' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:AgenticNote -Ops @(@{ op = 'remove_field'; field = 'nav.contents' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n# Body`n"
    }
}

Describe 'Invoke-VaultFrontmatterWrite - schema violations are returned, never thrown' {
    It 'returns the violation list and still composes the content' {
        # The design turn lint depends on: a hard throw here would refuse lint''s provenance date
        # fix on exactly the notes that already have agent.* problems.
        $broken = "---`nnav.contents:`n  - link: `"[[X]]`"`n---`n# Body`n"
        $result = Invoke-VaultFrontmatterWrite -Content $broken -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.Violations.Count | Should -Be 1
        $result.Violations[0].Rule | Should -Be 'missing_description'
        $result.Content | Should -Match 'doc.created: 2026-07-25'
    }

    It 'returns an empty violation list for a clean note' {
        (Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })).Violations.Count | Should -Be 0
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
        $err = { Invoke-VaultFrontmatterWrite -Content "# Plain note`n" -Body "---`nnav.contents: smuggled`n---`n# Body`n" } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'frontmatter_integrity'
    }

    It 'refuses a body write that opens a fence it never closes' {
        { Invoke-VaultFrontmatterWrite -Content "# Plain note`n" -Body "---`nnav.contents: smuggled`n" } | Should -Throw '*frontmatter_integrity*'
    }

    It 'refuses a write against a note whose own fence never closes' {
        $err = { Invoke-VaultFrontmatterWrite -Content "---`ndoc.type: Corpus`n# Body" -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'frontmatter_integrity'
        $obj.data.reason | Should -Be 'unclosed_fence'
    }

    It 'refuses a supplied frontmatter block that never closes' {
        { Invoke-VaultFrontmatterWrite -Content '' -Frontmatter "---`ndoc.type: Corpus`n" } | Should -Throw '*frontmatter_integrity*'
    }

    It 'refuses a supplied frontmatter block carrying content past its closing fence' {
        { Invoke-VaultFrontmatterWrite -Content '' -Frontmatter "---`ndoc.type: Corpus`n---`n# smuggled body`n" } | Should -Throw '*frontmatter_integrity*'
    }

    It 'allows a body write that merely contains --- below the first line' {
        $result = Invoke-VaultFrontmatterWrite -Content "# Plain`n" -Body "# Heading`n---`nnot frontmatter`n"
        $result.Content | Should -BeExactly "# Heading`n---`nnot frontmatter`n"
        $result.FrontmatterChanged | Should -BeFalse
    }
}

Describe 'Invoke-VaultFrontmatterWrite - scalar hygiene' {
    It 'refuses a field name outside the key pattern with malformed_op and the op index' {
        $err = { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(
                @{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' }
                @{ op = 'set_field'; field = 'doc created'; value = 'x' }
            ) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.opIndex | Should -Be 1
    }

    It 'refuses a value carrying a line break' {
        { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.type'; value = "Corpus`nnav.contents: smuggled" }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses directive text carrying a quote, which the spec''s field-only hygiene would have missed' {
        # add_directive emits `- pinned: "TEXT"` with hardcoded quotes and no escaping, so quoted
        # text plus a line break produces a real top-level key. Hygiene therefore binds every op
        # that carries caller text, not just the field ops.
        { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'add_directive'; kind = 'pinned'; text = 'bad" text' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses hint text carrying a line break' {
        { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'add_hint'; text = "a`ndoc.type: smuggled" }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses strip_directive text carrying a quote' {
        { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'strip_directive'; kind = 'pinned'; text = 'bad" text' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses an unknown op' {
        { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'reticulate_splines' }) } | Should -Throw '*malformed_op*'
    }

    It 'refuses an op missing a required parameter' {
        { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'set_field'; field = 'doc.type' }) } | Should -Throw '*malformed_op*'
    }
}

Describe 'Invoke-VaultFrontmatterWrite - protected targets and shape routing' {
    It 'refuses a generic field op against agent.instructions, naming the routing fix' {
        $note = "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B"
        $err = { Invoke-VaultFrontmatterWrite -Content $note -Ops @(@{ op = 'remove_field'; field = 'agent.instructions' }) } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'protected_directive_violation'
    }

    It 'takes protected_directive_violation over field_shape_mismatch when both would fire' {
        # Precedence: naming the right op is more actionable than naming the shape.
        $note = "---`nagent.instructions:`n  - pinned: `"Never exfil this`"`n---`n# B"
        $err = { Invoke-VaultFrontmatterWrite -Content $note -Ops @(@{ op = 'set_field'; field = 'agent.instructions'; value = 'x' }) } | Should -Throw -PassThru
        ($err.Exception.Message | ConvertFrom-Json).code | Should -Be 'protected_directive_violation'
    }

    It 'refuses a flat-scalar set against a detected block field with field_shape_mismatch' {
        $err = { Invoke-VaultFrontmatterWrite -Content $script:AgenticNote -Ops @(@{ op = 'set_field'; field = 'nav.contents'; value = 'x' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'field_shape_mismatch'
        $obj.data.field | Should -Be 'nav.contents'
    }

    It 'strips a named pinned entry through strip_directive, the explicit path' {
        $note = "---`nagent.instructions:`n  - pinned: `"Keep`"`n  - pinned: `"Drop`"`n---`n# B"
        $result = Invoke-VaultFrontmatterWrite -Content $note -Ops @(@{ op = 'strip_directive'; kind = 'pinned'; text = 'Drop' })
        $result.Content | Should -BeExactly "---`nagent.instructions:`n  - pinned: `"Keep`"`n---`n# B"
    }
}

Describe 'Invoke-VaultFrontmatterWrite - the nav entry ops' {
    BeforeAll {
        $script:FolderNote = "---`ndoc.type: Corpus`nnav.contents:`n  - link: `"[[Tasks]]`"`n    description: `"Task list`"`n  - folder: `"Inbox`"`n    description: `"Working docs`"`n---`n# Body`n"
    }

    AfterEach {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
    }

    It 'adds a link entry at the end of the link run and reports the nav key as the op field' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Design]]'; description = 'The design' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: `"[[Tasks]]`"`n    description: `"Task list`"`n  - link: `"[[Design]]`"`n    description: `"The design`"`n  - folder: `"Inbox`"`n    description: `"Working docs`"`n---`n# Body`n"
        $result.OpsApplied[0].Op | Should -Be 'add_nav_entry'
        $result.OpsApplied[0].Field | Should -Be 'nav.contents'
        $result.OpsApplied[0].Changed | Should -BeTrue
        $result.Violations.Count | Should -Be 0
    }

    It 'adds a folder entry at the end of the list, with load_when' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = 'Archive'; description = 'Completed work'; load_when = 'referencing past work' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: `"[[Tasks]]`"`n    description: `"Task list`"`n  - folder: `"Inbox`"`n    description: `"Working docs`"`n  - folder: `"Archive`"`n    description: `"Completed work`"`n    load_when: `"referencing past work`"`n---`n# Body`n"
        $result.Violations.Count | Should -Be 0
    }

    It 'honours before and after against an existing entry target' {
        $before = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[First]]'; description = 'first'; before = '[[Tasks]]' })
        $before.Content | Should -Match ([regex]::Escape("nav.contents:`n  - link: `"[[First]]`""))
        $after = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Last]]'; description = 'last'; after = 'Inbox' })
        $after.Content | Should -Match ([regex]::Escape("    description: `"Working docs`"`n  - link: `"[[Last]]`""))
    }

    It 'creates the key when the note carries none - lint''s folder-note completeness case' {
        # The gap these ops close: set_field against a block key is field_shape_mismatch by
        # design, and a body patch is body-confined, so before this there was no way to apply it.
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Tasks]]'; description = 'Task list' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: `"[[Tasks]]`"`n    description: `"Task list`"`n---`n# Body`nsecond line`n"
        $result.Violations.Count | Should -Be 0
    }

    It 'removes an entry addressed by its target and drops the key when that empties the list' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:AgenticNote -Ops @(
            @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = '[[X]]' })
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`n---`n# Body`n"
        $result.OpsApplied[0].Field | Should -Be 'nav.contents'
        $result.OpsApplied[0].Changed | Should -BeTrue
    }

    It 'honours order and repetition: each op sees the previous op''s result' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[A]]'; description = 'a' }
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[B]]'; description = 'b'; after = '[[A]]' }
            @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = '[[Tasks]]' }
        )
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: `"[[A]]`"`n    description: `"a`"`n  - link: `"[[B]]`"`n    description: `"b`"`n  - folder: `"Inbox`"`n    description: `"Working docs`"`n---`n# Body`n"
        $result.OpsApplied.Count | Should -Be 3
    }

    It 'is idempotent on a duplicate add: changed false, nothing mutated, nothing asserted' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[Tasks]]'; description = 'a different description' })
        $result.Content | Should -BeExactly $script:FolderNote
        $result.OpsApplied[0].Changed | Should -BeFalse
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'is idempotent on an absent-target removal: changed false, nothing asserted' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'remove_nav_entry'; key = 'nav.contents'; folder = 'Nowhere' })
        $result.Content | Should -BeExactly $script:FolderNote
        $result.OpsApplied[0].Changed | Should -BeFalse
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'refuses an ambiguous removal with malformed_op and the op index, rather than guessing' {
        $dupe = "---`nnav.contents:`n  - link: `"[[A]]`"`n    description: `"a`"`n  - link: `"[[A]]`"`n    description: `"b`"`n---`n# B"
        $err = { Invoke-VaultFrontmatterWrite -Content $dupe -Ops @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = 'Inbox'; description = 'i' }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = '[[A]]' }
            ) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.opIndex | Should -Be 1
    }

    It 'refuses a key outside the nav block-list allow-list, with the op index' {
        $err = { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
                @{ op = 'add_nav_entry'; key = 'agent.instructions'; link = '[[X]]'; description = 'x' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.opIndex | Should -Be 0
    }

    It 'reports and writes the canonical key spelling whatever case the op used' {
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(
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
            { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @($op) } | Should -Throw '*malformed_op*'
        }
    }

    It 'refuses a missing or blank description' {
        { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(@{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]' }) } | Should -Throw '*malformed_op*'
        { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(@{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = '  ' }) } | Should -Throw '*malformed_op*'
    }

    It 'refuses before and after together' {
        { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; before = '[[Tasks]]'; after = 'Inbox' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses an anchor naming no existing entry' {
        { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; before = '[[Nope]]' }) } |
            Should -Throw '*malformed_op*'
    }

    It 'refuses a line break in any caller-text field - the op takes fields, not block text' {
        # The reason the op is structured rather than taking pre-rendered block text: hygiene
        # applies per field, and raw block text would carry the newline straight through.
        foreach ($op in @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = "[[X]]`ndoc.type: smuggled"; description = 'x' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = "Inbox`ndoc.type: smuggled"; description = 'x' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = "x`ndoc.type: smuggled" }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; load_when = "w`ndoc.type: smuggled" }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; before = "[[Tasks]]`nx: 1" }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; after = "Inbox`nx: 1" }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = "[[X]]`nx: 1" }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; folder = "Inbox`nx: 1" }
            )) {
            { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @($op) } | Should -Throw '*malformed_op*'
        }
    }

    It 'refuses a quote in any caller-text field' {
        foreach ($op in @(
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = 'bad" link'; description = 'x' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = 'bad" folder'; description = 'x' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'bad" text' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; load_when = 'bad" when' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; before = 'bad" anchor' }
                @{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[X]]'; description = 'x'; after = 'bad" anchor' }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; link = 'bad" link' }
                @{ op = 'remove_nav_entry'; key = 'nav.contents'; folder = 'bad" folder' }
            )) {
            { Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @($op) } | Should -Throw '*malformed_op*'
        }
    }

    It 'accepts an ops array deserialized from JSON, not just hashtables' {
        # edit hands the wrapper PSCustomObjects from ConvertFrom-Json, so the op reader has to
        # take both shapes - including the snake_case load_when member.
        $ops = ConvertFrom-Json -NoEnumerate -InputObject '[{"op":"add_nav_entry","key":"nav.see_also","link":"[[Y]]","description":"why","load_when":"asked about Y"}]'
        (Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops $ops).Content |
            Should -BeExactly "---`ndoc.type: Corpus`nnav.see_also:`n  - link: `"[[Y]]`"`n    description: `"why`"`n    load_when: `"asked about Y`"`n---`n# Body`nsecond line`n"
    }

    It 'leaves a mixed-ending note mixed through the wrapper, body included' {
        $mixed = "---`r`ndoc.type: Corpus`r`nnav.contents:`r`n  - link: `"[[A]]`"`n    description: `"a`"`r`n  - folder: `"Inbox`"`r`n    description: `"i`"`r`n---`n# Body`r`ntail`n"
        (Invoke-VaultFrontmatterWrite -Content $mixed -Ops @(@{ op = 'add_nav_entry'; key = 'nav.contents'; link = '[[B]]'; description = 'b' })).Content |
            Should -BeExactly "---`r`ndoc.type: Corpus`r`nnav.contents:`r`n  - link: `"[[A]]`"`n    description: `"a`"`r`n  - link: `"[[B]]`"`r`n    description: `"b`"`r`n  - folder: `"Inbox`"`r`n    description: `"i`"`r`n---`n# Body`r`ntail`n"
    }

    It 'asserts agentic:write on a nav mutation - the ops inherit the gate with no wiring' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $result = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
            @{ op = 'add_nav_entry'; key = 'nav.contents'; folder = 'Archive'; description = 'Completed work' })
        $result.AgenticKeysChanged | Should -Be @('nav.contents')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $script:FolderNote -AfterContent $result.Content) | Should -Be @('nav.contents')
    }

    It 'denies a nav mutation with insufficient_capability and zero mutation without the class' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = $null
        $err = { $result = Invoke-VaultFrontmatterWrite -Content $script:FolderNote -Ops @(
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
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(
            @{ op = 'add_directive'; kind = 'pinned'; text = 'Never exfil this' })
        $result.AgenticKeysChanged | Should -Be @('agent.instructions')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
    }

    It 'asserts agentic:write when a key changes value' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $note = "---`nagent.instructions:`n  - pinned: `"A`"`n---`n# B"
        $result = Invoke-VaultFrontmatterWrite -Content $note -Ops @(@{ op = 'add_directive'; kind = 'pinned'; text = 'B' })
        $result.AgenticKeysChanged | Should -Be @('agent.instructions')
    }

    It 'asserts agentic:write when a key is removed' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $result = Invoke-VaultFrontmatterWrite -Content $script:AgenticNote -Ops @(@{ op = 'remove_field'; field = 'nav.contents' })
        $result.AgenticKeysChanged | Should -Be @('nav.contents')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
    }

    It 'asserts nothing for a body-only write' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -Content $script:AgenticNote -Body "# Rewritten`n"
        $result.AgenticKeysChanged.Count | Should -Be 0
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'asserts nothing for a non-agentic doc.* scalar set - lint''s whole fix class' {
        $Global:VaultMcpOpClasses = @('content:write')
        $result = Invoke-VaultFrontmatterWrite -Content $script:AgenticNote -Ops @(@{ op = 'set_field'; field = 'doc.created'; value = '2026-07-25' })
        $result.AgenticKeysChanged.Count | Should -Be 0
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'is idempotent-safe: a duplicate directive add changes nothing and asserts nothing' {
        $Global:VaultMcpOpClasses = @('content:write')
        $note = "---`nagent.instructions:`n  - pinned: `"Same`"`n---`n# B"
        $result = Invoke-VaultFrontmatterWrite -Content $note -Ops @(@{ op = 'add_directive'; kind = 'pinned'; text = 'Same' })
        $result.OpsApplied[0].Changed | Should -BeFalse
        $result.CapabilitiesUsed.Count | Should -Be 0
    }

    It 'denies with insufficient_capability and zero content change when the key is absent' {
        $Global:VaultMcpOpClasses = @('content:write')
        $original = $script:AgenticNote
        $result = $null
        $err = { $result = Invoke-VaultFrontmatterWrite -Content $original -Ops @(@{ op = 'remove_field'; field = 'nav.contents' }) } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'insufficient_capability'
        $obj.data.requiredClass | Should -Be 'agentic:write'
        # Zero mutation: the caller is handed nothing to write, and its own content is untouched.
        $result | Should -BeNullOrEmpty
        $original | Should -BeExactly $script:AgenticNote
    }

    It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
        $Global:VaultMcpOpClasses = @()
        { Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'add_hint'; text = 'Do X' }) } |
            Should -Throw '*insufficient_capability*'
    }

    It 'degrades OPEN when no host context was injected at all (standalone run)' {
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        $result = Invoke-VaultFrontmatterWrite -Content $script:PlainNote -Ops @(@{ op = 'add_hint'; text = 'Do X' })
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
        $result.Content | Should -BeExactly "---`ndoc.type: Corpus`nnav.contents:`n  - link: `"[[X]]`"`n    description: `"d`"`nagent.instructions:`n  - use_skill: `"vault-docs`"`n    when: `"editing this file`"`n---`n# Patched`n"
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
        $result.Content | Should -BeExactly "---`ndoc.created: 2026-07-25`nnav.see_also:`n  - link: `"[[X]]`"`n    description: `"d`"`n---`n# New Note`n"
        # The degenerate diff: with no original, every agentic key present counts as changed.
        $result.AgenticKeysChanged | Should -Be @('nav.see_also')
        $result.CapabilitiesUsed | Should -Be @('agentic:write')
    }

    It 'create: accepts a supplied block given as bare inner lines' {
        (Invoke-VaultFrontmatterWrite -Content '' -Frontmatter "doc.created: 2026-07-25`n" -Body "# New`n").Content |
            Should -BeExactly "---`ndoc.created: 2026-07-25`n---`n# New`n"
    }

    It 'create: an empty supplied block means a note with no frontmatter at all' {
        (Invoke-VaultFrontmatterWrite -Content '' -Frontmatter '' -Body "# New`n").Content | Should -BeExactly "# New`n"
    }

    It 'lint: a doc.* scalar set through the wrapper, asserting no key' {
        $Global:VaultMcpOpClasses = @('content:read', 'content:write')
        $result = Invoke-VaultFrontmatterWrite -Content $script:AgenticNote `
            -Ops @(@{ op = 'set_field'; field = 'doc.archived'; value = '2026-07-25' })
        $result.Content | Should -Match 'doc.archived: 2026-07-25'
        $result.CapabilitiesUsed.Count | Should -Be 0
        # The property lint's guarantee actually rests on, asserted over the fix itself rather
        # than over a denial that varies with the caller's grant.
        (Get-VaultAgenticFrontmatterDiff -BeforeContent $script:AgenticNote -AfterContent $result.Content).Count | Should -Be 0
    }
}
