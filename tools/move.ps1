<#
.SYNOPSIS
Relocates or renames a file or folder within the vault as one atomic, write-locked
operation, rewriting every reference to the moved item so the link graph stays intact.
See Plan-Tool-Move (vault-primary) for the full spec. IMPLEMENTATION SLICE 2: argument
validation, source resolution, dest construction, and scope membership are implemented
below - the write-lock/plan-validate-apply structure, the ambiguity gate, the
reference-rewrite engine, and the folder-move path (directory move, folder-note sync,
prefix rewrite) are later slices (see the plan's Proposed Implementation Checklist). The
mutation itself is still stubbed: this slice validates and constructs the full target
set but performs no write.
.PARAMETER Source
Vault-relative path of the file or folder to move.
.PARAMETER Dest
Dual semantics on the "/" discriminator: a bare name (no "/") renames in place, keeping
the current parent; a value containing "/" is the full vault-relative destination. A
leading "/" targets the vault root. ".md" is optional for notes.
.PARAMETER Kind
Intent assertion - "file" or "folder" - inferred from the source's shape when omitted;
fails fast (kind_mismatch) when supplied and wrong. Deliberately not a ValidateSet: the
spec requires "project"/"area" to be rejected with move's own invalid_argument JSON error
(project/area moves carry a registry entry move does not own), not a PowerShell binding
error, so any value reaches the script body for in-script validation.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.PARAMETER Force
Overrides the reference-rewrite ambiguity gate: unforced, a move whose rewrite would
involve or create bare-name ambiguity fails in validate; forced, it proceeds with
skip-and-report and path-qualified emission. Not yet implemented in this slice.
.NOTES
OpClass: content:write

move asserts no additional op class ("key") beyond its content:write floor, even when a
reference rewrite lands inside nav.*/agent.* frontmatter - its rewrites preserve routing
intent rather than author it. See Plan-Tool-Move's Op Classes section. Runs under the
per-vault write lock as plan -> validate -> apply once the later slices land; this slice's
scriptblock performs the full validate phase (no apply yet) inside that same lock, since
validation reads link-graph-adjacent vault state (existence, occupancy, scope) that a
concurrent operation could invalidate between resolution and a later slice's apply.
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Dest,
    [string] $Kind,
    [string] $Vault,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# ---------------------------------------------------------------------------
# Dest basename validation: link-breaking characters Obsidian itself forbids in
# filenames ([, ], #, ^, |) plus Windows-invalid filename characters (reserved
# punctuation, control characters, and a trailing dot/space). Checked against the
# basename only, after dest construction - never the full path, which legitimately
# contains "/".
# ---------------------------------------------------------------------------
$script:InvalidBasenameCharsPattern = '[\[\]#\^\|<>:"/\\\?\*\x00-\x1F]'

function Test-VaultMoveDestBasenameValid {
    <#
    .SYNOPSIS
    True when $Basename contains none of the link-breaking or OS-invalid characters and
    does not end in a dot or space (both invalid trailing characters on Windows).
    #>
    param([Parameter(Mandatory)] [string] $Basename)
    (-not ($Basename -match $script:InvalidBasenameCharsPattern)) -and ($Basename -notmatch '[. ]$')
}

function Resolve-VaultMoveDestPath {
    <#
    .SYNOPSIS
    Constructs the dest vault-relative path from the raw Dest argument per the spec's dual
    bare-name/full-path semantics: a bare name (no "/") renames in place, keeping the
    source's current parent; a value containing "/" is read as the full vault-relative
    destination, with a leading "/" (vault-root spelling) normalized away. ".md" is
    appended when missing, but only when the source is itself a markdown note - a non-md
    file or a folder gets no forced extension.

    Dest is constructed, not resolved (Plan-Tool-Move): it names a target that does not yet
    exist, so this never touches the filesystem.
    #>
    param(
        [Parameter(Mandatory)] [string] $RawDest,
        [Parameter(Mandatory)] [string] $SourcePath,
        [Parameter(Mandatory)] [bool] $IsNote
    )

    $trimmed = $RawDest.Trim().Replace('\', '/')
    if ($trimmed.Contains('/')) {
        # Full vault-relative destination; a leading "/" (vault root) is normalized away.
        $destPath = $trimmed.TrimStart('/')
    }
    else {
        # Bare name: rename in place, keeping the source's current parent folder.
        $slashIndex = $SourcePath.LastIndexOf('/')
        $parent = if ($slashIndex -ge 0) { $SourcePath.Substring(0, $slashIndex) } else { '' }
        $destPath = if ($parent) { "$parent/$trimmed" } else { $trimmed }
    }

    if ($IsNote -and -not $destPath.ToLowerInvariant().EndsWith('.md')) {
        $destPath = "$destPath.md"
    }
    $destPath
}

$lockAbandoned = $false
$result = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$lockAbandoned) -ScriptBlock {
    # Required: the script-scope preference does not reach cmdlets invoked from inside a
    # module-function scriptblock, and without it a failed mutation is non-terminating and
    # gets reported as success.
    $ErrorActionPreference = 'Stop'

    # =======================================================================
    # VALIDATE. Read-only: resolve the source, construct the destination, and check every
    # invariant this slice owns before any mutation. Cheap, source-independent checks
    # (kind, empty dest) run first; checks that need the source's resolved shape or parent
    # (dest==source, dest-inside-source, occupancy, scope) follow source resolution and
    # dest construction, per Plan-Tool-Move's Proposed Implementation Checklist ordering.
    # Later slices (ambiguity gate, reference-rewrite engine, folder-move path, apply)
    # extend this scriptblock after this point - nothing here writes to the vault.
    # =======================================================================

    # --- kind: reject project/area outright (registry-carrying roles move does not own),
    # and reject anything else that isn't a recognized shape - before touching the vault. ---
    $normalizedKind = if ($Kind) { $Kind.Trim().ToLowerInvariant() } else { '' }
    if ($normalizedKind -in @('project', 'area')) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "kind '$Kind' is not valid for move - project/area moves carry a registry entry move does not own. Pass 'folder' (or omit kind) and compose the registry update via write.")
    }
    if ($normalizedKind -and $normalizedKind -notin @('file', 'folder')) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "kind '$Kind' is not a recognized value - move accepts 'file' or 'folder'.")
    }

    # --- dest must be non-empty. ---
    if ([string]::IsNullOrWhiteSpace($Dest)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'dest is empty.')
    }

    $sourcePath = $Source.Trim().Replace('\', '/').Trim('/')
    if ([string]::IsNullOrWhiteSpace($sourcePath)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'source is empty.')
    }

    # --- Resolve source: not_found on a miss; determine its shape (file vs folder). ---
    $fullSource = Resolve-VaultPath -Path $sourcePath -Vault $Vault
    $isFile = Test-Path -LiteralPath $fullSource -PathType Leaf
    $isFolder = (-not $isFile) -and (Test-Path -LiteralPath $fullSource -PathType Container)
    if (-not $isFile -and -not $isFolder) {
        throw (New-VaultToolError -Code 'not_found' -Message "Nothing at source path: $sourcePath" -Data @{ source = $sourcePath })
    }
    $resolvedKind = if ($isFile) { 'file' } else { 'folder' }

    # --- kind_mismatch: asserted kind (once narrowed to file/folder above) must match the
    # source's resolved shape. ---
    if ($normalizedKind -and $normalizedKind -ne $resolvedKind) {
        throw (New-VaultToolError -Code 'kind_mismatch' -Message "Asserted kind '$normalizedKind' does not match the source's resolved shape '$resolvedKind'." -Data @{ assertedKind = $normalizedKind; resolvedKind = $resolvedKind })
    }

    $isNote = $isFile -and $sourcePath.ToLowerInvariant().EndsWith('.md')

    # --- Construct dest from the raw Dest argument's dual bare-name/full-path semantics. ---
    $destPath = Resolve-VaultMoveDestPath -RawDest $Dest -SourcePath $sourcePath -IsNote $isNote

    $sourceBasename = ($sourcePath -split '/')[-1]
    $destBasename = ($destPath -split '/')[-1]

    # --- dest == source: a literal (case-sensitive) match is a true no-op; a case-only
    # difference is a legal rename (Windows is case-insensitive, but the rename still
    # changes link display text - see Plan-Tool-Move's Errors section). ---
    $sameLiteral = $destPath -ceq $sourcePath
    $sameCaseInsensitive = $destPath -ieq $sourcePath
    if ($sameLiteral) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "dest is the same as source: $sourcePath")
    }

    # --- dest inside the source folder: only meaningful for a folder move (a file has no
    # interior to move into); moving a directory into its own descendant is nonsensical and
    # Move-Item itself cannot perform it. ---
    if ($resolvedKind -eq 'folder' -and $destPath.StartsWith("$sourcePath/", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "dest is inside the source folder: $destPath is under $sourcePath")
    }

    # --- link-breaking / OS-invalid characters in the dest basename. ---
    if (-not (Test-VaultMoveDestBasenameValid -Basename $destBasename)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "dest basename contains a link-breaking or OS-invalid character: $destBasename")
    }

    # --- Naming-convention check: warn only, never fail the operation. ---
    $namingWarnings = @()
    if (-not (Test-VaultNameConvention -Name $destBasename)) {
        $namingWarnings += "Destination name '$destBasename' does not follow the vault's Title-Case-With-Hyphens naming convention."
    }

    # --- already_exists: destination occupied, checked before any rewrite. Case-only
    # renames are exempt - Windows' case-insensitive filesystem would otherwise resolve the
    # new spelling to the source itself and misreport a legal rename as a collision. ---
    $fullDest = Resolve-VaultPath -Path $destPath -Vault $Vault
    if (-not $sameCaseInsensitive -and (Test-Path -LiteralPath $fullDest)) {
        throw (New-VaultToolError -Code 'already_exists' -Message "Destination already exists: $destPath" -Data @{ dest = $destPath })
    }

    # --- Scope membership: both endpoints must fall within the grant's ambient scope
    # roots. The reference rewrite itself is deliberately NOT scope-limited (Plan-Tool-
    # Move's Scope Handling) - only these two endpoints are ever checked against scope, so
    # this check happens here and nowhere else in the operation. A null or empty
    # scope-roots set means the grant is unscoped for this vault (including the
    # unauthenticated/standalone `pwsh -File` case, which injects no
    # VaultMcpScopeRoots global at all) - degrades open, matching every other chokepoint's
    # scope treatment. ---
    $resolvedVault = Resolve-VaultRoot -Vault $Vault
    $ambientScopeRoots = Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolvedVault.Name -ScopeRoots $ambientScopeRoots
    if ($null -ne $flatScopeRoots -and $flatScopeRoots.Count -gt 0) {
        $offending = @()
        if (-not (Test-VaultPathInScope -Path $sourcePath -ScopeRoots $flatScopeRoots)) { $offending += $sourcePath }
        if (-not (Test-VaultPathInScope -Path $destPath -ScopeRoots $flatScopeRoots)) { $offending += $destPath }
        if ($offending.Count -gt 0) {
            throw (New-VaultToolError -Code 'out_of_scope' -Message "Path(s) fall outside the grant's scope for vault `"$($resolvedVault.Name)`": $($offending -join ', ')" -Data @{ paths = $offending })
        }
    }

    # =======================================================================
    # END OF THIS SLICE'S SCOPE. Ambiguity gate, reference-rewrite engine, folder-move
    # path (directory Move-Item, folder-note rename, prefix rewrite), and apply are later
    # slices (Plan-Tool-Move's Proposed Implementation Checklist) - stubbed below so the
    # return shape is already correct and those slices extend this scriptblock rather than
    # re-deriving oldPath/newPath/kind/pureMove.
    #
    # pureMove is computed now (a basename comparison, not a mutation) because it gates the
    # rewrite engine the next slice adds: true only on a case-sensitive basename match, per
    # the spec's note that a case-only rename still changes link display text and so is not
    # basename-unchanged for rewrite purposes.
    # =======================================================================

    $pureMove = $destBasename -ceq $sourceBasename

    [ordered]@{
        oldPath                    = $sourcePath
        newPath                    = $destPath
        kind                       = $resolvedKind
        pureMove                   = $pureMove
        referencesRewritten        = 0
        filesRewritten             = @()
        referencesSkippedAmbiguous = $null
        basenameCollisionCreated   = $false
        folderNoteOrphaned         = $false
        namingWarnings             = $namingWarnings
        filesSkippedUnreadable     = 0
        folderNoteRenamed          = $null
        containedFilesMoved        = @()
        lockWarning                = $null
        stub                       = $true
    }
}

if ($lockAbandoned) {
    $result.lockWarning = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
