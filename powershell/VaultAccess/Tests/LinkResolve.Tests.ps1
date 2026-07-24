# Tests for VaultAccess Public/LinkResolve.ps1 (Obsidian bare-link resolution ranking).
# Pure - no vault fixture needed, just the imported module.
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force
}

Describe 'Resolve-VaultBareLink' {
    # Ranks same-basename candidates by the empirically-mapped Obsidian resolution rule
    # (Resources/Software/Obsidian-Link-Behavior.md): shortest relative path from the linking
    # file (character count), ties broken alphabetically (ordinal, case-insensitive). These
    # fixtures pin the empirical scenarios the model was mapped against.

    It 'returns the sole candidate unchanged' {
        Resolve-VaultBareLink -LinkingFile 'X/Y.md' -Candidates @('Only/Dup.md') | Should -Be 'Only/Dup.md'
    }

    It 'returns null for an empty candidate set' {
        Resolve-VaultBareLink -LinkingFile 'X/Y.md' -Candidates @() | Should -BeNullOrEmpty
    }

    It 'Left-vs-Right: the shorter relative path wins' {
        # From the vault root, "Left/Dup.md" (11 chars) beats "Right/Dup.md" (12 chars).
        Resolve-VaultBareLink -LinkingFile 'Root-Link.md' -Candidates @('Right/Dup.md', 'Left/Dup.md') |
            Should -Be 'Left/Dup.md'
    }

    It 'breaks an exact-length tie alphabetically (ordinal, case-insensitive)' {
        # "../A/Dupe.md" and "../B/Dupe.md" are both 12 chars from Inbox/Refs.md; A wins.
        Resolve-VaultBareLink -LinkingFile 'Inbox/Refs.md' -Candidates @('B/Dupe.md', 'A/Dupe.md') |
            Should -Be 'A/Dupe.md'
    }

    It 'is order-independent (same winner regardless of candidate order)' {
        $a = Resolve-VaultBareLink -LinkingFile 'Inbox/Refs.md' -Candidates @('A/Dupe.md', 'B/Dupe.md')
        $b = Resolve-VaultBareLink -LinkingFile 'Inbox/Refs.md' -Candidates @('B/Dupe.md', 'A/Dupe.md')
        $a | Should -Be $b
        $a | Should -Be 'A/Dupe.md'
    }

    It 'a candidate inside the linking file''s own subtree beats a globally-shorter path elsewhere (T3 Links-B)' {
        # From Tests/T3/Deep/T3-Links-B.md: the in-subtree candidate "Down/T3-Dup.md" (14 chars)
        # beats the sibling-branch "../North/T3-Dup.md" (18 chars), even though the latter's
        # vault-absolute path is no longer. Resolution is relative, not vault-global.
        Resolve-VaultBareLink -LinkingFile 'Tests/T3/Deep/T3-Links-B.md' `
            -Candidates @('Tests/T3/North/T3-Dup.md', 'Tests/T3/Deep/Down/T3-Dup.md') |
            Should -Be 'Tests/T3/Deep/Down/T3-Dup.md'
    }

    It 'fewer characters beat fewer folder hops (T11)' {
        # From Tests/T11/T11-Links.md: two hops down at "A/B/T11-Dup.md" (14 chars) beats one hop
        # down at "Alphabet-Zoo-Folder/T11-Dup.md" (30 chars) - confirmed in Obsidian 2026-07-23.
        Resolve-VaultBareLink -LinkingFile 'Tests/T11/T11-Links.md' `
            -Candidates @('Tests/T11/Alphabet-Zoo-Folder/T11-Dup.md', 'Tests/T11/A/B/T11-Dup.md') |
            Should -Be 'Tests/T11/A/B/T11-Dup.md'
    }

    It 'ties break on the candidate vault path, not the relative path (T12)' {
        # From Tests/T12/Sub/T12-Links.md: "../T12-Dup.md" and "Al/T12-Dup.md" are both 13 chars.
        # Relative-path alphabetical would pick the parent ('.' < 'A'); Obsidian picks the subtree
        # copy, matching ordinal comparison of the vault-absolute paths ("Tests/T12/Sub/Al/..."
        # < "Tests/T12/T12-Dup.md" at 'S' vs 'T') - confirmed in Obsidian 2026-07-23.
        Resolve-VaultBareLink -LinkingFile 'Tests/T12/Sub/T12-Links.md' `
            -Candidates @('Tests/T12/T12-Dup.md', 'Tests/T12/Sub/Al/T12-Dup.md') |
            Should -Be 'Tests/T12/Sub/Al/T12-Dup.md'
    }

    It 'measures distance from the linking file, not the vault root (moving closer changes the winner)' {
        # A/B/Link.md resolves [[Dup]] to A/Dupe.md ("../Dupe.md", 10) over Far/Dupe.md; once a
        # same-basename file sits at A/B/Dupe.md ("Dupe.md", 7) it becomes the winner.
        Resolve-VaultBareLink -LinkingFile 'A/B/Link.md' -Candidates @('A/Dupe.md', 'Far/Dupe.md') |
            Should -Be 'A/Dupe.md'
        Resolve-VaultBareLink -LinkingFile 'A/B/Link.md' -Candidates @('A/Dupe.md', 'A/B/Dupe.md') |
            Should -Be 'A/B/Dupe.md'
    }
}
