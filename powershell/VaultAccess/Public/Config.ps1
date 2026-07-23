# Public/Config.ps1 - vault config load and name/path resolution.
# Mirrored core: Initialize-VaultAccess <-> VaultConfig.Load, Resolve-VaultRoot <->
# VaultConfig.Resolve/VaultAccess.ResolveVault, Resolve-VaultPath <-> VaultAccess.ResolvePath.
# Changes to path-validity semantics land as a shared tests/cases/*.json case. See
# ../VaultAccess.psm1 for the full mirror policy.

function Initialize-VaultAccess {
    <#
    .SYNOPSIS
    Loads the vault name-to-root map from vaults.json. Called implicitly with the default
    path (repo-root vaults.json, overridable via $env:VAULT_MCP_VAULTS_JSON) on first use;
    call explicitly with -ConfigPath to point at a different config (e.g. in tests).

    A configured vault whose root directory does not exist is skipped per-entry rather than
    failing the whole load: it is excluded from the resolvable Vaults map, recorded (name to
    reason) in the returned config's SkippedVaults, and the other vaults keep working. A
    default_vault whose root is missing does not fail the load either - only Resolve-VaultRoot
    against it fails, as "configured but currently unavailable". Mirrors the C# side's
    VaultConfig.Load/SkippedVaults; there is no PowerShell equivalent of the C# host's
    health-state recording (ServerHealthState) - this module only mirrors the load/resolution
    semantics, not host diagnostics.
    #>
    [CmdletBinding()]
    param(
        [string] $ConfigPath
    )

    if (-not $ConfigPath) {
        # Anchored to the module root (set by VaultAccess.psm1), not $PSScriptRoot, so the
        # default resolves the same wherever a chokepoint function's file lives under the module.
        $ConfigPath = if ($env:VAULT_MCP_VAULTS_JSON) { $env:VAULT_MCP_VAULTS_JSON } else { Join-Path $script:VaultAccessModuleRoot '../../vaults.json' }
    }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "Vault config not found: $ConfigPath"
    }

    $raw = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if (-not $raw.default_vault) { throw "${ConfigPath}: missing or empty `"default_vault`"." }
    if (-not $raw.vaults) { throw "${ConfigPath}: missing `"vaults`" object." }

    $vaults = @{}
    $skipped = @{}
    foreach ($entry in $raw.vaults.PSObject.Properties) {
        if (-not $entry.Value) { throw "${ConfigPath}: vault `"$($entry.Name)`" has an empty root path." }
        # GetUnresolvedProviderPathFromPSPath fully qualifies a path (relative or absolute)
        # without requiring it to exist - the PowerShell-aware equivalent of .NET's
        # Path.GetFullPath, which the C# side uses for the same reason: a skipped vault's
        # reason text still needs a fully-qualified path.
        $fullRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath([string]$entry.Value)
        if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
            # Per-entry skip, not a whole-file failure: the vault is excluded from the
            # resolvable set (operations targeting it fail as "unavailable") while every
            # other vault keeps working. Still a configured vault name.
            $skipped[$entry.Name] = "root directory does not exist: $fullRoot"
            continue
        }
        $vaults[$entry.Name] = $fullRoot
    }
    if ($vaults.Count -eq 0 -and $skipped.Count -eq 0) { throw "${ConfigPath}: `"vaults`" must contain at least one entry." }
    if (-not $vaults.ContainsKey([string]$raw.default_vault) -and -not $skipped.ContainsKey([string]$raw.default_vault)) {
        throw "${ConfigPath}: `"default_vault`" ($($raw.default_vault)) is not a configured vault."
    }

    $script:VaultConfig = [pscustomobject]@{
        DefaultVault  = [string]$raw.default_vault
        Vaults        = $vaults
        SkippedVaults = $skipped
    }
}

function Get-VaultAccessConfig {
    <#
    .SYNOPSIS
    Returns the loaded vault config (DefaultVault + Vaults map + SkippedVaults map),
    initializing from the default path on first use.
    #>
    [CmdletBinding()]
    param()

    if (-not $script:VaultConfig) { Initialize-VaultAccess }
    $script:VaultConfig
}

function Resolve-VaultRoot {
    <#
    .SYNOPSIS
    Resolves a vault name (or the default when omitted) to @{ Name; Root }.

    A vault skipped at load time (root directory missing - see Initialize-VaultAccess) fails
    with a "configured but currently unavailable" error naming the reason, distinct from the
    "Unknown vault" error an unconfigured name gets - mirrors the C# side's
    VaultConfig.Resolve.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault
    )

    $config = Get-VaultAccessConfig
    $name = if ([string]::IsNullOrWhiteSpace($Vault)) { $config.DefaultVault } else { $Vault }
    $match = $config.Vaults.Keys | Where-Object { $_ -ieq $name } | Select-Object -First 1
    if (-not $match) {
        if ($config.SkippedVaults.ContainsKey($name)) {
            throw "Vault `"$name`" is configured but currently unavailable: $($config.SkippedVaults[$name])."
        }
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
