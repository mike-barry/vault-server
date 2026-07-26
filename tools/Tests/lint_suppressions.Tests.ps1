# Pester suite for tools/lint_suppressions.ps1. Exercises the script directly (not through the
# HTTP dispatcher) against a scratch copy of test-vault/, mirroring create.Tests.ps1's setup.
# Run with: Invoke-Pester tools/Tests/lint_suppressions.Tests.ps1
#
# Lives in tools/Tests/ rather than directly beside the script: ScriptToolLoader scans
# tools/*.ps1 (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting directly
# in tools/ would itself be loaded as a (broken) tool.
#
# SCOPE NOTE: this suite covers CRUD on all three lists, the sectioned broken-wikilinks parser
# (global vs scoped namespaces, section creation on add, section-absence as not_found), the
# byte-faithful rewrite, the Tier-2 return and write-class envelope on every action, the error
# codes, the capability-denial three-way split for the lint:manage key, the deliberate absence
# of scope binding, cross-process lock_timeout, and standalone invocability with the JSON error
# shape on stderr.
#
# The state files are created by test setup rather than committed into test-vault/: they are
# vault state, and a committed copy would make the fixture's own suppressions part of every
# assertion.

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot '..' 'lint_suppressions.ps1'
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Invoke-SuppressionsError {
        # Runs lint_suppressions.ps1, expecting it to throw the shared {code, message, data?}
        # JSON error shape, and returns the parsed error object. Defined in this top-level
        # BeforeAll (rather than in a Describe body) so it survives into Pester v5's Run phase.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'lint_suppressions.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }
}

Describe 'lint_suppressions' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-lintsupp-pester-' + [guid]::NewGuid().ToString('N'))
        $script:VaultDir = Join-Path $script:TempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:VaultDir -Recurse

        $script:StateDir = Join-Path $script:VaultDir '.vault/state/vault-lint'
        New-Item -ItemType Directory -Path $script:StateDir -Force | Out-Null
        $script:BrokenFile = Join-Path $script:StateDir 'known-broken-wikilinks.txt'
        $script:SubfoldersFile = Join-Path $script:StateDir 'known-ignored-subfolders.txt'
        $script:ExceptionsFile = Join-Path $script:StateDir 'filename-exceptions.txt'

        # A sectioned fixture with every structural feature the parser has to carry through
        # untouched: a header comment, a trailing comment on an entry, blank lines, and two
        # [path:...] sections.
        $script:BrokenOriginal = "# ignore list header`nAlpha`nBeta   # placeholder`n`n[path:Areas/Vault-Dev/]`nGamma`n`n[path:Projects/Widget]`nDelta`n"
        [System.IO.File]::WriteAllText($script:BrokenFile, $script:BrokenOriginal, (New-Object System.Text.UTF8Encoding $false))
        [System.IO.File]::WriteAllText($script:SubfoldersFile, "# subfolders`nArchive/Old/Transcripts`n", (New-Object System.Text.UTF8Encoding $false))
        [System.IO.File]::WriteAllText($script:ExceptionsFile, "iCloud`nmd`n", (New-Object System.Text.UTF8Encoding $false))

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
        It 'returns a single well-formed JSON object for list' {
            $output = & $script:ScriptPath -Action 'list' -List 'filename-exceptions'
            { $output | ConvertFrom-Json } | Should -Not -Throw
            $result = $output | ConvertFrom-Json
            $result.list | Should -Be 'filename-exceptions'
            $result.entries.Count | Should -Be 2
            $result.entries[0].entry | Should -Be 'iCloud'
        }

        It 'emits the envelope on list too - one tool, one return convention' {
            $output = & $script:ScriptPath -Action 'list' -List 'filename-exceptions'
            $output | Should -Match '"capabilitiesUsed":\s*\[\]'
            $output | Should -Match '"filesSkippedUnreadable":\s*\[\]'
            ($output | ConvertFrom-Json).PSObject.Properties.Name | Should -Not -Contain 'lockWarning'
        }

        It 'returns the mutation summary shape with capabilitiesUsed naming lint:manage' {
            $result = & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'jQuery' | ConvertFrom-Json
            $result.list | Should -Be 'filename-exceptions'
            $result.action | Should -Be 'add'
            $result.entry | Should -Be 'jQuery'
            $result.capabilitiesUsed | Should -Be @('lint:manage')
            $result.file | Should -Be '.vault/state/vault-lint/filename-exceptions.txt'
        }

        It 'omits scope entirely on a mutation that targeted no section' {
            # Same posture lockWarning takes: an always-present null would read as "global" even
            # on a flat list, which has no sections at all.
            $result = & $script:ScriptPath -Action 'add' -List 'broken-wikilinks' -Entry 'Epsilon' | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Not -Contain 'scope'
        }

        It 'omits lockWarning when the lock was not abandoned' {
            $result = & $script:ScriptPath -Action 'add' -List 'ignored-subfolders' -Entry 'Resources/Calendar' | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Not -Contain 'lockWarning'
        }

        It 'emits filesSkippedUnreadable as [] on a mutation (structurally always empty)' {
            $output = & $script:ScriptPath -Action 'add' -List 'ignored-subfolders' -Entry 'Resources/Calendar'
            $output | Should -Match '"filesSkippedUnreadable":\s*\[\]'
        }

        It 'matches action and list case-insensitively after a trim' {
            $result = & $script:ScriptPath -Action '  LIST  ' -List ' Filename-Exceptions ' | ConvertFrom-Json
            $result.list | Should -Be 'filename-exceptions'
            $result.entries.Count | Should -Be 2
        }
    }

    Context 'CRUD on the flat lists' {
        It 'lists, adds, and removes on filename-exceptions' {
            & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'iOS' | Out-Null
            (& $script:ScriptPath -Action 'list' -List 'filename-exceptions' | ConvertFrom-Json).entries.entry |
                Should -Be @('iCloud', 'md', 'iOS')
            & $script:ScriptPath -Action 'remove' -List 'filename-exceptions' -Entry 'md' | Out-Null
            (& $script:ScriptPath -Action 'list' -List 'filename-exceptions' | ConvertFrom-Json).entries.entry |
                Should -Be @('iCloud', 'iOS')
        }

        It 'lists, adds, and removes on ignored-subfolders' {
            & $script:ScriptPath -Action 'add' -List 'ignored-subfolders' -Entry 'Resources/Calendar' | Out-Null
            (& $script:ScriptPath -Action 'list' -List 'ignored-subfolders' | ConvertFrom-Json).entries.entry |
                Should -Be @('Archive/Old/Transcripts', 'Resources/Calendar')
            & $script:ScriptPath -Action 'remove' -List 'ignored-subfolders' -Entry 'Archive/Old/Transcripts' | Out-Null
            (& $script:ScriptPath -Action 'list' -List 'ignored-subfolders' | ConvertFrom-Json).entries.entry |
                Should -Be @('Resources/Calendar')
        }

        It 'carries no scope field on a flat list''s entries' {
            $entries = (& $script:ScriptPath -Action 'list' -List 'ignored-subfolders' | ConvertFrom-Json).entries
            $entries[0].PSObject.Properties.Name | Should -Not -Contain 'scope'
        }

        It 'reads a list whose file does not exist yet as empty' {
            Remove-Item -LiteralPath $script:ExceptionsFile -Force
            $output = & $script:ScriptPath -Action 'list' -List 'filename-exceptions'
            $output | Should -Match '"entries":\s*\[\]'
        }

        It 'creates the state file (and its directory) on an add into a list with no file' {
            Remove-Item -LiteralPath $script:StateDir -Recurse -Force
            & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'iCloud' | Out-Null
            [System.IO.File]::ReadAllText($script:ExceptionsFile) | Should -BeExactly "iCloud`n"
        }

        It 'ignores comment and blank lines when listing' {
            [System.IO.File]::WriteAllText($script:ExceptionsFile, "# c1`n`n  # c2`niCloud   # trailing`n", (New-Object System.Text.UTF8Encoding $false))
            $entries = (& $script:ScriptPath -Action 'list' -List 'filename-exceptions' | ConvertFrom-Json).entries
            $entries.Count | Should -Be 1
            $entries[0].entry | Should -Be 'iCloud'
        }
    }

    Context 'the sectioned broken-wikilinks list' {
        It 'lists global and scoped entries with their scope' {
            $entries = (& $script:ScriptPath -Action 'list' -List 'broken-wikilinks' | ConvertFrom-Json).entries
            $entries.Count | Should -Be 4
            $entries[0].entry | Should -Be 'Alpha'
            $entries[0].scope | Should -BeNullOrEmpty
            ($entries | Where-Object { $_.entry -eq 'Gamma' }).scope | Should -Be 'Areas/Vault-Dev'
            ($entries | Where-Object { $_.entry -eq 'Delta' }).scope | Should -Be 'Projects/Widget'
        }

        It 'filters the listing to one section under scope_path, matched case-insensitively' {
            $entries = (& $script:ScriptPath -Action 'list' -List 'broken-wikilinks' -ScopePath 'areas/vault-dev/' | ConvertFrom-Json).entries
            $entries.Count | Should -Be 1
            $entries[0].entry | Should -Be 'Gamma'
        }

        It 'adds a global entry before the first section header, not into a section' {
            # An entry appended past a header would silently become part of that section.
            & $script:ScriptPath -Action 'add' -List 'broken-wikilinks' -Entry 'Epsilon' | Out-Null
            $entries = (& $script:ScriptPath -Action 'list' -List 'broken-wikilinks' | ConvertFrom-Json).entries
            ($entries | Where-Object { $_.entry -eq 'Epsilon' }).scope | Should -BeNullOrEmpty
        }

        It 'adds into an existing section and reports the scope' {
            $result = & $script:ScriptPath -Action 'add' -List 'broken-wikilinks' -Entry 'Zeta' -ScopePath 'Areas/Vault-Dev' | ConvertFrom-Json
            $result.scope | Should -Be 'Areas/Vault-Dev'
            $result.scopeCreated | Should -BeFalse
            $entries = (& $script:ScriptPath -Action 'list' -List 'broken-wikilinks' -ScopePath 'Areas/Vault-Dev' | ConvertFrom-Json).entries
            $entries.entry | Should -Be @('Gamma', 'Zeta')
        }

        It 'CREATES a section on an add naming one that does not exist' {
            # Decided and documented: there is no create-section action, so an add is the only
            # way a section comes into being. It is appended at the end of the file, which leaves
            # every existing section's contents where they are.
            $result = & $script:ScriptPath -Action 'add' -List 'broken-wikilinks' -Entry 'Eta' -ScopePath 'Inbox/' | ConvertFrom-Json
            $result.scope | Should -Be 'Inbox'
            $result.scopeCreated | Should -BeTrue
            [System.IO.File]::ReadAllText($script:BrokenFile) | Should -BeExactly ($script:BrokenOriginal + "[path:Inbox]`nEta`n")
        }

        It 'treats global and scoped namespaces as distinct for duplicates' {
            # Gamma already exists under [path:Areas/Vault-Dev/]; adding it globally suppresses a
            # different thing and is not a duplicate.
            & $script:ScriptPath -Action 'add' -List 'broken-wikilinks' -Entry 'Gamma' | Out-Null
            $entries = (& $script:ScriptPath -Action 'list' -List 'broken-wikilinks' | ConvertFrom-Json).entries
            @($entries | Where-Object { $_.entry -eq 'Gamma' }).Count | Should -Be 2
        }

        It 'errors already_exists on a duplicate within the SAME section' {
            $err = Invoke-SuppressionsError @{ Action = 'add'; List = 'broken-wikilinks'; Entry = 'Gamma'; ScopePath = 'Areas/Vault-Dev' }
            $err.code | Should -Be 'already_exists'
            $err.data.scopePath | Should -Be 'Areas/Vault-Dev'
            [System.IO.File]::ReadAllText($script:BrokenFile) | Should -BeExactly $script:BrokenOriginal
        }

        It 'errors already_exists on a global duplicate, matched case-insensitively' {
            $err = Invoke-SuppressionsError @{ Action = 'add'; List = 'broken-wikilinks'; Entry = 'alpha' }
            $err.code | Should -Be 'already_exists'
            $err.data.entry | Should -Be 'alpha'
        }

        It 'removes only from the targeted section' {
            & $script:ScriptPath -Action 'add' -List 'broken-wikilinks' -Entry 'Gamma' | Out-Null
            & $script:ScriptPath -Action 'remove' -List 'broken-wikilinks' -Entry 'Gamma' -ScopePath 'Areas/Vault-Dev' | Out-Null
            $entries = (& $script:ScriptPath -Action 'list' -List 'broken-wikilinks' | ConvertFrom-Json).entries
            @($entries | Where-Object { $_.entry -eq 'Gamma' }).Count | Should -Be 1
            ($entries | Where-Object { $_.entry -eq 'Gamma' }).scope | Should -BeNullOrEmpty
        }

        It 'errors not_found removing a global entry through a scope_path it is not in' {
            $err = Invoke-SuppressionsError @{ Action = 'remove'; List = 'broken-wikilinks'; Entry = 'Alpha'; ScopePath = 'Areas/Vault-Dev' }
            $err.code | Should -Be 'not_found'
            [System.IO.File]::ReadAllText($script:BrokenFile) | Should -BeExactly $script:BrokenOriginal
        }

        It 'errors not_found on a scope_path section that does not exist (list and remove)' {
            foreach ($action in @('list', 'remove')) {
                $params = @{ Action = $action; List = 'broken-wikilinks'; ScopePath = 'Nowhere' }
                if ($action -eq 'remove') { $params['Entry'] = 'Alpha' }
                $err = Invoke-SuppressionsError $params
                $err.code | Should -Be 'not_found'
                $err.data.scopePath | Should -Be 'Nowhere'
            }
        }
    }

    Context 'byte-faithful rewrite' {
        It 'leaves every untouched line exactly as it was, comments and blanks included' {
            & $script:ScriptPath -Action 'remove' -List 'broken-wikilinks' -Entry 'Delta' -ScopePath 'Projects/Widget' | Out-Null
            [System.IO.File]::ReadAllText($script:BrokenFile) |
                Should -BeExactly "# ignore list header`nAlpha`nBeta   # placeholder`n`n[path:Areas/Vault-Dev/]`nGamma`n`n[path:Projects/Widget]`n"
        }

        It 'removes an entry line carrying a trailing comment as one line' {
            & $script:ScriptPath -Action 'remove' -List 'broken-wikilinks' -Entry 'Beta' | Out-Null
            [System.IO.File]::ReadAllText($script:BrokenFile) | Should -Not -Match 'placeholder'
        }

        It 'preserves CRLF terminators and inserts a CRLF line into a CRLF file' {
            [System.IO.File]::WriteAllText($script:ExceptionsFile, "# c`r`niCloud`r`n", (New-Object System.Text.UTF8Encoding $false))
            & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'md' | Out-Null
            [System.IO.File]::ReadAllText($script:ExceptionsFile) | Should -BeExactly "# c`r`niCloud`r`nmd`r`n"
        }

        It 'terminates a file whose last line had no terminator before appending to it' {
            [System.IO.File]::WriteAllText($script:ExceptionsFile, 'iCloud', (New-Object System.Text.UTF8Encoding $false))
            & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'md' | Out-Null
            [System.IO.File]::ReadAllText($script:ExceptionsFile) | Should -BeExactly "iCloud`nmd`n"
        }

        It 'writes UTF-8 with no BOM' {
            & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'Caf' | Out-Null
            $bytes = [System.IO.File]::ReadAllBytes($script:ExceptionsFile)
            # No EF BB BF preamble.
            $bytes[0] | Should -Be 0x69
        }

        It 'strips a BOM rather than carrying it into the first entry' {
            # Reads accept a BOM; writes never emit one, so a BOM-carrying file is normalized on
            # the first mutation instead of the preamble riding into an entry value.
            [System.IO.File]::WriteAllText($script:ExceptionsFile, "iCloud`n", (New-Object System.Text.UTF8Encoding $true))
            (& $script:ScriptPath -Action 'list' -List 'filename-exceptions' | ConvertFrom-Json).entries[0].entry | Should -Be 'iCloud'
            & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'md' | Out-Null
            [System.IO.File]::ReadAllBytes($script:ExceptionsFile)[0] | Should -Be 0x69
        }
    }

    Context 'errors - argument validation' {
        It 'errors invalid_argument on an unrecognized action' {
            $err = Invoke-SuppressionsError @{ Action = 'update'; List = 'filename-exceptions'; Entry = 'x' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.action | Should -Be 'update'
        }

        It 'errors invalid_argument on an unrecognized list' {
            $err = Invoke-SuppressionsError @{ Action = 'list'; List = 'anchors' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.list | Should -Be 'anchors'
        }

        It 'errors invalid_argument on scope_path against a non-sectioned list' {
            foreach ($flat in @('ignored-subfolders', 'filename-exceptions')) {
                $err = Invoke-SuppressionsError @{ Action = 'add'; List = $flat; Entry = 'x'; ScopePath = 'Projects' }
                $err.code | Should -Be 'invalid_argument'
                $err.data.list | Should -Be $flat
            }
        }

        It 'errors invalid_argument on an empty scope_path' {
            $err = Invoke-SuppressionsError @{ Action = 'list'; List = 'broken-wikilinks'; ScopePath = '  /  ' }
            $err.code | Should -Be 'invalid_argument'
        }

        It 'errors invalid_argument when entry is missing for add or remove' {
            foreach ($action in @('add', 'remove')) {
                $err = Invoke-SuppressionsError @{ Action = $action; List = 'filename-exceptions' }
                $err.code | Should -Be 'invalid_argument'
                $err.data.param | Should -Be 'entry'
            }
        }

        It 'errors invalid_argument on a whitespace-only entry' {
            $err = Invoke-SuppressionsError @{ Action = 'add'; List = 'filename-exceptions'; Entry = '   ' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'entry'
        }

        It 'refuses entry as inert for action list rather than ignoring it' {
            $err = Invoke-SuppressionsError @{ Action = 'list'; List = 'filename-exceptions'; Entry = 'iCloud' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'entry'
        }

        It 'refuses an entry that would not round-trip through the file format' {
            foreach ($bad in @("a`nb", 'has # comment', '[path:Projects/X]')) {
                $err = Invoke-SuppressionsError @{ Action = 'add'; List = 'broken-wikilinks'; Entry = $bad }
                $err.code | Should -Be 'invalid_argument'
            }
            [System.IO.File]::ReadAllText($script:BrokenFile) | Should -BeExactly $script:BrokenOriginal
        }

        It 'errors not_found removing an entry that is not there' {
            $err = Invoke-SuppressionsError @{ Action = 'remove'; List = 'filename-exceptions'; Entry = 'nope' }
            $err.code | Should -Be 'not_found'
            $err.data.entry | Should -Be 'nope'
            [System.IO.File]::ReadAllText($script:ExceptionsFile) | Should -BeExactly "iCloud`nmd`n"
        }

        It 'errors not_found removing from a list whose file does not exist' {
            Remove-Item -LiteralPath $script:ExceptionsFile -Force
            $err = Invoke-SuppressionsError @{ Action = 'remove'; List = 'filename-exceptions'; Entry = 'iCloud' }
            $err.code | Should -Be 'not_found'
        }
    }

    Context 'capability denial (add/remove assert the lint:manage key)' {
        It 'denies an add with insufficient_capability and zero mutation' {
            $Global:VaultMcpOpClasses = @('content:read', 'content:write', 'agentic:write')
            $err = Invoke-SuppressionsError @{ Action = 'add'; List = 'filename-exceptions'; Entry = 'iOS' }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'lint:manage'
            $err.data.action | Should -Be 'add'
            # Validate-phase classification: the file is untouched.
            [System.IO.File]::ReadAllText($script:ExceptionsFile) | Should -BeExactly "iCloud`nmd`n"
        }

        It 'denies a remove for the same reason, with zero mutation' {
            $Global:VaultMcpOpClasses = @('content:write')
            $err = Invoke-SuppressionsError @{ Action = 'remove'; List = 'filename-exceptions'; Entry = 'iCloud' }
            $err.code | Should -Be 'insufficient_capability'
            [System.IO.File]::ReadAllText($script:ExceptionsFile) | Should -BeExactly "iCloud`nmd`n"
        }

        It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
            $Global:VaultMcpOpClasses = @()
            $err = Invoke-SuppressionsError @{ Action = 'add'; List = 'filename-exceptions'; Entry = 'iOS' }
            $err.code | Should -Be 'insufficient_capability'
        }

        It 'allows the mutation when the injected set holds lint:manage' {
            $Global:VaultMcpOpClasses = @('content:read', 'lint:manage')
            $result = & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'iOS' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('lint:manage')
        }

        It 'matches the key case-insensitively, as op-class names do everywhere' {
            $Global:VaultMcpOpClasses = @('LINT:MANAGE')
            $result = & $script:ScriptPath -Action 'add' -List 'filename-exceptions' -Entry 'iOS' | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Be @('lint:manage')
        }

        It 'requires no key for list, even with an injected-but-empty set' {
            $Global:VaultMcpOpClasses = @()
            $result = & $script:ScriptPath -Action 'list' -List 'filename-exceptions' | ConvertFrom-Json
            $result.entries.Count | Should -Be 2
            $result.capabilitiesUsed.Count | Should -Be 0
        }
    }

    Context 'not scope-bound' {
        It 'mutates under an ambient scope-roots map that would exclude the state file' {
            # Suppressions are vault state, not vault content: scope roots bind content-class
            # operations over notes, and gating suppression management by them would make
            # suppressions unmanageable exactly for the constrained agents they exist to serve.
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $result = & $script:ScriptPath -Action 'add' -List 'broken-wikilinks' -Entry 'Epsilon' | ConvertFrom-Json
            $result.entry | Should -Be 'Epsilon'
        }

        It 'lists under the same scope-roots map' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            (& $script:ScriptPath -Action 'list' -List 'broken-wikilinks' | ConvertFrom-Json).entries.Count | Should -Be 4
        }

        It 'accepts an entry naming a path outside the scope roots' {
            $Global:VaultMcpScopeRoots = @{ scratch = @('Projects') }
            $result = & $script:ScriptPath -Action 'add' -List 'ignored-subfolders' -Entry 'Areas/Outdoors/Rides' | ConvertFrom-Json
            $result.entry | Should -Be 'Areas/Outdoors/Rides'
        }
    }

    Context 'vault resolution' {
        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-SuppressionsError @{ Action = 'list'; List = 'filename-exceptions'; Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
            $err.data.vault | Should -Be 'nope'
        }

        It 'refuses a configured vault whose root is missing with vault_unavailable' {
            $configPath = Join-Path $script:TempRoot 'with-missing.json'
            @{ default_vault = 'scratch'; vaults = @{ scratch = $script:VaultDir; gone = (Join-Path $script:TempRoot 'no-such-root') } } |
                ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-SuppressionsError @{ Action = 'list'; List = 'filename-exceptions'; Vault = 'gone' }
            $err.code | Should -Be 'vault_unavailable'
        }
    }

    Context 'lock_timeout' {
        It 'fails lock_timeout when the write lock is held by another process' -Skip:(-not $IsWindows) {
            # The write lock cannot be contended in-process (the mutex is reentrant for the
            # calling thread), so a genuinely separate process must hold it - the cross-process
            # convention in Testing.md. The chokepoint raises the coded shape itself and this
            # tool does not catch it, so what this pins is that the code reaches the caller.
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
                    $err = Invoke-SuppressionsError @{ Action = 'add'; List = 'filename-exceptions'; Entry = 'iOS' }
                    $err.code | Should -Be 'lock_timeout'
                    $err.data.timeoutSeconds | Should -Be 1
                    [System.IO.File]::ReadAllText($script:ExceptionsFile) | Should -BeExactly "iCloud`nmd`n"
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

        It 'takes no lock for list - a held lock does not block a read' -Skip:(-not $IsWindows) {
            $modulePath = Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1'
            $configPath = $env:VAULT_MCP_VAULTS_JSON
            $signalPath = Join-Path $script:TempRoot 'lock-held-list.signal'

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
                (Test-Path -LiteralPath $signalPath) | Should -BeTrue

                $prevTimeout = $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS
                $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = '1'
                try {
                    (& $script:ScriptPath -Action 'list' -List 'filename-exceptions' | ConvertFrom-Json).entries.Count | Should -Be 2
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
        It 'declares the content:read floor in its .NOTES comment-help (the loader-parsed op class)' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*content:read\s*$'
        }

        It 'documents every parameter in comment-help, which the loader requires to load the tool' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            foreach ($p in @('Action', 'List', 'Entry', 'ScopePath', 'Vault')) {
                $scriptText | Should -Match "(?m)^\s*\.PARAMETER\s+$p\s*$"
            }
        }

        It 'declares no ValidateSet, so an unrecognized value is this tool''s own error' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Not -Match '\[ValidateSet\('
            $scriptText | Should -Match '(?ms)\.PARAMETER Action.*\blist\b.*\badd\b.*\bremove\b'
        }

        It 'registers lint:manage in op-classes.json, without which the floor-plus-key model cannot express it' {
            # The key this tool asserts must be a registered class: OpClassRegistry validates
            # every name a grant or a .NOTES floor names, and mint_token refuses a name absent
            # from the registry outright.
            $registry = Get-Content -LiteralPath (Join-Path $script:RepoRoot 'op-classes.json') -Raw | ConvertFrom-Json
            $row = $registry | Where-Object { $_.name -eq 'lint:manage' }
            $row | Should -Not -BeNullOrEmpty
            $row.description | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'lint_suppressions standalone invocability' {
    # Per Tool-Contracts's Tool Authoring Standards: every tool script must be invocable outside
    # the MCP server - pwsh -File against a real vault, no runspace context, no ambient scope, no
    # environment setup - and carry a unit test asserting it. This spawns a genuinely separate
    # pwsh process so no global left over from the host's ScriptToolRunner wrapper
    # (VaultMcpScopeRoots, VaultMcpOpClasses) or from another test in this same process could be
    # mistaken for injected host context.

    BeforeEach {
        $script:StandaloneTempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-lintsupp-standalone-' + [guid]::NewGuid().ToString('N'))
        $script:StandaloneVaultDir = Join-Path $script:StandaloneTempRoot 'scratch'
        Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'test-vault') -Destination $script:StandaloneVaultDir -Recurse

        $stateDir = Join-Path $script:StandaloneVaultDir '.vault/state/vault-lint'
        New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
        $script:StandaloneExceptionsFile = Join-Path $stateDir 'filename-exceptions.txt'
        [System.IO.File]::WriteAllText($script:StandaloneExceptionsFile, "iCloud`n", (New-Object System.Text.UTF8Encoding $false))

        $script:StandaloneConfigPath = Join-Path $script:StandaloneTempRoot 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $script:StandaloneVaultDir } } |
            ConvertTo-Json | Set-Content -LiteralPath $script:StandaloneConfigPath
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneTempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File lint_suppressions.ps1" invocation (the Tool Authoring Standard spelling)' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'lint_suppressions.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Action 'list' -List 'filename-exceptions'
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            ($rawOutput | ConvertFrom-Json).entries[0].entry | Should -Be 'iCloud'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'degrades OPEN on a mutation with no injected op-class set' {
        # The third state of the capability split, and the reason it needs a separate process: a
        # leftover global in the test runspace would make this pass vacuously.
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneConfigPath
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'lint_suppressions.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile -Action 'add' -List 'filename-exceptions' -Entry 'md'
            $LASTEXITCODE | Should -Be 0
            $result = $rawOutput | ConvertFrom-Json
            # The echo still names the class: capabilitiesUsed records what the operation needed,
            # not what a grant happened to be checked against.
            $result.capabilitiesUsed | Should -Be @('lint:manage')
            [System.IO.File]::ReadAllText($script:StandaloneExceptionsFile) | Should -BeExactly "iCloud`nmd`n"
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
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'lint_suppressions.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -Action 'add' -List 'filename-exceptions' -Entry 'iCloud' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"already_exists"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}
