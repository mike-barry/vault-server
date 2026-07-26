# Public/Io.ps1 - note read/write and the skip-missing classifier.
# PowerShell-only: the C# side mirrors the chokepoint only where the host has real callers, and
# nothing in the host reads or writes notes - so these ops have no C# counterpart to keep in
# step and this module is the sole full-operation implementation. The chokepoint has no move
# operation at all: the `move` tool runs its own Move-Item over paths resolved and scope-checked
# through this module. See ../VaultAccess.psm1 for the full mirror policy.

function Read-VaultNote {
    <#
    .SYNOPSIS
    Reads a note's full content by vault-relative path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath $Path -ScopeRoots $flatScopeRoots
    Get-Content -LiteralPath (Resolve-VaultPath -Path $Path -Vault $Vault) -Raw -ErrorAction Stop
}

function Test-VaultNoteMissingError {
    <#
    .SYNOPSIS
    True when a failed note read means there is nothing to read - the note vanished between
    enumeration and read, or the path is a broken link - rather than a real failure
    (permissions, sharing violation, I/O). The shared classifier for skip-missing, so every
    enumerate-then-read tool uses one definition.

    Classification comes from the error record, never a Test-Path probe: a dangling symlink
    is enumerated as a file and satisfies Test-Path -PathType Leaf while being unreadable,
    and a probe is itself racy - a path deleted and recreated between the read and the probe
    reports as present.

    The inner-exception chain is walked because a not-found raised from a .NET call rather
    than a cmdlet arrives wrapped in MethodInvocationException.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [System.Management.Automation.ErrorRecord] $ErrorRecord
    )

    if ($ErrorRecord.CategoryInfo.Category -eq 'ObjectNotFound') { return $true }

    $ex = $ErrorRecord.Exception
    while ($ex) {
        if ($ex -is [System.IO.FileNotFoundException] -or
            $ex -is [System.IO.DirectoryNotFoundException] -or
            $ex -is [System.Management.Automation.ItemNotFoundException]) {
            return $true
        }
        $ex = $ex.InnerException
    }
    return $false
}

function Write-VaultNote {
    <#
    .SYNOPSIS
    Writes a note as UTF-8 without BOM to an existing directory. Enforces the chokepoint
    invariant that a write never brings a directory into existence: a missing parent is
    refused with the shared `parent_not_found` code naming it, never created.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath $Path -ScopeRoots $flatScopeRoots
    $full = Resolve-VaultPath -Path $Path -Vault $Vault
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        # The missing parent is reported vault-relative with forward slashes - the same path
        # vocabulary the caller supplied - so a tool surfaces this error unchanged rather
        # than re-deriving the parent or leaking the absolute vault root.
        $missingParent = ($dir.Substring($resolved.Root.Length) -replace '^[\\/]+', '') -replace '\\', '/'
        throw (New-VaultToolError -Code 'parent_not_found' -Message "Parent directory does not exist: $missingParent" -Data @{ path = $Path; parent = $missingParent })
    }
    [System.IO.File]::WriteAllText($full, $Content)
}
