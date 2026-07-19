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

function Test-VaultPathInScope {
    <#
    .SYNOPSIS
    True if $Path falls under any of $ScopeRoots - folder-prefix containment matched by path
    segment, not raw string prefix, so a root of "Projects/Foo" does not match
    "Projects/Foo-Other/...". Empty $ScopeRoots means unscoped (always in scope). Every root
    (and the path itself) is normalized before any match is attempted, so one malformed root
    makes the whole check throw regardless of whether a different root would have matched -
    an unscopeable query errors rather than silently degrading. Mirrors the C# side's
    VaultAccess.IsPathInScope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [AllowNull()] [string[]] $ScopeRoots
    )

    if ($null -eq $ScopeRoots -or $ScopeRoots.Count -eq 0) { return $true }

    $pathSegments = ConvertTo-VaultScopeSegments -Path $Path
    $rootSegmentsList = @($ScopeRoots | ForEach-Object { , (ConvertTo-VaultScopeSegments -Path $_) })

    foreach ($rootSegments in $rootSegmentsList) {
        if ($rootSegments.Count -gt $pathSegments.Count) { continue }
        $isMatch = $true
        for ($i = 0; $i -lt $rootSegments.Count; $i++) {
            if ($rootSegments[$i] -ine $pathSegments[$i]) { $isMatch = $false; break }
        }
        if ($isMatch) { return $true }
    }
    return $false
}

function Get-VaultAccessAmbientScopeRoots {
    <#
    .SYNOPSIS
    Reads the host-injected VaultMcpScopeRoots global variable (set by ScriptToolRunner right
    before invoking a tool script, in the same runspace - see its InvokeAsync doc comment),
    or $null when nothing set it (direct module use, e.g. Pester tests calling chokepoint
    functions without going through the host). Used as the default value for every chokepoint
    function's -ScopeRoots parameter so tool scripts get path-scope enforcement without
    plumbing it through by hand.
    #>
    [CmdletBinding()]
    param()

    (Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue)
}

function Assert-VaultOperationAllowed {
    <#
    .SYNOPSIS
    Grant-check hook: checks a resolved vault-relative path against the grant's folder-prefix
    $ScopeRoots before a read, write, or enumeration proceeds. Op-class enforcement happens
    separately in the C# host's ScriptToolDispatcher (grants.json); this hook is path scope
    only, distinct from which vault a grant may touch at all (Resolve-VaultRoot).

    $ScopeRoots null or empty means the caller's grant is unscoped: full access within its
    granted vaults, so this is a no-op. The literal path "." - used by Get-VaultNotePath to
    gate the listing operation itself, not any one note - is always allowed here even under a
    scoped grant; per-note filtering happens inside Get-VaultNotePath after listing, via
    Test-VaultPathInScope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidateSet('Read', 'Write')] [string] $Op,
        [Parameter(Mandatory)] [string] $Vault,
        [Parameter(Mandatory)] [string] $RelativePath,
        [AllowNull()] [string[]] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    if ($null -eq $ScopeRoots -or $ScopeRoots.Count -eq 0) { return }
    if ($RelativePath -eq '.') { return }
    if (-not (Test-VaultPathInScope -Path $RelativePath -ScopeRoots $ScopeRoots)) {
        throw "Path is outside the grant's scope for vault `"$Vault`": $RelativePath"
    }
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
    dot-directories (.git, .obsidian, ...) and - when $ScopeRoots is a non-empty scoped
    grant - any note outside those folder-prefix roots (Test-VaultPathInScope). Out-of-scope
    notes are silently dropped from the result, not surfaced as an error - a search or
    wikilink resolution built on this enumeration (Resolve-WikiLink) sees a naturally
    scope-filtered result set.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault,
        [AllowNull()] [string[]] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Read -Vault $resolved.Name -RelativePath '.' -ScopeRoots $ScopeRoots

    Get-ChildItem -LiteralPath $resolved.Root -Filter '*.md' -File -Recurse -Force |
        ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $_.FullName).Replace('\', '/')
            $segments = $relative -split '/'
            $dirSegments = if ($segments.Count -gt 1) { $segments[0..($segments.Count - 2)] } else { @() }
            if ($dirSegments | Where-Object { $_.StartsWith('.') }) { return }
            if ($null -ne $ScopeRoots -and $ScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $relative -ScopeRoots $ScopeRoots)) { return }
            $relative
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
        [string] $Vault,
        [AllowNull()] [string[]] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $target = $Link.Trim()
    if ($target.StartsWith('[[') -and $target.EndsWith(']]')) { $target = $target.Substring(2, $target.Length - 4) }
    $target = ($target -split '\|')[0]
    $target = ($target -split '#')[0]
    $target = $target.Trim().Replace('\', '/').Trim('/')
    if (-not $target) { throw "Empty wikilink: $Link" }

    $qualified = $target.Contains('/')
    $found = @(Get-VaultNotePath -Vault $Vault -ScopeRoots $ScopeRoots | Where-Object {
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
        [string] $Vault,
        [AllowNull()] [string[]] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Read -Vault $resolved.Name -RelativePath $Path -ScopeRoots $ScopeRoots
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
        [string] $Vault,
        [AllowNull()] [string[]] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Write -Vault $resolved.Name -RelativePath $Path -ScopeRoots $ScopeRoots
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
        [string] $Vault,
        [AllowNull()] [string[]] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    Assert-VaultOperationAllowed -Op Write -Vault $resolved.Name -RelativePath $Path -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Op Write -Vault $resolved.Name -RelativePath $NewPath -ScopeRoots $ScopeRoots

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
    'Test-VaultPathInScope'
    'Assert-VaultOperationAllowed'
)
