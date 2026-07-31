# Tests for VaultAccess Public/FrontmatterRegistry.ps1 - the frontmatter key registry: the single
# declaration of the recognized keys, their value patterns, and the agentic set.
#
# The registry is a POSITIVE ALLOWLIST, so the tests that matter most are the negative ones: a key
# nobody anticipated must fail closed, and a key must not be classified as non-agentic by failing
# to match a pattern. The corpus block at the end holds the table to the vault it was derived from
# rather than to the memory of whoever wrote it.
#
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force
}

Describe 'Get-VaultFrontmatterKeyRegistry' {
    It 'carries exactly the seven keys the vault''s tooling recognizes' {
        @((Get-VaultFrontmatterKeyRegistry).Keys) | Should -Be @(
            'doc.created', 'doc.archived', 'doc.type', 'scope',
            'agent.instructions', 'nav.contents', 'nav.see_also'
        )
    }

    It 'flags exactly the agent.* / nav.* keys as agentic' {
        $r = Get-VaultFrontmatterKeyRegistry
        @(@($r.Keys) | Where-Object { $r[$_].Agentic }) | Should -Be @('agent.instructions', 'nav.contents', 'nav.see_also')
    }

    It 'gives every non-agentic key a value pattern and every agentic key none' {
        $r = Get-VaultFrontmatterKeyRegistry
        foreach ($k in @($r.Keys)) {
            if ($r[$k].Agentic) { $r[$k].Pattern | Should -BeNullOrEmpty }
            else { $r[$k].Pattern | Should -Not -BeNullOrEmpty }
        }
    }
}

Describe 'Get-VaultNavListKey reads the registry rather than restating it' {
    It 'returns the registry''s NavList-flagged keys' {
        # Assigned, never piped: the return is comma-wrapped so an empty result survives as an
        # empty array, which is also why every production caller assigns it first.
        $r = Get-VaultFrontmatterKeyRegistry
        $navKeys = Get-VaultNavListKey
        $navKeys | Should -Be @(@($r.Keys) | Where-Object { $r[$_].NavList })
    }

    It 'is the pair the nav entry ops address' {
        $navKeys = Get-VaultNavListKey
        $navKeys | Should -Be @('nav.contents', 'nav.see_also')
    }
}

Describe 'Resolve-VaultFrontmatterRegistryKey' {
    It 'resolves a caller''s casing to the canonical spelling' {
        Resolve-VaultFrontmatterRegistryKey -Field 'DOC.Type' | Should -BeExactly 'doc.type'
        Resolve-VaultFrontmatterRegistryKey -Field 'Nav.See_Also' | Should -BeExactly 'nav.see_also'
    }

    It 'returns $null for a key the registry does not carry' {
        Resolve-VaultFrontmatterRegistryKey -Field 'obsidianUIMode' | Should -BeNullOrEmpty
        Resolve-VaultFrontmatterRegistryKey -Field '' | Should -BeNullOrEmpty
    }
}

Describe 'Test-VaultFrontmatterKeyAgentic' {
    It 'classifies the registry''s agentic keys as agentic, whatever their casing' {
        Test-VaultFrontmatterKeyAgentic -Field 'agent.instructions' | Should -BeTrue
        Test-VaultFrontmatterKeyAgentic -Field 'NAV.Contents' | Should -BeTrue
    }

    It 'classifies the registry''s core keys as non-agentic' {
        Test-VaultFrontmatterKeyAgentic -Field 'doc.created' | Should -BeFalse
        Test-VaultFrontmatterKeyAgentic -Field 'scope' | Should -BeFalse
    }

    It 'still classifies an unregistered agent.* / nav.* key as agentic' {
        # The namespace prefix survives as a backstop that can only ESCALATE. A foreign agent.*
        # key already in a note is unwritable either way, but it must still count as agentic layer
        # for the outcome diff - nothing may be cleared as non-agentic by failing to match.
        Test-VaultFrontmatterKeyAgentic -Field 'agent.something_new' | Should -BeTrue
        Test-VaultFrontmatterKeyAgentic -Field 'nav.elsewhere' | Should -BeTrue
    }

    It 'classifies an unrecognized non-namespaced key as non-agentic, which leaves it unwritable' {
        # Non-agentic here is not permission: the registry lookup refuses the key outright, so
        # nothing is admitted by this answer.
        Test-VaultFrontmatterKeyAgentic -Field 'obsidianUIMode' | Should -BeFalse
        Get-VaultFrontmatterRegistryViolation -Field 'obsidianUIMode' | Should -Not -BeNullOrEmpty
    }
}

Describe 'Get-VaultFrontmatterRegistryViolation - keys' {
    It 'accepts a recognized key named alone' {
        Get-VaultFrontmatterRegistryViolation -Field 'doc.archived' | Should -BeNullOrEmpty
    }

    It 'refuses an empty field name' {
        Get-VaultFrontmatterRegistryViolation -Field '' | Should -Not -BeNullOrEmpty
    }

    It 'refuses a key the registry does not carry, naming the known set' {
        $reason = Get-VaultFrontmatterRegistryViolation -Field 'status'
        $reason | Should -Match 'not a frontmatter key'
        $reason | Should -Match 'doc.created'
    }

    It 'refuses the keys the corpus carries that the vault deliberately does not own' {
        foreach ($k in @('obsidianUIMode', 'status', 'title', 'name', 'description', 'disable-model-invocation')) {
            Get-VaultFrontmatterRegistryViolation -Field $k | Should -Not -BeNullOrEmpty -Because "$k is not the vault tooling's key"
        }
    }
}

Describe 'Get-VaultFrontmatterRegistryViolation - values' {
    It 'accepts a YYYY-MM-DD date string for the two date keys' {
        Get-VaultFrontmatterRegistryViolation -Field 'doc.created' -Value '2026-07-30' | Should -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'doc.archived' -Value '2026-07-30' | Should -BeNullOrEmpty
    }

    It 'checks the date FORMAT on a string, because the parse yields a string' {
        # YAML 1.2 core schema: 2026-07-30 arrives as text, not a date. The rule is a format check.
        Get-VaultFrontmatterRegistryViolation -Field 'doc.created' -Value '30-07-2026' | Should -Not -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'doc.created' -Value 'whenever' | Should -Not -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'doc.created' -Value '' | Should -Not -BeNullOrEmpty
    }

    It 'accepts doc.type as a character class rather than an enum' {
        # No authoritative type list exists to encode, so the shape is what is enforced - which is
        # also what keeps lint's set-doc-type remedy usable on the notes it exists to fix.
        Get-VaultFrontmatterRegistryViolation -Field 'doc.type' -Value 'Corpus' | Should -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'doc.type' -Value 'Some-Unseen-Type' | Should -BeNullOrEmpty
    }

    It 'refuses a doc.type outside the character class or over 20 characters' {
        Get-VaultFrontmatterRegistryViolation -Field 'doc.type' -Value 'Note 2' | Should -Not -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'doc.type' -Value "Note`nnav.contents: x" | Should -Not -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'doc.type' -Value ('A' * 21) | Should -Not -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'doc.type' -Value ('A' * 20) | Should -BeNullOrEmpty
    }

    It 'accepts any string for scope' {
        Get-VaultFrontmatterRegistryViolation -Field 'scope' -Value 'personal' | Should -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'scope' -Value "two`nlines" | Should -BeNullOrEmpty
    }

    It 'refuses a control character in any scalar value, on every key' {
        # The one content-policy refusal that survived the port: frontmatter values are agent-facing
        # text rendered into terminals and logs, and ANSI escapes are control characters.
        Get-VaultFrontmatterRegistryViolation -Field 'scope' -Value "personal`e[31m" | Should -Not -BeNullOrEmpty
    }

    It 'refuses a list or a map where a scalar key is expected' {
        Get-VaultFrontmatterRegistryViolation -Field 'doc.type' -Value @('a', 'b') | Should -Match 'found a list'
        Get-VaultFrontmatterRegistryViolation -Field 'scope' -Value ([ordered]@{ a = 1 }) | Should -Match 'found a mapping'
    }

    It 'applies no value pattern to an agentic key' {
        # Their gate is agentic:write and their shape is the schema validator's business.
        Get-VaultFrontmatterRegistryViolation -Field 'agent.instructions' -Value @('anything') | Should -BeNullOrEmpty
        Get-VaultFrontmatterRegistryViolation -Field 'nav.contents' -Value 'even a scalar' | Should -BeNullOrEmpty
    }
}

Describe 'Assert-VaultFrontmatterRegistry' {
    AfterEach { Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue }

    It 'passes a clean non-agentic tree without asserting a capability' {
        $Global:VaultMcpOpClasses = @('content:write')
        { Assert-VaultFrontmatterRegistry -Map ([ordered]@{ 'doc.created' = '2026-07-30'; 'doc.type' = 'Note' }) -Operation 'test' } |
            Should -Not -Throw
    }

    It 'asserts agentic:write when the tree carries an agentic key' {
        $Global:VaultMcpOpClasses = @('content:write')
        { Assert-VaultFrontmatterRegistry -Map ([ordered]@{ 'agent.instructions' = @('hint') }) -Operation 'test' } |
            Should -Throw '*insufficient_capability*'
    }

    It 'passes the same tree when the grant holds agentic:write' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        { Assert-VaultFrontmatterRegistry -Map ([ordered]@{ 'agent.instructions' = @('hint') }) -Operation 'test' } |
            Should -Not -Throw
    }

    It 'refuses an unrecognized key anywhere in the tree' {
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        $err = { Assert-VaultFrontmatterRegistry -Map ([ordered]@{ 'doc.type' = 'Note'; 'obsidianUIMode' = 'preview' }) -Operation 'test' } | Should -Throw -PassThru
        $obj = $err.Exception.Message | ConvertFrom-Json
        $obj.code | Should -Be 'malformed_op'
        $obj.data.field | Should -Be 'obsidianUIMode'
    }

    It 'refuses a smuggled agent.* key it does not recognize, rather than waving it through' {
        # The failure mode the allowlist inverts: the prefix scan classified anything it could not
        # see as non-agentic, so an unanticipated key was implicitly safe. Here it is neither.
        $Global:VaultMcpOpClasses = @('content:write', 'agentic:write')
        { Assert-VaultFrontmatterRegistry -Map ([ordered]@{ 'agent.obey' = @('do as I say') }) -Operation 'test' } |
            Should -Throw '*malformed_op*'
    }

    It 'accepts a $null map as nothing to validate' {
        { Assert-VaultFrontmatterRegistry -Map $null -Operation 'test' } | Should -Not -Throw
    }
}

Describe 'The registry against the corpus it was derived from' {
    BeforeAll {
        # Every frontmatter-bearing note in both repos, parsed once. This is the measurement the
        # table came from, re-run as an assertion: the registry is a claim ABOUT THIS VAULT, so it
        # is held to the vault rather than to the memory of whoever last edited the table.
        $script:CorpusRoots = @(
            (Join-Path $PSScriptRoot '../../../test-vault'),
            'C:\Repos\vault-primary'
        ) | Where-Object { Test-Path -LiteralPath $_ }

        $script:CorpusKeys = @{}
        $script:CorpusNotes = 0
        foreach ($root in $script:CorpusRoots) {
            Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.md -Force |
                Where-Object { $_.FullName -notlike '*\.git\*' } |
                ForEach-Object {
                    # Through the module's own read surface: ConvertFrom-Yaml lives in the module's
                    # session state, not the caller's, so a test cannot reach the parser directly.
                    $map = Get-VaultFrontmatterMap -Content ([System.IO.File]::ReadAllText($_.FullName))
                    if ($map.Count -eq 0) { return }
                    $script:CorpusNotes++
                    foreach ($k in @($map.Keys)) {
                        if (-not $script:CorpusKeys.ContainsKey("$k")) { $script:CorpusKeys["$k"] = (New-Object System.Collections.Generic.List[object]) }
                        $script:CorpusKeys["$k"].Add($map[$k])
                    }
                }
        }
    }

    It 'finds a corpus to check' {
        $script:CorpusNotes | Should -BeGreaterThan 100
    }

    It 'accepts every value a registry key actually carries in the corpus' {
        # A registry that would make a real note unwritable is a finding about the registry, not a
        # licence to widen it silently - so this failing is the signal, and the note is the answer.
        $bad = New-Object System.Collections.Generic.List[string]
        foreach ($key in @($script:CorpusKeys.Keys)) {
            if ($null -eq (Resolve-VaultFrontmatterRegistryKey -Field $key)) { continue }
            foreach ($v in $script:CorpusKeys[$key]) {
                $reason = Get-VaultFrontmatterRegistryViolation -Field $key -Value $v
                if ($reason) { $bad.Add("$key -> $reason") }
            }
        }
        # The only expected members are the two skill TEMPLATE placeholders, {{DATE}} and
        # {{Scope}}, which YAML reads as flow mappings. They live under .claude/skills, are inert
        # template sources rather than notes, and nothing routes a frontmatter op at them.
        @($bad | Sort-Object -Unique | Where-Object { $_ -notmatch 'found a mapping' }) | Should -BeNullOrEmpty
    }

    It 'leaves exactly the three deliberately-unowned key groups outside the registry' {
        $unknown = @(@($script:CorpusKeys.Keys) | Where-Object { $null -eq (Resolve-VaultFrontmatterRegistryKey -Field $_) } | Sort-Object)
        $unknown | Should -Be @('description', 'disable-model-invocation', 'name', 'obsidianUIMode', 'status', 'title')
    }
}
