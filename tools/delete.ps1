<#
.SYNOPSIS
Removes one file or folder from the vault as a single write-locked plan-validate-apply operation.

Call this when:
- a file or folder should stop existing. The target must exist - nothing at the path is not_found,
  never a silent no-op
- you are removing a folder that has contents. Pass recursive true; it is required for a non-empty
  folder and refused against a file target rather than ignored
- you want the blast radius before committing to it. apply:false removes nothing and returns the
  identical report - the full file list, hidden files, the dangling-backlink census, and the
  subset git cannot restore - with `applied` distinguishing a plan from a result. Mutation is the
  default here, so a single-file delete needs no ceremony

Not for relocating or renaming - that is move, which rewrites every reference; deleting and
re-creating breaks every link that pointed at the old path. Not for emptying a note while keeping
the file - that is edit with an empty body. A nested repository under the target is refused rather
than descended into, so removing a submodule or a vendored clone is not this tool's job.

The folder case enumerates its contents first, validates the whole set, and only then removes - so
filesDeleted[] is a declared plan rather than a post-hoc discovery. The lock serializes the
operation against other writers; it does not make it atomic - a folder delete that fails partway
raises partial_state naming the files already removed.

There is deliberately no force/purge parameter: git is the undo, and the tool never offers a
git-bypassing hard delete. Because git is the backstop, the return names the subset git cannot
restore (unrecoverableFiles) rather than letting recoveryHint imply cover it does not have. See the
Delete corpus doc (vault-primary) for the full contract.
.PARAMETER path
Vault-relative path of the file or folder to delete. Taken verbatim after separator
normalization - ".md" is NEVER inferred, so "Foo" and "Foo.md" are different values. Nothing
at the path is not_found: a missing sole target is a real error, never a silent no-op.
.PARAMETER kind
Intent assertion - "file" or "folder" - inferred from the target's shape when omitted; fails
fast (kind_mismatch) when supplied and wrong. Deliberately not a ValidateSet, so every value
reaches the script body and an unrecognized one is refused with delete's own invalid_argument
JSON error rather than a PowerShell binding error.
.PARAMETER recursive
Folder-only: required $true to delete a folder that has any contents. Supplied against a file
target it is refused invalid_argument (inert-param fail-fast) rather than ignored - silently
accepting a flag that does nothing is how a caller's wrong mental model survives a call. It is
NOT an override for the nested-repository refusal.
.PARAMETER apply
Mutation opt-in, default TRUE - uniquely among the tools that carry it, because for delete
mutation is the point of the call and a single-file delete should not need a ceremony. False
runs plan and validate and returns the identical report - the complete blast radius, hidden
files and unrecoverableFiles included - without removing anything; `applied` is the single
field that distinguishes a plan from a result. A plan still takes the write lock and still
refuses everything a real call would refuse.
.PARAMETER vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:write

delete asserts agentic:write in-script when ANY .md file in the target set carries a frontmatter
key under the agent.* / nav.* prefixes. Deletion is the other degenerate case of edit's
outcome diff - the diff is the removal of the note's entire frontmatter - so without the key a
content:write agent could shed a pinned: constraint by deleting its carrier note. Classification
is validate-phase over the enumerated set, so a denial is insufficient_capability with zero
removal, naming the offending paths. Standalone (no injected class set) degrades open; an
injected-but-empty set denies.

Two enumerations, deliberately: the destruction plan comes from a direct filesystem walk
(Get-ChildItem -Force), so dot-files and gitignored files are IN the plan; the danglingBacklinks
census keeps the shared ripgrep layers, because that is a link-graph question over agent-visible
files. The return carries the write-class envelope: capabilitiesUsed and filesSkippedUnreadable[]
always ([] when empty), lockWarning only when an abandoned mutex was inherited.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $path,
    [string] $kind,
    [bool] $recursive = $false,
    [bool] $apply = $true,
    [string] $vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# Presence is decided here, in script scope, from $PSBoundParameters: -Recursive $false and an
# unsupplied -Recursive are the same value but not the same request, and only the supplied one
# is refused as inert against a file target.
$script:RecursiveBound = $PSBoundParameters.ContainsKey('recursive') -and $null -ne $Recursive

# ---------------------------------------------------------------------------
# Link grammar and its comparators are the module's (VaultAccess Public/LinkResolve.ps1),
# shared with move and lint. delete uses them report-only - it identifies links that pointed at
# a deleted target and never rewrites one, because a deletion leaves no new name to point at.
# The pattern is bound to a script variable so the match call site below stays unchanged.
# ---------------------------------------------------------------------------
$script:VaultLinkPattern = Get-VaultLinkPattern

function Get-VaultDeleteFolderPlan {
    <#
    .SYNOPSIS
    Walks a folder target with Get-ChildItem -Force and returns @{ Files; Dirs } as
    vault-relative paths - the destruction plan.

    -Force (and a direct filesystem walk rather than the shared ripgrep census) is the invariant
    this function protects: the census hides dot-files and honors .gitignore, which would make a
    ".placeholder"-only folder enumerate as empty, under-report filesDeleted[], and run the
    agentic classification over a set that excludes exactly the gitignored notes it must cover.

    The walk REFUSES rather than descends when it meets a nested repository root (a ".git"
    directory or file below the target): removing a submodule or a vendored clone is deliberate
    work that does not belong to a vault-content primitive, and enumerating one would bury the
    real plan in thousands of object files that are all untracked from the parent repo's view.
    Refusing at the entry, before descending, is also what keeps the refusal cheap.
    #>
    param(
        [Parameter(Mandatory)] [string] $RelPath,
        # Empty is the "configured default vault" spelling every chokepoint here accepts.
        [AllowEmptyString()] [string] $Vault
    )

    $files = [System.Collections.Generic.List[string]]::new()
    $dirs = [System.Collections.Generic.List[string]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($RelPath)

    while ($queue.Count -gt 0) {
        $currentRel = $queue.Dequeue()
        $currentFull = Resolve-VaultPath -Path $currentRel -Vault $Vault
        foreach ($entry in @(Get-ChildItem -LiteralPath $currentFull -Force -ErrorAction Stop)) {
            $entryRel = "$currentRel/$($entry.Name)"
            if ($entry.Name -eq '.git') {
                throw (New-VaultToolError -Code 'nested_repository' -Message "'$RelPath' contains a nested repository root at '$entryRel'. delete refuses to enumerate or remove one, and recursive does not override this - removing a submodule or a vendored clone is deliberate work outside a vault-content primitive." -Data @{ path = $RelPath; nestedRepository = $entryRel })
            }
            if ($entry.PSIsContainer) {
                $dirs.Add($entryRel)
                $queue.Enqueue($entryRel)
            }
            else {
                $files.Add($entryRel)
            }
        }
    }

    @{ Files = $files; Dirs = $dirs }
}

function Get-VaultDeleteTrackedFile {
    <#
    .SYNOPSIS
    The subset of the target set git has tracked, as a case-insensitive set of vault-relative
    paths - or $null when the vault is not a git repository (or git is unavailable), which is
    the "git can restore nothing here" answer rather than "everything is fine".

    `git ls-files` is the whole authority, not `git check-ignore`: unrecoverable means UNTRACKED,
    and a gitignored file is untracked by definition, so check-ignore is subsumed - and it would
    misclassify a force-added ignored file, which git tracks and can restore.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $PathSpec
    )

    $tracked = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    # git is an external dependency whose absence must degrade rather than fail the delete, so
    # this is the one place the tool's Stop preference is relaxed.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # ":(literal)" disables pathspec globbing, so a filename carrying "*" or "[" is matched
        # as itself. A directory pathspec matches everything beneath it.
        $raw = & git -C $Root ls-files -z -- ":(literal)$PathSpec" 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
    }
    catch {
        return $null
    }
    finally {
        $ErrorActionPreference = $previous
    }

    foreach ($entry in (($raw -join "`n").Split([char]0))) {
        if (-not [string]::IsNullOrWhiteSpace($entry)) { [void]$tracked.Add($entry.Replace('\', '/')) }
    }
    $tracked
}

# Vault resolution before the write lock: Invoke-VaultWriteLock derives its mutex name from the
# resolved root, so it resolves the vault itself and would raise the mirrored core's plain text
# before the locked scriptblock ever runs. Resolving here refuses an unconfigured name as
# unknown_vault and a configured-but-root-missing vault as vault_unavailable (Tool-Contracts:
# Error Convention), with nothing locked.
$resolvedVault = Resolve-VaultToolRoot -Vault $Vault

$lockAbandoned = $false
$result = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$lockAbandoned) -ScriptBlock {
    # Required: the script-scope preference does not reach cmdlets invoked from inside a
    # module-function scriptblock, and without it a failed mutation is non-terminating and gets
    # reported as success.
    $ErrorActionPreference = 'Stop'

    # =======================================================================
    # VALIDATE. Argument shape, then the operand's state, then the full enumerated target set -
    # every refusal this tool owns fires before the first removal, so an apply:false plan and a
    # real call refuse identically.
    # =======================================================================

    # --- kind: narrowed before anything touches the vault. An unrecognized value is delete's own
    # coded refusal rather than a binding error. ---
    $normalizedKind = if ($Kind) { $Kind.Trim().ToLowerInvariant() } else { '' }
    if ($normalizedKind -and $normalizedKind -notin @('file', 'folder')) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "kind '$Kind' is not a recognized value - delete accepts 'file' or 'folder'." -Data @{ kind = $Kind })
    }

    $targetPath = $Path.Trim().Replace('\', '/').Trim('/')
    if ([string]::IsNullOrWhiteSpace($targetPath)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'path is empty.' -Data @{ path = $Path })
    }

    # --- A rooted or vault-escaping path is a malformed operand, not a miss. ---
    try {
        $fullPath = Resolve-VaultPath -Path $targetPath -Vault $Vault
    }
    catch {
        throw (New-VaultToolError -Code 'invalid_argument' -Message $_.Exception.Message -Data @{ path = $targetPath })
    }

    # --- Resolve the operand: not_found on a miss (a missing sole target is a real error, never
    # a silent no-op), then determine its shape. ".md" is never inferred, so the path is looked
    # up exactly as the caller spelled it. ---
    $isFile = Test-Path -LiteralPath $fullPath -PathType Leaf
    $isFolder = (-not $isFile) -and (Test-Path -LiteralPath $fullPath -PathType Container)
    if (-not $isFile -and -not $isFolder) {
        throw (New-VaultToolError -Code 'not_found' -Message "Nothing at path: $targetPath" -Data @{ path = $targetPath })
    }
    $resolvedKind = if ($isFile) { 'file' } else { 'folder' }

    if ($normalizedKind -and $normalizedKind -ne $resolvedKind) {
        throw (New-VaultToolError -Code 'kind_mismatch' -Message "Asserted kind '$normalizedKind' does not match the target's resolved shape '$resolvedKind'." -Data @{ assertedKind = $normalizedKind; resolvedKind = $resolvedKind })
    }

    # --- Inert-param fail-fast: recursive is meaningless against a file and is refused rather
    # than ignored, so a caller who believes it is deleting a folder learns otherwise here. ---
    if ($resolvedKind -eq 'file' -and $script:RecursiveBound) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "recursive is inert for a file target - it guards folder deletes only. Drop it, or pass the folder's path." -Data @{ path = $targetPath; kind = 'file'; param = 'recursive' })
    }

    # --- Scope membership gates the OPERAND only: a folder's contents are inside it by
    # construction, and the danglingBacklinks scan is deliberately vault-wide and unscoped
    # (mirroring move's link machinery, and report-only here). Absent or empty roots degrade
    # open - a standalone run injects no VaultMcpScopeRoots at all; read through Get-Variable
    # because under Set-StrictMode an unset global is an error. ---
    $ambientScopeRoots = Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolvedVault.Name -ScopeRoots $ambientScopeRoots
    if ($null -ne $flatScopeRoots -and $flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $targetPath -ScopeRoots $flatScopeRoots)) {
        throw (New-VaultToolError -Code 'out_of_scope' -Message "Path is outside the grant's scope for vault `"$($resolvedVault.Name)`": $targetPath" -Data @{ vault = $resolvedVault.Name; path = $targetPath })
    }

    # --- PLAN. The destruction plan, from a direct filesystem walk for a folder (dot-files and
    # gitignored files included) or the single path for a file. nested_repository fires inside
    # the walk. ---
    $planFiles = [System.Collections.Generic.List[string]]::new()
    $planDirs = [System.Collections.Generic.List[string]]::new()
    if ($resolvedKind -eq 'folder') {
        $plan = Get-VaultDeleteFolderPlan -RelPath $targetPath -Vault $Vault
        $planFiles = $plan.Files
        $planDirs = $plan.Dirs
    }
    else {
        $planFiles.Add($targetPath)
    }

    # --- unrecoverableFiles: the subset of the plan git cannot restore. This is what stops
    # recoveryHint from being a lie - git is the whole reason delete needs no dedicated
    # destructive op class, and it covers only what it tracks. A non-repo vault (or no git on
    # PATH) makes the WHOLE plan unrecoverable, which is the honest answer, not an empty list. ---
    $trackedFiles = Get-VaultDeleteTrackedFile -Root $resolvedVault.Root -PathSpec $targetPath
    $vaultIsRepo = ($null -ne $trackedFiles)
    $unrecoverableFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($rel in $planFiles) {
        if (-not $vaultIsRepo -or -not $trackedFiles.Contains($rel)) { $unrecoverableFiles.Add($rel) }
    }

    # --- folder_not_empty: the guard the enumeration above exists to make honest. Contents means
    # anything the walk found, hidden and gitignored included, so a ".placeholder"-only triad
    # folder trips this rather than reading as empty. The data carries unrecoverableFiles so the
    # refusal itself shows what a recursive retry would destroy beyond git's reach. ---
    if ($resolvedKind -eq 'folder' -and -not $Recursive) {
        $childCount = $planFiles.Count + $planDirs.Count
        if ($childCount -gt 0) {
            $sample = @(@($planFiles) + @($planDirs) | Sort-Object | Select-Object -First 5)
            throw (New-VaultToolError -Code 'folder_not_empty' -Message "Folder '$targetPath' is not empty ($childCount item(s), hidden and gitignored files included) - pass recursive: true to delete it and everything under it." -Data @{
                    path               = $targetPath
                    childCount         = $childCount
                    sampleChildren     = $sample
                    unrecoverableFiles = $unrecoverableFiles.ToArray()
                })
        }
    }

    # --- Agentic classification over the enumerated target set. Deleting a note removes its whole
    # frontmatter, so the outcome diff against an empty result is exactly "which agent.*/nav.*
    # keys does this note carry" - the same shared predicate edit and create classify on, reached
    # by its other degenerate case. Without this a content:write agent could shed a pinned:
    # constraint by deleting its carrier note. Validate-phase, so a denial removes nothing. ---
    $agenticPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($rel in $planFiles) {
        if (-not $rel.ToLowerInvariant().EndsWith('.md')) { continue }
        $relFull = Resolve-VaultPath -Path $rel -Vault $Vault
        try {
            $noteContent = [System.IO.File]::ReadAllText($relFull)
        }
        catch {
            # A target note that is PRESENT but unreadable blocks the delete: the classification
            # cannot run, and proceeding would remove a note whose agentic layer was never
            # checked. Only a target that vanished between the walk and this read is passed over
            # - there is nothing left to classify or to delete, and it is not a scan skip.
            if (Test-VaultNoteMissingError -ErrorRecord $_) { continue }
            throw (New-VaultToolError -Code 'read_failure' -Message "Could not read target note '$rel' for agentic classification: $($_.Exception.Message)" -Data @{ path = $rel; error = $_.Exception.Message })
        }
        $agenticKeys = Get-VaultAgenticFrontmatterDiff -BeforeContent $noteContent -AfterContent ''
        if ($agenticKeys.Count -gt 0) { $agenticPaths.Add($rel) }
    }

    $capabilitiesUsed = @()
    if ($agenticPaths.Count -gt 0) {
        # The refusal names the offending paths at the throw - the error convention forbids
        # catching a chokepoint's error to add coordinates afterwards.
        Assert-VaultOpClass -OpClass 'agentic:write' -Data @{ requiredClass = 'agentic:write'; paths = $agenticPaths.ToArray() }
        $capabilitiesUsed = @('agentic:write')
    }

    # --- Read-only pre-flight: every file in the plan must be writable, caught before the first
    # removal so a read-only file partway through cannot leave a half-deleted folder. ---
    $readOnlyFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($rel in $planFiles) {
        $relFull = Resolve-VaultPath -Path $rel -Vault $Vault
        if ((Test-Path -LiteralPath $relFull) -and (([System.IO.File]::GetAttributes($relFull) -band [System.IO.FileAttributes]::ReadOnly) -ne 0)) {
            $readOnlyFiles.Add($rel)
        }
    }
    if ($readOnlyFiles.Count -gt 0) {
        throw (New-VaultToolError -Code 'readonly_targets' -Message "The delete cannot proceed: $($readOnlyFiles.Count) target file(s) are read-only: $($readOnlyFiles.ToArray() -join ', ')." -Data @{ readOnlyFiles = $readOnlyFiles.ToArray() })
    }

    # --- danglingBacklinks: the one place delete keeps the shared ripgrep census, because this
    # is a link-graph question and agent-visible files are its correct domain. Report-only and
    # vault-wide unscoped - delete cannot rewrite these links (there is no new name to point at),
    # so it surfaces them instead. Files inside the plan are not scanned: their own links go away
    # with them. ---
    $deletedPathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($rel in $planFiles) { [void]$deletedPathSet.Add($rel) }

    $deletedComponents = [System.Collections.Generic.List[string[]]]::new()
    $deletedBasenameKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($rel in $planFiles) {
        $deletedComponents.Add(((Get-VaultLinkKey -Name $rel) -split '/'))
        [void]$deletedBasenameKeys.Add((Get-VaultLinkKey -Name (($rel -split '/')[-1])))
    }

    $allFiles = @(Get-VaultFilePath -Vault $Vault -ScopeRoots $null)
    $censusCache = @{}

    $danglingBacklinks = [System.Collections.Generic.List[object]]::new()
    # Paths, not a count (Tool-Contracts: Return Convention) - the count is derivable and the
    # paths are the actionable half.
    $filesSkippedUnreadable = [System.Collections.Generic.List[string]]::new()

    foreach ($rel in (Get-VaultNotePath -Vault $Vault -ScopeRoots $null)) {
        if ($deletedPathSet.Contains($rel)) { continue }
        $full = Resolve-VaultPath -Path $rel -Vault $Vault
        try {
            $content = [System.IO.File]::ReadAllText($full)
        }
        catch {
            # Vanished between enumeration and read (or a dangling symlink) - skip and record the
            # path. A present-but-unreadable file is a real failure, not a skip.
            if (Test-VaultNoteMissingError -ErrorRecord $_) { $filesSkippedUnreadable.Add($rel); continue }
            throw (New-VaultToolError -Code 'read_failure' -Message "Could not read '$rel' during the backlink scan: $($_.Exception.Message)" -Data @{ path = $rel; error = $_.Exception.Message })
        }

        foreach ($m in $script:VaultLinkPattern.Matches($content)) {
            $key = Get-VaultLinkKey -Name $m.Groups['target'].Value
            $hit = $false
            if ($key.Contains('/')) {
                # Path-qualified: a component-aligned suffix match is definitive.
                $candidate = $key -split '/'
                foreach ($target in $deletedComponents) {
                    if (Test-VaultPathSuffixMatch -Target $target -Candidate $candidate) { $hit = $true; break }
                }
            }
            elseif ($deletedBasenameKeys.Contains($key)) {
                # Bare: resolve it the way Obsidian would (Resolve-VaultBareLink, shared with
                # move) and report only when the winner is a file being deleted - a same-basename
                # bystander elsewhere in the vault is the link's real target and is untouched.
                if (-not $censusCache.ContainsKey($key)) {
                    $censusCache[$key] = @($allFiles | Where-Object { (Get-VaultLinkKey -Name (($_ -split '/')[-1])) -ieq $key })
                }
                $winner = Resolve-VaultBareLink -LinkingFile $rel -Candidates $censusCache[$key]
                if ($null -ne $winner -and $deletedPathSet.Contains($winner)) { $hit = $true }
            }
            if ($hit) {
                $danglingBacklinks.Add([ordered]@{ file = $rel; link = $m.Groups['target'].Value })
            }
        }
    }

    # --- Folder-note invariant reporting. Both fields are always present so the caller reads one
    # shape; each is structurally false for the other kind. ---
    $targetBasename = ($targetPath -split '/')[-1]
    $folderNoteDeleted = $false
    $folderNoteOrphaned = $false
    if ($resolvedKind -eq 'folder') {
        $folderNoteDeleted = [bool]($planFiles | Where-Object { $_ -ieq "$targetPath/$targetBasename.md" })
    }
    else {
        # Deleting Foo/Foo.md while Foo/ survives with content breaks the folder-note invariant.
        # Warn-and-proceed: delete says so and leaves the folder for lint's completeness pass.
        $components = $targetPath -split '/'
        if ($components.Count -ge 2 -and $targetBasename.ToLowerInvariant().EndsWith('.md')) {
            $parentRel = ($components[0..($components.Count - 2)]) -join '/'
            $noteName = $targetBasename.Substring(0, $targetBasename.Length - 3)
            if ($noteName -ieq $components[$components.Count - 2]) {
                $parentFull = Resolve-VaultPath -Path $parentRel -Vault $Vault
                $survivors = @(Get-ChildItem -LiteralPath $parentFull -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -ine $targetBasename })
                $folderNoteOrphaned = ($survivors.Count -gt 0)
            }
        }
    }

    # --- recoveryHint. Qualified, never blanket: it names the git command that restores what git
    # tracked, and says plainly when part (or all) of the plan is beyond it. ---
    $recoveryHint = if (-not $vaultIsRepo) {
        "The vault is not a git repository (or git is unavailable), so nothing deleted here can be recovered - every file in filesDeleted is listed in unrecoverableFiles."
    }
    elseif ($unrecoverableFiles.Count -gt 0) {
        "Recover the tracked files with: git checkout -- '$targetPath'. This does NOT cover the $($unrecoverableFiles.Count) untracked or gitignored file(s) in unrecoverableFiles - git never had them, so they are gone."
    }
    else {
        "Recover with: git checkout -- '$targetPath'"
    }

    $report = [ordered]@{
        applied            = $Apply
        path               = $targetPath
        kind               = $resolvedKind
        filesDeleted       = $planFiles.ToArray()
        unrecoverableFiles = $unrecoverableFiles.ToArray()
        folderNoteDeleted  = $folderNoteDeleted
        folderNoteOrphaned = $folderNoteOrphaned
        danglingBacklinks  = $danglingBacklinks.ToArray()
        # Write-class envelope: always present, [] when empty. lockWarning is the deliberate
        # exception and is attached post-lock only when it fires.
        capabilitiesUsed   = $capabilitiesUsed
        filesSkippedUnreadable = $filesSkippedUnreadable.ToArray()
        recoveryHint       = $recoveryHint
    }

    # =======================================================================
    # ASSESS-BEFORE-MUTATE. apply:false stops here: plan and validate have both run, so the
    # report is complete and identical in shape to a real call - a plan and a result differ in
    # `applied` alone. Nothing above this line has removed anything.
    # =======================================================================
    if (-not $Apply) { return $report }

    # =======================================================================
    # APPLY. Files first, one at a time, so the running $deleted list plus the untouched
    # remainder is exactly what partial_state must name; the folder's now-empty directory tree
    # goes last.
    # =======================================================================
    $deleted = [System.Collections.Generic.List[string]]::new()
    try {
        foreach ($rel in $planFiles) {
            Remove-Item -LiteralPath (Resolve-VaultPath -Path $rel -Vault $Vault) -Force -ErrorAction Stop
            $deleted.Add($rel)
        }
        if ($resolvedKind -eq 'folder') {
            Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
        }
    }
    catch {
        # Re-throw a clean {code, message, data?} error unchanged; wrap anything else as
        # partial_state only once a removal has actually landed.
        if ($_.Exception.Message -match '^\s*\{.*"code"') { throw }
        if ($deleted.Count -gt 0) {
            # The plan is walked in order, so everything past the deleted count is untouched.
            $remaining = @($planFiles | Select-Object -Skip $deleted.Count)
            throw (New-VaultToolError -Code 'partial_state' -Message "Folder delete partially applied then failed: $($deleted.Count) of $($planFiles.Count) file(s) under '$targetPath' were removed before the error: $($_.Exception.Message). Recover the tracked ones with: git checkout -- '$targetPath'. Files git never tracked are not recoverable." -Data @{
                    filesDeleted = $deleted.ToArray()
                    remaining    = $remaining
                    error        = $_.Exception.Message
                })
        }
        throw
    }

    $report
}

# lockWarning is present ONLY when the write inherited an abandoned mutex and is omitted entirely
# otherwise - the envelope's deliberate exception to always-present (Tool-Contracts: Return
# Convention): it is a warning, not a status field, and an always-present null would read as a
# checked-and-clear signal.
if ($lockAbandoned) {
    $result['lockWarning'] = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
