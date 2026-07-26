# Public/LinkResolve.ps1 - the wikilink grammar and bare-wikilink resolution under
# same-basename duplication.
# PowerShell-only: this encodes Obsidian's empirically-mapped bare-link resolution rule
# (Resources/Software/Obsidian-Link-Behavior.md in vault-primary), with no C# counterpart in
# VaultAccess.cs. It is the shared comparator move's protection pass keys on, and `delete` and
# `lint` need the identical rule - so it lives in the module, not script-local to move.ps1.
# See ../VaultAccess.psm1 for the full mirror policy.
#
# The grammar below (pattern, key normalization, suffix match) joined it here at `lint`'s
# landing, on the rule of three: move rewrites links, delete censuses them, and lint's
# broken-wikilinks pass scans them, and all three must agree on what a link is and on what
# "the same name" means. Three verbatim copies is how that agreement drifts.

function Get-VaultLinkPattern {
    <#
    .SYNOPSIS
    The vault's wikilink grammar: (!?)[[ target (#heading)? (|alias)? ]], as a compiled [regex]
    with named groups embed / target / heading / alias.

    The target class excludes the link-structural characters, so it captures a bare basename or
    a vault-relative path but stops at the heading, the alias, or the closing brackets. Only the
    target is ever rewritten by a consumer; the embed marker, #heading, and |alias are preserved
    verbatim. The `embed` group is empty for a plain link and "!" for a transclusion, which is
    how a consumer that must not treat an attachment embed as a note link tells the two apart.

    Returns the one shared instance (constructed at module load, in VaultAccess.psm1 with the
    module's other load-time state) rather than compiling a new regex per call.
    #>
    [CmdletBinding()]
    param()
    $script:VaultAccessLinkPattern
}

function Get-VaultLinkKey {
    <#
    .SYNOPSIS
    Normalizes a filename or link target to its wikilink-namespace key: a trailing ".md" is
    stripped (notes are linked extension-less), any other extension is kept (an image or
    attachment is linked by its full filename). Backslashes fold to "/" and a trailing "/" is
    trimmed.

    This one function keys move's basename censuses and the moved item's old/new identity,
    delete's deleted set, lint's note index, and each scanned link's target - so every consumer
    agrees on what "the same name" means.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Name)
    $n = $Name.Trim().Replace('\', '/').TrimEnd('/')
    if ($n.ToLowerInvariant().EndsWith('.md')) { $n = $n.Substring(0, $n.Length - 3) }
    $n
}

function Test-VaultPathSuffixMatch {
    <#
    .SYNOPSIS
    True when $Candidate's path components are a component-aligned suffix of $Target's - the
    match Obsidian's "shortest path when possible" links need, since it writes partial suffixes
    like [[Foo/Note]] for a note at Deep/Foo/Note. Comparison is case-insensitive (Windows
    filesystem). A single-component candidate is handled by the bare-basename rule, not here, so
    callers pass only path-qualified (slash-bearing) candidates.
    #>
    [CmdletBinding()]
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

    EMPIRICAL RULE - CONFIRMED. The rule is empirical (see Obsidian-Link-Behavior.md) and
    both refinement micro-tests are resolved (2026-07-23): T11 confirmed pure character
    count with no extra weight for `..` components (two short hops beat one long one), and
    T12 confirmed the tie-break compares candidate VAULT paths, not relative paths (`..`
    sorts before letters relatively, yet Obsidian picked the subtree copy). Should a future
    Obsidian version change the rule, the score is the single `$score` line below and the
    tie-break is the [string]::Compare on candidate paths; nothing else in this function
    (or its callers) encodes the rule.
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
