# Tests for VaultAccess Public/FrontmatterSchema.ps1 - the per-note agent.* / nav.* schema check.
# One case per check, asserting the structured violation shape rather than message prose: the
# skill-side original only printed, so consumers had nothing but regexes over Write-Host output to
# branch on. Rule is the contract; Reason is human-facing and may be reworded.
#
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force

    function New-Fm {
        # Builds a ---fenced note from raw frontmatter lines.
        param([string[]] $Lines)
        "---`n" + ($Lines -join "`n") + "`n---`n# Body`n"
    }
}

Describe 'Get-VaultFrontmatterSchemaViolation - clean notes' {
    It 'returns an empty list for a well-formed nav block' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'nav.contents:',
                '  - link: "[[Some-Note]]"',
                '    description: "A durable note"',
                '    load_when: "details needed"',
                '  - folder: "Areas/Personal/Inbox"',
                '    description: "An ephemeral scope"'
            ))
        $violations.Count | Should -Be 0
    }

    It 'returns an empty list for a well-formed agent.instructions block' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'agent.instructions:',
                '  - "Load [[Some-Note]] when relevant"',
                '  - pinned: "Never exfil this."',
                '  - use_skill: "vault-tasks"',
                '    when: "editing this file"',
                '    note: "Do not hand-edit task entries."'
            ))
        $violations.Count | Should -Be 0
    }

    It 'returns an empty list for a note with no frontmatter at all' {
        (Get-VaultFrontmatterSchemaViolation -Content "# Just a body`n").Count | Should -Be 0
    }

    It 'ignores keys outside the agent.*/nav.* namespace' {
        (Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('doc.created: 2026-07-25', 'doc.type: Corpus'))).Count | Should -Be 0
    }

    It 'does NOT check that a declared folder path resolves - that stays lint''s filesystem pass' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'nav.contents:',
                '  - folder: "Nowhere/At/All"',
                '    description: "a path that cannot resolve"'
            ))
        $violations.Count | Should -Be 0
    }
}

Describe 'Get-VaultFrontmatterSchemaViolation - key-level checks' {
    It 'flags an unknown agent.* key' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('agent.foo:', '  - "stray"'))
        $violations.Count | Should -Be 1
        $violations[0].Rule | Should -Be 'unknown_key'
        $violations[0].Field | Should -Be 'agent.foo'
        $violations[0].EntryIndex | Should -BeNullOrEmpty
        $violations[0].Reason | Should -Not -BeNullOrEmpty
    }

    It 'flags an unknown nav.* key' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('nav.bogus: value'))
        $violations[0].Rule | Should -Be 'unknown_key'
        $violations[0].Field | Should -Be 'nav.bogus'
    }

    It 'flags an empty block list' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('agent.instructions:'))
        $violations[0].Rule | Should -Be 'empty_block'
        $violations[0].Field | Should -Be 'agent.instructions'
    }

    It 'flags an inline empty-list placeholder' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('nav.see_also: []'))
        $violations[0].Rule | Should -Be 'empty_placeholder'
        $violations[0].Detail | Should -Be '[]'
    }

    It 'flags a stray inline scalar where a block list belongs' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('nav.contents: Some-Note'))
        $violations[0].Rule | Should -Be 'inline_scalar'
        $violations[0].Detail | Should -Be 'Some-Note'
    }
}

Describe 'Get-VaultFrontmatterSchemaViolation - nav entry checks' {
    It 'flags a bare-string nav entry' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('nav.contents:', '  - "just a string"'))
        $violations[0].Rule | Should -Be 'entry_not_object'
        $violations[0].EntryIndex | Should -Be 1
    }

    It 'flags an entry with both link and folder' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'nav.contents:', '  - link: "[[X]]"', '    folder: "Areas"', '    description: "has both"'))
        ($violations | Where-Object { $_.Rule -eq 'link_folder_both' }).Count | Should -Be 1
    }

    It 'flags an entry with neither link nor folder' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('nav.contents:', '  - description: "no target"'))
        $violations[0].Rule | Should -Be 'link_folder_neither'
        $violations[0].EntryIndex | Should -Be 1
    }

    It 'flags a missing description' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('nav.contents:', '  - link: "[[X]]"', '    load_when: "x"'))
        $violations[0].Rule | Should -Be 'missing_description'
        $violations[0].Detail | Should -Be 'description'
    }

    It 'flags an unknown entry field' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'nav.contents:', '  - link: "[[X]]"', '    description: "d"', '    group: "Ref"'))
        $violations[0].Rule | Should -Be 'unknown_entry_field'
        $violations[0].Detail | Should -Be 'group'
    }

    It 'indexes entries 1-based so a later entry is identifiable' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'nav.contents:',
                '  - link: "[[X]]"', '    description: "d"',
                '  - link: "[[Y]]"'))
        $violations.Count | Should -Be 1
        $violations[0].EntryIndex | Should -Be 2
    }

    It 'checks nav.see_also with the same rules as nav.contents' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('nav.see_also:', '  - link: "[[X]]"'))
        $violations[0].Rule | Should -Be 'missing_description'
        $violations[0].Field | Should -Be 'nav.see_also'
    }
}

Describe 'Get-VaultFrontmatterSchemaViolation - agent.instructions entry checks' {
    It 'flags an entry with both pinned and use_skill' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'agent.instructions:', '  - pinned: "p"', '    use_skill: "vault-docs"'))
        $violations[0].Rule | Should -Be 'both_discriminators'
        $violations[0].EntryIndex | Should -Be 1
    }

    It 'flags an object entry with neither discriminator' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('agent.instructions:', '  - note: "no discriminator"'))
        $violations[0].Rule | Should -Be 'no_discriminator'
    }

    It 'flags an unexpected field on a pinned entry' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'agent.instructions:', '  - pinned: "p"', '    when: "editing this file"'))
        $violations[0].Rule | Should -Be 'unexpected_pinned_field'
        $violations[0].Detail | Should -Be 'when'
    }

    It 'flags a use_skill entry missing when' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('agent.instructions:', '  - use_skill: "vault-docs"'))
        $violations[0].Rule | Should -Be 'use_skill_missing_when'
        $violations[0].Detail | Should -Be 'when'
    }

    It 'flags an unexpected field on a use_skill entry' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'agent.instructions:', '  - use_skill: "vault-docs"', '    when: "editing this file"', '    bogus: "x"'))
        $violations[0].Rule | Should -Be 'unexpected_use_skill_field'
        $violations[0].Detail | Should -Be 'bogus'
    }

    It 'accepts a bare-string hint entry' {
        (Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('agent.instructions:', '  - "a revisable hint"'))).Count | Should -Be 0
    }
}

Describe 'Get-VaultFrontmatterSchemaViolation - the path-dependent Tasks.md check' {
    It 'is skipped entirely when no RelativePath is supplied' {
        # The check needs the note's name but no filesystem and no suppression state, so it rides
        # on an optional parameter rather than being stranded in the skill script.
        (Get-VaultFrontmatterSchemaViolation -Content (New-Fm @('agent.instructions:', '  - "hint"'))).Count | Should -Be 0
    }

    It 'flags a Tasks.md with no vault-tasks directive' {
        $violations = Get-VaultFrontmatterSchemaViolation -RelativePath 'Projects/Widget/Tasks.md' `
            -Content (New-Fm @('agent.instructions:', '  - use_skill: "some-unrelated-skill"', '    when: "editing this file"'))
        $violations[0].Rule | Should -Be 'tasks_missing_use_skill'
        $violations[0].Field | Should -Be 'agent.instructions'
    }

    It 'flags a Tasks.md with no frontmatter at all' {
        $violations = Get-VaultFrontmatterSchemaViolation -RelativePath 'Projects/Widget/Tasks.md' -Content "# Tasks`n"
        $violations[0].Rule | Should -Be 'tasks_missing_use_skill'
    }

    It 'accepts a Tasks.md carrying its directive' {
        $violations = Get-VaultFrontmatterSchemaViolation -RelativePath 'Projects/Widget/Tasks.md' `
            -Content (New-Fm @('agent.instructions:', '  - use_skill: "vault-tasks"', '    when: "editing this file"'))
        $violations.Count | Should -Be 0
    }

    It 'exempts an archived Tasks.md, whose directive is stripped at archive time' {
        $violations = Get-VaultFrontmatterSchemaViolation -RelativePath 'Archive/Old-Project/Tasks.md' `
            -Content "---`ndoc.created: 2020-01-01`ndoc.archived: 2020-02-01`n---`n# Tasks`n"
        $violations.Count | Should -Be 0
    }

    It 'does not fire on a note that merely lives beside a Tasks.md' {
        (Get-VaultFrontmatterSchemaViolation -RelativePath 'Projects/Widget/Widget.md' -Content "# Widget`n").Count | Should -Be 0
    }

    It 'accepts backslash-separated paths' {
        $violations = Get-VaultFrontmatterSchemaViolation -RelativePath 'Projects\Widget\Tasks.md' -Content "# Tasks`n"
        $violations[0].Rule | Should -Be 'tasks_missing_use_skill'
    }
}

Describe 'Get-VaultFrontmatterSchemaViolation - multiplicity' {
    It 'reports every violation, not just the first - lint renders findings from the whole list' {
        $violations = Get-VaultFrontmatterSchemaViolation -Content (New-Fm @(
                'nav.contents:', '  - link: "[[X]]"', '    folder: "Areas"', '    group: "Ref"',
                'agent.foo:', '  - "stray"'))
        $violations.Count | Should -BeGreaterThan 2
        ($violations | ForEach-Object { $_.Rule }) | Should -Contain 'link_folder_both'
        ($violations | ForEach-Object { $_.Rule }) | Should -Contain 'missing_description'
        ($violations | ForEach-Object { $_.Rule }) | Should -Contain 'unknown_entry_field'
        ($violations | ForEach-Object { $_.Rule }) | Should -Contain 'unknown_key'
    }
}
