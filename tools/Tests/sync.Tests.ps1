# Pester suite for tools/sync.ps1. Exercises the script directly (not through the HTTP dispatcher)
# against ISOLATED TEMP REPOSITORIES with temp bare "remotes" - never the vault's own worktree and
# never a real remote, following the vault-sync skill's unit-tests.ps1 template. Every fixture lives
# under a per-test temp base directory removed in AfterEach.
# Run with: Invoke-Pester tools/Tests/sync.Tests.ps1
#
# Lives in tools/Tests/ rather than beside sync.ps1: ScriptToolLoader scans tools/*.ps1
# (top-directory-only) to discover MCP tools, so a *.Tests.ps1 file sitting directly in tools/ would
# itself be loaded as a (broken) tool.
#
# SCOPE NOTE: the converge ladder rung by rung (detached reattach clean and dirty, mid-merge block,
# dirty-tree block, the gitlink-only advance under the floor and the mixed-tree pathspec-plus-block
# with a refreshed token, predicted-clean merge, predicted-conflict as a RESULT, remote-ahead
# fast-forward, local-ahead push), staged-change blocking on merge and preservation through a
# pathspec commit, the state token (mismatch carrying the fresh token, required-for-files-all),
# directive guards, the git:commit capability three-way split plus the incidental-versus-explicit
# denial split, offline fetch tolerance, cross-process lock contention and abandonment, standalone
# invocability, the git_failure payload at every throw site that can be provoked for real (plus a
# structural pin over all four, which is the only reach the merge-resolve site has), and the ported
# New-ConflictFilePath in isolation. Also DISPATCHES each sync-directive
# remedy back through sync, taken off a real result rather than hand-authored - the rule that payload
# shape assertions alone do not satisfy.

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'sync.ps1')).Path
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    # --- The remedy payload contract. EVERY parsed sync result in this file comes back through
    # ConvertFrom-SyncResult, so the contract Describe at the bottom walks every remedy this suite
    # can produce rather than a set some fixture was written to re-create. The error path
    # (Invoke-SyncError) keeps plain ConvertFrom-Json: a {code, message, data?} envelope carries no
    # remedies. ---
    . (Join-Path $PSScriptRoot 'RemedyContract.ps1')
    $global:SyncRemedySamples = [System.Collections.Generic.List[object]]::new()

    function ConvertFrom-SyncResult {
        # Parses sync's Tier-2 JSON and records every RemedyOption in it. Deliberately a pipeline
        # parser rather than an invocation wrapper: the suite drives sync with named arguments in
        # forty different combinations, so wrapping the CALL would have meant reshaping all of them,
        # while wrapping the PARSE reshapes nothing about how sync is exercised.
        param([Parameter(Mandatory, ValueFromPipeline)] [AllowEmptyString()] [string[]] $Json)
        begin { $lines = [System.Collections.Generic.List[string]]::new() }
        process { foreach ($line in $Json) { $lines.Add($line) } }
        end {
            $result = ($lines -join "`n") | ConvertFrom-Json
            foreach ($remedy in (Get-VaultRemedyOption -Node $result)) { $global:SyncRemedySamples.Add($remedy) }
            $result
        }
    }

    function Invoke-SyncError {
        # Runs sync.ps1 expecting the shared {code, message, data?} JSON error shape, and returns the
        # parsed error object. Defined in this top-level BeforeAll (rather than in a Describe body) so
        # it survives into Pester v5's Run phase.
        param([hashtable] $Params)
        $errorRecord = $null
        try {
            & $script:ScriptPath @Params | Out-Null
            throw 'sync.ps1 was expected to throw but did not.'
        }
        catch {
            $errorRecord = $_
        }
        $errorRecord.Exception.Message | ConvertFrom-Json
    }

    function Initialize-SyncGitRepo {
        param([Parameter(Mandatory)] [string] $Path, [switch] $Bare)
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
        if ($Bare) {
            git init -q --bare -b master $Path 2>&1 | Out-Null
            return
        }
        git init -q -b master $Path 2>&1 | Out-Null
        git -C $Path config user.email 'test@example.com' 2>&1 | Out-Null
        git -C $Path config user.name 'Test' 2>&1 | Out-Null
        git -C $Path config commit.gpgsign false 2>&1 | Out-Null
        git -C $Path config protocol.file.allow always 2>&1 | Out-Null
    }

    function Set-SyncRepoFile {
        param([Parameter(Mandatory)] [string] $Repo, [Parameter(Mandatory)] [string] $Rel, [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)
        $abs = Join-Path $Repo ($Rel -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        $dir = Split-Path -Parent $abs
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [System.IO.File]::WriteAllText($abs, $Content, (New-Object System.Text.UTF8Encoding $false))
    }

    function Add-SyncCommit {
        param([Parameter(Mandatory)] [string] $Repo, [Parameter(Mandatory)] [string] $Message)
        git -C $Repo add -A 2>&1 | Out-Null
        git -C $Repo commit -q -m $Message 2>&1 | Out-Null
    }

    function New-SyncVaultFixture {
        # A bare "remote" plus the vault working repo with one pushed commit, wired into a scratch
        # vaults.json. Returns @{ Remote; Vault; Config }.
        param([Parameter(Mandatory)] [string] $Base)
        $remote = Join-Path $Base 'remote.git'
        $vault = Join-Path $Base 'scratch'
        Initialize-SyncGitRepo -Path $remote -Bare
        Initialize-SyncGitRepo -Path $vault
        git -C $vault remote add origin $remote 2>&1 | Out-Null
        Set-SyncRepoFile -Repo $vault -Rel 'File.md' -Content "initial`n"
        Add-SyncCommit -Repo $vault -Message 'initial commit'
        git -C $vault push -q -u origin master 2>&1 | Out-Null

        $config = Join-Path $Base 'vaults.json'
        @{ default_vault = 'scratch'; vaults = @{ scratch = $vault } } | ConvertTo-Json | Set-Content -LiteralPath $config
        @{ Remote = $remote; Vault = $vault; Config = $config }
    }

    function New-SyncPeerClone {
        # A second working clone of the same remote, standing in for another machine.
        param([Parameter(Mandatory)] [string] $Base, [Parameter(Mandatory)] [string] $Remote, [string] $Name = 'peer')
        $peer = Join-Path $Base $Name
        git clone -q $Remote $peer 2>&1 | Out-Null
        git -C $peer config user.email 'peer@example.com' 2>&1 | Out-Null
        git -C $peer config user.name 'Peer' 2>&1 | Out-Null
        git -C $peer config commit.gpgsign false 2>&1 | Out-Null
        $peer
    }

    function Get-SyncHeadMessage {
        param([Parameter(Mandatory)] [string] $Repo, [int] $Index = 1)
        (git -C $Repo log -$Index --pretty=%B) -join "`n"
    }

    function ConvertTo-SyncDispatchValue {
        # Mirrors ScriptToolDispatcher.ConvertJsonElement: a JSON object becomes a dictionary, an
        # array stays an array, scalars pass through. A remedy read back off a real sync result is a
        # PSCustomObject tree; this puts it in the shape the dispatcher actually splats, so a
        # dispatch-through-sync test exercises the real call boundary rather than a hand-authored
        # approximation of it.
        param($Value)
        if ($null -eq $Value) { return $null }
        if ($Value -is [object[]]) {
            # Comma-wrapped so a ONE-element array survives the return: PowerShell enumerates a
            # function's output, and an unrolled commit.files would re-encode as a bare string, which
            # sync (correctly) refuses. The same guard the tool scripts' -NoEnumerate parses carry.
            return , @($Value | ForEach-Object { ConvertTo-SyncDispatchValue -Value $_ })
        }
        if ($Value.PSObject.TypeNames[0] -eq 'System.Management.Automation.PSCustomObject') {
            $map = @{}
            foreach ($prop in $Value.PSObject.Properties) {
                $map[$prop.Name] = ConvertTo-SyncDispatchValue -Value $prop.Value
            }
            return $map
        }
        $Value
    }

    function Get-SyncEncodingParameter {
        <#
        The parameters ScriptToolParameter.EncodesStructuredArgument is true for, READ OFF sync.ps1
        rather than listed here: a parameter whose .NET static type is [string] and whose `.SCHEMA`
        side-car declares a non-string, non-null top-level `type`. That is the whole encode signal,
        transcribed - the help block is located the same way ScriptToolLoader.HelpBlockRegex locates
        it, its sections split the same way SectionHeaderRegex splits them, and the type test is
        ScriptToolParameter.DeclaresNonStringType over the three shapes a `type` can take (a string,
        an array of them, or absent - and absent declares nothing, so it does not encode).

        Derived rather than hardcoded on purpose: a hardcoded @('commit') would keep encoding after
        someone deleted the block that authorizes it, so every dispatch test below would stay green
        while the real host had quietly stopped. Reading the signal makes the suite fail with the
        server.
        #>
        $help = [regex]::Match([System.IO.File]::ReadAllText($script:ScriptPath), '(?s)<#(.*?)#>').Groups[1].Value
        $headers = [regex]::Matches($help, '(?m)^[ \t]*\.(?<tag>[A-Za-z]+)\b[ \t]*(?<arg>\S+)?[ \t]*$')
        $declared = (Get-Command -Name $script:ScriptPath).Parameters
        $encoding = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt $headers.Count; $i++) {
            if ($headers[$i].Groups['tag'].Value -ne 'SCHEMA') { continue }
            $name = $headers[$i].Groups['arg'].Value
            if (-not $declared.ContainsKey($name) -or $declared[$name].ParameterType -ne [string]) { continue }
            $start = $headers[$i].Index + $headers[$i].Length
            $end = if ($i + 1 -lt $headers.Count) { $headers[$i + 1].Index } else { $help.Length }
            $schema = ConvertFrom-Json -InputObject $help.Substring($start, $end - $start).Trim()
            if ($schema.PSObject.Properties.Name -notcontains 'type') { continue }
            # Ordinal comparison (-cne), like the server's: JSON Schema's type vocabulary is
            # lowercase and case-sensitive, so "Array" is not the keyword.
            if (@(@($schema.type) | Where-Object { $_ -is [string] -and $_ -cne 'string' -and $_ -cne 'null' }).Count -gt 0) {
                $encoding.Add($name)
            }
        }
        $encoding.ToArray()
    }

    function ConvertTo-SyncDispatchArgument {
        <#
        WHAT STANDS IN FOR THE HOST, AND WHY IT IS HONEST.

        A remedy's `commit` rides as a real nested object, and the thing that turns it into the
        [string] $commit sync declares is the HOST, at the call boundary:
        ScriptToolArgumentEncoder.BindArgument, on the signal Get-SyncEncodingParameter reads. These
        tests drive sync.ps1 directly rather than through the dispatcher, so that one step has to be
        modelled here. This is a line-for-line transcription of BindArgument's two rules, and
        nothing more:

          1. A STRUCTURED arrival value (array or object) is serialized, EXACTLY AS IT ARRIVED, and
             only for a parameter that encodes. No reshaping, no filling in, no @() wrap - a payload
             the real dispatcher would send malformed has to reach sync malformed and earn its
             refusal.
          2. Everything else takes the plain conversion, including a value that arrives as a STRING
             on an encoding parameter - the encoder's guard against encoding an already-encoded
             payload twice.

        The two serializers differ only in escaping (System.Text.Json with relaxed escaping there,
        ConvertTo-Json here); both emit valid JSON that sync's own ConvertFrom-Json parses to the
        same payload, and neither changes the SHAPE, which is the whole subject of these tests.
        Depth 64 matches System.Text.Json's default maximum, so a payload this helper can encode is
        one the host can encode - it does not quietly truncate where the host would not.
        #>
        param([Parameter(Mandatory)] [string] $Name, $Value)
        $arrivedStructured = ($Value -is [object[]]) -or
            ($null -ne $Value -and $Value.PSObject.TypeNames[0] -eq 'System.Management.Automation.PSCustomObject')
        if (($script:SyncEncodingParameters -contains $Name) -and $arrivedStructured) {
            return (ConvertTo-Json -InputObject $Value -Depth 64 -Compress)
        }
        ConvertTo-SyncDispatchValue -Value $Value
    }

    function ConvertTo-SyncRemedySplat {
        # A remedy's params as the dispatcher would splat them: every top-level key bound through the
        # transcription above, so an encoding parameter's structured value arrives as JSON text and
        # every other key converts plainly. Nothing is filled in - what sync emitted is what the
        # second call receives.
        param([Parameter(Mandatory)] $Params)
        $splat = @{}
        foreach ($prop in $Params.PSObject.Properties) {
            $splat[$prop.Name] = ConvertTo-SyncDispatchArgument -Name $prop.Name -Value $prop.Value
        }
        $splat
    }

    function Resolve-SyncCommitRemedySplat {
        # A commit remedy's params made dispatchable: the caller fills the one dotted path `requires`
        # names (commit.message) IN THE STRUCTURED PAYLOAD and hands the whole thing to the dispatch
        # boundary. Encoding is emphatically not the caller's job - ConvertTo-SyncRemedySplat does
        # it because the host does it. Everything else (apply, commit.files, commit.staged,
        # state_token) is verbatim.
        param([Parameter(Mandatory)] $Params, [Parameter(Mandatory)] [string] $Message)
        $Params.commit.message = $Message
        ConvertTo-SyncRemedySplat -Params $Params
    }

    # Resolved once, here, so every dispatch below binds arguments the way the host would.
    $script:SyncEncodingParameters = Get-SyncEncodingParameter

    function Get-SyncBlockedResolution {
        param([Parameter(Mandatory)] $Result, [Parameter(Mandatory)] [string] $Reason)
        $blocked = @($Result.blocked | Where-Object { $_.reason -eq $Reason })
        $blocked.Count | Should -Be 1 -Because "the fixture must have driven sync into the $Reason blocked state"
        $blocked[0].resolution
    }
}

Describe 'sync' {
    BeforeEach {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-sync-pester-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:TempRoot | Out-Null
        $script:Fixture = New-SyncVaultFixture -Base $script:TempRoot
        $script:VaultDir = $script:Fixture.Vault
        $env:VAULT_MCP_VAULTS_JSON = $script:Fixture.Config
    }

    AfterEach {
        Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpScopeRoots -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name VaultMcpOpClasses -Scope Global -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'status mode (the default) and the return envelope' {
        It 'returns a well-formed Tier-2 object reporting a synced repo, with no mutation' {
            $output = & $script:ScriptPath
            { $output | ConvertFrom-Json } | Should -Not -Throw

            $result = $output | ConvertFrom-Json
            $result.synced | Should -BeTrue
            $result.repos.'.'.branch | Should -Be 'master'
            $result.repos.'.'.detached | Should -BeFalse
            $result.repos.'.'.ahead | Should -Be 0
            $result.repos.'.'.behind | Should -Be 0
            $result.actions.Count | Should -Be 0
            $result.plannedActions.Count | Should -Be 0
            $result.blocked.Count | Should -Be 0
            $result.stateToken | Should -Match '^[0-9a-f]{12}$'
        }

        It 'carries the write-class envelope, with lockWarning omitted when it did not fire' {
            $result = & $script:ScriptPath | ConvertFrom-SyncResult
            $names = $result.PSObject.Properties.Name
            $names | Should -Contain 'capabilitiesUsed'
            $names | Should -Contain 'filesSkippedUnreadable'
            $names | Should -Contain 'plannedActions'
            $names | Should -Not -Contain 'lockWarning'
            $result.capabilitiesUsed.Count | Should -Be 0
            # Always emitted and always empty: sync runs no enumerate-then-read loop over notes.
            $result.filesSkippedUnreadable.Count | Should -Be 0
        }

        It 'reports the three status buckets separately rather than as one uncommitted blob' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'New.md' -Content "new`n"
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Staged.md' -Content "staged`n"
            git -C $script:VaultDir add -- 'Staged.md' 2>&1 | Out-Null

            $result = & $script:ScriptPath | ConvertFrom-SyncResult
            $result.repos.'.'.unstaged | Should -Be @('File.md')
            $result.repos.'.'.untracked | Should -Be @('New.md')
            $result.repos.'.'.staged | Should -Be @('Staged.md')
        }

        It 'plans without mutating: a dirty tree yields plannedActions under a directive and no commit' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $result = & $script:ScriptPath -Commit '{"message":"Plan only","files":["File.md"]}' | ConvertFrom-SyncResult

            $result.plannedActions.Count | Should -Be 1
            $result.plannedActions[0].action | Should -Be 'commit'
            $result.actions.Count | Should -Be 0
            (git -C $script:VaultDir status --porcelain) | Should -Match 'File.md'
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
        }
    }

    Context 'ladder rung 1 - detached HEAD' {
        It 'reattaches a clean detached HEAD to the derived branch' {
            $first = (git -C $script:VaultDir rev-parse HEAD).Trim()
            git -C $script:VaultDir checkout -q $first 2>&1 | Out-Null
            (& $script:ScriptPath | ConvertFrom-SyncResult).repos.'.'.detached | Should -BeTrue

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'reattach'
            $result.repos.'.'.detached | Should -BeFalse
            $result.repos.'.'.branch | Should -Be 'master'
        }

        It 'blocks a DIRTY detached HEAD as detached_head_dirty rather than forcing it' {
            $first = (git -C $script:VaultDir rev-parse HEAD).Trim()
            git -C $script:VaultDir checkout -q $first 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "dirty while detached`n"

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $result.blocked.Count | Should -Be 1
            $result.blocked[0].reason | Should -Be 'detached_head_dirty'
            $result.blocked[0].resolution.action | Should -Be 'manual'
            # Reported, never forced: HEAD is still detached and the edit is still there.
            (git -C $script:VaultDir symbolic-ref -q --short HEAD) | Should -BeNullOrEmpty
        }
    }

    Context 'ladder rung 2 - mid-merge from a prior crash' {
        It 'blocks mid_merge with the unmerged file list and never adopts the merge' {
            $peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
            Set-SyncRepoFile -Repo $peer -Rel 'File.md' -Content "peer version`n"
            Add-SyncCommit -Repo $peer -Message 'peer edit'
            git -C $peer push -q 2>&1 | Out-Null

            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "local version`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local edit'
            git -C $script:VaultDir fetch -q 2>&1 | Out-Null
            git -C $script:VaultDir merge origin/master 2>&1 | Out-Null   # leaves a real merge state

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $result.repos.'.'.midMerge | Should -BeTrue
            $result.repos.'.'.unmergedFiles | Should -Contain 'File.md'
            @($result.blocked | ForEach-Object { $_.reason }) | Should -Contain 'mid_merge'
            # Still mid-merge: the tool does not adopt a crash's merge state.
            Test-Path -LiteralPath (Join-Path $script:VaultDir '.git/MERGE_HEAD') | Should -BeTrue
        }
    }

    Context 'ladder rung 4 - dirty tree and the commit directive' {
        It 'blocks a dirty tree as uncommitted_changes with a RemedyOption requiring commit.message' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult

            $blocked = @($result.blocked | Where-Object { $_.reason -eq 'uncommitted_changes' })
            $blocked.Count | Should -Be 1
            $blocked[0].resolution.action | Should -Be 'sync-directive'
            $blocked[0].resolution.requires | Should -Be @('commit.message')
            $blocked[0].resolution.params.commit.files | Should -Be @('File.md')
            $blocked[0].resolution.params.state_token | Should -Be $result.stateToken
        }

        It 'commits a named file set under a directive, appends the trailer, and pushes' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $token = (& $script:ScriptPath | ConvertFrom-SyncResult).stateToken

            $result = & $script:ScriptPath -Apply $true -Commit '{"message":"Record the change","files":["File.md"]}' -state_token $token | ConvertFrom-SyncResult
            $result.synced | Should -BeTrue
            @($result.actions | ForEach-Object { $_.action }) | Should -Be @('commit', 'push')
            $result.capabilitiesUsed | Should -Be @('git:commit')

            $message = Get-SyncHeadMessage -Repo $script:VaultDir
            $message | Should -Match 'Record the change'
            $message | Should -Match 'Generated-by: vault-sync'
            (git -C $script:Fixture.Remote log -1 --pretty=%s) | Should -Be 'Record the change'
        }

        It 'commits untracked files named in the directive (they are git added first)' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Inbox/New.md' -Content "brand new`n"
            $result = & $script:ScriptPath -Apply $true -Commit '{"message":"Add a note","files":["Inbox/New.md"]}' | ConvertFrom-SyncResult
            $result.synced | Should -BeTrue
            (git -C $script:VaultDir ls-files -- 'Inbox/New.md') | Should -Be 'Inbox/New.md'
        }
    }

    Context 'pre-existing staged changes' {
        It 'blocks staged_changes when a merge is required, naming the include remedy' {
            $peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
            Set-SyncRepoFile -Repo $peer -Rel 'Other.md' -Content "peer file`n"
            Add-SyncCommit -Repo $peer -Message 'peer commit'
            git -C $peer push -q 2>&1 | Out-Null

            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Foreign.md' -Content "staged by someone else`n"
            git -C $script:VaultDir add -- 'Foreign.md' 2>&1 | Out-Null

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $blocked = @($result.blocked | Where-Object { $_.reason -eq 'staged_changes' })
            $blocked.Count | Should -Be 1
            $blocked[0].resolution.params.commit.staged | Should -Be 'include'
            # Nothing merged, and the tool never unstages.
            (git -C $script:VaultDir status --porcelain) | Should -Match 'A  Foreign.md'
        }

        It 'preserves a foreign staged entry through a pathspec commit' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Foreign.md' -Content "staged by someone else`n"
            git -C $script:VaultDir add -- 'Foreign.md' 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "mine`n"

            $result = & $script:ScriptPath -Apply $true -Commit '{"message":"Only mine","files":["File.md"]}' | ConvertFrom-SyncResult
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'commit'

            # The commit holds File.md alone, and Foreign.md is still staged and uncommitted.
            (git -C $script:VaultDir show --name-only --pretty=format: HEAD).Trim() | Should -Be 'File.md'
            (git -C $script:VaultDir status --porcelain) | Should -Match 'A  Foreign.md'
        }

        It 'refuses to let files:"all" silently absorb a foreign staged entry' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Foreign.md' -Content "staged by someone else`n"
            git -C $script:VaultDir add -- 'Foreign.md' 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "mine`n"
            $token = (& $script:ScriptPath | ConvertFrom-SyncResult).stateToken

            $result = & $script:ScriptPath -Apply $true -Commit '{"message":"Sweep","files":"all"}' -state_token $token | ConvertFrom-SyncResult
            @($result.blocked | ForEach-Object { $_.reason }) | Should -Contain 'staged_changes'
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
        }

        It 'folds a foreign staged entry in when the directive says staged:"include"' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Foreign.md' -Content "staged by someone else`n"
            git -C $script:VaultDir add -- 'Foreign.md' 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "mine`n"
            $token = (& $script:ScriptPath | ConvertFrom-SyncResult).stateToken

            $result = & $script:ScriptPath -Apply $true -Commit '{"message":"Sweep","files":"all","staged":"include"}' -state_token $token | ConvertFrom-SyncResult
            $result.synced | Should -BeTrue
            $files = @((git -C $script:VaultDir show --name-only --pretty=format: HEAD) | Where-Object { $_ })
            $files | Should -Contain 'File.md'
            $files | Should -Contain 'Foreign.md'
        }
    }

    Context 'ladder rung 5 - divergence, prediction, and conflict as a RESULT' {
        BeforeEach {
            # Local and remote each gain a commit the other lacks.
            $script:Peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
        }

        It 'merges under the FLOOR when the probe predicts clean, asserting no key' {
            Set-SyncRepoFile -Repo $script:Peer -Rel 'Remote.md' -Content "remote only`n"
            Add-SyncCommit -Repo $script:Peer -Message 'remote commit'
            git -C $script:Peer push -q 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Local.md' -Content "local only`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local commit'

            # git:sync alone: the merge lands, and only the push is withheld.
            $Global:VaultMcpOpClasses = @('git:sync')
            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult

            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'merge'
            $result.capabilitiesUsed.Count | Should -Be 0
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'Generated-by: vault-sync'
            @($result.blocked | ForEach-Object { $_.reason }) | Should -Contain 'requires_capability'
        }

        It 'reports a predicted conflict as a blocked RESULT, never isError, with nothing merged' {
            Set-SyncRepoFile -Repo $script:Peer -Rel 'File.md' -Content "remote version`n"
            Add-SyncCommit -Repo $script:Peer -Message 'remote edit'
            git -C $script:Peer push -q 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "local version`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local edit'
            $headBefore = (git -C $script:VaultDir rev-parse HEAD).Trim()

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $blocked = @($result.blocked | Where-Object { $_.reason -eq 'merge_conflicts' })
            $blocked.Count | Should -Be 1
            $blocked[0].detail | Should -Match 'File.md'
            $blocked[0].resolution.params.on_conflict | Should -Be 'keep_local_save_remote'
            # Predicted read-only: HEAD unmoved, no merge state, clean tree.
            (git -C $script:VaultDir rev-parse HEAD).Trim() | Should -Be $headBefore
            $result.repos.'.'.midMerge | Should -BeFalse
            (git -C $script:VaultDir status --porcelain) | Should -BeNullOrEmpty
        }

        It 'resolves with keep_local_save_remote inside one span and exits clean' {
            Set-SyncRepoFile -Repo $script:Peer -Rel 'File.md' -Content "remote version`n"
            Add-SyncCommit -Repo $script:Peer -Message 'remote edit'
            git -C $script:Peer push -q 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "local version`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local edit'

            $result = & $script:ScriptPath -Apply $true -on_conflict 'keep_local_save_remote' | ConvertFrom-SyncResult
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'resolve-conflicts'
            $result.capabilitiesUsed | Should -Contain 'git:commit'

            # Local won; the remote body survives as a sibling; the repo is not mid-merge.
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'File.md')).Trim() | Should -Be 'local version'
            $siblings = @(Get-ChildItem -LiteralPath $script:VaultDir -Filter '*CONFLICT*')
            $siblings.Count | Should -Be 1
            [System.IO.File]::ReadAllText($siblings[0].FullName).Trim() | Should -Be 'remote version'
            $result.repos.'.'.midMerge | Should -BeFalse
            Test-Path -LiteralPath (Join-Path $script:VaultDir '.git/MERGE_HEAD') | Should -BeFalse

            $message = Get-SyncHeadMessage -Repo $script:VaultDir
            $message | Should -Match 'Kept local File.md'
            $message | Should -Match 'Generated-by: vault-sync'
        }
    }

    Context 'ladder rungs 6 and 7 - fast-forward and push' {
        It 'fast-forwards when the remote is ahead only' {
            $peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
            Set-SyncRepoFile -Repo $peer -Rel 'Other.md' -Content "peer file`n"
            Add-SyncCommit -Repo $peer -Message 'peer commit'
            git -C $peer push -q 2>&1 | Out-Null

            $plan = & $script:ScriptPath | ConvertFrom-SyncResult
            $plan.repos.'.'.behind | Should -Be 1
            @($plan.plannedActions | ForEach-Object { $_.action }) | Should -Contain 'fast-forward'

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'fast-forward'
            $result.synced | Should -BeTrue
            Test-Path -LiteralPath (Join-Path $script:VaultDir 'Other.md') | Should -BeTrue
        }

        It 'reports a rejected push as the retryable push_rejected blocked state' {
            # A pre-receive hook that declines stands in for the remote moving between the fetch and
            # the push: what is pinned is the classification of a rejected push, deterministically,
            # rather than a race that would make the test flaky.
            $hook = Join-Path $script:Fixture.Remote 'hooks/pre-receive'
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $hook) | Out-Null
            [System.IO.File]::WriteAllText($hook, "#!/bin/sh`nexit 1`n")

            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "local`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local commit'

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $blocked = @($result.blocked | Where-Object { $_.reason -eq 'push_rejected' })
            if ($blocked.Count -eq 0) {
                Set-ItResult -Skipped -Because 'this machine did not run the remote pre-receive hook, so a rejected push could not be constructed'
                return
            }
            $blocked[0].resolution.action | Should -Be 'retry'
            # Re-calling is the recovery path, so the remedy is dispatchable with no caller judgment.
            $blocked[0].resolution.requires.Count | Should -Be 0
        }
    }

    Context 'submodules - discovery, deepest-first ordering, and the gitlink carve-out' {
        BeforeEach {
            # A submodule with its own bare remote, added to the vault and pushed. Then the submodule
            # gains a pushed commit, leaving the PARENT's only delta as the gitlink entry.
            $script:SubRemote = Join-Path $script:TempRoot 'sub-remote.git'
            $script:SubSeed = Join-Path $script:TempRoot 'sub-seed'
            Initialize-SyncGitRepo -Path $script:SubRemote -Bare
            Initialize-SyncGitRepo -Path $script:SubSeed
            git -C $script:SubSeed remote add origin $script:SubRemote 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:SubSeed -Rel 'Sub.md' -Content "sub initial`n"
            Add-SyncCommit -Repo $script:SubSeed -Message 'sub initial'
            git -C $script:SubSeed push -q -u origin master 2>&1 | Out-Null

            git -C $script:VaultDir -c protocol.file.allow=always submodule add -q $script:SubRemote sub 2>&1 | Out-Null
            Add-SyncCommit -Repo $script:VaultDir -Message 'add submodule'
            git -C $script:VaultDir push -q 2>&1 | Out-Null

            $script:SubDir = Join-Path $script:VaultDir 'sub'
            git -C $script:SubDir config user.email 'test@example.com' 2>&1 | Out-Null
            git -C $script:SubDir config user.name 'Test' 2>&1 | Out-Null
            git -C $script:SubDir config commit.gpgsign false 2>&1 | Out-Null
            git -C $script:SubDir checkout -q master 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:SubDir -Rel 'Sub.md' -Content "sub moved on`n"
            Add-SyncCommit -Repo $script:SubDir -Message 'sub second'
            git -C $script:SubDir push -q 2>&1 | Out-Null
        }

        It 'discovers the submodule and keys the report by repo path' {
            $result = & $script:ScriptPath | ConvertFrom-SyncResult
            $result.repos.PSObject.Properties.Name | Should -Contain '.'
            $result.repos.PSObject.Properties.Name | Should -Contain 'sub'
        }

        It 'advances a GITLINK-ONLY dirty tree under the floor, with no git:commit key' {
            # The whole point of the carve-out: a git:sync-only agent completes a submodule sync.
            $Global:VaultMcpOpClasses = @('git:sync')
            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult

            $pointer = @($result.actions | Where-Object { $_.action -eq 'commit-submodule-pointer' })
            $pointer.Count | Should -Be 1
            $result.capabilitiesUsed.Count | Should -Be 0

            $message = Get-SyncHeadMessage -Repo $script:VaultDir
            $message | Should -Match 'Update sub submodule'
            $message | Should -Match 'Generated-by: vault-sync'
            # The pointer commit is the only thing the floor could do; publishing it needs the key.
            @($result.blocked | ForEach-Object { $_.reason }) | Should -Contain 'requires_capability'
        }

        It 'pathspec-commits the gitlink of a MIXED tree and blocks the remainder with a REFRESHED token' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "caller content`n"
            $tokenBefore = (& $script:ScriptPath | ConvertFrom-SyncResult).stateToken
            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult

            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'commit-submodule-pointer'
            (git -C $script:VaultDir show --name-only --pretty=format: HEAD).Trim() | Should -Be 'sub'

            $blocked = @($result.blocked | Where-Object { $_.reason -eq 'uncommitted_changes' -and $_.repo -eq '.' })
            $blocked.Count | Should -Be 1
            $blocked[0].resolution.params.commit.files | Should -Be @('File.md')
            $blocked[0].resolution.requires | Should -Be @('commit.message')
            # The tool's own pointer commit invalidated the caller's token, so the remedy carries the
            # REFRESHED one - not the token the caller was holding - and it matches the state the call
            # actually leaves behind, making the follow-up dispatchable verbatim.
            $blocked[0].resolution.params.state_token | Should -Not -Be $tokenBefore
            $blocked[0].resolution.params.state_token | Should -Be $result.stateToken
        }

        It 'reattaches a detached submodule HEAD, the sole tool-mediated fix for one' {
            $tip = (git -C $script:SubDir rev-parse HEAD).Trim()
            git -C $script:SubDir checkout -q $tip 2>&1 | Out-Null
            (& $script:ScriptPath | ConvertFrom-SyncResult).repos.sub.detached | Should -BeTrue

            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            @($result.actions | Where-Object { $_.repo -eq 'sub' -and $_.action -eq 'reattach' }) | Should -Not -BeNullOrEmpty
            $result.repos.sub.detached | Should -BeFalse
        }
    }

    Context 'state token' {
        It 'refuses a stale token with state_mismatch, carrying the fresh one, and mutates nothing' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $err = Invoke-SyncError @{ Apply = $true; Commit = '{"message":"m","files":["File.md"]}'; state_token = 'deadbeef0000' }

            $err.code | Should -Be 'state_mismatch'
            $err.data.stateToken | Should -Match '^[0-9a-f]{12}$'
            $err.data.stateToken | Should -Not -Be 'deadbeef0000'
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
        }

        It 'changes when another actor stages something between calls (the porcelain input)' {
            $before = (& $script:ScriptPath | ConvertFrom-SyncResult).stateToken
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Foreign.md' -Content "x`n"
            git -C $script:VaultDir add -- 'Foreign.md' 2>&1 | Out-Null
            (& $script:ScriptPath | ConvertFrom-SyncResult).stateToken | Should -Not -Be $before
        }

        It 'is required when commit.files is "all"' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $err = Invoke-SyncError @{ Apply = $true; Commit = '{"message":"m","files":"all"}' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'state_token'
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
        }

        It 'is optional with an explicit file array, which pins the set structurally' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $result = & $script:ScriptPath -Apply $true -Commit '{"message":"No token needed","files":["File.md"]}' | ConvertFrom-SyncResult
            $result.synced | Should -BeTrue
        }
    }

    Context 'remedy dispatch - every computed payload exercised against sync itself' {
        # Tool-Contracts' durable rule: a computed payload that NAMES another tool must be dispatched
        # against that tool in test - shape assertions do not substitute. sync's three sync-directive
        # remedies name sync, so each one is taken off a REAL result (never hand-authored: that is the
        # failure mode being retired) and splatted back in. Three breaks lived here undetected behind
        # payload-shape assertions alone: two closed with the snake_case rename, and the third - a
        # nested commit object that could not enter a [string] parameter - with the call-boundary
        # encoding these dispatches now model, key by key, through ConvertTo-SyncRemedySplat.

        It 'dispatches apply-conflict-strategy VERBATIM - it declares no requires, so it must be' {
            $peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
            Set-SyncRepoFile -Repo $peer -Rel 'File.md' -Content "remote version`n"
            Add-SyncCommit -Repo $peer -Message 'remote edit'
            git -C $peer push -q 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "local version`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local edit'

            $blockedResult = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $remedy = Get-SyncBlockedResolution -Result $blockedResult -Reason 'merge_conflicts'
            $remedy.action | Should -Be 'sync-directive'
            $remedy.requires.Count | Should -Be 0

            # Nothing is filled in and nothing is encoded - all three keys are scalars sync emitted.
            $splat = ConvertTo-SyncRemedySplat -Params $remedy.params
            @($splat.Keys | Sort-Object) | Should -Be @('apply', 'on_conflict', 'state_token')
            $result = & $script:ScriptPath @splat | ConvertFrom-SyncResult

            # The strategy the remedy advertised actually applied: local won, the remote body survives
            # as a sibling, and the repo is not left mid-merge.
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'resolve-conflicts'
            $result.capabilitiesUsed | Should -Contain 'git:commit'
            [System.IO.File]::ReadAllText((Join-Path $script:VaultDir 'File.md')).Trim() | Should -Be 'local version'
            @(Get-ChildItem -LiteralPath $script:VaultDir -Filter '*CONFLICT*').Count | Should -Be 1
            $result.repos.'.'.midMerge | Should -BeFalse
        }

        It 'gates apply-conflict-strategy on the token it carries - a moved remote is state_mismatch with nothing merged' {
            $peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
            Set-SyncRepoFile -Repo $peer -Rel 'File.md' -Content "remote version`n"
            Add-SyncCommit -Repo $peer -Message 'remote edit'
            git -C $peer push -q 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "local version`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local edit'

            $remedy = Get-SyncBlockedResolution -Result (& $script:ScriptPath -Apply $true | ConvertFrom-SyncResult) -Reason 'merge_conflicts'
            $splat = ConvertTo-SyncRemedySplat -Params $remedy.params

            # The remote moves after the strategy was authorized, so the carried token is now stale.
            Set-SyncRepoFile -Repo $peer -Rel 'Later.md' -Content "later remote`n"
            Add-SyncCommit -Repo $peer -Message 'later remote commit'
            git -C $peer push -q 2>&1 | Out-Null
            $headBefore = (git -C $script:VaultDir rev-parse HEAD).Trim()

            $err = Invoke-SyncError $splat
            $err.code | Should -Be 'state_mismatch'
            $err.data.suppliedToken | Should -Be $remedy.params.state_token
            $err.data.stateToken | Should -Not -Be $remedy.params.state_token
            # Zero mutation: HEAD unmoved, no merge state, no conflict sibling written.
            (git -C $script:VaultDir rev-parse HEAD).Trim() | Should -Be $headBefore
            Test-Path -LiteralPath (Join-Path $script:VaultDir '.git/MERGE_HEAD') | Should -BeFalse
            @(Get-ChildItem -LiteralPath $script:VaultDir -Filter '*CONFLICT*').Count | Should -Be 0
        }

        It 'accepts the state_token commit-changes emitted, with only commit.message filled in' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $blockedResult = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $remedy = Get-SyncBlockedResolution -Result $blockedResult -Reason 'uncommitted_changes'
            $remedy.requires | Should -Be @('commit.message')

            $splat = Resolve-SyncCommitRemedySplat -Params $remedy.params -Message 'Record the change'
            $splat['state_token'] | Should -Be $blockedResult.stateToken

            $result = & $script:ScriptPath @splat | ConvertFrom-SyncResult
            $result.synced | Should -BeTrue
            @($result.actions | ForEach-Object { $_.action }) | Should -Be @('commit', 'push')
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'Record the change'
            (git -C $script:VaultDir show --name-only --pretty=format: HEAD).Trim() | Should -Be 'File.md'
        }

        It 'refuses commit-changes'' own token once another actor stages something, mutating nothing' {
            # The gate that was silently BYPASSED before this branch: state_token did not bind, so the
            # argument was dropped and this call committed under a token that no longer described the
            # repo. The refusal is the whole reason the remedy carries a token at all.
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $remedy = Get-SyncBlockedResolution -Result (& $script:ScriptPath -Apply $true | ConvertFrom-SyncResult) -Reason 'uncommitted_changes'
            $splat = Resolve-SyncCommitRemedySplat -Params $remedy.params -Message 'Record the change'

            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Foreign.md' -Content "x`n"
            git -C $script:VaultDir add -- 'Foreign.md' 2>&1 | Out-Null

            $err = Invoke-SyncError $splat
            $err.code | Should -Be 'state_mismatch'
            $err.data.suppliedToken | Should -Be $remedy.params.state_token
            $err.data.stateToken | Should -Not -Be $remedy.params.state_token
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
            ((git -C $script:VaultDir status --porcelain) -join "`n") | Should -Match 'File.md'
        }

        It 'dispatches include-staged, folding the staged set the remedy named into the commit' {
            $peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
            Set-SyncRepoFile -Repo $peer -Rel 'Other.md' -Content "peer file`n"
            Add-SyncCommit -Repo $peer -Message 'peer commit'
            git -C $peer push -q 2>&1 | Out-Null

            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Foreign.md' -Content "staged by someone else`n"
            git -C $script:VaultDir add -- 'Foreign.md' 2>&1 | Out-Null

            $blockedResult = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $remedy = Get-SyncBlockedResolution -Result $blockedResult -Reason 'staged_changes'
            $remedy.params.commit.staged | Should -Be 'include'

            $splat = Resolve-SyncCommitRemedySplat -Params $remedy.params -Message 'Fold the index in'
            $splat['state_token'] | Should -Be $blockedResult.stateToken

            $result = & $script:ScriptPath @splat | ConvertFrom-SyncResult
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'commit'
            ((git -C $script:VaultDir log --pretty=%B) -join "`n") | Should -Match 'Fold the index in'
            (git -C $script:VaultDir ls-files -- 'Foreign.md') | Should -Be 'Foreign.md'
            # The staged entry the block named is folded in, so the merge it blocked ran on top.
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'merge'
            (git -C $script:VaultDir status --porcelain) | Should -BeNullOrEmpty
        }

        It 'carries a commit remedy''s NESTED commit object across the boundary as JSON text, and commits' {
            # sync's `commit` is a nested object inside a payload whose parameter is [string], so it
            # only reaches the script if the call boundary encodes it. The signal is the `.SCHEMA
            # commit` side-car declaring type "object" while the .NET type stays string; nothing
            # hand-lists this parameter, here or in the server. What the remedy emits is what gets
            # serialized - no reshaping between - which is why the encoded text is asserted verbatim.
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $remedy = Get-SyncBlockedResolution -Result (& $script:ScriptPath -Apply $true | ConvertFrom-SyncResult) -Reason 'uncommitted_changes'
            $remedy.params.commit.PSObject.TypeNames[0] | Should -Be 'System.Management.Automation.PSCustomObject'

            # Only the one path `requires` names is filled, and it is filled in the STRUCTURED payload.
            $remedy.params.commit.message = 'Record the change'
            $splat = ConvertTo-SyncRemedySplat -Params $remedy.params
            $splat['commit'] | Should -BeOfType ([string])
            $splat['commit'] | Should -Be '{"message":"Record the change","files":["File.md"]}'

            $result = & $script:ScriptPath @splat | ConvertFrom-SyncResult
            @($result.actions | ForEach-Object { $_.action }) | Should -Contain 'commit'
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'Record the change'
            (git -C $script:VaultDir show --name-only --pretty=format: HEAD).Trim() | Should -Be 'File.md'
        }

        It 'dispatches commit-changes over a multi-path set, committing exactly the files it named' {
            # The commit directive as a WHOLE, end to end: a mixed dirty tree (one tracked edit, one
            # untracked note) produces a remedy naming both, the caller fills commit.message and
            # nothing else, and the second call commits that exact set under that exact message.
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'Inbox/Second.md' -Content "new note`n"

            $blockedResult = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult
            $remedy = Get-SyncBlockedResolution -Result $blockedResult -Reason 'uncommitted_changes'
            $remedy.action | Should -Be 'sync-directive'
            $remedy.requires | Should -Be @('commit.message')
            @($remedy.params.commit.files | Sort-Object) | Should -Be @('File.md', 'Inbox/Second.md')

            $splat = Resolve-SyncCommitRemedySplat -Params $remedy.params -Message 'Record both notes'
            $splat['state_token'] | Should -Be $blockedResult.stateToken

            $result = & $script:ScriptPath @splat | ConvertFrom-SyncResult
            $result.synced | Should -BeTrue
            @($result.actions | ForEach-Object { $_.action }) | Should -Be @('commit', 'push')
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'Record both notes'
            @(((git -C $script:VaultDir show --name-only --pretty=format: HEAD) -join "`n").Trim() -split "`r?`n" | Sort-Object) |
                Should -Be @('File.md', 'Inbox/Second.md')
            (git -C $script:VaultDir status --porcelain) | Should -BeNullOrEmpty
        }
    }

    Context 'directive guards' {
        It 'refuses malformed commit JSON as malformed_op with a reason' {
            $err = Invoke-SyncError @{ Commit = '{not json' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'not valid JSON'
        }

        It 'refuses a commit payload that is a JSON array rather than an object' {
            $err = Invoke-SyncError @{ Commit = '[{"message":"m","files":["a"]}]' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'not a JSON object'
        }

        It 'refuses a commit directive with no message - wording is caller-side' {
            $err = Invoke-SyncError @{ Commit = '{"files":["File.md"]}' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'commit.message'
        }

        It 'refuses a commit directive with no files' {
            $err = Invoke-SyncError @{ Commit = '{"message":"m"}' }
            $err.code | Should -Be 'malformed_op'
            $err.data.reason | Should -Match 'commit.files'
        }

        It 'refuses a files string other than "all"' {
            $err = Invoke-SyncError @{ Commit = '{"message":"m","files":"everything"}' }
            $err.code | Should -Be 'malformed_op'
        }

        It 'refuses a staged value other than "include"' {
            $err = Invoke-SyncError @{ Commit = '{"message":"m","files":["a"],"staged":"absorb"}' }
            $err.code | Should -Be 'malformed_op'
        }

        It 'refuses an unrecognized on_conflict member as invalid_argument, not a binding error' {
            $err = Invoke-SyncError @{ on_conflict = 'take_remote' }
            $err.code | Should -Be 'invalid_argument'
            $err.data.param | Should -Be 'on_conflict'
        }

        It 'accepts the strategy name case-insensitively after a trim' {
            $result = & $script:ScriptPath -on_conflict '  Keep_Local_Save_Remote ' | ConvertFrom-SyncResult
            $result.synced | Should -BeTrue
        }
    }

    Context 'capability denial - the incidental-versus-explicit split' {
        It 'EXPLICIT: a commit directive without git:commit is insufficient_capability with zero mutation' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $Global:VaultMcpOpClasses = @('git:sync')
            $err = Invoke-SyncError @{ Apply = $true; Commit = '{"message":"m","files":["File.md"]}' }

            $err.code | Should -Be 'insufficient_capability'
            $err.data.requiredClass | Should -Be 'git:commit'
            $err.data.directive | Should -Be 'commit'
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
        }

        It 'EXPLICIT: an on_conflict directive without git:commit is insufficient_capability' {
            $Global:VaultMcpOpClasses = @('git:sync')
            $err = Invoke-SyncError @{ Apply = $true; on_conflict = 'keep_local_save_remote' }
            $err.code | Should -Be 'insufficient_capability'
            $err.data.directive | Should -Be 'on_conflict'
        }

        It 'INCIDENTAL: the same shortfall with no directive is a blocked entry, not an error' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $Global:VaultMcpOpClasses = @('git:sync')
            $result = & $script:ScriptPath -Apply $true | ConvertFrom-SyncResult

            $blocked = @($result.blocked | Where-Object { $_.reason -eq 'requires_capability' })
            $blocked.Count | Should -Be 1
            $blocked[0].resolution.action | Should -Be 'requires-capability'
            $blocked[0].resolution.params.required_class | Should -Be 'git:commit'
            # Data, not failure - and still zero mutation.
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
        }

        It 'DENIES on an injected-but-empty class set (authenticated "holds nothing")' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $Global:VaultMcpOpClasses = @()
            $err = Invoke-SyncError @{ Apply = $true; Commit = '{"message":"m","files":["File.md"]}' }
            $err.code | Should -Be 'insufficient_capability'
        }

        It 'allows the same directive when the injected set holds git:commit' {
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $Global:VaultMcpOpClasses = @('git:sync', 'git:commit')
            $result = & $script:ScriptPath -Apply $true -Commit '{"message":"Allowed","files":["File.md"]}' | ConvertFrom-SyncResult
            $result.capabilitiesUsed | Should -Contain 'git:commit'
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'Allowed'
        }
    }

    Context 'fetch failure tolerance' {
        It 'reports local state with a blocked entry when the remote is unreachable' {
            git -C $script:VaultDir remote set-url origin (Join-Path $script:TempRoot 'does-not-exist.git') 2>&1 | Out-Null

            $result = & $script:ScriptPath | ConvertFrom-SyncResult
            $blocked = @($result.blocked | Where-Object { $_.reason -in @('network_unavailable', 'fetch_failed') })
            if ($blocked.Count -eq 0) {
                Set-ItResult -Skipped -Because 'this git build did not fail the fetch against a missing local remote'
                return
            }
            $blocked[0].resolution.action | Should -Be 'retry'
            # Local state is still reported - the call did not fail.
            $result.repos.'.'.branch | Should -Be 'master'
            $result.stateToken | Should -Match '^[0-9a-f]{12}$'
        }
    }

    Context 'git_failure - genuinely unexpected git breakage, provoked for real' {
        # Every It here drives a REAL git invocation to a real non-zero exit and asserts the error
        # sync actually threw. Nothing is hand-built: a payload assertion over a constructed
        # hashtable would stay green while the throw site drifted, which is the failure this
        # Context exists to prevent. The fourth site (merge-resolve) carries no exitCode by
        # design and cannot be reached without a defect - its shape is pinned structurally in the
        # 'git_failure payload contract' Describe below.

        It 'carries {repo, operation, exitCode, output} from the canonical producer when a commit fails' {
            # A pre-commit hook that refuses is the cleanest real commit failure: git itself exits
            # non-zero on an operation sync considered authorized, which is exactly what
            # Assert-SyncGitOk is for.
            $hooks = (Join-Path $script:TempRoot 'hooks') -replace '\\', '/'
            New-Item -ItemType Directory -Path $hooks | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $hooks 'pre-commit'), "#!/bin/sh`nexit 1`n", (New-Object System.Text.UTF8Encoding $false))
            git -C $script:VaultDir config core.hooksPath $hooks 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"

            $err = Invoke-SyncError @{ Apply = $true; Commit = '{"message":"m","files":["File.md"]}' }

            $err.code | Should -Be 'git_failure'
            $err.data.PSObject.Properties.Name | Should -Contain 'repo'
            $err.data.PSObject.Properties.Name | Should -Contain 'operation'
            $err.data.PSObject.Properties.Name | Should -Contain 'exitCode'
            $err.data.PSObject.Properties.Name | Should -Contain 'output'
            $err.data.repo | Should -Be '.'
            $err.data.operation | Should -Be 'commit'
            $err.data.exitCode | Should -Not -Be 0
            # The commit really did not happen.
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
        }

        It 'carries exitCode when the merge fails with NO conflicted files and is aborted' {
            # Diverge with a real content conflict so the probe predicts one and on_conflict routes
            # into Resolve-SyncConflict, then plant a stale index.lock - a crashed git's leftover -
            # so the REAL merge dies before producing any conflicted file. That is precisely the
            # branch: non-zero merge, empty --diff-filter=U.
            $peer = New-SyncPeerClone -Base $script:TempRoot -Remote $script:Fixture.Remote
            Set-SyncRepoFile -Repo $peer -Rel 'File.md' -Content "remote version`n"
            Add-SyncCommit -Repo $peer -Message 'remote edit'
            git -C $peer push -q 2>&1 | Out-Null
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "local version`n"
            Add-SyncCommit -Repo $script:VaultDir -Message 'local edit'
            $headBefore = (git -C $script:VaultDir rev-parse HEAD).Trim()
            New-Item -ItemType File -Path (Join-Path $script:VaultDir '.git/index.lock') | Out-Null

            $err = Invoke-SyncError @{ Apply = $true; on_conflict = 'keep_local_save_remote' }

            $err.code | Should -Be 'git_failure'
            $err.data.operation | Should -Be 'merge'
            # Presence first: an ABSENT exitCode reads as $null, which passes a bare "not 0".
            $err.data.PSObject.Properties.Name | Should -Contain 'exitCode'
            $err.data.exitCode | Should -Not -Be 0
            $err.data.repo | Should -Be '.'
            $err.data.output | Should -Not -BeNullOrEmpty
            # Aborted, not left half-applied: HEAD unmoved and no merge state behind.
            (git -C $script:VaultDir rev-parse HEAD).Trim() | Should -Be $headBefore
            Test-Path -LiteralPath (Join-Path $script:VaultDir '.git/MERGE_HEAD') | Should -BeFalse
        }

        It 'carries exitCode when the merge-tree probe itself fails' {
            # Unrelated histories: the remote is force-replaced with a history sharing no merge
            # base, which leaves the repo genuinely diverged (ahead and behind both non-zero, so
            # rung 5 is reached) while merge-tree refuses outright with an exit above 1.
            $alien = Join-Path $script:TempRoot 'alien'
            Initialize-SyncGitRepo -Path $alien
            Set-SyncRepoFile -Repo $alien -Rel 'File.md' -Content "unrelated history`n"
            Add-SyncCommit -Repo $alien -Message 'alien root'
            git -C $alien push -q -f $script:Fixture.Remote 'master:master' 2>&1 | Out-Null

            $err = Invoke-SyncError @{ Apply = $true }

            $err.code | Should -Be 'git_failure'
            $err.data.operation | Should -Be 'merge-tree'
            $err.data.PSObject.Properties.Name | Should -Contain 'exitCode'
            $err.data.exitCode | Should -BeGreaterThan 1
            $err.data.repo | Should -Be '.'
            $err.data.output | Should -Match 'unrelated histories'
        }
    }

    Context 'vault resolution' {
        It 'refuses an unconfigured vault with unknown_vault' {
            $err = Invoke-SyncError @{ Vault = 'nope' }
            $err.code | Should -Be 'unknown_vault'
        }

        It 'refuses a vault that is not a git repository' {
            $plain = Join-Path $script:TempRoot 'plain'
            New-Item -ItemType Directory -Path $plain | Out-Null
            $configPath = Join-Path $script:TempRoot 'plain.json'
            @{ default_vault = 'plain'; vaults = @{ plain = $plain } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
            $env:VAULT_MCP_VAULTS_JSON = $configPath

            $err = Invoke-SyncError @{ }
            $err.code | Should -Be 'invalid_argument'
            $err.message | Should -Match 'not a git repository'
        }
    }

    Context 'lock behavior' {
        It 'fails lock_timeout when the write lock is held by another process' -Skip:(-not $IsWindows) {
            # The mutex is reentrant for the calling thread, so a genuinely separate process must hold
            # it - the cross-process convention in Testing.md. Each test builds its own vault root and
            # therefore its own mutex, so abandoned state cannot leak between tests.
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $modulePath = Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1'
            $signalPath = Join-Path $script:TempRoot 'lock-held.signal'

            $holder = Start-Job -ScriptBlock {
                param($ModulePath, $ConfigPath, $SignalPath)
                $env:VAULT_MCP_VAULTS_JSON = $ConfigPath
                Import-Module $ModulePath -Force
                Invoke-VaultWriteLock -Vault 'scratch' -ScriptBlock {
                    Set-Content -LiteralPath $SignalPath -Value 'held'
                    Start-Sleep -Seconds 30
                }
            } -ArgumentList $modulePath, $env:VAULT_MCP_VAULTS_JSON, $signalPath

            try {
                $deadline = (Get-Date).AddSeconds(20)
                while (-not (Test-Path -LiteralPath $signalPath) -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 100
                }
                (Test-Path -LiteralPath $signalPath) | Should -BeTrue -Because 'the background lock holder must have acquired before we contend'

                $prevTimeout = $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS
                $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = '1'
                try {
                    $err = Invoke-SyncError @{ Apply = $true; Commit = '{"message":"m","files":["File.md"]}' }
                    $err.code | Should -Be 'lock_timeout'
                    $err.data.timeoutSeconds | Should -Be 1
                    Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'initial commit'
                }
                finally {
                    if ($null -eq $prevTimeout) { Remove-Item Env:\VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS -ErrorAction SilentlyContinue }
                    else { $env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS = $prevTimeout }
                }
            }
            finally {
                Stop-Job $holder -ErrorAction SilentlyContinue
                Remove-Job $holder -Force -ErrorAction SilentlyContinue
            }
        }

        It 'reports lockWarning when it inherits a mutex abandoned by a dead process' -Skip:(-not $IsWindows) {
            # Abandoned is a one-shot signal the OS clears once a waiter consumes it, which is why this
            # test owns its vault root (and therefore its mutex) outright.
            Set-SyncRepoFile -Repo $script:VaultDir -Rel 'File.md' -Content "modified`n"
            $modulePath = Join-Path $script:RepoRoot 'powershell' 'VaultAccess' 'VaultAccess.psd1'
            $signalPath = Join-Path $script:TempRoot 'abandon.signal'
            $holderScript = Join-Path $script:TempRoot 'holder.ps1'

            # Start-Process rather than Start-Job, because the holder must be KILLED rather than
            # stopped: Stop-Job unwinds the pipeline and runs Invoke-VaultWriteLock's finally, which
            # releases the mutex cleanly and never produces the abandoned state under test.
            [System.IO.File]::WriteAllText($holderScript, @"
`$env:VAULT_MCP_VAULTS_JSON = '$($env:VAULT_MCP_VAULTS_JSON)'
Import-Module '$modulePath' -Force
Invoke-VaultWriteLock -Vault 'scratch' -ScriptBlock {
    Set-Content -LiteralPath '$signalPath' -Value 'held'
    Start-Sleep -Seconds 120
}
"@)
            $holder = Start-Process -FilePath (Get-Process -Id $PID).Path -PassThru -WindowStyle Hidden `
                -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $holderScript)

            $deadline = (Get-Date).AddSeconds(30)
            while (-not (Test-Path -LiteralPath $signalPath) -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 100
            }
            if (-not (Test-Path -LiteralPath $signalPath)) {
                Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
                Set-ItResult -Skipped -Because 'the background lock holder never signalled that it had acquired'
                return
            }

            # The waiter must already be CONTENDING when the holder dies. A named mutex with no other
            # open handle simply ceases to exist when its owner is killed, and the next process
            # creates a fresh one - so killing the holder first would produce no abandoned signal at
            # all. sync is therefore launched into the wait before the kill.
            $syncOut = Join-Path $script:TempRoot 'sync-out.json'
            $syncErr = Join-Path $script:TempRoot 'sync-err.txt'
            # Driven through a wrapper script rather than a command line, so the JSON directive is not
            # subject to cross-process argument quoting.
            # The wrapper writes the tool's own output straight to a file, so neither cross-process
            # argument quoting nor the console's redirect encoding can touch the JSON under test.
            $waiterScript = Join-Path $script:TempRoot 'waiter.ps1'
            [System.IO.File]::WriteAllText($waiterScript, @"
`$env:VAULT_MCP_VAULTS_JSON = '$($env:VAULT_MCP_VAULTS_JSON)'
try {
    `$out = & '$($script:ScriptPath)' -Apply `$true -Commit '{"message":"After abandonment","files":["File.md"]}' -WarningAction SilentlyContinue
    [System.IO.File]::WriteAllText('$syncOut', (`$out -join "``n"))
}
catch {
    [System.IO.File]::WriteAllText('$syncErr', `$_.Exception.Message)
}
"@)
            $waiter = Start-Process -FilePath (Get-Process -Id $PID).Path -PassThru -WindowStyle Hidden `
                -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $waiterScript)

            Start-Sleep -Seconds 6   # long enough for the waiter to reach its first lock span
            Stop-Process -Id $holder.Id -Force
            $waiter.WaitForExit(60000) | Out-Null

            $raw = if (Test-Path -LiteralPath $syncOut) { [System.IO.File]::ReadAllText($syncOut).Trim() } else { '' }
            if ([string]::IsNullOrWhiteSpace($raw)) {
                $detail = if (Test-Path -LiteralPath $syncErr) { [System.IO.File]::ReadAllText($syncErr) } else { '(no error recorded)' }
                Set-ItResult -Skipped -Because "the contending sync process produced no output: $detail"
                return
            }
            $result = $raw | ConvertFrom-Json
            $result.PSObject.Properties.Name | Should -Contain 'lockWarning'
            $result.lockWarning | Should -Match 'abandoned'
            # A loud warning on an otherwise-successful result, never a failure.
            Get-SyncHeadMessage -Repo $script:VaultDir | Should -Match 'After abandonment'
        }
    }

    Context 'op-class metadata declaration' {
        It 'declares the git:sync floor in its .NOTES comment-help (the loader-parsed op class)' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Match '(?m)^\s*OpClass:\s*git:sync\s*$'
        }

        It 'documents every parameter in comment-help, which the loader requires to load the tool' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            foreach ($p in @('apply', 'commit', 'on_conflict', 'state_token', 'vault')) {
                $scriptText | Should -Match "(?m)^\s*\.PARAMETER\s+$p\s*$"
            }
        }

        It 'declares no ValidateSet on on_conflict, so an unrecognized value is sync''s own error' {
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Not -Match '\[ValidateSet\('
            $scriptText | Should -Match '(?ms)\.PARAMETER on_conflict.*keep_local_save_remote'
        }

        It 'exposes no test-only injection hook on the MCP surface' {
            # The skill script's -VaultRootOverride does not survive the port: tool tests inject a
            # scratch vaults.json through VAULT_MCP_VAULTS_JSON, the same seam every other tool uses.
            $scriptText = Get-Content -LiteralPath $script:ScriptPath -Raw
            $scriptText | Should -Not -Match 'VaultRootOverride'
        }
    }
}

Describe 'sync - New-ConflictFilePath (ported verbatim, tested in isolation)' {
    # The function is lifted out of sync.ps1 by AST rather than dot-sourcing the script, which would
    # run the whole tool. These are the skill suite's own cases, carried over unchanged.
    BeforeAll {
        $scriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'sync.ps1')).Path
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)
        $fn = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-ConflictFilePath'
            }, $true)
        $fn | Should -Not -BeNullOrEmpty
        . ([scriptblock]::Create($fn.Extent.Text))
        $script:FixedNow = [datetime]'2026-07-17 14:32:00'
    }

    It 'builds the sibling name for a file with no directory' {
        New-ConflictFilePath -RelPath 'Foo.md' -Now $script:FixedNow | Should -Be 'Foo [CONFLICT 20260717 1432].md'
    }

    It 'preserves forward slashes in a nested path' {
        New-ConflictFilePath -RelPath 'Areas/Foo/Bar.md' -Now $script:FixedNow | Should -Be 'Areas/Foo/Bar [CONFLICT 20260717 1432].md'
    }

    It 'normalizes a backslash directory to forward slashes' {
        New-ConflictFilePath -RelPath 'Areas\Foo\Bar.md' -Now $script:FixedNow | Should -Be 'Areas/Foo/Bar [CONFLICT 20260717 1432].md'
    }

    It 'preserves a non-markdown extension' {
        New-ConflictFilePath -RelPath 'Assets/diagram.png' -Now $script:FixedNow | Should -Be 'Assets/diagram [CONFLICT 20260717 1432].png'
    }
}

Describe 'sync - the git_failure payload contract over every throw site' {
    # A SOURCE-LEVEL pin, deliberately, and it does not stand in for a behavioral test: three of the
    # four sites are provoked for real in the 'git_failure' Context above. What no behavioral test in
    # this suite can reach is the merge-resolve site - a should-never-happen guard downstream of a
    # resolution loop that clears every conflicted path, so provoking it needs a defect rather than a
    # fixture. Its deliberate OMISSION of exitCode is the field most likely to be "fixed" by a
    # contributor reading it as an oversight, so it is pinned here structurally over the AST (the
    # same lift-by-AST convention the New-ConflictFilePath Describe uses) rather than by a text
    # regex that would drift with formatting.
    #
    # The rule: git_failure carries {repo, operation, exitCode, output} wherever `operation` names a
    # single git invocation, and omits exitCode for the synthetic 'merge-resolve' - whose git diff
    # SUCCEEDED, so any exit code there would be fabricated.

    BeforeAll {
        $scriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'sync.ps1')).Path
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)

        $script:GitFailureSites = @(
            $ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'New-VaultToolError'
                }, $true) | ForEach-Object {
                $elements = $_.CommandElements
                $code = $null
                $data = $null
                for ($i = 0; $i -lt $elements.Count - 1; $i++) {
                    $element = $elements[$i]
                    if ($element -isnot [System.Management.Automation.Language.CommandParameterAst]) { continue }
                    if ($element.ParameterName -eq 'Code') { $code = $elements[$i + 1].Extent.Text.Trim("'", '"') }
                    if ($element.ParameterName -eq 'Data') { $data = $elements[$i + 1] }
                }
                if ($code -ne 'git_failure') { return }

                $hashtable = $data.Find({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)
                $keys = @($hashtable.KeyValuePairs | ForEach-Object { $_.Item1.Extent.Text.Trim("'", '"') })
                $operationPair = $hashtable.KeyValuePairs | Where-Object { $_.Item1.Extent.Text.Trim("'", '"') -eq 'operation' }
                [pscustomobject]@{
                    Line      = $_.Extent.StartLineNumber
                    Keys      = $keys
                    Operation = $operationPair.Item2.Extent.Text.Trim("'", '"')
                }
            }
        )
    }

    It 'finds every git_failure throw site in the script' {
        # The count is asserted HERE so the corpus may state it: a documented number with no test
        # behind it is the shape that goes stale.
        $script:GitFailureSites.Count | Should -Be 4
    }

    It 'carries exactly {repo, operation, exitCode, output} at every site naming one git invocation' {
        $sites = @($script:GitFailureSites | Where-Object { $_.Operation -ne 'merge-resolve' })
        $sites.Count | Should -Be 3
        foreach ($site in $sites) {
            ($site.Keys | Sort-Object) -join ',' | Should -Be 'exitCode,operation,output,repo' -Because "the site at line $($site.Line) names a single git invocation"
        }
    }

    It 'OMITS exitCode at the synthetic merge-resolve operation - deliberately, not an oversight' {
        $sites = @($script:GitFailureSites | Where-Object { $_.Operation -eq 'merge-resolve' })
        $sites.Count | Should -Be 1
        $sites[0].Keys | Should -Not -Contain 'exitCode' -Because 'the git diff it reports SUCCEEDED, so any exit code would be fabricated - do not "fix" this for symmetry'
        ($sites[0].Keys | Sort-Object) -join ',' | Should -Be 'operation,output,repo'
    }
}

Describe 'sync standalone invocability' {
    # Per Tool-Contracts's Tool Authoring Standards: every tool script must be invocable outside the
    # MCP server - pwsh -File against a real vault, no runspace context, no ambient scope, no
    # environment setup - and carry a unit test asserting it. These spawn a genuinely separate pwsh
    # process, so no global left over from the host's ScriptToolRunner wrapper or from another test in
    # this process could be mistaken for injected host context.

    BeforeEach {
        $script:StandaloneRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-sync-standalone-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:StandaloneRoot | Out-Null
        $script:StandaloneFixture = New-SyncVaultFixture -Base $script:StandaloneRoot
    }

    AfterEach {
        Remove-Item -LiteralPath $script:StandaloneRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'runs via a literal "pwsh -File sync.ps1" invocation (the Tool Authoring Standard spelling)' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneFixture.Config
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'sync.ps1')).Path
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile
            $LASTEXITCODE | Should -Be 0
            { $rawOutput | ConvertFrom-Json } | Should -Not -Throw
            ($rawOutput | ConvertFrom-Json).synced | Should -BeTrue
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'degrades OPEN on an explicit commit directive with no injected op-class set' {
        # The third state of the capability split, and the reason it needs a separate process: a
        # leftover global in the test runspace would make this pass vacuously.
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneFixture.Config
        try {
            $vault = $script:StandaloneFixture.Vault
            [System.IO.File]::WriteAllText((Join-Path $vault 'File.md'), "standalone edit`n")
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'sync.ps1')).Path
            # "-Apply:$true" rather than "-Apply $true": under pwsh -File every argument arrives as a
            # STRING, and PowerShell refuses string-to-bool for a typed parameter unless the value is
            # attached with a colon. Recorded in the Sync corpus doc as a standalone-invocation nuance.
            $rawOutput = & $psExe -NoProfile -NonInteractive -File $scriptFile '-Apply:$true' -Commit '{"message":"Standalone commit","files":["File.md"]}'
            $LASTEXITCODE | Should -Be 0
            $result = $rawOutput | ConvertFrom-Json
            $result.capabilitiesUsed | Should -Contain 'git:commit'
            ((git -C $vault log -1 --pretty=%B) -join "`n") | Should -Match 'Standalone commit'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }

    It 'surfaces the shared JSON error shape on stderr under pwsh -File' {
        $psExe = (Get-Process -Id $PID).Path
        $prev = $env:VAULT_MCP_VAULTS_JSON
        $env:VAULT_MCP_VAULTS_JSON = $script:StandaloneFixture.Config
        try {
            $scriptFile = (Resolve-Path (Join-Path $PSScriptRoot '..' 'sync.ps1')).Path
            $stderr = & $psExe -NoProfile -NonInteractive -File $scriptFile -on_conflict 'take_remote' 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $stderr | Should -Match '"code"\s*:\s*"invalid_argument"'
        }
        finally {
            if ($null -ne $prev) { $env:VAULT_MCP_VAULTS_JSON = $prev } else { Remove-Item Env:\VAULT_MCP_VAULTS_JSON -ErrorAction SilentlyContinue }
        }
    }
}

Describe 'sync remedy payload contract' {
    # LAST IN THE FILE ON PURPOSE. Pester runs Describes in file order, so by the time this one
    # starts, $global:SyncRemedySamples holds every remedy every ConvertFrom-SyncResult above
    # produced. The enumeration is therefore what sync EMITTED, not a list somebody wrote down.
    #
    # Add-SyncAction's {repo, action, detail} entries under actions[] / plannedActions[] are NOT in
    # this walk. They are excluded structurally - they carry no id, no label and no params - never
    # by field name and never by container name; see Get-VaultRemedyOption in RemedyContract.ps1.
    BeforeAll {
        $script:ToolsDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

        # The SPEC half. sync's re-call remedies name sync itself: their params ARE sync's own wire
        # names, and retry's {apply} is a real sync payload rather than advice. $null is "advisory,
        # dispatches nothing" - a real classification, not a missing one.
        $script:SyncToolContract = @{
            'sync-directive'      = 'sync'
            'retry'               = 'sync'
            'requires-capability' = $null
            'manual'              = $null
        }
        $script:SyncSites = @(Get-VaultRemedyConstructionSite -Path $script:ScriptPath -Builder 'New-SyncRemedy')
    }

    It 'passes -Tool at every New-SyncRemedy construction site' {
        $script:SyncSites.Count | Should -BeGreaterThan 0
        @($script:SyncSites | Where-Object { -not $_.HasTool } | ForEach-Object { "sync.ps1:$($_.Line)" }) |
            Should -BeNullOrEmpty -Because 'every remedy construction site must state its dispatch target'
    }

    It 'classifies every action sync can construct' {
        $constructible = @($script:SyncSites | ForEach-Object { $_.Action } | Where-Object { $_ } | Sort-Object -Unique)
        ($constructible -join ',') | Should -Be ((@($script:SyncToolContract.Keys) | Sort-Object) -join ',') `
            -Because 'a new remedy action must be classified as dispatching or advisory, not silently unclassified'
    }

    It 'emits every action it can construct, so the walk is not partial' {
        # The anti-vacuity guard. A walker that quietly found three remedies would satisfy every
        # per-remedy check below; it cannot satisfy this one.
        $emitted = @($global:SyncRemedySamples | ForEach-Object { $_.action } | Sort-Object -Unique)
        $constructible = @($script:SyncSites | ForEach-Object { $_.Action } | Where-Object { $_ } | Sort-Object -Unique)
        ($emitted -join ',') | Should -Be ($constructible -join ',')
    }

    It 'holds the RemedyOption contract on every remedy the suite emitted' {
        $samples = @($global:SyncRemedySamples)
        $samples.Count | Should -BeGreaterThan 0
        Write-Host "  sync remedy resolutions validated: $($samples.Count) across $((@($samples | ForEach-Object { $_.action } | Sort-Object -Unique)).Count) actions"
        $violations = @($samples | ForEach-Object {
                Test-VaultRemedyContract -Remedy $_ -ToolContract $script:SyncToolContract -ToolsDir $script:ToolsDir
            })
        $violations | Should -BeNullOrEmpty
    }

    It 'leaves Add-SyncAction entries out of the walk entirely' {
        # The second, unrelated `action` vocabulary. These report what the tool did or will do, so
        # they are not RemedyOptions, gain no `tool` field, and must never be validated as payloads.
        $entries = @($global:SyncRemedySamples | Where-Object {
                @($_.PSObject.Properties.Name) -contains 'repo' -and @($_.PSObject.Properties.Name) -contains 'detail' -and
                @($_.PSObject.Properties.Name) -notcontains 'params'
            })
        $entries | Should -BeNullOrEmpty
        @($global:SyncRemedySamples | ForEach-Object { $_.action }) |
            Should -Not -Contain 'commit' -Because 'commit is an Add-SyncAction verb, not a remedy action'
        @($global:SyncRemedySamples | ForEach-Object { $_.action }) |
            Should -Not -Contain 'fast-forward' -Because 'fast-forward is an Add-SyncAction verb, not a remedy action'
    }
}
