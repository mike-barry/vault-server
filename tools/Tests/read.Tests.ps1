# Pester suite for tools/read.ps1. Exercises the script directly (not through the HTTP
# dispatcher) against a scratch copy of test-vault/, mirroring move.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/read.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside read.ps1: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting
# directly in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: this suite covers the Tier-1 return across the part enum (full /
# frontmatter / body / sha, including the fence-less and empty-note cases, body's
# verbatim-after-fence contract, and sha's whole-file-bytes contract), path normalization
# and the no-.md-inference rule, the
# not_found / kind_mismatch / invalid_argument / out_of_scope / read_failure error codes,
# the content:read op-class metadata declaration (dispatcher floor-gating itself is proven
# generically C#-side), and standalone invocability.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'read.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-ReadError {
        # Runs read.ps1, expecting it to throw the shared {code, message, data?} JSON error
        # shape, and returns the parsed error object for assertions. Defined in this
        # top-level BeforeAll (rather than inline in the Describe body) so it survives into
        # Pester v5's Run phase - a bare `function` statement in a Describe body only
        # executes during Discovery and would not be visible from It blocks.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'read.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }
}

Describe 'read' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-read-pester-' + [guid]::NewGuid().ToString('N'))
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

    Context 'Tier-1 return - part enum' {
        It 'returns the full note content by default' {
            $content = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' | Out-String
            $content | Should -Match 'nav\.contents:'
            $content | Should -Match '# Widget\.md - Sample Project for Fixture Testing'
        }

        It 'returns raw text, not JSON (read is a Tier-1 native-format cognate)' {
            $content = & $script:ScriptPath -Path 'Resources/Gizmo-Reference.md' | Out-String
            $content | Should -Match 'quorvium alloy'
            $content.TrimStart() | Should -Not -Match '^\{'
        }

        It 'accepts part full explicitly, matching the default' {
            $default = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' | Out-String
            $explicit = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'full' | Out-String
            $explicit | Should -Be $default
        }

        It 'returns only the frontmatter block with part frontmatter' {
            $content = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'frontmatter' | Out-String
            $content | Should -Match 'nav\.contents:'
            $content | Should -Not -Match '# Widget\.md'
            # Raw block text between the fences - the fences themselves are not included.
            $content | Should -Not -Match '(?m)^---'
        }

        It 'returns an empty result for part frontmatter on a note with no frontmatter' {
            $content = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' -Part 'frontmatter' | Out-String
            $content.Trim() | Should -Be ''
        }

        It 'returns everything after the closing fence, verbatim, with part body' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Body-Case.md'
            # Byte-precise fixture: known frontmatter, known body (leading blank line kept).
            [System.IO.File]::WriteAllText($notePath, "---`ndoc.type: Test`n---`n`n# Body-Case`nline two`n")
            $body = & $script:ScriptPath -Path 'Inbox/Body-Case.md' -Part 'body'
            $body | Should -Be "`n# Body-Case`nline two`n"
        }

        It 'omits the frontmatter block from part body on a real fixture note' {
            $content = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'body' | Out-String
            $content | Should -Not -Match 'nav\.contents:'
            $content | Should -Match '# Widget\.md - Sample Project for Fixture Testing'
        }

        It 'returns the whole content with part body on a note with no frontmatter' {
            $full = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' | Out-String
            $body = & $script:ScriptPath -Path 'Projects/Widget/Tasks.md' -Part 'body' | Out-String
            $body | Should -Be $full
        }

        # An EMPTY frontmatter block - a "---" line immediately closed by another, with no
        # inner line at all - is a real shape, not a curiosity:
        # Remove-VaultUseSkillAgentInstructions leaves exactly it behind when it strips the last
        # entry from a note whose only key was agent.instructions. A zero-line block is still a
        # block, so part "body" is what follows it and not the whole file with its fences: `edit`
        # matches its patch against the substrate's body facet, and a part "body" reading that
        # disagreed would hand a caller a patch anchor resolving to patch_target_not_found.
        It 'returns the body, not the whole file, for an empty frontmatter block' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Empty-Fence.md'
            [System.IO.File]::WriteAllText($notePath, "---`n---`nBody")
            $body = & $script:ScriptPath -Path 'Inbox/Empty-Fence.md' -Part 'body'
            $body | Should -Be 'Body'
        }

        It 'returns an empty result for part frontmatter on an empty frontmatter block' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Empty-Fence-Fm.md'
            [System.IO.File]::WriteAllText($notePath, "---`n---`nBody")
            $frontmatter = & $script:ScriptPath -Path 'Inbox/Empty-Fence-Fm.md' -Part 'frontmatter'
            $frontmatter | Should -Be ''
        }

        It 'returns the body after an empty frontmatter block under CRLF endings' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Empty-Fence-Crlf.md'
            [System.IO.File]::WriteAllText($notePath, "---`r`n---`r`n# Note`r`nline two`r`n")
            $body = & $script:ScriptPath -Path 'Inbox/Empty-Fence-Crlf.md' -Part 'body'
            $body | Should -Be "# Note`r`nline two`r`n"
        }

        It 'returns an empty body for an empty frontmatter block with nothing after it' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Empty-Fence-Only.md'
            [System.IO.File]::WriteAllText($notePath, "---`n---`n")
            $body = & $script:ScriptPath -Path 'Inbox/Empty-Fence-Only.md' -Part 'body' | Out-String
            $body.Trim() | Should -Be ''
        }

        It 'agrees with the substrate body facet edit patches against, for an empty fence' {
            # Pins the coupling directly: part body is where a caller takes a patch anchor from,
            # and edit resolves that anchor against Split-VaultNoteContent's body.
            Import-Module (Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force
            $content = "---`n---`n# Note`nbody line`n"
            $notePath = Join-Path $script:VaultDir 'Inbox/Empty-Fence-Parity.md'
            [System.IO.File]::WriteAllText($notePath, $content)
            $body = & $script:ScriptPath -Path 'Inbox/Empty-Fence-Parity.md' -Part 'body'
            $body | Should -BeExactly (Split-VaultNoteContent -Content $content).Body
        }

        It 'matches part values case-insensitively' {
            $content = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'Frontmatter' | Out-String
            $content | Should -Match 'nav\.contents:'
        }

        It 'rejects an unrecognized part as invalid_argument' {
            $err = Invoke-ReadError @{ Path = 'Projects/Widget/Widget.md'; Part = 'banana' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.part | Should -Be 'banana'
        }

        It 'returns an empty result for an empty note' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/Empty-Note.md') -Value $null
            $content = & $script:ScriptPath -Path 'Inbox/Empty-Note.md' | Out-String
            $content.Trim() | Should -Be ''
        }
    }

    Context 'Tier-1 return - part sha' {
        # sha is the staleness token edit consumes as expected_sha: a caller reads the sha,
        # reads the content, then hands the sha back. The digest therefore covers the whole
        # file's bytes on disk, never the slice a caller happened to read.
        It 'returns the SHA-256 of the file bytes as lowercase 64-char hex for a known fixture' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Sha-Case.md'
            # Byte-precise fixture (WriteAllText is UTF-8 with no BOM), so the expected digest
            # is a literal constant rather than one recomputed by the same code under test.
            [System.IO.File]::WriteAllText($notePath, "---`ndoc.type: Test`n---`n`n# Sha-Case`nline two`n")
            $sha = (& $script:ScriptPath -Path 'Inbox/Sha-Case.md' -Part 'sha' | Out-String).Trim()
            $sha | Should -Be '395b96389461e3349dc411539f5c54228bdccc9b5ec833c2accbd7afd67c3f63'
            $sha | Should -Match '^[0-9a-f]{64}$'
        }

        It 'returns the SHA-256 of an empty note (no special-casing of empty content)' {
            [System.IO.File]::WriteAllText((Join-Path $script:VaultDir 'Inbox/Empty-Sha.md'), '')
            $sha = (& $script:ScriptPath -Path 'Inbox/Empty-Sha.md' -Part 'sha' | Out-String).Trim()
            $sha | Should -Be 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
        }

        It 'matches the digest of the raw bytes on disk for a real fixture note' {
            $expected = [System.BitConverter]::ToString(
                [System.Security.Cryptography.SHA256]::HashData(
                    [System.IO.File]::ReadAllBytes((Join-Path $script:VaultDir 'Projects/Widget/Widget.md')))).Replace('-', '').ToLowerInvariant()
            $sha = (& $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'sha' | Out-String).Trim()
            $sha | Should -Be $expected
        }

        It 'returns the same digest on a no-op re-read' {
            $first = (& $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'sha' | Out-String).Trim()
            $second = (& $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'sha' | Out-String).Trim()
            $second | Should -Be $first
        }

        It 'changes when the body changes' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Sha-Body-Change.md'
            [System.IO.File]::WriteAllText($notePath, "---`ndoc.type: Test`n---`n`nbody one`n")
            $before = (& $script:ScriptPath -Path 'Inbox/Sha-Body-Change.md' -Part 'sha' | Out-String).Trim()
            [System.IO.File]::WriteAllText($notePath, "---`ndoc.type: Test`n---`n`nbody two`n")
            $after = (& $script:ScriptPath -Path 'Inbox/Sha-Body-Change.md' -Part 'sha' | Out-String).Trim()
            $after | Should -Not -Be $before
        }

        It 'changes when only the frontmatter changes (the digest covers the whole file)' {
            $notePath = Join-Path $script:VaultDir 'Inbox/Sha-Frontmatter-Change.md'
            [System.IO.File]::WriteAllText($notePath, "---`ndoc.type: Test`n---`n`nunchanged body`n")
            $before = (& $script:ScriptPath -Path 'Inbox/Sha-Frontmatter-Change.md' -Part 'sha' | Out-String).Trim()
            $bodyBefore = & $script:ScriptPath -Path 'Inbox/Sha-Frontmatter-Change.md' -Part 'body' | Out-String

            [System.IO.File]::WriteAllText($notePath, "---`ndoc.type: Test`ndoc.created: 2026-07-25`n---`n`nunchanged body`n")
            $after = (& $script:ScriptPath -Path 'Inbox/Sha-Frontmatter-Change.md' -Part 'sha' | Out-String).Trim()
            $bodyAfter = & $script:ScriptPath -Path 'Inbox/Sha-Frontmatter-Change.md' -Part 'body' | Out-String

            $bodyAfter | Should -Be $bodyBefore
            $after | Should -Not -Be $before
        }

        It 'matches part sha case-insensitively' {
            $lower = (& $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part 'sha' | Out-String).Trim()
            $mixed = (& $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Part ' SHA ' | Out-String).Trim()
            $mixed | Should -Be $lower
        }

        It 'names sha among the accepted values when rejecting an unrecognized part' {
            $err = Invoke-ReadError @{ Path = 'Projects/Widget/Widget.md'; Part = 'banana' }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match "'sha'"
        }
    }

    Context 'path normalization' {
        It 'folds backslashes and trims a leading slash' {
            $content = & $script:ScriptPath -Path '\Projects\Widget\Widget.md' | Out-String
            $content | Should -Match '# Widget\.md - Sample Project for Fixture Testing'
        }

        It 'does not infer .md: a bare extensionless path is a miss, not a resolution' {
            $err = Invoke-ReadError @{ Path = 'Projects/Widget/Widget' }
            $err.code | Should -Be 'not_found'
        }
    }

    Context 'errors' {
        It 'errors not_found on a missing note (operate-on tools require exactly one target)' {
            $err = Invoke-ReadError @{ Path = 'Inbox/Nonexistent-Note.md' }
            $err.code | Should -Be 'not_found'
            $err.data.path | Should -Be 'Inbox/Nonexistent-Note.md'
        }

        It 'rejects a folder target as kind_mismatch (read is note-semantic)' {
            $err = Invoke-ReadError @{ Path = 'Projects/Widget' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.requiredKind | Should -Be 'note'
            $err.data.resolvedKind | Should -Be 'folder'
        }

        It 'rejects an existing non-md file as kind_mismatch' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/attachment.txt') -Value 'not a note'
            $err = Invoke-ReadError @{ Path = 'Inbox/attachment.txt' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'file'
        }

        It 'rejects the dropped Kind parameter as a binding error (the assertion is retired)' {
            { & $script:ScriptPath -Path 'Projects/Widget/Widget.md' -Kind 'note' 2>$null } |
                Should -Throw
        }

        It 'errors invalid_argument on a whitespace path' {
            $err = Invoke-ReadError @{ Path = '   ' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument on a vault-escaping path' {
            $err = Invoke-ReadError @{ Path = '../outside.md' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors read_failure (not not_found) when the note exists but is locked' {
            $lockedPath = Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md'
            $handle = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                $err = Invoke-ReadError @{ Path = 'Resources/Gizmo-Reference.md' }
                $err.code | Should -Be 'read_failure'
            }
            finally {
                $handle.Dispose()
            }
        }

        # part sha reads the file as bytes rather than through Read-VaultNote, so it carries
        # its own copy of the error surface: the codes must be the ones every other part
        # raises, not a second vocabulary.
        It 'errors not_found for part sha on a missing note' {
            $err = Invoke-ReadError @{ Path = 'Inbox/Nonexistent-Note.md'; Part = 'sha' }
            $err.code | Should -Be 'not_found'
            $err.data.path | Should -Be 'Inbox/Nonexistent-Note.md'
        }

        It 'rejects a folder target as kind_mismatch for part sha' {
            $err = Invoke-ReadError @{ Path = 'Projects/Widget'; Part = 'sha' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'folder'
        }

        It 'rejects an existing non-md file as kind_mismatch for part sha' {
            Set-Content -LiteralPath (Join-Path $script:VaultDir 'Inbox/attachment.txt') -Value 'not a note'
            $err = Invoke-ReadError @{ Path = 'Inbox/attachment.txt'; Part = 'sha' }
            $err.code | Should -Be 'kind_mismatch'
            $err.data.resolvedKind | Should -Be 'file'
        }

        It 'errors read_failure for part sha when the note exists but is locked' {
            $lockedPath = Join-Path $script:VaultDir 'Resources/Gizmo-Reference.md'
            $handle = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
            try {
                $err = Invoke-ReadError @{ Path = 'Resources/Gizmo-Reference.md'; Part = 'sha' }
                $err.code | Should -Be 'read_failure'
                $err.data.path | Should -Be 'Resources/Gizmo-Reference.md'
            }
            finally {
                $handle.Dispose()
            }
        }

        It 'errors out_of_scope for part sha before any file access' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-ReadError @{ Path = 'Resources/Gizmo-Reference.md'; Part = 'sha' }
            $err.code | Should -Be 'out_of_scope'
        }
    }

    Context 'vault resolution' {
        # Both failure modes carry a structured code rather than the module's plain text
        # (Plan-Vault-Tool-Primitives: Error Convention), so the refusal is machine-routable
        # in a standalone run as well as under the dispatcher.
        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-ReadError @{ Path = 'Projects/Widget/Widget.md'; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
            $err.data.vault | Should -Be 'nope'
        }

        It 'refuses a configured vault whose root is missing with vault_unavailable' {
            $configPath = Join-Path $script:TempRoot 'with-missing.json'
            @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir; gone = (Join-Path $script:TempRoot 'no-such-root') } } |
                ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-ReadError @{ Path = 'Projects/Widget/Widget.md'; Vault = 'gone' }
            $err.code | Should -Be 'vault_unavailable'
            $err.data.reason | Should -Match 'does not exist'
        }
    }

    Context 'scope' {
        It 'errors out_of_scope when the path falls outside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $err = Invoke-ReadError @{ Path = 'Resources/Gizmo-Reference.md' }
            $err.code | Should -Be 'out_of_scope'
            $err.data.path | Should -Be 'Resources/Gizmo-Reference.md'
        }

        It 'reads normally when the path falls inside the ambient scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $content = & $script:ScriptPath -Path 'Projects/Widget/Widget.md' | Out-String
            $content | Should -Match '# Widget\.md'
        }
    }

    Context 'op class' {
        It 'declares the content:read floor in its .NOTES comment-help (the loader-parsed op class)' {
            # Dispatcher floor-gating itself is enforced C#-side and is generically proven by
            # the xUnit dispatcher test that refuses a tool to a grant lacking its class; read
            # inherits that uniform grant.Allows(tool.OpClass) check. What IS assertable
            # PowerShell-side is read's own metadata declaration - the OpClass line
            # ScriptToolLoader reads to derive the floor.
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*content:read\s*$'
        }
    }
}

Describe 'read standalone invocability' {
    # Per Plan-Vault-Tool-Primitives's Tool Authoring Standards: every tool script must be
    # invocable outside the MCP server - pwsh -File against a real vault, no runspace
    # context, no ambient scope, no environment setup - and carry a unit test asserting it.
    # This spawns a genuinely separate pwsh process (rather than dot-sourcing/calling the
    # script in this Pester session) so no global left over from the host's
    # ScriptToolRunner wrapper (VaultMcpScopeRoots, VaultMcpOpClasses) or from another test
    # in this same process could be mistaken for injected host context.

    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-read-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } | ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File read.ps1" invocation (the Tool Authoring Standard spelling)' {
        # A fresh pwsh process has none of ScriptToolRunner's injected globals; the vault
        # config passes through the child's inherited environment, which is what "no
        # environment setup" means for a scratch vault - a real standalone run would use the
        # module's own default-path fallback (module-relative vaults.json).
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'read.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Projects/Widget/Widget.md' | Out-String
            $LASTEXITCODE | Should -Be 0
            $rawOutput | Should -Match '# Widget\.md - Sample Project for Fixture Testing'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'returns part sha under a literal "pwsh -File read.ps1" invocation' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'read.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Projects/Widget/Widget.md' -Part 'sha' | Out-String
            $LASTEXITCODE | Should -Be 0
            $expected = [System.BitConverter]::ToString(
                [System.Security.Cryptography.SHA256]::HashData(
                    [System.IO.File]::ReadAllBytes((Join-Path $script:StandaloneVaultDir 'Projects/Widget/Widget.md')))).Replace('-', '').ToLowerInvariant()
            $rawOutput.Trim() | Should -Be $expected
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
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'read.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Path 'Inbox/Nonexistent-Note.md' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"not_found"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}
