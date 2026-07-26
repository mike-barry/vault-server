<#
.SYNOPSIS
Converges the vault's git repositories - the parent and every discovered submodule - toward
synced-and-clean, or reports exactly what blocks that. Status is the default and the routine
call: it fetches, assesses, and returns plannedActions plus a state token with no effect on any
working tree. Nothing mutates without apply:true. Judgment stays caller-side and enters as
directives (commit wording, conflict strategy); sequencing is the tool's, because sequencing is
deterministic given repo state. See the Sync corpus doc (vault-primary) for the full contract.

The tool never returns a repository mid-merge: a conflict is predicted read-only with
merge-tree --write-tree before any merge starts, and an authorized strategy runs the real merge
and its resolution inside ONE lock span that exits clean.
.PARAMETER Apply
Mutation opt-in, default FALSE - assessment is the routine call here and converging is the
deliberate one. False assesses and reports (plannedActions) with no effect beyond a fetch, which
touches remote-tracking refs only.
.PARAMETER Commit
Authorization to commit agent-authored working-tree changes, as a JSON string object (the derived
MCP schema cannot express a typed object), deserialized and validated in-script:
  {"message":"Note the thing","files":["Inbox/Note.md"]}
  {"message":"Sweep","files":"all","staged":"include"}
  message - REQUIRED, the caller's own wording. The tool never authors it; the submodule-pointer
            commit is the one carve-out, and it needs no directive.
  files   - REQUIRED, either an explicit array of repo-relative paths or the string "all". An
            explicit array pins the commit set structurally; "all" additionally REQUIRES
            state_token, since nothing else defines the set.
  staged  - OPTIONAL, only "include", folding pre-existing staged content into the commit. Absent,
            a pathspec commit preserves another actor's staged entries untouched. The tool NEVER
            unstages.
Malformed JSON, a non-object payload, or a missing/invalid member is malformed_op with {reason}.
Supplying this parameter ASSERTS git:commit, so a caller lacking the key is refused
insufficient_capability rather than reported as blocked.
.PARAMETER OnConflict
Conflict-resolution strategy authorization. One member in V1:
  keep_local_save_remote - merge with the local side winning each conflicted file, preserving the
                           remote body as a "<name> [CONFLICT yyyyMMdd HHmm]<ext>" sibling, with
                           tool-authored per-conflict provenance lines in the commit body.
Deliberately not a ValidateSet, so every value reaches the script body and an unrecognized one is
refused with sync's own invalid_argument JSON error rather than a PowerShell binding error.
Matched case-insensitively after a trim. Supplying this parameter ASSERTS git:commit.
.PARAMETER StateToken
Opaque gate from a prior call's stateToken, comparing the state a directive was authorized over
against the state now. A mismatch is state_mismatch with the fresh token in data and ZERO
mutation. REQUIRED when commit.files is "all"; optional otherwise, because an explicit file array
already pins the set structurally.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: git:sync

The floor is git:sync: observing state and converging toward the remote without introducing
agent-authored content - fetch, reattach, fast-forward, clean merge commits, submodule checkout,
and the submodule-pointer commit that records one. The key git:commit is asserted in-script for
the three things that DO introduce agent-authored content: committing under a commit directive,
resolving conflicts under an on_conflict directive, and pushing.

Capability denial surfaces two ways, the one licensed exception to uniform insufficient_capability
(Tool-Contracts: Op Classes on Tools). An INCIDENTAL shortfall - converge would need a commit or a
push and the caller passed no directive - is a blocked entry with reason requires_capability, data
rather than failure. An EXPLICIT directive without the key is an insufficient_capability error.
Nothing mutates either way; only the reporting differs. Standalone (no injected class set)
degrades open.

RECORDED EXCEPTION to the one-lock-span-per-operation standard: this tool takes SEVERAL spans, one
per mutation. Licensed by fetch and the merge-tree probe touching no tree, index, or HEAD, so the
lock covers exactly the mutations and no lock span encloses a network call except push, which IS
the mutation. Every locked scriptblock re-asserts $ErrorActionPreference = 'Stop' first.

There is deliberately NO out_of_scope check anywhere: both git classes are whole-repo by
construction, so no operation here is scopeable, and the conflict sibling write is deliberately
unscoped - gating it would make conflict resolution fail exactly when a scoped agent most needs it.

Repos are DISCOVERED from git submodule status, never enumerated, and applied deepest-first so each
submodule settles before its parent records the pointer. A conflict is a RESULT, never isError.
The return carries the write-class envelope: capabilitiesUsed and filesSkippedUnreadable[] always
(the latter always [] - sync runs no enumerate-then-read loop over notes), lockWarning only when an
abandoned mutex was inherited.
#>
param(
    [bool] $Apply = $false,
    [string] $Commit,
    [string] $OnConflict,
    [string] $StateToken,
    [string] $Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# The trailer names the CAPABILITY, not the implementation, so the mechanics moving from skill to
# tool does not change what the commit is. It carries the vault's whole tool-authored-commit
# history and is the only machine-readable actor discriminator this vault has.
$script:CommitTrailer = 'Generated-by: vault-sync'

# merge-tree --write-tree, the mechanism the whole converge model rests on, landed in git 2.38.
$script:MergeTreeMinMajor = 2
$script:MergeTreeMinMinor = 38
$script:MergeTreeChecked = $false

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

$script:LockAbandoned = $false
$script:Actions = [System.Collections.Generic.List[object]]::new()
$script:PlannedActions = [System.Collections.Generic.List[object]]::new()
$script:Blocked = [System.Collections.Generic.List[object]]::new()
$script:CapabilitiesUsed = [ordered]@{}

# ---------------------------------------------------------------------------
# git plumbing. Ported from the vault-sync skill's vault-sync-git.ps1, with the merge-result
# classifier retired - converge decides BEFORE acting, so there is no aftermath to classify - and
# its network-failure regexes repointed at fetch output.
# ---------------------------------------------------------------------------

function Invoke-SyncGit {
    <#
    .SYNOPSIS
    Runs git against one repository and returns @{ ExitCode; Output }. git's own non-zero exits are
    data here, not terminating errors - every rung classifies its own - so this is the one place the
    tool's Stop preference is relaxed.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepoPath,
        [Parameter(Mandatory)] [string[]] $Arguments
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw = & git -C $RepoPath @Arguments 2>&1
        $exit = $LASTEXITCODE
        $text = (@($raw) | ForEach-Object { "$_" }) -join "`n"
        return @{ ExitCode = $exit; Output = $text }
    }
    catch {
        # git missing from PATH surfaces here rather than as an exit code.
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message "git could not be invoked: $($_.Exception.Message)" -Data @{ dependency = 'git' })
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Test-SyncNetworkFailure {
    <#
    .SYNOPSIS
    True when git output names an unreachable or unauthenticated remote. Ported from
    Get-GitPullClassification's network branch and repointed at fetch/push output; the rest of that
    classifier (UpToDate/FastForward/MergedClean/MergedConflicts) retired with the operation enum.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Output)
    $Output -match 'Could not resolve host|Connection timed out|Permission denied|[Aa]uthentication failed|fatal: unable to access|Could not read from remote repository|does not appear to be a git repository|repository .* does not exist'
}

function New-ConflictFilePath {
    <#
    .SYNOPSIS
    Builds the "Filename [CONFLICT yyyyMMdd HHmm].md" sibling path for a conflicted file. Ported
    verbatim from the vault-sync skill; -Now lets tests pin a deterministic timestamp.
    #>
    param(
        [Parameter(Mandatory)] [string] $RelPath,
        [datetime] $Now = (Get-Date)
    )
    $dir = Split-Path $RelPath -Parent
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($RelPath)
    $ext = [System.IO.Path]::GetExtension($RelPath)
    $stamp = $Now.ToString('yyyyMMdd HHmm')
    $newName = "$stem [CONFLICT $stamp]$ext"
    if ($dir) { return (Join-Path $dir $newName) -replace '\\', '/' }
    return $newName
}

function Assert-SyncMergeTreeVersion {
    <#
    .SYNOPSIS
    Refuses dependency_unavailable below git 2.38 rather than falling back to an optimistic merge -
    that fallback is exactly the mid-merge hazard this design removes. Checked lazily, at the first
    probe, so a vault that never diverges works on an older git.
    #>
    if ($script:MergeTreeChecked) { return }
    $r = Invoke-SyncGit -RepoPath $script:VaultRoot -Arguments @('--version')
    if ($r.ExitCode -ne 0 -or $r.Output -notmatch '(\d+)\.(\d+)') {
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message "Could not determine the installed git version; sync requires git $($script:MergeTreeMinMajor).$($script:MergeTreeMinMinor) or newer for 'merge-tree --write-tree'." -Data @{ dependency = 'git'; output = $r.Output })
    }
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    if ($major -lt $script:MergeTreeMinMajor -or ($major -eq $script:MergeTreeMinMajor -and $minor -lt $script:MergeTreeMinMinor)) {
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message "git $major.$minor is below the $($script:MergeTreeMinMajor).$($script:MergeTreeMinMinor) floor that 'merge-tree --write-tree' requires. sync refuses rather than falling back to an optimistic merge that could leave the repository mid-conflict." -Data @{ dependency = 'git'; found = "$major.$minor"; required = "$($script:MergeTreeMinMajor).$($script:MergeTreeMinMinor)" })
    }
    $script:MergeTreeChecked = $true
}

# ---------------------------------------------------------------------------
# Repo discovery. Never enumerated: .gitmodules / git submodule status already carry this vault's
# topology, and baking the current shape into the tool surface is what the retired `repo` enum did.
# ---------------------------------------------------------------------------

function Get-SyncDirectSubmodule {
    <#
    .SYNOPSIS
    One repository's DIRECT submodule paths, relative to it. Uninitialized entries ("-" status) are
    dropped: they have no working tree to converge.
    #>
    param([Parameter(Mandatory)] [string] $RepoPath)
    $r = Invoke-SyncGit -RepoPath $RepoPath -Arguments @('submodule', 'status')
    if ($r.ExitCode -ne 0) { return }
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($r.Output -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^(.)([0-9a-fA-F]{7,64})\s+(.+?)(\s+\(.*\))?$') { continue }
        if ($Matches[1] -eq '-') { continue }
        $candidate = $Matches[3].Replace('\', '/').Trim('/').Trim()
        if ($candidate) { $paths.Add($candidate) }
    }
    # Returned unwrapped, so an empty result emits nothing and the caller's @() yields an empty
    # array. A ",@()" return would emit the empty array AS an element, which is what turned the
    # discovery walk below into an infinite loop the first time.
    $paths.ToArray()
}

function Get-SyncRepoTree {
    <#
    .SYNOPSIS
    Every repository this call covers, as records carrying Key (vault-relative path, "." for the
    parent), Full, Depth, and DirectSubmodules. Walked breadth-first from the parent, so nested
    submodules fall out of the walk rather than needing a special case.
    #>
    $repos = [System.Collections.Generic.List[object]]::new()
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue([pscustomobject]@{ Key = '.'; Full = $script:VaultRoot; Depth = 0 })

    while ($queue.Count -gt 0) {
        $entry = $queue.Dequeue()
        $direct = @(Get-SyncDirectSubmodule -RepoPath $entry.Full)
        $repos.Add([pscustomobject]@{
                Key              = $entry.Key
                Full             = $entry.Full
                Depth            = $entry.Depth
                DirectSubmodules = $direct
            })
        foreach ($sub in $direct) {
            # Defensive: an empty or self-referential path would re-enqueue this same repository.
            if ([string]::IsNullOrWhiteSpace($sub) -or $sub -eq '.') { continue }
            $childKey = if ($entry.Key -eq '.') { $sub } else { "$($entry.Key)/$sub" }
            $childFull = Join-Path $entry.Full ($sub -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path -LiteralPath $childFull -PathType Container)) { continue }
            $queue.Enqueue([pscustomobject]@{ Key = $childKey; Full = $childFull; Depth = $entry.Depth + 1 })
        }
    }
    $repos.ToArray()
}

# ---------------------------------------------------------------------------
# State assessment.
# ---------------------------------------------------------------------------

function Get-SyncRepoState {
    <#
    .SYNOPSIS
    One repository's whole assessed state. The three status buckets (staged / unstaged / untracked)
    are reported separately rather than as one uncommitted blob: partial staging is another actor's
    EXPRESSED INTENT, and the distinction is a first-class condition callers branch on.
    #>
    param([Parameter(Mandatory)] $Repo)

    $headR = Invoke-SyncGit -RepoPath $Repo.Full -Arguments @('rev-parse', 'HEAD')
    $head = if ($headR.ExitCode -eq 0) { $headR.Output.Trim() } else { '' }

    $branchR = Invoke-SyncGit -RepoPath $Repo.Full -Arguments @('symbolic-ref', '-q', '--short', 'HEAD')
    $branch = if ($branchR.ExitCode -eq 0 -and $branchR.Output.Trim()) { $branchR.Output.Trim() } else { $null }

    $upstreamR = Invoke-SyncGit -RepoPath $Repo.Full -Arguments @('rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{upstream}')
    $upstream = if ($upstreamR.ExitCode -eq 0 -and $upstreamR.Output.Trim()) { $upstreamR.Output.Trim() } else { $null }

    $upstreamSha = ''
    $ahead = 0
    $behind = 0
    if ($upstream) {
        $usR = Invoke-SyncGit -RepoPath $Repo.Full -Arguments @('rev-parse', '@{upstream}')
        if ($usR.ExitCode -eq 0) { $upstreamSha = $usR.Output.Trim() }
        $countR = Invoke-SyncGit -RepoPath $Repo.Full -Arguments @('rev-list', '--left-right', '--count', "HEAD...$upstream")
        if ($countR.ExitCode -eq 0 -and $countR.Output.Trim() -match '^(\d+)\s+(\d+)$') {
            $ahead = [int]$Matches[1]
            $behind = [int]$Matches[2]
        }
    }

    # -z rather than line-oriented porcelain: a path carrying a space, a quote, or a non-ASCII byte
    # is emitted verbatim between NULs instead of C-quoted, so the buckets and the state token both
    # read the same unambiguous bytes. -uall because the default collapses an untracked directory to
    # a single "Dir/" entry, which is not a path a commit directive could ever name.
    $statusR = Invoke-SyncGit -RepoPath $Repo.Full -Arguments @('status', '--porcelain', '-z', '-uall')
    $porcelain = if ($statusR.ExitCode -eq 0) { $statusR.Output } else { '' }

    $staged = [System.Collections.Generic.List[string]]::new()
    $unstaged = [System.Collections.Generic.List[string]]::new()
    $untracked = [System.Collections.Generic.List[string]]::new()
    $unmerged = [System.Collections.Generic.List[string]]::new()

    # Index-walked rather than foreach: a rename/copy entry is followed by a SECOND NUL-separated
    # field holding the original path, which must be consumed rather than read as a status line.
    $entries = @($porcelain -split "`0")
    $i = 0
    while ($i -lt $entries.Count) {
        $entry = $entries[$i]
        $i++
        if ($entry.Length -lt 3) { continue }
        $x = $entry[0]
        $y = $entry[1]
        $path = $entry.Substring(3).Replace('\', '/')
        if ($x -eq 'R' -or $x -eq 'C') { $i++ }

        $xy = "$x$y"
        if ($xy -eq '??') { $untracked.Add($path); continue }
        if ($xy -in @('DD', 'AU', 'UD', 'UA', 'DU', 'AA', 'UU')) { $unmerged.Add($path); continue }
        if ($x -ne ' ' -and $x -ne '?' -and $x -ne '!') { $staged.Add($path) }
        if ($y -ne ' ' -and $y -ne '?' -and $y -ne '!') { $unstaged.Add($path) }
    }

    # MERGE_HEAD in the REAL git-dir (a submodule's .git is a file, not a directory), cross-checked
    # against unmerged porcelain markers - ported from the skill's Test-GitMidMerge.
    $gitDirR = Invoke-SyncGit -RepoPath $Repo.Full -Arguments @('rev-parse', '--git-dir')
    $mergeHeadPresent = $false
    if ($gitDirR.ExitCode -eq 0 -and $gitDirR.Output.Trim()) {
        $gitDir = $gitDirR.Output.Trim()
        $gitDirAbs = if ([System.IO.Path]::IsPathRooted($gitDir)) { $gitDir } else { Join-Path $Repo.Full $gitDir }
        $mergeHeadPresent = Test-Path -LiteralPath (Join-Path $gitDirAbs 'MERGE_HEAD')
    }

    [pscustomobject]@{
        Key         = $Repo.Key
        Full        = $Repo.Full
        Depth       = $Repo.Depth
        Submodules  = @($Repo.DirectSubmodules)
        Head        = $head
        Branch      = $branch
        Detached    = ($null -eq $branch)
        Upstream    = $upstream
        UpstreamSha = $upstreamSha
        Ahead       = $ahead
        Behind      = $behind
        Staged      = $staged.ToArray()
        Unstaged    = $unstaged.ToArray()
        Untracked   = $untracked.ToArray()
        Unmerged    = $unmerged.ToArray()
        MidMerge    = ($mergeHeadPresent -or $unmerged.Count -gt 0)
        Porcelain   = $porcelain
    }
}

function Get-SyncAllState {
    <#
    .SYNOPSIS
    Assessed state for every discovered repo, keyed by repo path. Recomputed after apply, so the
    returned repos map and state token describe the world as the call LEAVES it.
    #>
    $map = [ordered]@{}
    foreach ($repo in $script:RepoTree) {
        $map[$repo.Key] = Get-SyncRepoState -Repo $repo
    }
    $map
}

function Get-SyncStateToken {
    <#
    .SYNOPSIS
    SHA-256 truncated to 12 hex over each repo's (HEAD, upstream tip, porcelain status), composed
    parent-first then submodules sorted by path so the token is order-stable across calls. Porcelain's
    XY letters encode staged-versus-unstaged, so another actor staging something between calls trips
    the gate for free.

    KNOWN HOLE, accepted and documented: content drift WITHIN a file that stays in the same status
    bucket is invisible, so two actors editing one file between status and apply means the second's
    content rides along under the first's message. Content hashing was weighed and dropped as
    disproportionate for a default-path operation.
    #>
    param([Parameter(Mandatory)] $States)

    $keys = @('.') + @($States.Keys | Where-Object { $_ -ne '.' } | Sort-Object -CaseSensitive)
    $sb = [System.Text.StringBuilder]::new()
    foreach ($key in $keys) {
        if (-not $States.Contains($key)) { continue }
        $s = $States[$key]
        [void]$sb.Append("$key`n$($s.Head)`n$($s.UpstreamSha)`n$($s.Porcelain)`n")
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString()))
    }
    finally {
        $sha.Dispose()
    }
    ([System.BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()).Substring(0, 12)
}

# ---------------------------------------------------------------------------
# Result shaping.
# ---------------------------------------------------------------------------

function New-SyncRemedy {
    <#
    .SYNOPSIS
    The shared RemedyOption (Tool-Contracts: The RemedyOption Shape). `requires` names the dotted
    param paths sync structurally cannot fill because they need caller judgment - commit.message
    above all - so a resolution says plainly whether it is dispatchable verbatim.
    #>
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $Action,
        [Parameter(Mandatory)] $Params,
        [string[]] $Requires = @()
    )
    [ordered]@{ id = $Id; label = $Label; action = $Action; params = $Params; requires = @($Requires) }
}

function Add-SyncBlocked {
    param(
        [Parameter(Mandatory)] [string] $Repo,
        [Parameter(Mandatory)] [string] $Reason,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Detail,
        [Parameter(Mandatory)] $Resolution
    )
    $script:Blocked.Add([ordered]@{ repo = $Repo; reason = $Reason; detail = $Detail; resolution = $Resolution })
}

function Add-SyncAction {
    param(
        [Parameter(Mandatory)] [string] $Repo,
        [Parameter(Mandatory)] [string] $Action,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Detail,
        [Parameter(Mandatory)] [bool] $Planned
    )
    $entry = [ordered]@{ repo = $Repo; action = $Action; detail = $Detail }
    if ($Planned) { $script:PlannedActions.Add($entry) } else { $script:Actions.Add($entry) }
}

function New-SyncCommitMessage {
    <#
    .SYNOPSIS
    Composes a commit message with the standard trailer. EVERY commit the tool makes carries it -
    the tool-authored submodule-pointer commit and clean merge commits included - because the
    trailer names the capability and a tool-authored commit is exactly what it exists to mark.
    #>
    param(
        [Parameter(Mandatory)] [string] $Summary,
        [string[]] $BodyLines = @()
    )
    $parts = @($Summary)
    if ($BodyLines -and $BodyLines.Count -gt 0) { $parts += ($BodyLines -join "`n") }
    $parts += $script:CommitTrailer
    $parts -join "`n`n"
}

function Invoke-SyncCommit {
    <#
    .SYNOPSIS
    Commits through a temp message file, so a multi-line body and the trailer survive without shell
    quoting. -Pathspec runs a PATHSPEC commit, which commits exactly the named set and leaves an
    unrelated staged entry of another actor's untouched (verified against git 2.55).
    #>
    param(
        [Parameter(Mandatory)] [string] $RepoPath,
        [Parameter(Mandatory)] [string] $Message,
        [string[]] $Pathspec
    )
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($tmp, $Message, $script:Utf8NoBom)
        $gitArgs = @('commit', '--file', $tmp)
        if ($Pathspec -and $Pathspec.Count -gt 0) { $gitArgs += @('--') + $Pathspec }
        Invoke-SyncGit -RepoPath $RepoPath -Arguments $gitArgs
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SyncStagePath {
    <#
    .SYNOPSIS
    Stages the named paths. Untracked files need this before a pathspec commit will include them
    (verified against git 2.55). Staging is purely internal to an apply span - nothing is ever left
    staged between calls - and nothing is ever UNstaged.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepoPath,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Paths
    )
    if ($Paths.Count -eq 0) { return }
    Invoke-SyncGit -RepoPath $RepoPath -Arguments (@('add', '--') + $Paths)
}

function Assert-SyncGitOk {
    <#
    .SYNOPSIS
    Raises git_failure with raw output for genuinely unexpected git breakage during an authorized
    mutation. Expected non-success outcomes never reach here - they are classified by their own rung
    and reported as blocked states.
    #>
    param(
        [Parameter(Mandatory)] $Result,
        [Parameter(Mandatory)] [string] $Repo,
        [Parameter(Mandatory)] [string] $Operation
    )
    if ($Result.ExitCode -eq 0) { return }
    throw (New-VaultToolError -Code 'git_failure' -Message "git $Operation failed in '$Repo' (exit $($Result.ExitCode)): $($Result.Output)" -Data @{ repo = $Repo; operation = $Operation; exitCode = $Result.ExitCode; output = $Result.Output })
}

function Resolve-SyncConflict {
    <#
    .SYNOPSIS
    keep_local_save_remote, executed inside ONE lock span: run the real merge, resolve every
    conflicted file to the local side, preserve the remote body as a "[CONFLICT ...]" sibling, and
    commit. The repository is mid-merge only inside that span, on a deterministic path with no agent
    round trip in the middle; a failure aborts the merge rather than leaving one behind.

    The sibling write performs NO out_of_scope check, deliberately: it lands next to the conflicted
    file wherever the merge put it, and gating it would make conflict resolution fail exactly when a
    scoped agent most needs it.
    #>
    param([Parameter(Mandatory)] $State)

    $merge = Invoke-SyncGit -RepoPath $State.Full -Arguments @('merge', '--no-commit', '--no-ff', $State.Upstream)
    $conflictedR = Invoke-SyncGit -RepoPath $State.Full -Arguments @('diff', '--name-only', '--diff-filter=U')
    $conflicted = @($conflictedR.Output -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim().Replace('\', '/') })

    if ($conflicted.Count -eq 0 -and $merge.ExitCode -ne 0) {
        # A merge that failed for a reason other than a content conflict must not be left half-applied.
        Invoke-SyncGit -RepoPath $State.Full -Arguments @('merge', '--abort') | Out-Null
        throw (New-VaultToolError -Code 'git_failure' -Message "Merge of '$($State.Upstream)' into '$($State.Key)' failed with no conflicted files and was aborted: $($merge.Output)" -Data @{ repo = $State.Key; operation = 'merge'; output = $merge.Output })
    }

    $bodyLines = [System.Collections.Generic.List[string]]::new()
    $savedFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $conflicted) {
        # Stage 3 is the remote side. It is absent for a delete/modify conflict, where there is no
        # remote body to preserve and the provenance line says so.
        $theirs = Invoke-SyncGit -RepoPath $State.Full -Arguments @('show', ":3:$file")
        if ($theirs.ExitCode -eq 0) {
            $conflictRel = New-ConflictFilePath -RelPath $file
            $conflictAbs = Join-Path $State.Full ($conflictRel -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            $conflictDir = Split-Path -Parent $conflictAbs
            if ($conflictDir -and -not (Test-Path -LiteralPath $conflictDir)) {
                New-Item -ItemType Directory -Force -Path $conflictDir | Out-Null
            }
            [System.IO.File]::WriteAllText($conflictAbs, $theirs.Output, $script:Utf8NoBom)
            Invoke-SyncGit -RepoPath $State.Full -Arguments @('add', '--', $conflictRel) | Out-Null
            $savedFiles.Add($conflictRel)
            $bodyLines.Add("Kept local $file; remote version saved as $conflictRel")
        }
        else {
            $bodyLines.Add("Kept local $file; no remote version to preserve (delete/modify conflict)")
        }

        $ours = Invoke-SyncGit -RepoPath $State.Full -Arguments @('checkout', '--ours', '--', $file)
        if ($ours.ExitCode -ne 0) {
            # Deleted on our side: honoring "local wins" means the deletion stands.
            Invoke-SyncGit -RepoPath $State.Full -Arguments @('rm', '-f', '--', $file) | Out-Null
        }
        else {
            Invoke-SyncGit -RepoPath $State.Full -Arguments @('add', '--', $file) | Out-Null
        }
    }

    $remaining = Invoke-SyncGit -RepoPath $State.Full -Arguments @('diff', '--name-only', '--diff-filter=U')
    if (@($remaining.Output -split "`n" | Where-Object { $_.Trim() }).Count -gt 0) {
        Invoke-SyncGit -RepoPath $State.Full -Arguments @('merge', '--abort') | Out-Null
        throw (New-VaultToolError -Code 'git_failure' -Message "Conflict resolution left unmerged entries in '$($State.Key)'; the merge was aborted rather than committed half-resolved." -Data @{ repo = $State.Key; operation = 'merge-resolve'; output = $remaining.Output })
    }

    $summary = "Merge $($State.Upstream) into $($State.Branch) (keep_local_save_remote)"
    $commit = Invoke-SyncCommit -RepoPath $State.Full -Message (New-SyncCommitMessage -Summary $summary -BodyLines $bodyLines.ToArray())
    Assert-SyncGitOk -Result $commit -Repo $State.Key -Operation 'commit (conflict resolution)'

    @{ Conflicted = $conflicted; Saved = $savedFiles.ToArray() }
}

# ---------------------------------------------------------------------------
# Locked mutation spans.
#
# Each span's scriptblock is defined at SCRIPT scope and reads its operands from $script:Lock*
# variables rather than closing over a calling function's locals: a scriptblock handed to a module
# function resolves unbound variables through its defining session state, where script scope is
# reliable and another function's locals are not.
# ---------------------------------------------------------------------------

$script:LockGitBlock = {
    $ErrorActionPreference = 'Stop'
    Invoke-SyncGit -RepoPath $script:LockRepo -Arguments $script:LockArgs
}

$script:LockCommitBlock = {
    $ErrorActionPreference = 'Stop'
    Invoke-SyncStagePath -RepoPath $script:LockRepo -Paths $script:LockPaths | Out-Null
    if ($script:LockUsePathspec) {
        Invoke-SyncCommit -RepoPath $script:LockRepo -Message $script:LockMessage -Pathspec $script:LockPaths
    }
    else {
        # The caller opted in to another actor's staged content, so the whole index is committed.
        Invoke-SyncCommit -RepoPath $script:LockRepo -Message $script:LockMessage
    }
}

$script:LockResolveBlock = {
    $ErrorActionPreference = 'Stop'
    Resolve-SyncConflict -State $script:LockState
}

function Invoke-SyncLocked {
    <#
    .SYNOPSIS
    Runs one mutation span under the per-vault write mutex. Several of these run per call - the
    recorded exception to one-lock-per-operation - and an abandoned mutex inherited by ANY span
    raises the single lockWarning on an otherwise-successful result.
    #>
    param([Parameter(Mandatory)] [scriptblock] $ScriptBlock)
    $abandoned = $false
    $result = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$abandoned) -ScriptBlock $ScriptBlock
    if ($abandoned) { $script:LockAbandoned = $true }
    $result
}

function Invoke-SyncLockedGit {
    param(
        [Parameter(Mandatory)] [string] $RepoPath,
        [Parameter(Mandatory)] [string[]] $Arguments
    )
    $script:LockRepo = $RepoPath
    $script:LockArgs = $Arguments
    Invoke-SyncLocked -ScriptBlock $script:LockGitBlock
}

function Invoke-SyncLockedCommit {
    param(
        [Parameter(Mandatory)] [string] $RepoPath,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Paths,
        [Parameter(Mandatory)] [string] $Message,
        [Parameter(Mandatory)] [bool] $UsePathspec
    )
    $script:LockRepo = $RepoPath
    $script:LockPaths = $Paths
    $script:LockMessage = $Message
    $script:LockUsePathspec = $UsePathspec
    Invoke-SyncLocked -ScriptBlock $script:LockCommitBlock
}

function Invoke-SyncLockedResolve {
    param([Parameter(Mandatory)] $State)
    $script:LockState = $State
    Invoke-SyncLocked -ScriptBlock $script:LockResolveBlock
}

# ---------------------------------------------------------------------------
# Blocked-state emitters.
# ---------------------------------------------------------------------------

function Add-SyncCapabilityBlock {
    <#
    .SYNOPSIS
    The INCIDENTAL half of the capability split: converge would need a key the caller lacks and no
    directive requested it, so this is data in the normal return rather than a failure. An EXPLICIT
    directive without the key is insufficient_capability, raised at validation.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepoKey,
        [Parameter(Mandatory)] [string] $Detail
    )
    Add-SyncBlocked -Repo $RepoKey -Reason 'requires_capability' -Detail "$Detail - the grant does not hold it, so convergence stops here with nothing mutated." `
        -Resolution (New-SyncRemedy -Id 'obtain-git-commit' -Label 'Obtain a grant carrying git:commit' -Action 'requires-capability' -Params ([ordered]@{ requiredClass = 'git:commit' }))
}

function Add-SyncUncommittedBlock {
    <#
    .SYNOPSIS
    The uncommitted_changes block, or the incidental capability shortfall when the caller could not
    commit even with a directive. `requires` names commit.message - the one thing the tool
    structurally cannot supply, because wording is caller-side judgment.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepoKey,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Paths
    )
    if (-not $script:HasCommitKey) {
        Add-SyncCapabilityBlock -RepoKey $RepoKey -Detail "committing $($Paths.Count) uncommitted path(s) needs git:commit"
        return
    }
    Add-SyncBlocked -Repo $RepoKey -Reason 'uncommitted_changes' -Detail "$($Paths.Count) uncommitted path(s): $(@($Paths) -join ', ')" `
        -Resolution (New-SyncRemedy -Id 'commit-changes' -Label 'Re-issue with a commit directive naming these files and your own message' -Action 'sync-directive' `
            -Params ([ordered]@{ apply = $true; commit = [ordered]@{ message = ''; files = @($Paths) }; state_token = $script:PreApplyToken }) -Requires @('commit.message'))
}

function Add-SyncStagedBlock {
    <#
    .SYNOPSIS
    Pre-detects the index state git refuses to merge over. git requires the index to match HEAD and
    otherwise errors with "local changes would be overwritten by merge", naming files the merge does
    not even touch; reporting it here yields a clear state instead of that misleading error.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepoKey,
        [Parameter(Mandatory)] $State,
        [Parameter(Mandatory)] [string] $Operation
    )
    Add-SyncBlocked -Repo $RepoKey -Reason 'staged_changes' -Detail "A $Operation cannot run while $(@($State.Staged).Count) path(s) are staged by another actor: $(@($State.Staged) -join ', ')" `
        -Resolution (New-SyncRemedy -Id 'include-staged' -Label 'Re-issue with a commit directive carrying staged:"include", or resolve the index by hand' -Action 'sync-directive' `
            -Params ([ordered]@{ apply = $true; commit = [ordered]@{ message = ''; files = @($State.Staged); staged = 'include' }; state_token = $script:PreApplyToken }) -Requires @('commit.message'))
}

# ---------------------------------------------------------------------------
# Ladder support.
# ---------------------------------------------------------------------------

function Get-SyncDerivedBranch {
    <#
    .SYNOPSIS
    The branch a detached repo reattaches to: the parent's .gitmodules `branch` key if it sets one,
    otherwise the remote's default HEAD, otherwise the sole local branch when there is exactly one.
    $null when none resolves, which is a blocked state - the tool never guesses which history a
    detached HEAD belongs on.
    #>
    param([Parameter(Mandatory)] $State)

    if ($State.Key -ne '.') {
        $lastSlash = $State.Key.LastIndexOf('/')
        $parentFull = if ($lastSlash -lt 0) { $script:VaultRoot } else { Join-Path $script:VaultRoot ($State.Key.Substring(0, $lastSlash) -replace '/', [System.IO.Path]::DirectorySeparatorChar) }
        $subName = if ($lastSlash -lt 0) { $State.Key } else { $State.Key.Substring($lastSlash + 1) }
        if (Test-Path -LiteralPath (Join-Path $parentFull '.gitmodules') -PathType Leaf) {
            $r = Invoke-SyncGit -RepoPath $parentFull -Arguments @('config', '-f', '.gitmodules', '--get-regexp', '^submodule\..*\.branch$')
            if ($r.ExitCode -eq 0) {
                foreach ($line in ($r.Output -split "`n")) {
                    if ($line -match '^submodule\.(.+)\.branch\s+(\S+)$' -and ($Matches[1] -eq $subName -or $Matches[1] -eq $State.Key)) {
                        return $Matches[2]
                    }
                }
            }
        }
    }

    $headR = Invoke-SyncGit -RepoPath $State.Full -Arguments @('symbolic-ref', '--short', 'refs/remotes/origin/HEAD')
    if ($headR.ExitCode -eq 0 -and $headR.Output.Trim() -match '^origin/(.+)$') { return $Matches[1] }

    $branchR = Invoke-SyncGit -RepoPath $State.Full -Arguments @('for-each-ref', '--format=%(refname:short)', 'refs/heads')
    if ($branchR.ExitCode -eq 0) {
        $names = @($branchR.Output -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
        if ($names.Count -eq 1) { return $names[0] }
    }
    $null
}

function Test-SyncRepoDirty {
    param([Parameter(Mandatory)] $State)
    ($State.Staged.Count + $State.Unstaged.Count + $State.Untracked.Count) -gt 0
}

function Get-SyncDirtyPath {
    <#
    .SYNOPSIS
    Every path the working tree or index differs on, deduplicated - the commit candidate set.
    #>
    param([Parameter(Mandatory)] $State)
    $set = [System.Collections.Generic.List[string]]::new()
    foreach ($p in (@($State.Staged) + @($State.Unstaged) + @($State.Untracked))) {
        if (-not $set.Contains($p)) { $set.Add($p) }
    }
    $set.ToArray()
}

function Split-SyncGitlinkPath {
    <#
    .SYNOPSIS
    Splits a dirty path set into gitlink entries - submodule pointer bumps the tool commits itself
    under the floor - and everything else, which is the caller's own content and needs the caller's
    own message.
    #>
    param(
        [Parameter(Mandatory)] $State,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Paths
    )
    $gitlinks = [System.Collections.Generic.List[string]]::new()
    $others = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $Paths) {
        if (@($State.Submodules) -contains $p) { $gitlinks.Add($p) } else { $others.Add($p) }
    }
    @{ Gitlinks = $gitlinks.ToArray(); Others = $others.ToArray() }
}

function Get-SyncGitlinkSha {
    <#
    .SYNOPSIS
    The recorded gitlink sha per direct submodule, used to detect which pointers a fast-forward moved
    so only those working trees are checked out.
    #>
    param([Parameter(Mandatory)] $State)
    $map = @{}
    foreach ($sub in @($State.Submodules)) {
        $r = Invoke-SyncGit -RepoPath $State.Full -Arguments @('rev-parse', "HEAD:$sub")
        if ($r.ExitCode -eq 0) { $map[$sub] = $r.Output.Trim() }
    }
    $map
}

# ---------------------------------------------------------------------------
# The converge ladder. Evaluated per repo; each rung either advances or blocks, and blocking never
# leaves a partial mutation behind.
# ---------------------------------------------------------------------------

function Invoke-SyncRepoLadder {
    <#
    .SYNOPSIS
    Walks one repository up the ladder. -DoApply false plans instead of mutating, which is what makes
    status a genuine dry run rather than a bare state dump. Actions, planned actions, and blocked
    entries accumulate on the script-scope lists.
    #>
    param(
        [Parameter(Mandatory)] $Repo,
        [Parameter(Mandatory)] [bool] $DoApply
    )

    # Without a current remote view every remaining rung would decide on stale data, so a repo whose
    # fetch failed stops after its blocked entry with local state still reported.
    if ($script:FetchFailed.ContainsKey($Repo.Key)) { return }

    $state = Get-SyncRepoState -Repo $Repo
    $planned = -not $DoApply

    # --- Rung 1: detached HEAD. Clean tree reattaches to the derived branch (the checkout creates no
    # history); dirty or underivable is reported, never forced. ---
    if ($state.Detached) {
        if ($state.Staged.Count -gt 0 -or $state.Unstaged.Count -gt 0 -or $state.MidMerge) {
            Add-SyncBlocked -Repo $Repo.Key -Reason 'detached_head_dirty' -Detail "HEAD is detached at $($state.Head) with uncommitted tracked changes, so reattaching would carry or lose them." `
                -Resolution (New-SyncRemedy -Id 'resolve-detached-by-hand' -Label 'Resolve the detached HEAD by hand - reported, never forced' -Action 'manual' -Params ([ordered]@{ repo = $Repo.Key; head = $state.Head }))
            return
        }
        $branch = Get-SyncDerivedBranch -State $state
        if (-not $branch) {
            Add-SyncBlocked -Repo $Repo.Key -Reason 'detached_head_underivable' -Detail "HEAD is detached at $($state.Head) and no target branch is derivable - neither a .gitmodules branch key nor a remote default HEAD resolves." `
                -Resolution (New-SyncRemedy -Id 'resolve-detached-by-hand' -Label 'Check out the intended branch by hand' -Action 'manual' -Params ([ordered]@{ repo = $Repo.Key; head = $state.Head }))
            return
        }
        if ($planned) {
            Add-SyncAction -Repo $Repo.Key -Action 'reattach' -Detail "Reattach detached HEAD to '$branch'" -Planned $true
        }
        else {
            $r = Invoke-SyncLockedGit -RepoPath $state.Full -Arguments @('checkout', $branch)
            Assert-SyncGitOk -Result $r -Repo $Repo.Key -Operation "checkout $branch"
            Add-SyncAction -Repo $Repo.Key -Action 'reattach' -Detail "Reattached detached HEAD to '$branch'" -Planned $false
            $state = Get-SyncRepoState -Repo $Repo
        }
    }

    # --- Rung 2: mid-merge from a prior crash. The tool's own resolution span always exits clean, so
    # a merge state found here is somebody else's wreckage and is never adopted. ---
    if ($state.MidMerge) {
        Add-SyncBlocked -Repo $Repo.Key -Reason 'mid_merge' -Detail "The repository is mid-merge with $(@($state.Unmerged).Count) unmerged file(s): $(@($state.Unmerged) -join ', ')" `
            -Resolution (New-SyncRemedy -Id 'finish-merge-by-hand' -Label 'Finish or abort the in-progress merge by hand' -Action 'manual' -Params ([ordered]@{ repo = $Repo.Key; unmergedFiles = @($state.Unmerged) }))
        return
    }

    # --- Rung 3: pre-existing staged content. It blocks ONLY where a merge is required (behind > 0),
    # because git refuses to merge over a non-clean index; otherwise it is ordinary uncommitted
    # content and falls to rung 4. A commit directive carrying staged:"include" clears it at rung 4,
    # so that case skips this block rather than pre-empting its own remedy. ---
    if ($state.Staged.Count -gt 0 -and $state.Behind -gt 0 -and
        -not ($script:CommitDirective -and $script:CommitDirective.IncludeStaged)) {
        Add-SyncStagedBlock -RepoKey $Repo.Key -State $state -Operation 'merge'
        return
    }

    # --- Rung 4: dirty working tree. ---
    if (Test-SyncRepoDirty -State $state) {
        $dirty = @(Get-SyncDirtyPath -State $state)
        $split = Split-SyncGitlinkPath -State $state -Paths $dirty

        # A directive's file set is intersected with THIS repo's dirty paths: one call covers several
        # repos, and a pathspec naming a path this repo does not have would fail the commit outright.
        $requested = @()
        if ($script:CommitDirective) {
            $candidates = if ($script:CommitDirective.FilesAll) { $dirty } else { @($script:CommitDirective.Files) }
            $requested = @($dirty | Where-Object { $candidates -contains $_ })
        }

        if ($requested.Count -gt 0) {
            if ($script:CommitDirective.FilesAll -and $state.Staged.Count -gt 0 -and -not $script:CommitDirective.IncludeStaged) {
                # "all" cannot express "except another actor's staged entries", and absorbing them
                # silently is exactly the expressed-intent destruction the staged split exists to
                # prevent, so this blocks rather than guessing.
                Add-SyncStagedBlock -RepoKey $Repo.Key -State $state -Operation 'files:"all" commit'
                return
            }
            if ($planned) {
                Add-SyncAction -Repo $Repo.Key -Action 'commit' -Detail "Commit $($requested.Count) path(s) with the caller's message" -Planned $true
            }
            else {
                $committed = Invoke-SyncLockedCommit -RepoPath $state.Full -Paths $requested `
                    -Message (New-SyncCommitMessage -Summary $script:CommitDirective.Message) `
                    -UsePathspec (-not $script:CommitDirective.IncludeStaged)
                Assert-SyncGitOk -Result $committed -Repo $Repo.Key -Operation 'commit'
                $script:CapabilitiesUsed['git:commit'] = $true
                Add-SyncAction -Repo $Repo.Key -Action 'commit' -Detail "Committed $($requested.Count) path(s): $($script:CommitDirective.Message)" -Planned $false
                $state = Get-SyncRepoState -Repo $Repo
                if (Test-SyncRepoDirty -State $state) {
                    Add-SyncUncommittedBlock -RepoKey $Repo.Key -Paths @(Get-SyncDirtyPath -State $state)
                    return
                }
            }
        }
        elseif ($split.Gitlinks.Count -gt 0) {
            # Rung 4's gitlink sub-case: a pointer commit's content is fully determined by the
            # checkout that produced it, so no judgment is available and the tool authors it under the
            # FLOOR, asserting no key. This is what lets a git:sync-only agent finish a submodule sync.
            $names = @($split.Gitlinks)
            $summary = if ($names.Count -eq 1) { "Update $($names[0]) submodule" } else { "Update $($names -join ', ') submodules" }
            if ($planned) {
                Add-SyncAction -Repo $Repo.Key -Action 'commit-submodule-pointer' -Detail $summary -Planned $true
            }
            else {
                $committed = Invoke-SyncLockedCommit -RepoPath $state.Full -Paths $names `
                    -Message (New-SyncCommitMessage -Summary $summary) -UsePathspec $true
                Assert-SyncGitOk -Result $committed -Repo $Repo.Key -Operation 'commit (submodule pointer)'
                Add-SyncAction -Repo $Repo.Key -Action 'commit-submodule-pointer' -Detail $summary -Planned $false
                $state = Get-SyncRepoState -Repo $Repo
                # The tool just moved HEAD with no caller directive, so the caller's token is stale
                # through no fault of its own. Every resolution emitted from here carries the fresh one.
                $script:PreApplyToken = Get-SyncStateToken -States (Get-SyncAllState)
            }

            # A MIXED tree: the gitlink paths are settled above, the caller's own content still needs
            # the caller's own message.
            if ($split.Others.Count -gt 0) {
                Add-SyncUncommittedBlock -RepoKey $Repo.Key -Paths $split.Others
                return
            }
        }
        else {
            Add-SyncUncommittedBlock -RepoKey $Repo.Key -Paths $dirty
            return
        }
    }

    if (-not $state.Upstream) {
        Add-SyncBlocked -Repo $Repo.Key -Reason 'no_upstream' -Detail "No upstream is configured for $(if ($state.Branch) { "branch '$($state.Branch)'" } else { 'HEAD' }), so there is nothing to converge toward." `
            -Resolution (New-SyncRemedy -Id 'set-upstream' -Label 'Configure a remote-tracking branch by hand' -Action 'manual' -Params ([ordered]@{ repo = $Repo.Key; branch = $state.Branch }))
        return
    }

    # --- Rung 5: diverged. Probe read-only before deciding - under the retired operation enum, merely
    # discovering whether a conflict existed committed the repo to the merge state. ---
    if ($state.Ahead -gt 0 -and $state.Behind -gt 0) {
        if ($state.Staged.Count -gt 0) {
            Add-SyncStagedBlock -RepoKey $Repo.Key -State $state -Operation 'merge'
            return
        }
        Assert-SyncMergeTreeVersion
        $probe = Invoke-SyncGit -RepoPath $state.Full -Arguments @('merge-tree', '--write-tree', '--name-only', 'HEAD', $state.Upstream)
        if ($probe.ExitCode -gt 1) {
            throw (New-VaultToolError -Code 'git_failure' -Message "The merge-tree probe failed in '$($Repo.Key)': $($probe.Output)" -Data @{ repo = $Repo.Key; operation = 'merge-tree'; exitCode = $probe.ExitCode; output = $probe.Output })
        }

        if ($probe.ExitCode -eq 0) {
            # A clean merge commit's content is fully determined by its two parents, so it stays under
            # the floor: blocking an agent on a divergence it did not create is the wrong failure.
            $summary = "Merge $($state.Upstream) into $($state.Branch)"
            if ($planned) {
                Add-SyncAction -Repo $Repo.Key -Action 'merge' -Detail "$summary (predicted clean)" -Planned $true
            }
            else {
                $merged = Invoke-SyncLockedGit -RepoPath $state.Full -Arguments @('merge', '--no-ff', '-m', (New-SyncCommitMessage -Summary $summary), $state.Upstream)
                Assert-SyncGitOk -Result $merged -Repo $Repo.Key -Operation 'merge'
                Add-SyncAction -Repo $Repo.Key -Action 'merge' -Detail $summary -Planned $false
                $state = Get-SyncRepoState -Repo $Repo
            }
        }
        else {
            # The probe's conflicted-file section runs from the second line to the first blank line.
            $lines = @($probe.Output -split "`n")
            $conflicted = [System.Collections.Generic.List[string]]::new()
            for ($i = 1; $i -lt $lines.Count; $i++) {
                if ([string]::IsNullOrWhiteSpace($lines[$i])) { break }
                $conflicted.Add($lines[$i].Trim())
            }

            if (-not $script:OnConflictStrategy) {
                if (-not $script:HasCommitKey) {
                    Add-SyncCapabilityBlock -RepoKey $Repo.Key -Detail "resolving the predicted conflict in $($conflicted.Count) file(s) needs git:commit"
                    return
                }
                Add-SyncBlocked -Repo $Repo.Key -Reason 'merge_conflicts' -Detail "A merge with $($state.Upstream) is predicted to conflict in $($conflicted.Count) file(s): $(@($conflicted) -join ', '). Predicted read-only - nothing was merged and the tree is untouched." `
                    -Resolution (New-SyncRemedy -Id 'apply-conflict-strategy' -Label 'Re-issue with on_conflict: keep_local_save_remote' -Action 'sync-directive' `
                        -Params ([ordered]@{ apply = $true; on_conflict = 'keep_local_save_remote'; state_token = $script:PreApplyToken }))
                return
            }

            if ($planned) {
                Add-SyncAction -Repo $Repo.Key -Action 'resolve-conflicts' -Detail "Merge $($state.Upstream) applying keep_local_save_remote to $($conflicted.Count) predicted conflict(s)" -Planned $true
            }
            else {
                $resolution = Invoke-SyncLockedResolve -State $state
                $script:CapabilitiesUsed['git:commit'] = $true
                Add-SyncAction -Repo $Repo.Key -Action 'resolve-conflicts' -Detail "Merged $($state.Upstream) keeping local in $(@($resolution.Conflicted).Count) file(s); remote bodies saved as: $(@($resolution.Saved) -join ', ')" -Planned $false
                $state = Get-SyncRepoState -Repo $Repo
            }
        }
    }

    # --- Rung 6: remote ahead only. Fast-forward, then check out any submodule pointer the
    # fast-forward moved so the working tree matches what was just recorded. ---
    if ($state.Behind -gt 0 -and $state.Ahead -eq 0) {
        if ($state.Staged.Count -gt 0) {
            Add-SyncStagedBlock -RepoKey $Repo.Key -State $state -Operation 'fast-forward'
            return
        }
        if ($planned) {
            Add-SyncAction -Repo $Repo.Key -Action 'fast-forward' -Detail "Fast-forward $($state.Behind) commit(s) from $($state.Upstream)" -Planned $true
        }
        else {
            $before = Get-SyncGitlinkSha -State $state
            $ff = Invoke-SyncLockedGit -RepoPath $state.Full -Arguments @('merge', '--ff-only', $state.Upstream)
            Assert-SyncGitOk -Result $ff -Repo $Repo.Key -Operation 'merge --ff-only'
            Add-SyncAction -Repo $Repo.Key -Action 'fast-forward' -Detail "Fast-forwarded $($state.Behind) commit(s) from $($state.Upstream)" -Planned $false

            $after = Get-SyncGitlinkSha -State $state
            $moved = @($after.Keys | Where-Object { -not $before.ContainsKey($_) -or $before[$_] -ne $after[$_] })
            if ($moved.Count -gt 0) {
                $checkout = Invoke-SyncLockedGit -RepoPath $state.Full -Arguments (@('submodule', 'update', '--init', '--') + $moved)
                Assert-SyncGitOk -Result $checkout -Repo $Repo.Key -Operation 'submodule update'
                Add-SyncAction -Repo $Repo.Key -Action 'checkout-submodule' -Detail "Checked out submodule pointer(s) the fast-forward moved: $($moved -join ', ')" -Planned $false
            }
            $state = Get-SyncRepoState -Repo $Repo
        }
    }

    # --- Rung 7: local ahead only. Push publishes agent-authored content, so it needs the key. ---
    if ($state.Ahead -gt 0 -and $state.Behind -eq 0) {
        if (-not $script:HasCommitKey) {
            Add-SyncCapabilityBlock -RepoKey $Repo.Key -Detail "pushing $($state.Ahead) local commit(s) needs git:commit"
            return
        }
        if ($planned) {
            Add-SyncAction -Repo $Repo.Key -Action 'push' -Detail "Push $($state.Ahead) commit(s) to $($state.Upstream)" -Planned $true
            return
        }
        Assert-VaultOpClass -OpClass 'git:commit' -Data @{ requiredClass = 'git:commit'; repo = $Repo.Key; operation = 'push' }
        $script:CapabilitiesUsed['git:commit'] = $true
        $push = Invoke-SyncLockedGit -RepoPath $state.Full -Arguments @('push')
        if ($push.ExitCode -eq 0) {
            Add-SyncAction -Repo $Repo.Key -Action 'push' -Detail "Pushed $($state.Ahead) commit(s) to $($state.Upstream)" -Planned $false
            return
        }
        if (Test-SyncNetworkFailure -Output $push.Output) {
            Add-SyncBlocked -Repo $Repo.Key -Reason 'network_unavailable' -Detail "The remote was unreachable during push: $($push.Output)" `
                -Resolution (New-SyncRemedy -Id 'retry-when-online' -Label 'Re-call once the remote is reachable' -Action 'retry' -Params ([ordered]@{ apply = $true }))
            return
        }
        if ($push.Output -match 'rejected|non-fast-forward|fetch first') {
            # Re-calling is the recovery path: convergence is idempotent, so the next call
            # re-converges against the moved remote.
            Add-SyncBlocked -Repo $Repo.Key -Reason 'push_rejected' -Detail "The remote moved between the fetch and the push: $($push.Output)" `
                -Resolution (New-SyncRemedy -Id 'reconverge' -Label 'Re-call to re-converge against the moved remote' -Action 'retry' -Params ([ordered]@{ apply = $true }))
            return
        }
        Assert-SyncGitOk -Result $push -Repo $Repo.Key -Operation 'push'
    }
}

# ---------------------------------------------------------------------------
# VALIDATE. Argument shape and directive guards first - outside any lock and before the fetch - so a
# malformed call refuses without touching the network or a working tree.
# ---------------------------------------------------------------------------

$script:CommitDirective = $null
if (-not [string]::IsNullOrWhiteSpace($Commit)) {
    try {
        # -NoEnumerate is load-bearing: without it a JSON ARRAY unrolls and its single element
        # becomes indistinguishable from a caller who sent one object, so the array refusal below
        # would never fire (Tool-Contracts: Argument Convention).
        $parsed = ConvertFrom-Json -InputObject $Commit -NoEnumerate
    }
    catch {
        throw (New-VaultToolError -Code 'malformed_op' -Message "commit is not valid JSON: $($_.Exception.Message)" -Data @{ reason = 'commit is not valid JSON' })
    }
    if ($parsed -is [array] -or $parsed -isnot [pscustomobject]) {
        throw (New-VaultToolError -Code 'malformed_op' -Message 'commit must be a JSON object, e.g. {"message":"...","files":["Path.md"]}.' -Data @{ reason = 'commit is not a JSON object' })
    }
    $names = @($parsed.PSObject.Properties.Name)
    if ($names -notcontains 'message' -or [string]::IsNullOrWhiteSpace([string]$parsed.message)) {
        throw (New-VaultToolError -Code 'malformed_op' -Message 'commit.message is required and must be a non-empty string - the tool never authors a commit message for caller content.' -Data @{ reason = 'commit.message is missing or empty' })
    }
    if ($names -notcontains 'files') {
        throw (New-VaultToolError -Code 'malformed_op' -Message 'commit.files is required - either an explicit array of repo-relative paths or the string "all".' -Data @{ reason = 'commit.files is missing' })
    }
    $filesAll = $false
    $files = @()
    if ($parsed.files -is [string]) {
        if ($parsed.files.Trim().ToLowerInvariant() -ne 'all') {
            throw (New-VaultToolError -Code 'malformed_op' -Message 'commit.files as a string accepts only "all"; otherwise pass an array of paths.' -Data @{ reason = 'commit.files string is not "all"' })
        }
        $filesAll = $true
    }
    else {
        $files = @(@($parsed.files) | ForEach-Object { "$_".Replace('\', '/').Trim() } | Where-Object { $_ })
        if ($files.Count -eq 0) {
            throw (New-VaultToolError -Code 'malformed_op' -Message 'commit.files is an empty set - name at least one path, or pass "all".' -Data @{ reason = 'commit.files is empty' })
        }
    }
    $includeStaged = $false
    if ($names -contains 'staged') {
        $stagedValue = "$($parsed.staged)".Trim().ToLowerInvariant()
        if ($stagedValue -ne 'include') {
            throw (New-VaultToolError -Code 'malformed_op' -Message 'commit.staged accepts only "include".' -Data @{ reason = 'commit.staged is not "include"' })
        }
        $includeStaged = $true
    }
    # Conditional requiredness, enforced in the script body because the derived schema cannot express
    # it: an explicit file array pins the commit set structurally, so only "all" - where nothing else
    # defines the set - needs the token.
    if ($filesAll -and [string]::IsNullOrWhiteSpace($StateToken)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'state_token is required when commit.files is "all": nothing else pins which files the message authorizes. Call sync with no arguments to obtain one.' -Data @{ param = 'state_token' })
    }
    $script:CommitDirective = [pscustomobject]@{
        Message       = [string]$parsed.message
        Files         = $files
        FilesAll      = $filesAll
        IncludeStaged = $includeStaged
    }
}

$script:OnConflictStrategy = $null
if (-not [string]::IsNullOrWhiteSpace($OnConflict)) {
    $normalized = $OnConflict.Trim().ToLowerInvariant()
    if ($normalized -ne 'keep_local_save_remote') {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "on_conflict '$OnConflict' is not a recognized strategy - sync accepts 'keep_local_save_remote' in V1." -Data @{ param = 'on_conflict'; value = $OnConflict })
    }
    $script:OnConflictStrategy = $normalized
}

# --- Capability posture. The EXPLICIT half of the split fires here, at validation, so a denied
# directive mutates nothing; the INCIDENTAL half is reported per repo as a blocked state. ---
$injectedClasses = Get-Variable -Name VaultMcpOpClasses -Scope Global -ValueOnly -ErrorAction SilentlyContinue
$script:HasCommitKey = ($null -eq $injectedClasses) -or (@($injectedClasses) -contains 'git:commit')
if ($script:CommitDirective -or $script:OnConflictStrategy) {
    Assert-VaultOpClass -OpClass 'git:commit' -Data @{ requiredClass = 'git:commit'; directive = $(if ($script:CommitDirective) { 'commit' } else { 'on_conflict' }) }
    $script:CapabilitiesUsed['git:commit'] = $true
}

# Vault resolution before any lock: Invoke-VaultWriteLock derives its mutex name from the resolved
# root and would raise the mirrored core's plain text before a locked scriptblock ever ran.
$resolvedVault = Resolve-VaultToolRoot -Vault $Vault
$script:VaultRoot = $resolvedVault.Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

$repoProbe = Invoke-SyncGit -RepoPath $script:VaultRoot -Arguments @('rev-parse', '--git-dir')
if ($repoProbe.ExitCode -ne 0) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "Vault '$($resolvedVault.Name)' is not a git repository, so there is nothing for sync to converge." -Data @{ vault = $resolvedVault.Name; root = $script:VaultRoot })
}

$script:RepoTree = @(Get-SyncRepoTree)

# ---------------------------------------------------------------------------
# FETCH - unlocked, because it mutates remote-tracking refs only, which are invisible to every vault
# reader. A failure does not fail the call: local state is still reported, with a blocked entry
# naming the unreachable remote.
# ---------------------------------------------------------------------------

$script:FetchFailed = @{}
foreach ($repo in $script:RepoTree) {
    $upstreamCheck = Invoke-SyncGit -RepoPath $repo.Full -Arguments @('rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{upstream}')
    if ($upstreamCheck.ExitCode -ne 0) { continue }
    $fetch = Invoke-SyncGit -RepoPath $repo.Full -Arguments @('fetch', '--prune')
    if ($fetch.ExitCode -eq 0) { continue }
    $script:FetchFailed[$repo.Key] = $true
    if (Test-SyncNetworkFailure -Output $fetch.Output) {
        Add-SyncBlocked -Repo $repo.Key -Reason 'network_unavailable' -Detail "The remote could not be reached, so this report describes local state only: $($fetch.Output)" `
            -Resolution (New-SyncRemedy -Id 'retry-when-online' -Label 'Re-call once the remote is reachable' -Action 'retry' -Params ([ordered]@{ apply = $Apply }))
    }
    else {
        Add-SyncBlocked -Repo $repo.Key -Reason 'fetch_failed' -Detail "git fetch failed, so this report describes local state only: $($fetch.Output)" `
            -Resolution (New-SyncRemedy -Id 'retry-fetch' -Label 'Re-call once the fetch failure is resolved' -Action 'retry' -Params ([ordered]@{ apply = $Apply }))
    }
}

# ---------------------------------------------------------------------------
# ASSESS and gate. The token is composed post-fetch, so its upstream input reflects the remote tip
# the ladder is about to decide against.
# ---------------------------------------------------------------------------

$preStates = Get-SyncAllState
$script:PreApplyToken = Get-SyncStateToken -States $preStates

if (-not [string]::IsNullOrWhiteSpace($StateToken)) {
    if ($StateToken.Trim() -ne $script:PreApplyToken) {
        # The gate can only fail closed: a stale token after compaction and a fabricated one both miss
        # the comparison. The fresh token rides along so the retry is one call.
        throw (New-VaultToolError -Code 'state_mismatch' -Message "state_token '$($StateToken.Trim())' does not describe the repository state as it is now - another actor moved it between calls. Nothing was mutated; re-read the state and re-issue with the token in data." -Data @{ suppliedToken = $StateToken.Trim(); stateToken = $script:PreApplyToken })
    }
}

# ---------------------------------------------------------------------------
# CONVERGE. Deepest-first, so each submodule reaches a settled state before its parent records the
# pointer; nested submodules fall out of the depth ordering rather than needing a special case.
# ---------------------------------------------------------------------------

$ordered = @($script:RepoTree | Sort-Object -Property @{ Expression = 'Depth'; Descending = $true }, @{ Expression = 'Key'; Descending = $false })
foreach ($repo in $ordered) {
    Invoke-SyncRepoLadder -Repo $repo -DoApply $Apply
}

# ---------------------------------------------------------------------------
# REPORT. State and token are recomputed post-apply, so a tool-authored submodule-pointer commit
# hands back a usable token rather than invalidating the caller's.
# ---------------------------------------------------------------------------

$finalStates = if ($Apply) { Get-SyncAllState } else { $preStates }
$finalToken = if ($Apply) { Get-SyncStateToken -States $finalStates } else { $script:PreApplyToken }

$reposOut = [ordered]@{}
$allLevel = $true
foreach ($key in $finalStates.Keys) {
    $s = $finalStates[$key]
    $reposOut[$key] = [ordered]@{
        head          = $s.Head
        branch        = $s.Branch
        detached      = [bool]$s.Detached
        ahead         = $s.Ahead
        behind        = $s.Behind
        staged        = @($s.Staged)
        unstaged      = @($s.Unstaged)
        untracked     = @($s.Untracked)
        midMerge      = [bool]$s.MidMerge
        unmergedFiles = @($s.Unmerged)
    }
    if ($s.Ahead -ne 0 -or $s.Behind -ne 0 -or $s.Detached -or $s.MidMerge -or (Test-SyncRepoDirty -State $s)) { $allLevel = $false }
}

$result = [ordered]@{
    # The field a caller branches on, replacing the retired enum's pullStatus.
    synced                 = ($allLevel -and $script:Blocked.Count -eq 0)
    repos                  = $reposOut
    actions                = @($script:Actions)
    plannedActions         = @($script:PlannedActions)
    blocked                = @($script:Blocked)
    stateToken             = $finalToken
    # Write-class envelope: always present, [] when empty. filesSkippedUnreadable can never be
    # populated here - sync runs no enumerate-then-read loop over notes - but the caller still reads
    # one shape.
    capabilitiesUsed       = @($script:CapabilitiesUsed.Keys)
    filesSkippedUnreadable = @()
}

# lockWarning is present ONLY when a span inherited an abandoned mutex and is omitted entirely
# otherwise - the envelope's deliberate exception to always-present (Tool-Contracts: Return
# Convention): it is a warning, not a status field.
if ($script:LockAbandoned) {
    $result['lockWarning'] = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
