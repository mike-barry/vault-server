# VaultAccess.psm1 - the vault-access chokepoint for PowerShell script tools.
#
# ALL vault I/O in tool scripts flows through this module - path resolution, wikilink
# resolution, reads, writes, and the grant check. Tool scripts never run raw
# Get-Content/Set-Content against vault paths. The C# counterpart for host code is
# src/VaultMcpServer/Vault/VaultAccess.cs, which mirrors these operations and semantics -
# keep the two in step.
#
# Vault conventions (wikilink forms, filename rules, layout) are encoded only here and in
# the C# class; they implement the vault's Document-Conventions.md and Vault-Guide.md
# (Resources/Vault/Guides/ in the vault repo).

$script:VaultConfig = $null

function Initialize-VaultAccess {
    <#
    .SYNOPSIS
    Loads the vault name-to-root map from vaults.json. Called implicitly with the default
    path (repo-root vaults.json, overridable via $env:VAULT_MCP_VAULTS_JSON) on first use;
    call explicitly with -ConfigPath to point at a different config (e.g. in tests).
    #>
    [CmdletBinding()]
    param(
        [string] $ConfigPath
    )

    if (-not $ConfigPath) {
        $ConfigPath = if ($env:VAULT_MCP_VAULTS_JSON) { $env:VAULT_MCP_VAULTS_JSON } else { Join-Path $PSScriptRoot '../../vaults.json' }
    }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "Vault config not found: $ConfigPath"
    }

    $raw = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if (-not $raw.default_vault) { throw "${ConfigPath}: missing or empty `"default_vault`"." }
    if (-not $raw.vaults) { throw "${ConfigPath}: missing `"vaults`" object." }

    $vaults = @{}
    foreach ($entry in $raw.vaults.PSObject.Properties) {
        if (-not $entry.Value) { throw "${ConfigPath}: vault `"$($entry.Name)`" has an empty root path." }
        $root = (Resolve-Path -LiteralPath $entry.Value -ErrorAction SilentlyContinue).Path
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) {
            throw "${ConfigPath}: vault `"$($entry.Name)`" root does not exist: $($entry.Value)"
        }
        $vaults[$entry.Name] = $root
    }
    if ($vaults.Count -eq 0) { throw "${ConfigPath}: `"vaults`" must contain at least one entry." }
    if (-not $vaults.ContainsKey([string]$raw.default_vault)) {
        throw "${ConfigPath}: `"default_vault`" ($($raw.default_vault)) is not a configured vault."
    }

    $script:VaultConfig = [pscustomobject]@{
        DefaultVault = [string]$raw.default_vault
        Vaults       = $vaults
    }
}

function Get-VaultAccessConfig {
    <#
    .SYNOPSIS
    Returns the loaded vault config (DefaultVault + Vaults map), initializing from the
    default path on first use.
    #>
    [CmdletBinding()]
    param()

    if (-not $script:VaultConfig) { Initialize-VaultAccess }
    $script:VaultConfig
}

function Assert-VaultOperationAllowed {
    # Grant-check hook. Phase 1 stub: always allows. Op-class enforcement happens in the
    # C# host's bearer middleware before a tool script ever runs; per-path scoping is the
    # phase 2 hook this signature reserves. Every operation below calls this before
    # touching the file system.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidateSet('Read', 'Write')] [string] $Op,
        [Parameter(Mandatory)] [string] $Vault,
        [Parameter(Mandatory)] [string] $RelativePath
    )
}

function Resolve-VaultRoot {
    <#
    .SYNOPSIS
    Resolves a vault name (or the default when omitted) to @{ Name; Root }.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault
    )

    $config = Get-VaultAccessConfig
    $name = if ([string]::IsNullOrWhiteSpace($Vault)) { $config.DefaultVault } else { $Vault }
    $match = $config.Vaults.Keys | Where-Object { $_ -ieq $name } | Select-Object -First 1
    if (-not $match) {
        throw "Unknown vault: $name. Configured vaults: $($config.Vaults.Keys -join ', ')"
    }
    [pscustomobject]@{ Name = $match; Root = $config.Vaults[$match] }
}

function Resolve-VaultPath {
    <#
    .SYNOPSIS
    Resolves a vault-relative path to an absolute path, rejecting rooted paths and any
    path that escapes the vault root.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $Vault
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Path is empty.' }
    if ([System.IO.Path]::IsPathRooted($Path)) { throw "Path must be vault-relative, not rooted: $Path" }

    $resolved = Resolve-VaultRoot -Vault $Vault
    $full = [System.IO.Path]::GetFullPath((Join-Path $resolved.Root $Path))
    $rootPrefix = $resolved.Root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $full.Equals($resolved.Root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes vault `"$($resolved.Name)`": $Path"
    }
    $full
}

function Get-VaultNotePath {
    <#
    .SYNOPSIS
    Enumerates all notes in a vault as vault-relative, forward-slash paths, skipping
    dot-directories (.git, .obsidian, ...).
    #>
    [CmdletBinding()]
    param(
        [string] $Vault
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Read -Vault $resolved.Name -RelativePath '.'

    Get-ChildItem -LiteralPath $resolved.Root -Filter '*.md' -File -Recurse -Force |
        ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $_.FullName).Replace('\', '/')
            $segments = $relative -split '/'
            $dirSegments = if ($segments.Count -gt 1) { $segments[0..($segments.Count - 2)] } else { @() }
            if (-not ($dirSegments | Where-Object { $_.StartsWith('.') })) { $relative }
        }
}

function Resolve-WikiLink {
    <#
    .SYNOPSIS
    Resolves a wikilink to a vault-relative note path. Accepts every convention form:
    [[Name]], bare Name, [[Folder/Name]] (path-qualified, matched on trailing path
    segments), and either with #heading and/or |display suffixes, which are ignored.
    Returns $null when nothing matches; throws when the link is ambiguous.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Link,
        [string] $Vault
    )

    $target = $Link.Trim()
    if ($target.StartsWith('[[') -and $target.EndsWith(']]')) { $target = $target.Substring(2, $target.Length - 4) }
    $target = ($target -split '\|')[0]
    $target = ($target -split '#')[0]
    $target = $target.Trim().Replace('\', '/').Trim('/')
    if (-not $target) { throw "Empty wikilink: $Link" }

    $qualified = $target.Contains('/')
    $found = @(Get-VaultNotePath -Vault $Vault | Where-Object {
        $noteNoExt = $_.Substring(0, $_.Length - 3)
        if ($qualified) {
            $noteNoExt -ieq $target -or $noteNoExt.EndsWith("/$target", [System.StringComparison]::OrdinalIgnoreCase)
        }
        else {
            ($noteNoExt -split '/')[-1] -ieq $target
        }
    })

    switch ($found.Count) {
        0 { return $null }
        1 { return $found[0] }
        default { throw "Ambiguous wikilink [[$target]]: $($found -join ', ')" }
    }
}

function Read-VaultNote {
    <#
    .SYNOPSIS
    Reads a note's full content by vault-relative path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $Vault
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Read -Vault $resolved.Name -RelativePath $Path
    Get-Content -LiteralPath (Resolve-VaultPath -Path $Path -Vault $Vault) -Raw -ErrorAction Stop
}

function Write-VaultNote {
    <#
    .SYNOPSIS
    Writes a note as UTF-8 without BOM, creating parent directories as needed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [string] $Vault
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Write -Vault $resolved.Name -RelativePath $Path
    $full = Resolve-VaultPath -Path $Path -Vault $Vault
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($full, $Content)
}

function Move-VaultNote {
    <#
    .SYNOPSIS
    Moves/renames a note from one vault-relative path to another, creating parent
    directories as needed. Throws if the source note does not exist or the destination
    already exists.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $NewPath,
        [string] $Vault
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Write -Vault $resolved.Name -RelativePath $Path
    Assert-VaultOperationAllowed -Op Write -Vault $resolved.Name -RelativePath $NewPath

    $fullOld = Resolve-VaultPath -Path $Path -Vault $Vault
    $fullNew = Resolve-VaultPath -Path $NewPath -Vault $Vault

    if (-not (Test-Path -LiteralPath $fullOld -PathType Leaf)) {
        throw "Source note not found: $Path"
    }
    if (Test-Path -LiteralPath $fullNew) {
        throw "Destination already exists: $NewPath"
    }

    $dir = [System.IO.Path]::GetDirectoryName($fullNew)
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Move-Item -LiteralPath $fullOld -Destination $fullNew
}

Export-ModuleMember -Function @(
    'Initialize-VaultAccess'
    'Get-VaultAccessConfig'
    'Resolve-VaultRoot'
    'Resolve-VaultPath'
    'Get-VaultNotePath'
    'Resolve-WikiLink'
    'Read-VaultNote'
    'Write-VaultNote'
    'Move-VaultNote'
)
