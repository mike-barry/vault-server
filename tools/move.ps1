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

# ---------------------------------------------------------------------------
# Reference-rewrite engine. A single link-matching grammar drives both the
# basename censuses' "does a bare link to the old name exist" signal and the
# actual rewrite, so `move` scans the vault once (Plan-Tool-Move: "One scan, one
# grammar"). The grammar covers wikilinks and embeds; only the link *target* is
# ever rewritten - the embed marker, #heading, and |alias are preserved verbatim.
# ---------------------------------------------------------------------------

# (!?)[[ target (#heading)? (|alias)? ]]  - target excludes the link-structural
# characters, so it captures a bare basename or a vault-relative path but stops at
# the heading / alias / closing brackets.
$script:VaultLinkPattern = [regex]'(?<embed>!?)\[\[(?<target>[^\[\]\|#]+)(?<heading>#[^\[\]\|]*)?(?<alias>\|[^\[\]]*)?\]\]'

function Get-VaultLinkKey {
    <#
    .SYNOPSIS
    Normalizes a filename or link target to its wikilink-namespace key: a trailing ".md"
    is stripped (notes are linked extension-less), any other extension is kept (an image or
    attachment is linked by its full filename). Backslashes fold to "/" and a trailing "/"
    is trimmed. This one function keys the basename censuses, the moved item's old/new
    identity, and each scanned link's target, so all three agree on what "the same name"
    means.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Name)
    $n = $Name.Trim().Replace('\', '/').TrimEnd('/')
    if ($n.ToLowerInvariant().EndsWith('.md')) { $n = $n.Substring(0, $n.Length - 3) }
    $n
}

function Test-VaultPathSuffixMatch {
    <#
    .SYNOPSIS
    True when $Candidate's path components are a component-aligned suffix of $Target's - the
    match Obsidian's "shortest path when possible" links need, since it writes partial
    suffixes like [[Foo/Note]] for a note at Deep/Foo/Note. Comparison is case-insensitive
    (Windows filesystem). A single-component candidate is handled by the bare-basename rule,
    not here, so callers pass only path-qualified (slash-bearing) candidates.
    #>
    param(
        [Parameter(Mandatory)] [string[]] $Target,
        [Parameter(Mandatory)] [string[]] $Candidate
    )
    if ($Candidate.Count -eq 0 -or $Candidate.Count -gt $Target.Count) { return $false }
    for ($i = 1; $i -le $Candidate.Count; $i++) {
        if ($Target[$Target.Count - $i] -ine $Candidate[$Candidate.Count - $i]) { return $false }
    }
    $true
}

function Convert-VaultLinkReferences {
    <#
    .SYNOPSIS
    Single-pass rewrite of one file's content. Classifies every link target as bare or
    path-qualified, decides per the identify/emit rules (Plan-Tool-Move: Reference Rewrite)
    whether it refers to the moved item, and rebuilds the file's text with only the matching
    targets repointed - embed marker, heading, and alias copied through untouched.

    Returns a PSCustomObject: NewContent (the rewritten text), RewriteCount (link
    occurrences actually changed), AmbiguousBareCount (bare links to a non-unique old
    basename, left untouched), and HadBareToOld (whether any bare link to the old basename
    was seen at all - the ambiguity gate's "at least one bare link exists" signal).

    Decision inputs are all resolved before the scan (censuses, force, pureMove), so this
    produces final content in the same pass that feeds the gate; when the gate then refuses
    an unforced move, the content is simply discarded (nothing has been written yet).
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $OldKeyBasename,
        [Parameter(Mandatory)] [string[]] $OldKeyPathComponents,
        [Parameter(Mandatory)] [string] $NewTargetBare,
        [Parameter(Mandatory)] [string] $NewTargetFull,
        [Parameter(Mandatory)] [bool] $OldUnique,
        [Parameter(Mandatory)] [bool] $EmitBare,
        # ProcessBare is false on a pure relocation (basename unchanged): bare links stay
        # textually valid and are never touched, while the path-qualified scan still runs.
        [Parameter(Mandatory)] [bool] $ProcessBare
    )

    $emit = if ($EmitBare) { $NewTargetBare } else { $NewTargetFull }

    $rewriteCount = 0
    $ambiguousBare = 0
    $hadBareToOld = $false

    $sb = [System.Text.StringBuilder]::new()
    $cursor = 0
    foreach ($m in $script:VaultLinkPattern.Matches($Content)) {
        [void]$sb.Append($Content.Substring($cursor, $m.Index - $cursor))
        $cursor = $m.Index + $m.Length

        $key = Get-VaultLinkKey -Name $m.Groups['target'].Value
        $newTarget = $null

        if ($key.Contains('/')) {
            # Path-qualified: definitive suffix match, rewritten regardless of pureMove or
            # old-basename uniqueness (a path is never ambiguous).
            if (Test-VaultPathSuffixMatch -Target $OldKeyPathComponents -Candidate ($key -split '/')) {
                $newTarget = $emit
            }
        }
        elseif ($ProcessBare -and ($key -ieq $OldKeyBasename)) {
            $hadBareToOld = $true
            if ($OldUnique) {
                # Unique old basename: a bare link definitively refers to the moved item.
                $newTarget = $emit
            }
            else {
                # Non-unique: intent unknowable, never rewritten by guess. Under force this is
                # the skip-and-report path; unforced, the gate refuses before any write.
                $ambiguousBare++
            }
        }

        if ($null -ne $newTarget) {
            $rebuilt = $m.Groups['embed'].Value + '[[' + $newTarget + $m.Groups['heading'].Value + $m.Groups['alias'].Value + ']]'
            if ($rebuilt -cne $m.Value) {
                [void]$sb.Append($rebuilt)
                $rewriteCount++
            }
            else {
                [void]$sb.Append($m.Value)
            }
        }
        else {
            [void]$sb.Append($m.Value)
        }
    }
    [void]$sb.Append($Content.Substring($cursor))

    [pscustomobject]@{
        NewContent         = $sb.ToString()
        RewriteCount       = $rewriteCount
        AmbiguousBareCount = $ambiguousBare
        HadBareToOld       = $hadBareToOld
    }
}

function Convert-VaultFolderLinkReferences {
    <#
    .SYNOPSIS
    Single-pass rewrite of one file's content for a FOLDER move. A folder move carries two
    rewrites in one scan (Plan-Tool-Move: Folder-move correctness core):

    (a) the old-folder-path PREFIX substitution for path-qualified links under the old
        folder - this is what repairs links to contained files as a byproduct of the prefix,
        not a per-child loop. A qualified link's leading components are a component-aligned
        suffix of the old folder path (Obsidian writes shortest-suffix paths like
        [[Widget/Note]]), followed by the contained relative path; the folder-suffix portion
        is swapped for the same-length suffix of the new folder path and the contained tail
        is preserved verbatim - so the link keeps its original shape and still resolves. The
        one exception is a qualified link to the FOLDER NOTE itself: because the folder note's
        basename tracks the folder's, such a link changes in TWO components (prefix and leaf).

    (b) the bare-link rewrite for the folder note's old basename when it changed - the folder
        note is the only basename event in a folder op (contained basenames don't change), so
        this is exactly the file-move bare-link rule applied to the folder note, ambiguity
        gate and all.

    Path-qualified links are always definitive (a path is never ambiguous) and rewritten
    regardless of the folder-note basename census. Bare links are only ever the folder note's
    basename; a bare link to a contained file keeps its name and is left untouched.

    Returns NewContent, RewriteCount, AmbiguousBareCount (bare folder-note links left
    untouched because the old basename is non-unique), and HadBareToOld.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string[]] $OldFolderComponents,
        [Parameter(Mandatory)] [string[]] $NewFolderComponents,
        [Parameter(Mandatory)] [string]   $OldFolderPathKey,
        [Parameter(Mandatory)] [System.Collections.Generic.HashSet[string]] $ContainedKeySet,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]   $OldFolderNoteKey,
        [Parameter(Mandatory)] [string]   $OldFolderBasename,
        [Parameter(Mandatory)] [string]   $NewFolderBasename,
        # The folder note exists AND its basename changed: enables both the folder-note leaf
        # swap in qualified links and the bare-link rewrite. False on a pure relocation (basename
        # kept) or when the folder note is absent - the prefix scan still runs and repairs.
        [Parameter(Mandatory)] [bool]     $FolderNoteBasenameChanged,
        [Parameter(Mandatory)] [string]   $FolderNoteNewBareTarget,
        [Parameter(Mandatory)] [string]   $FolderNoteNewFullTarget,
        [Parameter(Mandatory)] [bool]     $OldFolderNoteUnique,
        [Parameter(Mandatory)] [bool]     $EmitBareFolderNote
    )

    $emitBare = if ($EmitBareFolderNote) { $FolderNoteNewBareTarget } else { $FolderNoteNewFullTarget }

    $rewriteCount = 0
    $ambiguousBare = 0
    $hadBareToOld = $false

    $sb = [System.Text.StringBuilder]::new()
    $cursor = 0
    foreach ($m in $script:VaultLinkPattern.Matches($Content)) {
        [void]$sb.Append($Content.Substring($cursor, $m.Index - $cursor))
        $cursor = $m.Index + $m.Length

        $key = Get-VaultLinkKey -Name $m.Groups['target'].Value
        $newTarget = $null

        if ($key.Contains('/')) {
            # Path-qualified: does it point at a file under the old folder? Its leading k
            # components must be a component-aligned suffix of the old folder path, and the
            # remaining tail must resolve to a real contained file. Largest k wins (most
            # specific); the ContainedKeySet membership guard keeps a coincidental prefix
            # match on an unrelated link from being rewritten.
            $L = $key -split '/'
            $matchedK = 0
            $maxK = [Math]::Min($OldFolderComponents.Count, $L.Count - 1)
            for ($k = $maxK; $k -ge 1; $k--) {
                $aligned = $true
                for ($i = 0; $i -lt $k; $i++) {
                    if ($L[$i] -ine $OldFolderComponents[$OldFolderComponents.Count - $k + $i]) { $aligned = $false; break }
                }
                if ($aligned) {
                    $tail = @($L[$k..($L.Count - 1)])
                    $candidateFullKey = "$OldFolderPathKey/$($tail -join '/')"
                    if ($ContainedKeySet.Contains($candidateFullKey)) { $matchedK = $k; break }
                }
            }
            if ($matchedK -gt 0) {
                $tail = @($L[$matchedK..($L.Count - 1)])
                # Two-component emit: the folder note's leaf also changes when its basename did.
                if ($FolderNoteBasenameChanged -and $tail.Count -eq 1 -and ($tail[0] -ieq $OldFolderBasename) -and (("$OldFolderPathKey/$($tail[0])") -ieq $OldFolderNoteKey)) {
                    $tail = @($NewFolderBasename)
                }
                $newSuffix = @($NewFolderComponents[($NewFolderComponents.Count - $matchedK)..($NewFolderComponents.Count - 1)])
                $newTarget = (@($newSuffix) + @($tail)) -join '/'
            }
        }
        elseif ($FolderNoteBasenameChanged -and ($key -ieq $OldFolderBasename)) {
            # Bare link to the folder note - the one bare basename event in a folder op.
            $hadBareToOld = $true
            if ($OldFolderNoteUnique) {
                $newTarget = $emitBare
            }
            else {
                $ambiguousBare++
            }
        }

        if ($null -ne $newTarget) {
            $rebuilt = $m.Groups['embed'].Value + '[[' + $newTarget + $m.Groups['heading'].Value + $m.Groups['alias'].Value + ']]'
            if ($rebuilt -cne $m.Value) {
                [void]$sb.Append($rebuilt)
                $rewriteCount++
            }
            else {
                [void]$sb.Append($m.Value)
            }
        }
        else {
            [void]$sb.Append($m.Value)
        }
    }
    [void]$sb.Append($Content.Substring($cursor))

    [pscustomobject]@{
        NewContent         = $sb.ToString()
        RewriteCount       = $rewriteCount
        AmbiguousBareCount = $ambiguousBare
        HadBareToOld       = $hadBareToOld
    }
}

$lockAbandoned = $false
try {
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

    # pureMove is a case-sensitive basename comparison: a case-only rename still changes link
    # display text, so it is NOT basename-unchanged for rewrite purposes and does not short-
    # circuit the bare-link scan.
    $pureMove = $destBasename -ceq $sourceBasename
    $basenameChanged = -not $pureMove

    # =======================================================================
    # FOLDER MOVE. One atomic operation under the single lock carrying three coupled effects
    # (Plan-Tool-Move: Role, Folder-move correctness core): the directory Move-Item, the
    # folder-note invariant (Foo/Foo.md -> Bar/Bar.md, only when the folder's basename changed),
    # and one vault scan applying two rewrites - the old-folder-path prefix substitution for
    # qualified links under the old path (which repairs links to contained files as a byproduct)
    # and the folder note's bare-link rewrite when its basename changed. Contained basenames
    # never change, so the folder note's rename is the only basename event; the ambiguity gate
    # applies to it exactly as in a file move. Nothing before the "APPLY" marker mutates.
    # =======================================================================
    if ($resolvedKind -eq 'folder') {
        $oldFolderPathKey = Get-VaultLinkKey -Name $sourcePath
        $newFolderPathKey = Get-VaultLinkKey -Name $destPath
        $oldFolderComponents = $oldFolderPathKey -split '/'
        $newFolderComponents = $newFolderPathKey -split '/'
        $oldFolderBasename = $sourceBasename
        $newFolderBasename = $destBasename

        # The folder-note invariant fires only when the folder's basename changed (a pure
        # relocation keeps the folder note's name); a case-only folder rename still counts as a
        # change (link display text changes), matching pureMove's case-sensitive comparison.
        $folderBasenameChanged = -not $pureMove

        # Folder note: Foo/Foo.md. Convention, not guaranteed - a missing folder note is
        # warn-and-proceed (folderNoteRenamed: null), never an error (Plan-Tool-Move: Errors).
        $folderNoteOldRel = "$sourcePath/$oldFolderBasename.md"
        $folderNoteFull = Resolve-VaultPath -Path $folderNoteOldRel -Vault $Vault
        $folderNoteExists = Test-Path -LiteralPath $folderNoteFull -PathType Leaf
        $folderNoteBasenameChanged = $folderNoteExists -and $folderBasenameChanged

        $oldFolderNoteKey = if ($folderNoteExists) { Get-VaultLinkKey -Name $folderNoteOldRel } else { '' }
        # The folder note's post-move identity (basename may have changed): its bare new
        # basename and its full new vault path, both keyed for canonical emit.
        $folderNoteNewBareTarget = $newFolderBasename
        $folderNoteNewFullTarget = Get-VaultLinkKey -Name "$destPath/$newFolderBasename.md"

        # --- Contained files (every file under the old folder, not only .md - embeds to a
        # contained image must repoint too). Shared enumeration with glob via Get-VaultFilePath,
        # vault-wide (ScopeRoots $null). Keyed into a case-insensitive set so the scan can
        # confirm a qualified link's tail resolves to a real contained file. ---
        $containedKeySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $containedRels = [System.Collections.Generic.List[string]]::new()
        $folderPrefix = "$sourcePath/"
        foreach ($rel in @(Get-VaultFilePath -Vault $Vault -ScopeRoots $null)) {
            if ($rel.StartsWith($folderPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $containedRels.Add($rel)
                [void]$containedKeySet.Add((Get-VaultLinkKey -Name $rel))
            }
        }

        # Remaps a contained file's old vault-relative path to its post-move path: the folder
        # prefix is swapped, and the top-level folder note's leaf is renamed when the basename
        # changed. Used both to compute where a rewritten contained note is written and to
        # report containedFilesMoved.
        $remapContained = {
            param([string] $Rel)
            $inner = $Rel.Substring($sourcePath.Length + 1)
            if ($folderNoteBasenameChanged -and ($Rel -ieq $folderNoteOldRel)) {
                $inner = "$newFolderBasename.md"
            }
            "$destPath/$inner"
        }

        # --- Folder-note basename censuses + ambiguity gate. Only a folder-note basename change
        # is a basename event; a pure relocation or an absent folder note skips the gate
        # entirely (location cannot change basename uniqueness). Same rules as a file move. ---
        $oldFolderNoteUnique = $true
        $newCollision = $false
        $newCensusOthers = @()
        if ($folderNoteBasenameChanged) {
            $oldKeyBasename = Get-VaultLinkKey -Name $oldFolderBasename
            $newKeyBasename = Get-VaultLinkKey -Name $newFolderBasename
            $allFiles = @(Get-VaultFilePath -Vault $Vault -ScopeRoots $null)
            $oldCensus = @($allFiles | Where-Object { (Get-VaultLinkKey -Name (($_ -split '/')[-1])) -ieq $oldKeyBasename })
            $newCensus = @($allFiles | Where-Object { (Get-VaultLinkKey -Name (($_ -split '/')[-1])) -ieq $newKeyBasename })
            $oldFolderNoteUnique = ($oldCensus.Count -le 1)
            # The folder note itself carries the old basename and is excluded; any OTHER file with
            # the new basename is a standing collision.
            $newCensusOthers = @($newCensus | Where-Object { $_ -cne $folderNoteOldRel })
            $newCollision = ($newCensusOthers.Count -gt 0)

            if ($newCollision -and -not $Force) {
                throw (New-VaultToolError -Code 'basename_collision' -Message "Folder-note destination basename '$newFolderBasename' already exists elsewhere in the vault: $($newCensusOthers -join ', '). Pick a different folder name, or pass force to emit path-qualified links." -Data @{ collidingPaths = $newCensusOthers })
            }
        }
        $emitBareFolderNote = -not $newCollision
        $basenameCollisionCreated = ($folderNoteBasenameChanged -and $newCollision -and $Force)

        # --- Single vault scan. Builds each file's rewritten content (prefix substitution for
        # qualified links under the old folder + the folder-note bare rewrite) and tallies the
        # gate signals. Nothing written here. ---
        $rewriteTargets = [System.Collections.Generic.List[object]]::new()
        $ambiguousFiles = [System.Collections.Generic.List[string]]::new()
        $referencesRewritten = 0
        $referencesSkippedAmbiguous = 0
        $filesSkippedUnreadable = 0
        $hadBareToOldAny = $false

        foreach ($rel in (Get-VaultNotePath -Vault $Vault -ScopeRoots $null)) {
            $full = Resolve-VaultPath -Path $rel -Vault $Vault
            try {
                $content = [System.IO.File]::ReadAllText($full)
            }
            catch {
                if (Test-VaultNoteMissingError -ErrorRecord $_) { $filesSkippedUnreadable++; continue }
                throw (New-VaultToolError -Code 'read_failure' -Message "Could not read '$rel' during the reference scan: $($_.Exception.Message)" -Data @{ path = $rel; error = $_.Exception.Message })
            }

            $r = Convert-VaultFolderLinkReferences -Content $content `
                -OldFolderComponents $oldFolderComponents -NewFolderComponents $newFolderComponents `
                -OldFolderPathKey $oldFolderPathKey -ContainedKeySet $containedKeySet `
                -OldFolderNoteKey $oldFolderNoteKey `
                -OldFolderBasename $oldFolderBasename -NewFolderBasename $newFolderBasename `
                -FolderNoteBasenameChanged $folderNoteBasenameChanged `
                -FolderNoteNewBareTarget $folderNoteNewBareTarget -FolderNoteNewFullTarget $folderNoteNewFullTarget `
                -OldFolderNoteUnique $oldFolderNoteUnique -EmitBareFolderNote $emitBareFolderNote

            if ($r.HadBareToOld) { $hadBareToOldAny = $true }
            if ($r.AmbiguousBareCount -gt 0) {
                $referencesSkippedAmbiguous += $r.AmbiguousBareCount
                $ambiguousFiles.Add($rel)
            }
            if ($r.RewriteCount -gt 0) {
                $referencesRewritten += $r.RewriteCount
                $rewriteTargets.Add([pscustomobject]@{ Path = $rel; NewContent = $r.NewContent })
            }
        }

        # --- Ambiguity gate, part 2: ambiguous_references for the folder-note basename. ---
        if ($folderNoteBasenameChanged -and -not $oldFolderNoteUnique -and $hadBareToOldAny -and -not $Force) {
            throw (New-VaultToolError -Code 'ambiguous_references' -Message "The folder note's old basename '$oldFolderBasename' is not unique in the vault and bare links to it exist, so they cannot be rewritten unambiguously: $($oldCensus -join ', '). Rename to a unique folder name, or pass force to skip the ambiguous bare links." -Data @{ duplicatePaths = $oldCensus; referencingFiles = $ambiguousFiles.ToArray() })
        }

        # --- Read-only pre-flight: every rewrite target plus the folder note (which is renamed)
        # must be writable, caught before the first mutation. ---
        $readOnlyFiles = [System.Collections.Generic.List[string]]::new()
        $readonlyCandidates = @($rewriteTargets | ForEach-Object { $_.Path })
        if ($folderNoteBasenameChanged) { $readonlyCandidates += $folderNoteOldRel }
        foreach ($check in $readonlyCandidates) {
            $checkFull = Resolve-VaultPath -Path $check -Vault $Vault
            if ((Test-Path -LiteralPath $checkFull) -and (([System.IO.File]::GetAttributes($checkFull) -band [System.IO.FileAttributes]::ReadOnly) -ne 0)) {
                $readOnlyFiles.Add($check)
            }
        }
        if ($readOnlyFiles.Count -gt 0) {
            throw (New-VaultToolError -Code 'readonly_targets' -Message "The move cannot proceed: $($readOnlyFiles.Count) target file(s) are read-only: $($readOnlyFiles.ToArray() -join ', ')." -Data @{ readOnlyFiles = $readOnlyFiles.ToArray() })
        }

        # =======================================================================
        # APPLY (folder). Directory move -> folder-note rename -> rewrites, all under the one
        # lock. $dirMoved / $folderNoteRenamedApplied / the running $rewrittenFiles list are
        # exactly the already-done set a mid-apply failure must name in partial_state.
        # =======================================================================
        $dirMoved = $false
        $folderNoteRenamedApplied = $false
        $rewrittenFiles = [System.Collections.Generic.List[string]]::new()
        $folderNoteNewRel = if ($folderNoteBasenameChanged) { "$destPath/$newFolderBasename.md" } else { $folderNoteOldRel -replace [regex]::Escape($sourcePath), $destPath }
        try {
            # Destination-parent autocreate: move into a not-yet-existing archive subfolder in
            # one call (Plan-Tool-Move: Design Guarantees).
            $destParent = [System.IO.Path]::GetDirectoryName($fullDest)
            if ($destParent -and -not (Test-Path -LiteralPath $destParent -PathType Container)) {
                New-Item -ItemType Directory -Path $destParent -Force -ErrorAction Stop | Out-Null
            }

            if ($sameCaseInsensitive) {
                # Case-only folder rename on a case-insensitive filesystem: bounce through a
                # unique temp name so the case change actually lands.
                $tempFull = "$fullDest.__movecase__$([guid]::NewGuid().ToString('N'))"
                Move-Item -LiteralPath $fullSource -Destination $tempFull -ErrorAction Stop
                Move-Item -LiteralPath $tempFull -Destination $fullDest -ErrorAction Stop
            }
            else {
                Move-Item -LiteralPath $fullSource -Destination $fullDest -ErrorAction Stop
            }
            $dirMoved = $true

            # Folder-note invariant: after the directory move the note lives at
            # <dest>/<oldBasename>.md; rename it to <dest>/<newBasename>.md. Only when the
            # basename changed and a folder note exists.
            if ($folderNoteBasenameChanged) {
                $folderNoteMovedFull = Resolve-VaultPath -Path "$destPath/$oldFolderBasename.md" -Vault $Vault
                $folderNoteTargetFull = Resolve-VaultPath -Path $folderNoteNewRel -Vault $Vault
                if ($oldFolderBasename -ieq $newFolderBasename) {
                    # Case-only basename change: temp-bounce as above.
                    $tempNote = "$folderNoteTargetFull.__movecase__$([guid]::NewGuid().ToString('N'))"
                    Move-Item -LiteralPath $folderNoteMovedFull -Destination $tempNote -ErrorAction Stop
                    Move-Item -LiteralPath $tempNote -Destination $folderNoteTargetFull -ErrorAction Stop
                }
                else {
                    Move-Item -LiteralPath $folderNoteMovedFull -Destination $folderNoteTargetFull -ErrorAction Stop
                }
                $folderNoteRenamedApplied = $true
            }

            foreach ($tgt in $rewriteTargets) {
                # A contained rewrite target now lives at its post-move path; an external note
                # linking in stays put. Remap contained paths (folder-note leaf included).
                $writeRel = if ($tgt.Path.StartsWith($folderPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { & $remapContained $tgt.Path } else { $tgt.Path }
                $writeFull = Resolve-VaultPath -Path $writeRel -Vault $Vault
                [System.IO.File]::WriteAllText($writeFull, $tgt.NewContent)
                $rewrittenFiles.Add($writeRel)
            }
        }
        catch {
            if ($_.Exception.Message -match '^\s*\{.*"code"') { throw }
            if ($dirMoved) {
                throw (New-VaultToolError -Code 'partial_state' -Message "Folder move partially applied then failed: '$sourcePath' was moved to '$destPath'$(if ($folderNoteRenamedApplied) { ' and the folder note renamed' }) and $($rewrittenFiles.Count) file(s) rewritten before the error: $($_.Exception.Message). Recover with: git checkout -- <files>." -Data @{
                        filesRewritten     = $rewrittenFiles.ToArray()
                        directoryMoved     = $dirMoved
                        folderNoteRenamed  = $folderNoteRenamedApplied
                        error              = $_.Exception.Message
                    })
            }
            throw
        }

        $containedFilesMoved = @($containedRels | ForEach-Object { & $remapContained $_ })

        return [ordered]@{
            oldPath                    = $sourcePath
            newPath                    = $destPath
            kind                       = $resolvedKind
            pureMove                   = $pureMove
            referencesRewritten        = $referencesRewritten
            filesRewritten             = $rewrittenFiles.ToArray()
            referencesSkippedAmbiguous = if ($referencesSkippedAmbiguous -gt 0) { [ordered]@{ count = $referencesSkippedAmbiguous; files = $ambiguousFiles.ToArray() } } else { $null }
            basenameCollisionCreated   = $basenameCollisionCreated
            folderNoteOrphaned         = $false
            namingWarnings             = $namingWarnings
            filesSkippedUnreadable     = $filesSkippedUnreadable
            folderNoteRenamed          = if ($folderNoteRenamedApplied) { [ordered]@{ oldPath = $folderNoteOldRel; newPath = $folderNoteNewRel } } else { $null }
            containedFilesMoved        = $containedFilesMoved
            lockWarning                = $null
        }
    }

    # =======================================================================
    # FILE MOVE. Complete the validate phase (censuses, ambiguity gate, single-scan rewrite
    # plan, read-only pre-flight), then apply (Move-Item, then the rewrites) - all still under
    # the one write lock. Nothing before the "APPLY" marker mutates the vault.
    # =======================================================================

    # --- The moved item's old/new identity in the wikilink namespace. ---
    $oldKeyBasename = Get-VaultLinkKey -Name $sourceBasename
    $newKeyBasename = Get-VaultLinkKey -Name $destBasename
    $oldKeyPathComponents = (Get-VaultLinkKey -Name $sourcePath) -split '/'
    $newTargetFull = Get-VaultLinkKey -Name $destPath
    $newTargetBare = $newKeyBasename

    # --- Basename censuses. Vault-wide (ScopeRoots $null) - uniqueness is a global property,
    # and the rewrite itself is not scope-limited (Plan-Tool-Move: Scope Handling). Shared with
    # glob via Get-VaultFilePath so move and glob agree on what exists. Keyed through
    # Get-VaultLinkKey so "Gizmo-Reference.md" and a bare [[Gizmo-Reference]] live in one
    # namespace. ---
    $allFiles = @(Get-VaultFilePath -Vault $Vault -ScopeRoots $null)
    $oldCensus = @($allFiles | Where-Object { (Get-VaultLinkKey -Name (($_ -split '/')[-1])) -ieq $oldKeyBasename })
    $newCensus = @($allFiles | Where-Object { (Get-VaultLinkKey -Name (($_ -split '/')[-1])) -ieq $newKeyBasename })

    # oldCensus includes the source itself; unique means nothing else shares the name.
    $oldUnique = ($oldCensus.Count -le 1)
    # newCollision: some OTHER file already carries the destination name (the source is
    # excluded - on a pure relocation it still carries that name). The destination path itself
    # is free (already_exists checked above).
    $newCensusOthers = @($newCensus | Where-Object { $_ -cne $sourcePath })
    $newCollision = ($newCensusOthers.Count -gt 0)

    # --- Ambiguity gate, part 1: basename_collision needs no scan (a census fact), so refuse
    # early when unforced. Pure relocations cannot change basename uniqueness, so the gate is
    # skipped entirely for them (Plan-Tool-Move: Design Guarantees). ---
    if ($basenameChanged -and $newCollision -and -not $Force) {
        throw (New-VaultToolError -Code 'basename_collision' -Message "Destination basename '$destBasename' already exists elsewhere in the vault: $($newCensusOthers -join ', '). Pick a different name, or pass force to emit path-qualified links." -Data @{ collidingPaths = $newCensusOthers })
    }

    # EmitBare: a rewritten link comes out as the bare new basename when that name is unique
    # vault-wide after the move, else as the full vault-relative path. Same rule whether the
    # collision was forced through or never existed.
    $emitBare = -not $newCollision
    $basenameCollisionCreated = ($basenameChanged -and $newCollision -and $Force)

    # --- Single scan over every note in the vault (ScopeRoots $null: the rewrite reaches
    # outside the grant's scope, by design). Builds each file's rewritten content and tallies
    # the gate signals. Nothing is written here. ---
    $rewriteTargets = [System.Collections.Generic.List[object]]::new()
    $ambiguousFiles = [System.Collections.Generic.List[string]]::new()
    $referencesRewritten = 0
    $referencesSkippedAmbiguous = 0
    $filesSkippedUnreadable = 0
    $hadBareToOldAny = $false

    foreach ($rel in (Get-VaultNotePath -Vault $Vault -ScopeRoots $null)) {
        $full = Resolve-VaultPath -Path $rel -Vault $Vault
        try {
            $content = [System.IO.File]::ReadAllText($full)
        }
        catch {
            # Vanished between enumeration and read (or a broken link) - skip and count.
            # A present-but-unreadable file (permissions, sharing, I/O) blocks the whole move.
            if (Test-VaultNoteMissingError -ErrorRecord $_) { $filesSkippedUnreadable++; continue }
            throw (New-VaultToolError -Code 'read_failure' -Message "Could not read '$rel' during the reference scan: $($_.Exception.Message)" -Data @{ path = $rel; error = $_.Exception.Message })
        }

        $r = Convert-VaultLinkReferences -Content $content `
            -OldKeyBasename $oldKeyBasename -OldKeyPathComponents $oldKeyPathComponents `
            -NewTargetBare $newTargetBare -NewTargetFull $newTargetFull `
            -OldUnique $oldUnique -EmitBare $emitBare -ProcessBare $basenameChanged

        if ($r.HadBareToOld) { $hadBareToOldAny = $true }
        if ($r.AmbiguousBareCount -gt 0) {
            $referencesSkippedAmbiguous += $r.AmbiguousBareCount
            $ambiguousFiles.Add($rel)
        }
        if ($r.RewriteCount -gt 0) {
            $referencesRewritten += $r.RewriteCount
            $rewriteTargets.Add([pscustomobject]@{ Path = $rel; NewContent = $r.NewContent })
        }
    }

    # --- Ambiguity gate, part 2: ambiguous_references needs the scan (does any bare link to a
    # non-unique old basename exist). Unforced, refuse before any write; forced, the scan
    # already skipped those bare links and the counts are reported. ---
    if ($basenameChanged -and -not $oldUnique -and $hadBareToOldAny -and -not $Force) {
        throw (New-VaultToolError -Code 'ambiguous_references' -Message "The old basename '$sourceBasename' is not unique in the vault and bare links to it exist, so they cannot be rewritten unambiguously: $($oldCensus -join ', '). Rename to a unique name, or pass force to skip the ambiguous bare links." -Data @{ duplicatePaths = $oldCensus; referencingFiles = $ambiguousFiles.ToArray() })
    }

    # --- Read-only pre-flight: the source and every file the rewrite would touch must be
    # writable, caught before the first mutation (Plan-Tool-Move: readonly_targets). ---
    $readOnlyFiles = [System.Collections.Generic.List[string]]::new()
    $readonlyCandidates = @($sourcePath) + @($rewriteTargets | ForEach-Object { $_.Path })
    foreach ($check in $readonlyCandidates) {
        $checkFull = Resolve-VaultPath -Path $check -Vault $Vault
        if ((Test-Path -LiteralPath $checkFull) -and (([System.IO.File]::GetAttributes($checkFull) -band [System.IO.FileAttributes]::ReadOnly) -ne 0)) {
            $readOnlyFiles.Add($check)
        }
    }
    if ($readOnlyFiles.Count -gt 0) {
        throw (New-VaultToolError -Code 'readonly_targets' -Message "The move cannot proceed: $($readOnlyFiles.Count) target file(s) are read-only: $($readOnlyFiles.ToArray() -join ', ')." -Data @{ readOnlyFiles = $readOnlyFiles.ToArray() })
    }

    # --- folderNoteOrphaned: file-moving a folder note (Foo/Foo.md) out of its folder, or
    # renaming it away from the folder's name, breaks the folder-note invariant. Warn-and-
    # proceed - the file moves like any other; the caller is only told the invariant broke. ---
    $sourceParentName = if ($oldKeyPathComponents.Count -ge 2) { $oldKeyPathComponents[$oldKeyPathComponents.Count - 2] } else { '' }
    $destKeyComponents = $newTargetFull -split '/'
    $destParentName = if ($destKeyComponents.Count -ge 2) { $destKeyComponents[$destKeyComponents.Count - 2] } else { '' }
    $sourceWasFolderNote = $isNote -and $sourceParentName -and ($oldKeyBasename -ieq $sourceParentName)
    $destIsFolderNote = $destParentName -and ($newKeyBasename -ieq $destParentName)
    $folderNoteOrphaned = [bool]($sourceWasFolderNote -and -not $destIsFolderNote)

    # =======================================================================
    # APPLY. Validation is complete; from here the vault is mutated. The move runs first, then
    # the rewrites - so $movedApplied plus the running $rewrittenFiles list is exactly the
    # already-done set a mid-apply failure must name in partial_state.
    # =======================================================================
    $movedApplied = $false
    $rewrittenFiles = [System.Collections.Generic.List[string]]::new()
    try {
        # Destination-parent autocreate: a caller can move into a not-yet-existing archive
        # subfolder in one call (Plan-Tool-Move: Design Guarantees).
        $destParent = [System.IO.Path]::GetDirectoryName($fullDest)
        if ($destParent -and -not (Test-Path -LiteralPath $destParent -PathType Container)) {
            New-Item -ItemType Directory -Path $destParent -Force -ErrorAction Stop | Out-Null
        }

        if ($sameCaseInsensitive) {
            # Case-only rename on a case-insensitive filesystem: Move-Item to a name that
            # differs only in case is a no-op (or errors) because source and dest resolve to
            # the same entry. Bounce through a unique temp name to force the case change.
            $tempFull = "$fullDest.__movecase__$([guid]::NewGuid().ToString('N'))"
            Move-Item -LiteralPath $fullSource -Destination $tempFull -ErrorAction Stop
            Move-Item -LiteralPath $tempFull -Destination $fullDest -ErrorAction Stop
        }
        else {
            Move-Item -LiteralPath $fullSource -Destination $fullDest -ErrorAction Stop
        }
        $movedApplied = $true

        foreach ($tgt in $rewriteTargets) {
            # A self-referential source note was scanned at its old path but now lives at the
            # destination - write the rewrite there.
            $writeRel = if ($tgt.Path -ceq $sourcePath) { $destPath } else { $tgt.Path }
            $writeFull = Resolve-VaultPath -Path $writeRel -Vault $Vault
            # Direct write, not Write-VaultNote: the rewrite is deliberately not scope-limited,
            # so it must bypass the chokepoint's scope assertion. UTF-8 without BOM, matching
            # Write-VaultNote's own [System.IO.File]::WriteAllText.
            [System.IO.File]::WriteAllText($writeFull, $tgt.NewContent)
            $rewrittenFiles.Add($writeRel)
        }
    }
    catch {
        # Re-throw a clean {code, message, data?} error unchanged; wrap anything else as
        # partial_state only once a mutation has actually landed (a pre-move failure leaves the
        # vault untouched and should surface as its own error).
        if ($_.Exception.Message -match '^\s*\{.*"code"') { throw }
        if ($movedApplied) {
            throw (New-VaultToolError -Code 'partial_state' -Message "Move partially applied then failed: '$sourcePath' was moved to '$destPath' and $($rewrittenFiles.Count) file(s) rewritten before the error: $($_.Exception.Message). Recover with: git checkout -- <files>." -Data @{
                    filesRewritten = $rewrittenFiles.ToArray()
                    directoryMoved = $movedApplied
                    error          = $_.Exception.Message
                })
        }
        throw
    }

    [ordered]@{
        oldPath                    = $sourcePath
        newPath                    = $destPath
        kind                       = $resolvedKind
        pureMove                   = $pureMove
        referencesRewritten        = $referencesRewritten
        filesRewritten             = $rewrittenFiles.ToArray()
        referencesSkippedAmbiguous = if ($referencesSkippedAmbiguous -gt 0) { [ordered]@{ count = $referencesSkippedAmbiguous; files = $ambiguousFiles.ToArray() } } else { $null }
        basenameCollisionCreated   = $basenameCollisionCreated
        folderNoteOrphaned         = $folderNoteOrphaned
        namingWarnings             = $namingWarnings
        filesSkippedUnreadable     = $filesSkippedUnreadable
        folderNoteRenamed          = $null
        containedFilesMoved        = @()
        lockWarning                = $null
    }
    }
}
catch {
    # A clean {code, message, data?} error (validate-stage refusal, partial_state, ...) passes
    # through unchanged. Invoke-VaultWriteLock's acquire-timeout is a plain string, outside that
    # convention - map it to the spec's lock_timeout code so callers see one shape.
    if ($_.Exception.Message -match '^\s*\{.*"code"') { throw }
    if ($_.Exception.Message -match 'waiting for the vault write lock') {
        throw (New-VaultToolError -Code 'lock_timeout' -Message $_.Exception.Message)
    }
    throw
}

if ($lockAbandoned) {
    $result.lockWarning = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
