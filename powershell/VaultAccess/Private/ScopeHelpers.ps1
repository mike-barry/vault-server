# Private/ScopeHelpers.ps1 - internal scope helpers, not exported (not in VaultAccess.psd1).
# See ../VaultAccess.psm1 for the module header and mirror policy. ConvertTo-VaultScopeSegments
# mirrors the C# side's VaultAccess.NormalizeScopeSegments; Get-VaultAccessAmbientScopeRoots is
# PowerShell-only host plumbing.

function ConvertTo-VaultScopeSegments {
    <#
    .SYNOPSIS
    Splits a folder-prefix scope root or candidate vault-relative path into normalized,
    case-preserved segments - resolving "." and ".." and rejecting rooted paths, root
    escapes, and empty/whitespace input. Mirrors the C# side's
    VaultAccess.NormalizeScopeSegments exactly - shared containment semantics land as a
    tests/cases/ JSON case, not side-specific logic. Throws for anything that cannot be
    normalized to a well-formed relative path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Scope path is empty.' }
    if ([System.IO.Path]::IsPathRooted($Path)) { throw "Scope path must be vault-relative, not rooted: $Path" }

    $segments = [System.Collections.Generic.List[string]]::new()
    foreach ($raw in $Path.Replace('\', '/').Split('/')) {
        if ($raw.Length -eq 0 -or $raw -eq '.') { continue }
        if ($raw -eq '..') {
            if ($segments.Count -eq 0) { throw "Scope path escapes the vault: $Path" }
            $segments.RemoveAt($segments.Count - 1)
            continue
        }
        $segments.Add($raw)
    }
    if ($segments.Count -eq 0) { throw "Scope path resolves to the vault root: $Path" }
    , $segments.ToArray()
}

function Get-VaultAccessAmbientScopeRoots {
    <#
    .SYNOPSIS
    Reads the host-injected VaultMcpScopeRoots global hashtable (set by ScriptToolRunner right
    before invoking a tool script, in the same runspace - see its InvokeAsync doc comment),
    or $null when nothing set it (direct module use, e.g. Pester tests calling chokepoint
    functions without going through the host). Keyed by vault name, mirroring the C# side's
    per-vault Grant.ScopeRoots map one-for-one - use Resolve-VaultScopeRoots to pick out the
    entry for a specific vault. Used as the default value for every chokepoint function's
    -ScopeRoots parameter so tool scripts get path-scope enforcement without plumbing it
    through by hand.
    #>
    [CmdletBinding()]
    param()

    (Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue)
}
