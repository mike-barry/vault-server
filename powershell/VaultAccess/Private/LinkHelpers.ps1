# Private/LinkHelpers.ps1 - internal link-resolution helpers, not exported (not in VaultAccess.psd1).
# See ../VaultAccess.psm1 for the module header and mirror policy. Get-VaultRelativeLinkPath is
# PowerShell-only, the relative-path constructor Resolve-VaultBareLink (Public/LinkResolve.ps1)
# ranks; kept private and separate so the path construction and the ranking/tie-break stay
# independently testable.

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
