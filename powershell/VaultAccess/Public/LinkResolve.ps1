# Public/LinkResolve.ps1 - bare-wikilink resolution under same-basename duplication.
# PowerShell-only: this encodes Obsidian's empirically-mapped bare-link resolution rule
# (Resources/Software/Obsidian-Link-Behavior.md in vault-primary), with no C# counterpart in
# VaultAccess.cs. It is the shared comparator move's protection pass keys on, and the future
# `delete` and `lint` primitives need the identical rule - so it lives in the module, not
# script-local to move.ps1. See ../VaultAccess.psm1 for the full mirror policy.

function Resolve-VaultBareLink {
    <#
    .SYNOPSIS
    Resolves a bare wikilink ([[Name]]) among same-basename candidate files to the single
    file Obsidian would open, deterministically.

    .DESCRIPTION
    Among the same-basename $Candidates, the winner is the file with the shortest RELATIVE
    PATH FROM THE LINKING FILE (character count); ties are broken alphabetically (ordinal,
    case-insensitive) on the candidate's vault-relative path. Resolution is relative, not
    vault-global: a deeper candidate inside the linking file's own subtree beats a
    globally-shorter path elsewhere. The relative path is spelled out with `..` components
    (e.g. `../North/T3-Dup.md`) and its raw character count is the comparison key.

    All inputs are vault-relative, forward-slash paths. $Candidates are assumed to already
    share a basename (the caller's census); this function only ranks them - it does not
    filter by name. Returns the winning candidate path, or $null for an empty candidate set.

    EMPIRICAL RULE - ADJUST HERE. The exact tie-break is empirical (see
    Obsidian-Link-Behavior.md). Two micro-tests (T11/T12) are pending on whether `..`
    components should carry extra weight versus a pure character count. That refinement is
    isolated to the single `$score` computation below (currently the plain string length of
    the spelled-out relative path) so it lands everywhere - move, and the future delete/lint
    primitives - at once. To weight `..` components, change how `$score` is derived from
    $rel; nothing else in this function (or its callers) encodes the rule.
    #>
    [CmdletBinding()]
    param(
        # Vault-relative path of the file that contains the link (its directory is the origin
        # the relative paths are measured from).
        [Parameter(Mandatory)] [string] $LinkingFile,
        # Vault-relative paths of the same-basename candidate files.
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Candidates
    )

    if ($Candidates.Count -eq 0) { return $null }
    if ($Candidates.Count -eq 1) { return $Candidates[0] }

    # The linking file's directory segments (empty when the file is at the vault root).
    # [string[]] cast: an `if` expression unwraps a single-element array to a scalar on
    # assignment; the constraint keeps it an array for Get-VaultRelativeLinkPath.
    $fromSegments = @($LinkingFile -split '/')
    [string[]] $fromDir = if ($fromSegments.Count -gt 1) { @($fromSegments[0..($fromSegments.Count - 2)]) } else { @() }

    $best = $null
    foreach ($candidate in $Candidates) {
        $rel = Get-VaultRelativeLinkPath -FromDir $fromDir -ToPath $candidate

        # ---- EMPIRICAL TIE-BREAK REFINEMENT POINT (T11/T12) ----
        # Currently a pure character count of the spelled-out relative path. A future
        # refinement (weighting `..` components) changes only this line.
        $score = $rel.Length

        $isBetter = $false
        if ($null -eq $best) {
            $isBetter = $true
        }
        elseif ($score -lt $best.Score) {
            $isBetter = $true
        }
        elseif ($score -eq $best.Score -and
            [string]::Compare($candidate, $best.Path, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $isBetter = $true
        }

        if ($isBetter) {
            $best = [pscustomobject]@{ Path = $candidate; Score = $score }
        }
    }

    $best.Path
}

function Get-VaultRelativeLinkPath {
    <#
    .SYNOPSIS
    Builds the spelled-out relative path from a linking file's directory ($FromDir segments)
    to a candidate file ($ToPath), with `..` components for each level walked up out of the
    common ancestor - e.g. FromDir = Tests/T3/Deep, ToPath = Tests/T3/North/T3-Dup.md yields
    "../North/T3-Dup.md". Segment comparison is case-insensitive (Windows filesystem). This
    is the string whose length Resolve-VaultBareLink ranks; kept separate so the relative-path
    construction and the ranking/tie-break stay independently testable.
    #>
    [CmdletBinding()]
    param(
        # Empty when the linking file is at the vault root; not mandatory so an empty array
        # binds cleanly (a mandatory [string[]] rejects @() as null).
        [AllowEmptyCollection()] [string[]] $FromDir = @(),
        [Parameter(Mandatory)] [string] $ToPath
    )

    $toSegments = @($ToPath -split '/')
    $fileName = $toSegments[-1]
    # [string[]] casts are load-bearing: a single-element array returned from an `if`
    # expression is unwrapped to a scalar string on assignment, which would then index by
    # character; the type constraint re-wraps it into an array.
    [string[]] $toDir = if ($toSegments.Count -gt 1) { @($toSegments[0..($toSegments.Count - 2)]) } else { @() }

    # Longest common leading directory run (case-insensitive).
    $common = 0
    while ($common -lt $FromDir.Count -and $common -lt $toDir.Count -and ($FromDir[$common] -ieq $toDir[$common])) {
        $common++
    }

    $up = $FromDir.Count - $common
    [string[]] $down = if ($common -lt $toDir.Count) { @($toDir[$common..($toDir.Count - 1)]) } else { @() }

    $segments = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $up; $i++) { $segments.Add('..') }
    foreach ($d in $down) { $segments.Add($d) }
    $segments.Add($fileName)

    $segments -join '/'
}
