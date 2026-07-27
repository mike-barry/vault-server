<#
.SYNOPSIS
Relocates or RENAMES a file or folder within the vault as one write-locked plan-validate-apply
operation, rewriting every reference to the moved item so the link graph stays intact.

Call this when:
- you are renaming a note or a folder, or moving one somewhere else. A dest with no "/" renames in
  place, keeping the current parent; a dest containing "/" is the full vault-relative destination
- you are reorganizing - archiving a doc, moving a note between projects or areas
- anything in the vault might link to the item. This is the ONLY tool that rewrites `[[wikilinks]]`
  and `![[embeds]]`, agentic nav.* frontmatter included, so relocating by create-plus-delete or
  delete-plus-create silently breaks references this call would have carried across
- you want the blast radius first. apply:false runs plan and validate and returns the full report -
  what would be moved, renamed, and rewritten - without touching the vault, and `applied`
  distinguishes a plan from a result. Mutation is the default here, since it is the point of a move

Not for editing a note's content - that is edit. Not for removing something - that is delete. Not
for changing what a file IS: the destination must preserve the source's extension, both-absent
included, so "notes.txt" to "notes.md" is refused with no override.

The lock serializes the operation against other writers; it does not make it atomic - a multi-file
rewrite that fails partway raises partial_state naming the files already written. Ambiguity is
never an error: mirroring Obsidian's own link maintenance (Obsidian-Link-Behavior), a move that
would change any bare link's resolution proceeds and protects the affected links - following the
moved file, or pinning a threatened same-basename bystander link to its current target's full
vault-relative path (Reference Rewrite / Protection). There is no ambiguity gate and no Force
override. See the Move corpus doc (vault-primary) for the full contract.
.PARAMETER Source
Vault-relative path of the file or folder to move.
.PARAMETER Dest
Dual semantics on the "/" discriminator: a bare name (no "/") renames in place, keeping
the current parent; a value containing "/" is the full vault-relative destination. A
leading "/" targets the vault root. Dest must preserve the source's extension, both-absent
included - ".md" is never inferred and an extension is never converted.
.PARAMETER Kind
Intent assertion - "file" or "folder" - inferred from the source's shape when omitted;
fails fast (kind_mismatch) when supplied and wrong. Deliberately not a ValidateSet, so
every value reaches the script body and is refused with move's own invalid_argument JSON
error rather than a PowerShell binding error. "project"/"area" retired from the kind
vocabulary in 2026-07 and are kept as an explicit legacy-argument refusal: the generic
branch would refuse them anyway, so the specific branch exists only to name the
replacement (kind "folder" plus skill-side registry composition) for a caller still
carrying the old vocabulary.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.PARAMETER Apply
Mutation opt-in, default true (mutation is the point of a move). False runs plan and
validate and returns the full report - what would be moved, renamed, and rewritten -
without touching the vault; the return's `applied` field distinguishes a plan from a
result. See Plan-Vault-Tool-Primitives's Assess-Before-Mutate Convention.

Declared last so it appends to the existing positional order rather than displacing
$Vault, which a standalone positional caller already binds at position 4.
.NOTES
OpClass: content:write

move asserts no additional op class ("key") beyond its content:write floor, even when a
reference rewrite lands inside nav.*/agent.* frontmatter - its rewrites preserve routing
intent rather than author it, so `capabilitiesUsed` is always empty. See Plan-Tool-Move's
Op Classes section. Runs under the per-vault write lock as plan -> validate -> apply;
apply:false returns after validate, having mutated nothing. The return carries the
write-class envelope (Plan-Vault-Tool-Primitives: Return Convention): capabilitiesUsed and
filesSkippedUnreadable[] always, [] when empty; lockWarning only when an abandoned mutex
was inherited.
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Dest,
    [string] $Kind,
    [string] $Vault,
    [bool] $Apply = $true
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
    destination, with a leading "/" (vault-root spelling) normalized away.

    No extension is ever inferred or appended - the path is taken verbatim after separator
    normalization (Plan-Vault-Tool-Primitives: Path Convention). Whether the resulting
    basename carries the source's extension is a separate, refusing check in the validate
    phase; this function only shapes the path.

    Dest is constructed, not resolved (Plan-Tool-Move): it names a target that does not yet
    exist, so this never touches the filesystem.
    #>
    param(
        [Parameter(Mandatory)] [string] $RawDest,
        [Parameter(Mandatory)] [string] $SourcePath
    )

    $trimmed = $RawDest.Trim().Replace('\', '/')
    if ($trimmed.Contains('/')) {
        # Full vault-relative destination; a leading "/" (vault root) is normalized away.
        $trimmed.TrimStart('/')
    }
    else {
        # Bare name: rename in place, keeping the source's current parent folder.
        $slashIndex = $SourcePath.LastIndexOf('/')
        $parent = if ($slashIndex -ge 0) { $SourcePath.Substring(0, $slashIndex) } else { '' }
        if ($parent) { "$parent/$trimmed" } else { $trimmed }
    }
}

function Get-VaultMoveDestSuggestion {
    <#
    .SYNOPSIS
    Rewrites the caller's raw Dest argument to carry $Extension, so an extension-preservation
    refusal can name the exact value that would have worked and the caller retries in one
    call. Operates on the raw spelling (bare name, relative path, or "/"-rooted path) rather
    than the constructed path, because that is what the caller passes back. An empty
    $Extension strips the extension AND its period by hand rather than through
    ChangeExtension: PowerShell binds $null to that overload's string parameter as "", which
    leaves a trailing dot - itself an invalid basename, so the suggestion would not work.
    #>
    param(
        [Parameter(Mandatory)] [string] $RawDest,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Extension
    )
    $normalized = $RawDest.Trim().Replace('\', '/')
    if ([string]::IsNullOrEmpty($Extension)) {
        $current = [System.IO.Path]::GetExtension($normalized)
        if ($current) { $normalized.Substring(0, $normalized.Length - $current.Length) } else { $normalized }
    }
    else { [System.IO.Path]::ChangeExtension($normalized, $Extension) }
}

# ---------------------------------------------------------------------------
# Reference-rewrite engine. A single link-matching grammar drives both the
# basename censuses' "does a bare link to the old name exist" signal and the
# actual rewrite, so `move` scans the vault once (Plan-Tool-Move: "One scan, one
# grammar"). The grammar covers wikilinks and embeds; only the link *target* is
# ever rewritten - the embed marker, #heading, and |alias are preserved verbatim.
# ---------------------------------------------------------------------------

# The grammar and its two comparators are the module's (VaultAccess Public/LinkResolve.ps1):
# move rewrites links, delete censuses them, and lint scans them, so all three read one
# definition of what a link is and of what "the same name" means. The pattern is bound to a
# script variable here so the match call sites below stay unchanged.
$script:VaultLinkPattern = Get-VaultLinkPattern

function New-VaultMoveBareNamespaces {
    <#
    .SYNOPSIS
    Builds the per-basename candidate sets (pre-move and post-move) the protection pass
    resolves each bare link against (Move corpus: Reference Rewrite / Protection). A
    namespace maps a lowercased basename key to its Pre and Post candidate path lists: Pre is
    the current census for that basename; Post is the census with the moved item remapped.

    When the old and new basenames are the same name (a pure relocation, or a case-only
    rename - both keep the item inside one basename namespace), there is a single shared
    namespace whose Post has the item moved from its old path to its new path. When the
    basenames differ, there are two: the old-basename namespace the item LEAVES (Post drops
    it) and the new-basename namespace it ENTERS at its new path (Post adds it). Membership
    keys are lowercased so a case-only rename collapses to the single-namespace branch rather
    than colliding two entries on one key.
    #>
    param(
        [Parameter(Mandatory)] [string] $OldKeyBasename,
        [Parameter(Mandatory)] [string] $NewKeyBasename,
        # OldCensus includes the moved item at its old path; NewCensus excludes it (pre-move it
        # still carries the old basename).
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $OldCensus,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $NewCensus,
        [Parameter(Mandatory)] [string] $MovedOldPath,
        [Parameter(Mandatory)] [string] $MovedNewPath
    )

    $ns = @{}
    if ($OldKeyBasename -ieq $NewKeyBasename) {
        # One shared namespace: the item stays in it but changes location (relocation) or case
        # (case-only rename). Post = census minus the item's old path, plus its new path.
        $ns[$OldKeyBasename.ToLowerInvariant()] = [pscustomobject]@{
            Pre  = @($OldCensus)
            Post = @(@($OldCensus | Where-Object { $_ -cne $MovedOldPath }) + $MovedNewPath)
        }
    }
    else {
        # Old-basename namespace: the moved item leaves it.
        $ns[$OldKeyBasename.ToLowerInvariant()] = [pscustomobject]@{
            Pre  = @($OldCensus)
            Post = @($OldCensus | Where-Object { $_ -cne $MovedOldPath })
        }
        # New-basename namespace: the moved item enters it at its new path.
        $ns[$NewKeyBasename.ToLowerInvariant()] = [pscustomobject]@{
            Pre  = @($NewCensus)
            Post = @(@($NewCensus) + $MovedNewPath)
        }
    }
    $ns
}

function Convert-VaultLinkReferences {
    <#
    .SYNOPSIS
    Single-pass rewrite of one file's content. Classifies every link target as bare or
    path-qualified, decides per the identify/emit/protection rules (Move corpus: Reference
    Rewrite) whether and how it must change, and rebuilds the file's text with only the
    affected targets repointed - embed marker, heading, and alias copied through untouched.

    Path-qualified links that suffix-match the moved item's old path follow it (canonical
    emit). Bare links are resolved pre- and post-move against their basename namespace
    (Resolve-VaultBareLink) and handled per Protection:
      - resolves to the moved item -> emit the moved item's canonical target (a no-op when
        the emitted text is identical, e.g. a pure relocation; a real edit on a case-only
        rename or when the basename changed).
      - resolves to a bystander whose winner the move would flip -> PIN the link to that
        bystander's full vault-relative path (never re-aimed).
      - resolves to a bystander the move does not affect -> untouched.

    Returns NewContent (the rewritten text), RewriteCount (link occurrences actually changed),
    and PinnedCount (the subset of those that were protective bystander pins).
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        # The linking file's own vault-relative path - the origin bare-link relative paths are
        # measured from. Pre and Post differ only when the linking file itself moves (a
        # self-referential source note, or a contained file in a folder move).
        [Parameter(Mandatory)] [string] $LinkingFilePre,
        [Parameter(Mandatory)] [string] $LinkingFilePost,
        [Parameter(Mandatory)] [string[]] $OldKeyPathComponents,
        # The moved item's old/new vault-relative paths - the winner identity Resolve-VaultBareLink
        # results are compared against.
        [Parameter(Mandatory)] [string] $MovedOldPath,
        [Parameter(Mandatory)] [string] $MovedNewPath,
        [Parameter(Mandatory)] [string] $NewTargetBare,
        [Parameter(Mandatory)] [string] $NewTargetFull,
        [Parameter(Mandatory)] [bool] $EmitBare,
        # keyLc -> [pscustomobject]@{ Pre; Post } from New-VaultMoveBareNamespaces.
        [Parameter(Mandatory)] [hashtable] $Namespaces,
        # ProcessBare is false only for the fast path where no bare link could change (a pure
        # relocation whose basename is unique vault-wide): the resolution scan is skipped and
        # bare links are left untouched, while the path-qualified scan still runs.
        [Parameter(Mandatory)] [bool] $ProcessBare
    )

    $emit = if ($EmitBare) { $NewTargetBare } else { $NewTargetFull }

    $rewriteCount = 0
    $pinnedCount = 0

    $sb = [System.Text.StringBuilder]::new()
    $cursor = 0
    foreach ($m in $script:VaultLinkPattern.Matches($Content)) {
        [void]$sb.Append($Content.Substring($cursor, $m.Index - $cursor))
        $cursor = $m.Index + $m.Length

        $key = Get-VaultLinkKey -Name $m.Groups['target'].Value
        $newTarget = $null
        $isPin = $false

        if ($key.Contains('/')) {
            # Path-qualified: definitive suffix match, always follows the moved item (a path is
            # never ambiguous).
            if (Test-VaultPathSuffixMatch -Target $OldKeyPathComponents -Candidate ($key -split '/')) {
                $newTarget = $emit
            }
        }
        elseif ($ProcessBare) {
            $keyLc = $key.ToLowerInvariant()
            if ($Namespaces.ContainsKey($keyLc)) {
                $ns = $Namespaces[$keyLc]
                $preWinner = Resolve-VaultBareLink -LinkingFile $LinkingFilePre -Candidates $ns.Pre
                $postWinner = Resolve-VaultBareLink -LinkingFile $LinkingFilePost -Candidates $ns.Post

                if ($null -ne $preWinner -and $preWinner -ceq $MovedOldPath) {
                    # The link currently resolves to the moved item; emit its canonical target.
                    # A pure relocation emits identical text (the rebuild guard drops it); a
                    # case-only rename or a basename change produces a real edit that follows it.
                    $newTarget = $emit
                }
                elseif ($null -ne $preWinner -and ($postWinner -cne $preWinner)) {
                    # The link resolves to a bystander whose winner the move would flip - pin it
                    # to that bystander's current target's full vault-relative path. Never re-aimed.
                    $newTarget = Get-VaultLinkKey -Name $preWinner
                    $isPin = $true
                }
            }
        }

        if ($null -ne $newTarget) {
            $rebuilt = $m.Groups['embed'].Value + '[[' + $newTarget + $m.Groups['heading'].Value + $m.Groups['alias'].Value + ']]'
            if ($rebuilt -cne $m.Value) {
                [void]$sb.Append($rebuilt)
                $rewriteCount++
                if ($isPin) { $pinnedCount++ }
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
        NewContent   = $sb.ToString()
        RewriteCount = $rewriteCount
        PinnedCount  = $pinnedCount
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

    (b) the protective bare-link pass for the folder note's basename - the folder note is the
        only basename event in a folder op (contained basenames don't change), so this is
        exactly the file-move protection rule (Move corpus: Protection) applied to the folder
        note: a bare link that resolves to the folder note follows it (canonical emit); a bare
        link to a same-basename bystander whose winner the move would flip is pinned to that
        bystander's full path. Relocating the folder note can flip a winner even without a
        rename, so this runs whenever the folder note exists and ambiguity is in play, not only
        on a basename change.

    Path-qualified links are always definitive (a path is never ambiguous) and rewritten
    regardless of the folder-note basename census. Bare links other than the folder note's
    basename (e.g. to a contained file, whose name does not change) are left untouched -
    contained-file bare-link protection is deliberately out of scope (Move corpus: Role).

    Returns NewContent, RewriteCount, and PinnedCount (protective bystander pins, a subset of
    RewriteCount).
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string]   $LinkingFilePre,
        [Parameter(Mandatory)] [string]   $LinkingFilePost,
        [Parameter(Mandatory)] [string[]] $OldFolderComponents,
        [Parameter(Mandatory)] [string[]] $NewFolderComponents,
        [Parameter(Mandatory)] [string]   $OldFolderPathKey,
        [Parameter(Mandatory)] [System.Collections.Generic.HashSet[string]] $ContainedKeySet,
        [Parameter(Mandatory)] [AllowEmptyString()] [string]   $OldFolderNoteKey,
        [Parameter(Mandatory)] [string]   $OldFolderBasename,
        [Parameter(Mandatory)] [string]   $NewFolderBasename,
        # The folder note exists AND its basename changed: drives the folder-note leaf swap in
        # a qualified link (the leaf changes with the folder's basename). False on a pure
        # relocation (basename kept) or when the folder note is absent.
        [Parameter(Mandatory)] [bool]     $FolderNoteBasenameChanged,
        # The folder note's old/new vault-relative paths - winner identity for the bare pass.
        [Parameter(Mandatory)] [string]   $FolderNoteOldPath,
        [Parameter(Mandatory)] [string]   $FolderNoteNewPath,
        [Parameter(Mandatory)] [string]   $FolderNoteNewBareTarget,
        [Parameter(Mandatory)] [string]   $FolderNoteNewFullTarget,
        [Parameter(Mandatory)] [bool]     $EmitBareFolderNote,
        # keyLc -> [pscustomobject]@{ Pre; Post } from New-VaultMoveBareNamespaces, for the
        # folder note's basename namespace(s).
        [Parameter(Mandatory)] [hashtable] $Namespaces,
        # False when no bare folder-note link could change (no folder note, or a pure
        # relocation whose folder-note basename is unique vault-wide): the bare pass is skipped.
        [Parameter(Mandatory)] [bool]     $ProcessBareFolderNote
    )

    $emitBare = if ($EmitBareFolderNote) { $FolderNoteNewBareTarget } else { $FolderNoteNewFullTarget }

    $rewriteCount = 0
    $pinnedCount = 0

    $sb = [System.Text.StringBuilder]::new()
    $cursor = 0
    foreach ($m in $script:VaultLinkPattern.Matches($Content)) {
        [void]$sb.Append($Content.Substring($cursor, $m.Index - $cursor))
        $cursor = $m.Index + $m.Length

        $key = Get-VaultLinkKey -Name $m.Groups['target'].Value
        $newTarget = $null
        $isPin = $false

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
        elseif ($ProcessBareFolderNote) {
            # Bare link whose basename is the folder note's - the one bare basename event in a
            # folder op. Resolve pre/post and apply the file-move protection rule.
            $keyLc = $key.ToLowerInvariant()
            if ($Namespaces.ContainsKey($keyLc)) {
                $ns = $Namespaces[$keyLc]
                $preWinner = Resolve-VaultBareLink -LinkingFile $LinkingFilePre -Candidates $ns.Pre
                $postWinner = Resolve-VaultBareLink -LinkingFile $LinkingFilePost -Candidates $ns.Post

                if ($null -ne $preWinner -and $preWinner -ceq $FolderNoteOldPath) {
                    # Currently resolves to the folder note; emit its canonical target (a no-op
                    # when the emitted text is identical).
                    $newTarget = $emitBare
                }
                elseif ($null -ne $preWinner -and ($postWinner -cne $preWinner)) {
                    # Resolves to a same-basename bystander whose winner the move would flip -
                    # pin it to that bystander's full vault-relative path.
                    $newTarget = Get-VaultLinkKey -Name $preWinner
                    $isPin = $true
                }
            }
        }

        if ($null -ne $newTarget) {
            $rebuilt = $m.Groups['embed'].Value + '[[' + $newTarget + $m.Groups['heading'].Value + $m.Groups['alias'].Value + ']]'
            if ($rebuilt -cne $m.Value) {
                [void]$sb.Append($rebuilt)
                $rewriteCount++
                if ($isPin) { $pinnedCount++ }
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
        NewContent   = $sb.ToString()
        RewriteCount = $rewriteCount
        PinnedCount  = $pinnedCount
    }
}

# Vault resolution before the write lock: the lock's mutex name is derived from the resolved
# vault root, so Invoke-VaultWriteLock resolves the vault itself and would raise the
# mirrored-core plain text before the locked scriptblock ever runs. Resolving here refuses an
# unconfigured name as unknown_vault and a configured-but-root-missing vault as
# vault_unavailable (Plan-Vault-Tool-Primitives: Error Convention), with nothing locked.
[void](Resolve-VaultToolRoot -Vault $Vault)

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
        throw (New-VaultToolError -Code 'invalid_argument' -Message "kind '$Kind' is not valid for move - project/area moves carry a registry entry move does not own. Pass 'folder' (or omit kind) and compose the registry update via edit.")
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

    # --- Construct dest from the raw Dest argument's dual bare-name/full-path semantics.
    # Verbatim: no extension is inferred (Plan-Vault-Tool-Primitives: Path Convention). ---
    $destPath = Resolve-VaultMoveDestPath -RawDest $Dest -SourcePath $sourcePath

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

    # --- Extension preservation. dest must carry the resolved source's extension,
    # both-absent included (Plan-Vault-Tool-Primitives: Path Convention). This is the whole
    # of move's extension policy: nothing is inferred and nothing is converted, because
    # changing what a file *is* is not a move primitive's job - so "Foo.md" -> "Bar" and
    # "diagram.png" -> "photo" both refuse, "LICENSE" -> "COPYING" passes, and there is no
    # override. A folder source has no extension to preserve, so the rule is file-only; the
    # check needs only the resolved source, never a note-versus-file conditional. The
    # message names the value that would have worked so the caller retries in one call. ---
    if ($resolvedKind -eq 'file') {
        $sourceExtension = [System.IO.Path]::GetExtension($sourceBasename)
        $destExtension = [System.IO.Path]::GetExtension($destBasename)
        # Case-insensitive: the filesystem is, and ".MD" names the same file type as ".md",
        # so a case difference is a spelling the caller chose, not a conversion.
        if ($destExtension -ine $sourceExtension) {
            $suggestion = Get-VaultMoveDestSuggestion -RawDest $Dest -Extension $sourceExtension
            $sourceLabel = if ($sourceExtension) { "extension '$sourceExtension'" } else { 'no extension' }
            $destLabel = if ($destExtension) { "extension '$destExtension'" } else { 'no extension' }
            throw (New-VaultToolError -Code 'invalid_argument' -Message "dest must preserve the source's extension: source '$sourceBasename' has $sourceLabel but dest '$destBasename' has $destLabel. Retry with dest '$suggestion'. move never infers or converts an extension, and there is no override." -Data @{
                    source          = $sourcePath
                    dest            = $destPath
                    sourceExtension = $sourceExtension
                    destExtension   = $destExtension
                    suggestedDest   = $suggestion
                })
        }
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
    # FOLDER MOVE. One locked operation carrying three coupled effects
    # (Plan-Tool-Move: Role, Folder-move correctness core): the directory Move-Item, the
    # folder-note invariant (Foo/Foo.md -> Bar/Bar.md, only when the folder's basename changed),
    # and one vault scan applying two rewrites - the old-folder-path prefix substitution for
    # qualified links under the old path (which repairs links to contained files as a byproduct)
    # and the folder note's protective bare-link pass. Contained basenames never change, so the
    # folder note is the only basename event; the protect-and-proceed rule applies to it exactly
    # as in a file move (follow or pin, never fail). Nothing before the "APPLY" marker mutates.
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

        # The folder note's post-move vault-relative path (leaf renamed when the basename
        # changed) - needed both for the protection pass's winner identity and the apply.
        $folderNoteNewRel = if ($folderNoteExists) { & $remapContained $folderNoteOldRel } else { $folderNoteOldRel -replace [regex]::Escape($sourcePath), $destPath }

        # --- Folder-note basename censuses + protection setup. The folder note is the only
        # basename event in a folder op (contained basenames don't change), so this is the
        # file-move protection rule applied to it. Censuses are taken whenever a folder note
        # exists - relocating it can flip a bare link's winner even without a rename, so a pure
        # relocation with a non-unique folder-note basename still needs the resolution scan. ---
        $oldFolderNoteUnique = $true
        $newCollision = $false
        $emitBareFolderNote = $true
        $folderNoteNamespaces = @{}
        $processBareFolderNote = $false
        if ($folderNoteExists) {
            $oldKeyBasename = Get-VaultLinkKey -Name $oldFolderBasename
            $newKeyBasename = Get-VaultLinkKey -Name $newFolderBasename
            $allFiles = @(Get-VaultFilePath -Vault $Vault -ScopeRoots $null)
            $oldCensus = @($allFiles | Where-Object { (Get-VaultLinkKey -Name (($_ -split '/')[-1])) -ieq $oldKeyBasename })
            $newCensus = @($allFiles | Where-Object { (Get-VaultLinkKey -Name (($_ -split '/')[-1])) -ieq $newKeyBasename })
            $oldFolderNoteUnique = ($oldCensus.Count -le 1)
            # The folder note carries the old basename and is excluded from the "others with the
            # new basename" set (on a pure relocation new == old, so it appears in newCensus).
            $newCensusOthers = @($newCensus | Where-Object { $_ -cne $folderNoteOldRel })
            $newCollision = ($newCensusOthers.Count -gt 0)
            $emitBareFolderNote = -not $newCollision

            # NewCensus for the namespace builder must exclude the moved item (the folder note),
            # matching the file-move convention that NewCensus excludes the moved file.
            $folderNoteNamespaces = New-VaultMoveBareNamespaces `
                -OldKeyBasename $oldKeyBasename -NewKeyBasename $newKeyBasename `
                -OldCensus $oldCensus -NewCensus $newCensusOthers `
                -MovedOldPath $folderNoteOldRel -MovedNewPath $folderNoteNewRel

            # Skip the bare pass only when nothing could change: a pure relocation whose
            # folder-note basename is unique vault-wide.
            $processBareFolderNote = -not ($pureMove -and $oldFolderNoteUnique)
        }

        # --- Single vault scan. Builds each file's rewritten content (prefix substitution for
        # qualified links under the old folder + the folder-note protection pass) and tallies
        # rewrites and protective pins. Nothing written here. ---
        $rewriteTargets = [System.Collections.Generic.List[object]]::new()
        $pinnedFiles = [System.Collections.Generic.List[string]]::new()
        $referencesRewritten = 0
        $referencesPinned = 0
        # Paths, not a count (Plan-Vault-Tool-Primitives: Return Convention) - the count is
        # derivable and the paths are the actionable half.
        $filesSkippedUnreadable = [System.Collections.Generic.List[string]]::new()

        foreach ($rel in (Get-VaultNotePath -Vault $Vault -ScopeRoots $null)) {
            $full = Resolve-VaultPath -Path $rel -Vault $Vault
            try {
                $content = [System.IO.File]::ReadAllText($full)
            }
            catch {
                if (Test-VaultNoteMissingError -ErrorRecord $_) { $filesSkippedUnreadable.Add($rel); continue }
                throw (New-VaultToolError -Code 'read_failure' -Message "Could not read '$rel' during the reference scan: $($_.Exception.Message)" -Data @{ path = $rel; error = $_.Exception.Message })
            }

            # A contained linking file's own path changes post-move, shifting the relative
            # distances the resolution rule measures; resolve pre against its old path, post
            # against its remapped path.
            $linkingFilePost = if ($rel.StartsWith($folderPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { & $remapContained $rel } else { $rel }

            $r = Convert-VaultFolderLinkReferences -Content $content `
                -LinkingFilePre $rel -LinkingFilePost $linkingFilePost `
                -OldFolderComponents $oldFolderComponents -NewFolderComponents $newFolderComponents `
                -OldFolderPathKey $oldFolderPathKey -ContainedKeySet $containedKeySet `
                -OldFolderNoteKey $oldFolderNoteKey `
                -OldFolderBasename $oldFolderBasename -NewFolderBasename $newFolderBasename `
                -FolderNoteBasenameChanged $folderNoteBasenameChanged `
                -FolderNoteOldPath $folderNoteOldRel -FolderNoteNewPath $folderNoteNewRel `
                -FolderNoteNewBareTarget $folderNoteNewBareTarget -FolderNoteNewFullTarget $folderNoteNewFullTarget `
                -EmitBareFolderNote $emitBareFolderNote `
                -Namespaces $folderNoteNamespaces -ProcessBareFolderNote $processBareFolderNote

            if ($r.RewriteCount -gt 0) {
                $referencesRewritten += $r.RewriteCount
                $rewriteTargets.Add([pscustomobject]@{ Path = $rel; NewContent = $r.NewContent })
            }
            if ($r.PinnedCount -gt 0) {
                $referencesPinned += $r.PinnedCount
                $pinnedFiles.Add($rel)
            }
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
        # ASSESS-BEFORE-MUTATE (folder). apply:false stops here (Plan-Vault-Tool-Primitives:
        # Assess-Before-Mutate Convention): plan and validate have both run, so the report is
        # complete, and nothing above this line has written to the vault.
        #
        # filesRewritten holds the WOULD-rewrite set at the POST-move paths the same call
        # with apply:true would report - a contained note is named where it will live, not
        # where it lives now - so a plan is a faithful prediction of its own result rather
        # than a second path vocabulary the caller has to translate. containedFilesMoved and
        # folderNoteRenamed read the same way (would-move, would-rename). `applied: false` is
        # the single field that distinguishes a plan from a result.
        # =======================================================================
        if (-not $Apply) {
            return [ordered]@{
                oldPath                         = $sourcePath
                newPath                         = $destPath
                kind                            = $resolvedKind
                applied                         = $false
                pureMove                        = $pureMove
                referencesRewritten             = $referencesRewritten
                filesRewritten                  = @($rewriteTargets | ForEach-Object {
                        if ($_.Path.StartsWith($folderPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { & $remapContained $_.Path } else { $_.Path }
                    })
                referencesQualifiedProtectively = if ($referencesPinned -gt 0) { [ordered]@{ count = $referencesPinned; files = $pinnedFiles.ToArray() } } else { $null }
                folderNoteOrphaned              = $false
                namingWarnings                  = $namingWarnings
                folderNoteRenamed               = if ($folderNoteBasenameChanged) { [ordered]@{ oldPath = $folderNoteOldRel; newPath = $folderNoteNewRel } } else { $null }
                containedFilesMoved             = @($containedRels | ForEach-Object { & $remapContained $_ })
                capabilitiesUsed                = @()
                filesSkippedUnreadable          = $filesSkippedUnreadable.ToArray()
            }
        }

        # =======================================================================
        # APPLY (folder). Directory move -> folder-note rename -> rewrites, all under the one
        # lock. $dirMoved / $folderNoteRenamedApplied / the running $rewrittenFiles list are
        # exactly the already-done set a mid-apply failure must name in partial_state.
        # =======================================================================
        $dirMoved = $false
        $folderNoteRenamedApplied = $false
        $rewrittenFiles = [System.Collections.Generic.List[string]]::new()
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
                # Recovery is git-shaped but NOT a bare `git checkout`: nothing is staged, so the
                # destination tree is untracked and git checkout can neither restore nor delete it.
                # Clean the destination first, or the source restore leaves the folder at two paths.
                throw (New-VaultToolError -Code 'partial_state' -Message "Folder move partially applied then failed: '$sourcePath' was moved to '$destPath'$(if ($folderNoteRenamedApplied) { ' and the folder note renamed' }) and $($rewrittenFiles.Count) file(s) rewritten before the error: $($_.Exception.Message). Nothing was staged, so '$destPath' is untracked. Recover with: git clean -fd -- '$destPath', then git checkout -- '$sourcePath' (which also restores the contained rewrites) plus any data.filesRewritten entry outside '$destPath'." -Data @{
                        filesRewritten     = $rewrittenFiles.ToArray()
                        moveApplied        = $dirMoved
                        folderNoteRenamed  = $folderNoteRenamedApplied
                        error              = $_.Exception.Message
                    })
            }
            throw
        }

        $containedFilesMoved = @($containedRels | ForEach-Object { & $remapContained $_ })

        return [ordered]@{
            oldPath                         = $sourcePath
            newPath                         = $destPath
            kind                            = $resolvedKind
            applied                         = $true
            pureMove                        = $pureMove
            referencesRewritten             = $referencesRewritten
            filesRewritten                  = $rewrittenFiles.ToArray()
            referencesQualifiedProtectively = if ($referencesPinned -gt 0) { [ordered]@{ count = $referencesPinned; files = $pinnedFiles.ToArray() } } else { $null }
            folderNoteOrphaned              = $false
            namingWarnings                  = $namingWarnings
            folderNoteRenamed               = if ($folderNoteRenamedApplied) { [ordered]@{ oldPath = $folderNoteOldRel; newPath = $folderNoteNewRel } } else { $null }
            containedFilesMoved             = $containedFilesMoved
            # Write-class envelope: always present, [] when empty. lockWarning is the
            # deliberate exception and is attached post-lock only when it fires.
            capabilitiesUsed                = @()
            filesSkippedUnreadable          = $filesSkippedUnreadable.ToArray()
        }
    }

    # =======================================================================
    # FILE MOVE. Complete the validate phase (censuses, protection scan, read-only pre-flight),
    # then apply (Move-Item, then the rewrites) - all still under the one write lock. Nothing
    # before the "APPLY" marker mutates the vault. Ambiguity is never an error: a move that
    # would flip any bare link's resolution proceeds and protects the affected links (Move
    # corpus: Reference Rewrite / Protection).
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
    # is free (already_exists checked above). Entering an occupied basename is allowed
    # (protect-and-proceed): the moved file's links emit full-path and threatened bystander
    # links are pinned.
    $newCensusOthers = @($newCensus | Where-Object { $_ -cne $sourcePath })
    $newCollision = ($newCensusOthers.Count -gt 0)

    # EmitBare: a rewritten link that follows the moved item comes out as the bare new basename
    # when that name is unique vault-wide after the move, else as the full vault-relative path
    # (Move corpus: Emit - Obsidian-validated).
    $emitBare = -not $newCollision

    # Bare-link namespaces (pre/post candidate sets) the protection pass resolves each bare link
    # against. On a case-only rename the old and new basenames share one lowercased key, so this
    # collapses to a single namespace; on a rename the item leaves the old set and enters the new.
    $bareNamespaces = New-VaultMoveBareNamespaces `
        -OldKeyBasename $oldKeyBasename -NewKeyBasename $newKeyBasename `
        -OldCensus $oldCensus -NewCensus $newCensusOthers `
        -MovedOldPath $sourcePath -MovedNewPath $destPath

    # ProcessBare is skipped only on the fast path where no bare link can change: a pure
    # relocation (basename unchanged, case and all) whose basename is unique vault-wide. Every
    # other case - a rename, a case-only rename, or a relocation into a non-unique namespace -
    # needs the resolution scan because relocation can flip a winner.
    $processBare = -not ($pureMove -and $oldUnique)

    # --- Single scan over every note in the vault (ScopeRoots $null: the rewrite reaches
    # outside the grant's scope, by design). Builds each file's rewritten content and tallies
    # rewrites and protective pins. Nothing is written here. ---
    $rewriteTargets = [System.Collections.Generic.List[object]]::new()
    $pinnedFiles = [System.Collections.Generic.List[string]]::new()
    $referencesRewritten = 0
    $referencesPinned = 0
    # Paths, not a count (Plan-Vault-Tool-Primitives: Return Convention).
    $filesSkippedUnreadable = [System.Collections.Generic.List[string]]::new()

    foreach ($rel in (Get-VaultNotePath -Vault $Vault -ScopeRoots $null)) {
        $full = Resolve-VaultPath -Path $rel -Vault $Vault
        try {
            $content = [System.IO.File]::ReadAllText($full)
        }
        catch {
            # Vanished between enumeration and read (or a broken link) - skip and record the
            # path. A present-but-unreadable file (permissions, sharing, I/O) blocks the move.
            if (Test-VaultNoteMissingError -ErrorRecord $_) { $filesSkippedUnreadable.Add($rel); continue }
            throw (New-VaultToolError -Code 'read_failure' -Message "Could not read '$rel' during the reference scan: $($_.Exception.Message)" -Data @{ path = $rel; error = $_.Exception.Message })
        }

        # A self-referential source note is scanned at its old path but lands at the destination;
        # its own relative distances shift, so resolve pre against sourcePath and post against dest.
        $linkingFilePost = if ($rel -ceq $sourcePath) { $destPath } else { $rel }

        $r = Convert-VaultLinkReferences -Content $content `
            -LinkingFilePre $rel -LinkingFilePost $linkingFilePost `
            -OldKeyPathComponents $oldKeyPathComponents `
            -MovedOldPath $sourcePath -MovedNewPath $destPath `
            -NewTargetBare $newTargetBare -NewTargetFull $newTargetFull `
            -EmitBare $emitBare -Namespaces $bareNamespaces -ProcessBare $processBare

        if ($r.RewriteCount -gt 0) {
            $referencesRewritten += $r.RewriteCount
            $rewriteTargets.Add([pscustomobject]@{ Path = $rel; NewContent = $r.NewContent })
        }
        if ($r.PinnedCount -gt 0) {
            $referencesPinned += $r.PinnedCount
            $pinnedFiles.Add($rel)
        }
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
    # ASSESS-BEFORE-MUTATE (file). apply:false stops here (Plan-Vault-Tool-Primitives:
    # Assess-Before-Mutate Convention): plan and validate have both run - censuses, the
    # protection scan, and the read-only pre-flight - so the report is complete, and nothing
    # above this line has written to the vault.
    #
    # filesRewritten holds the WOULD-rewrite set at the POST-move paths the same call with
    # apply:true would report (a self-referential source note is named at the destination),
    # so a plan is a faithful prediction of its own result rather than a second path
    # vocabulary. `applied: false` is the single field that distinguishes a plan from a result.
    # =======================================================================
    if (-not $Apply) {
        return [ordered]@{
            oldPath                         = $sourcePath
            newPath                         = $destPath
            kind                            = $resolvedKind
            applied                         = $false
            pureMove                        = $pureMove
            referencesRewritten             = $referencesRewritten
            filesRewritten                  = @($rewriteTargets | ForEach-Object { if ($_.Path -ceq $sourcePath) { $destPath } else { $_.Path } })
            referencesQualifiedProtectively = if ($referencesPinned -gt 0) { [ordered]@{ count = $referencesPinned; files = $pinnedFiles.ToArray() } } else { $null }
            folderNoteOrphaned              = $folderNoteOrphaned
            namingWarnings                  = $namingWarnings
            folderNoteRenamed               = $null
            containedFilesMoved             = @()
            capabilitiesUsed                = @()
            filesSkippedUnreadable          = $filesSkippedUnreadable.ToArray()
        }
    }

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
            # Recovery is git-shaped but NOT a bare `git checkout`: nothing is staged, so the
            # destination is an untracked copy git checkout can neither restore nor delete - and a
            # self-referential source note is rewritten AT the destination, so filesRewritten can
            # name it. Clean the destination first, or the source restore leaves a duplicate
            # basename - exactly the state move exists to prevent.
            throw (New-VaultToolError -Code 'partial_state' -Message "Move partially applied then failed: '$sourcePath' was moved to '$destPath' and $($rewrittenFiles.Count) file(s) rewritten before the error: $($_.Exception.Message). Nothing was staged, so '$destPath' is untracked. Recover with: git clean -f -- '$destPath', then git checkout -- '$sourcePath' plus every data.filesRewritten entry except '$destPath'." -Data @{
                    filesRewritten = $rewrittenFiles.ToArray()
                    moveApplied    = $movedApplied
                    error          = $_.Exception.Message
                })
        }
        throw
    }

    [ordered]@{
        oldPath                         = $sourcePath
        newPath                         = $destPath
        kind                            = $resolvedKind
        applied                         = $true
        pureMove                        = $pureMove
        referencesRewritten             = $referencesRewritten
        filesRewritten                  = $rewrittenFiles.ToArray()
        referencesQualifiedProtectively = if ($referencesPinned -gt 0) { [ordered]@{ count = $referencesPinned; files = $pinnedFiles.ToArray() } } else { $null }
        folderNoteOrphaned              = $folderNoteOrphaned
        namingWarnings                  = $namingWarnings
        folderNoteRenamed               = $null
        containedFilesMoved             = @()
        # Write-class envelope: always present, [] when empty. lockWarning is the deliberate
        # exception and is attached post-lock only when it fires.
        capabilitiesUsed                = @()
        filesSkippedUnreadable          = $filesSkippedUnreadable.ToArray()
    }
}

# lockWarning is present ONLY when the write inherited an abandoned mutex and is omitted
# entirely otherwise - the envelope's deliberate exception to always-present (Plan-Vault-Tool-
# Primitives: Return Convention): it is a warning, not a status field, and an always-present
# null would read as a checked-and-clear signal.
if ($lockAbandoned) {
    $result['lockWarning'] = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
