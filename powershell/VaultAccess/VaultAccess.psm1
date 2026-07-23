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
        $ConfigPath = if ($env:VAULT_MCP_VAULTS_JSON) { $env:VAULT_MCP_VAULTS_JSON } else { Join-Path $PSScriptRoot '../../vaults.json' }
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

function Resolve-VaultScopeRoots {
    <#
    .SYNOPSIS
    Resolves a grant's per-vault $ScopeRoots hashtable down to the flat, already-vault-scoped
    root list for one specific $Vault - the shape every chokepoint function (and
    Assert-VaultOperationAllowed, Test-VaultPathInScope) actually operates on. $ScopeRoots
    being $null means "no grant in play at all" (direct/no-grant callers) - unscoped for every
    vault, returns an empty array. A non-null $ScopeRoots missing an entry for $Vault (matched
    case-insensitively) is a grant configuration invariant violation - throws, mirroring the
    C# side's VaultAccess.ResolveVaultScopeRoots, which throws InvalidOperationException for
    the same case: every vault a grant can access must have an explicit entry (an empty set for
    full access). This is where "a scope root configured for one vault must not leak into
    another vault the same grant can access" is enforced: only the entry keyed to $Vault is
    ever consulted.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Vault,
        [AllowNull()] $ScopeRoots
    )

    if ($null -eq $ScopeRoots) { return @() }
    $key = @($ScopeRoots.Keys) | Where-Object { $_ -ieq $Vault } | Select-Object -First 1
    if ($null -eq $key) {
        throw "Grant configuration invariant violated: vault '$Vault' has no scope-roots entry in a non-null scope-roots map. Every vault a grant can access must have an explicit entry (an empty set for full access) - this should be impossible for any grant built through GrantStore.Load or GrantStore.Mint."
    }
    , @($ScopeRoots[$key])
}

function Assert-VaultOperationAllowed {
    <#
    .SYNOPSIS
    Grant-check hook: checks a resolved vault-relative path against the grant's folder-prefix
    $ScopeRoots before a read, write, or enumeration proceeds. Op-class enforcement happens
    separately in the C# host's ScriptToolDispatcher (grants.json); this hook is path scope
    only, distinct from which vault a grant may touch at all (Resolve-VaultRoot).

    Unlike the other chokepoint functions, $ScopeRoots here is already the flat, resolved root
    list for $Vault (see Resolve-VaultScopeRoots) - callers look it up once and reuse it for
    both this check and any per-note filtering they do themselves (e.g. Get-VaultNotePath).
    $ScopeRoots null or empty means the caller's grant is unscoped for this vault: full access,
    so this is a no-op. The literal path "." - used by Get-VaultNotePath to gate the listing
    operation itself, not any one note - is always allowed here even under a scoped grant;
    per-note filtering happens inside Get-VaultNotePath after listing, via Test-VaultPathInScope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Vault,
        [Parameter(Mandatory)] [string] $RelativePath,
        [AllowNull()] [string[]] $ScopeRoots
    )

    if ($null -eq $ScopeRoots -or $ScopeRoots.Count -eq 0) { return }
    if ($RelativePath -eq '.') { return }
    if (-not (Test-VaultPathInScope -Path $RelativePath -ScopeRoots $ScopeRoots)) {
        throw "Path is outside the grant's scope for vault `"$Vault`": $RelativePath"
    }
}

function Assert-VaultOpClass {
    <#
    .SYNOPSIS
    In-script op-class floor check: throws unless the calling grant holds $OpClass. Lets a tool
    whose declared floor op-class (the class the C# dispatcher already gated the call on via
    grant.Allows(tool.OpClass)) admit it, still require a *higher* class before an elevated
    operation - e.g. a content:read tool that performs a content:write mutation only on some
    branch asserts content:write there, so a content:read-only grant is denied in-script rather
    than at listing time.

    $GrantClasses defaults to the host-injected $Global:VaultMcpOpClasses (set by
    ScriptToolRunner right before invoking a tool script, in the same runspace - see its
    InvokeAsync doc comment), mirroring how the path-scope chokepoints default -ScopeRoots to the
    injected VaultMcpScopeRoots global. The $null-vs-empty distinction is load-bearing and matches
    how Test-VaultPathInScope treats an unscoped ($null) grant:

      - $GrantClasses is $null  -> no host context was injected (a standalone `pwsh -File` run, or
        a Pester test calling the function directly). Degrade open: return silently, exactly as an
        unscoped scope-roots map is treated as "no restriction in play".
      - $GrantClasses is an empty array -> host context WAS injected but the grant holds no op
        classes (e.g. the minimum-access floor). This is a real, authenticated "holds nothing"
        state and must DENY every asserted class - it is not the same as absent context.
      - $OpClass is a member -> pass (return).
      - otherwise -> throw insufficient_capability naming the missing class.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $OpClass,
        $GrantClasses = $Global:VaultMcpOpClasses
    )

    # $null means "no injected host context at all" - degrade open, same posture as an unscoped
    # scope-roots map. An empty (non-null) set is distinct: an injected but empty key ring denies.
    if ($null -eq $GrantClasses) { return }

    if ($GrantClasses -contains $OpClass) { return }

    # No shared PowerShell error-helper exists in this repo (tool scripts throw plain strings and
    # the C# runner surfaces the thrown message verbatim as the tool call's error text). Throw a
    # compact JSON object so the error carries a machine-inspectable { code, message } shape - the
    # code is the stable contract, the message is human-facing and names the missing class.
    $err = [ordered]@{
        code    = 'insufficient_capability'
        message = "Grant does not hold the required op class '$OpClass' for this operation."
    }
    throw ($err | ConvertTo-Json -Compress)
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

function Get-VaultNotePath {
    <#
    .SYNOPSIS
    Enumerates all notes in a vault as vault-relative, forward-slash paths, skipping
    dot-directories (.git, .obsidian, ...) and - when the vault has a non-empty scoped
    grant - any note outside those folder-prefix roots (Test-VaultPathInScope). Out-of-scope
    notes are silently dropped from the result, not surfaced as an error - a search or
    wikilink resolution built on this enumeration (Resolve-WikiLink) sees a naturally
    scope-filtered result set.

    $ScopeRoots is the grant's per-vault map (hashtable keyed by vault name, or $null); the
    entry for the resolved vault is picked out via Resolve-VaultScopeRoots before use, so a
    scope root configured for a different vault the grant can access never applies here.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath '.' -ScopeRoots $flatScopeRoots

    Get-ChildItem -LiteralPath $resolved.Root -Filter '*.md' -File -Recurse -Force |
        ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $_.FullName).Replace('\', '/')
            $segments = $relative -split '/'
            $dirSegments = if ($segments.Count -gt 1) { $segments[0..($segments.Count - 2)] } else { @() }
            if ($dirSegments | Where-Object { $_.StartsWith('.') }) { return }
            if ($flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $relative -ScopeRoots $flatScopeRoots)) { return }
            $relative
        }
}

function Resolve-WikiLink {
    <#
    .SYNOPSIS
    Resolves a wikilink to a vault-relative note path. Accepts every convention form:
    [[Name]], bare Name, [[Folder/Name]] (path-qualified, matched on trailing path
    segments), and either with #heading and/or |display suffixes, which are ignored.
    A trailing .md extension on the link itself is also stripped and ignored.
    Returns $null when nothing matches; throws when the link is ambiguous.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Link,
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $target = $Link.Trim()
    if ($target.StartsWith('[[') -and $target.EndsWith(']]')) { $target = $target.Substring(2, $target.Length - 4) }
    $target = ($target -split '\|')[0]
    $target = ($target -split '#')[0]
    $target = $target.Trim().Replace('\', '/').Trim('/')
    if ($target.ToLowerInvariant().EndsWith('.md')) { $target = $target.Substring(0, $target.Length - 3) }
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
    Writes a note as UTF-8 without BOM, creating parent directories as needed.
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
        # -ErrorAction Stop is explicit because $ErrorActionPreference does not reach this
        # scope when the caller runs inside a module-function scriptblock; without it a
        # failed mutation is non-terminating and the caller reports success.
        New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null
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
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath $Path -ScopeRoots $flatScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath $NewPath -ScopeRoots $flatScopeRoots

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
        # See Write-VaultNote for why -ErrorAction Stop is explicit rather than inherited.
        New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null
    }
    Move-Item -LiteralPath $fullOld -Destination $fullNew -ErrorAction Stop
}

function New-VaultToolError {
    <#
    .SYNOPSIS
    Builds the shared `{code, message, data?}` JSON error shape ratified in
    Plan-Vault-Tool-Primitives's Error Convention (vault-primary), returning it as a
    compact JSON string. A tool script throws the return value directly
    (`throw (New-VaultToolError -Code ... -Message ...)`) so the dispatcher - and a
    standalone `pwsh -File` run, which surfaces the same string on stderr - present one
    error shape regardless of which layer raised it. This is the tool-script-facing
    counterpart to Assert-VaultOpClass's inline `insufficient_capability` JSON, factored
    out so every tool script builds its own error codes (not_found, invalid_argument,
    kind_mismatch, ...) the same way instead of hand-rolling the object per call site.
    .PARAMETER Code
    Machine-routing error code - the stable field callers and tests branch on (e.g.
    not_found, already_exists, invalid_argument, kind_mismatch, out_of_scope).
    .PARAMETER Message
    Human/model-readable explanation of the failure.
    .PARAMETER Data
    Optional structured payload (offending paths, candidate lists, ...). Omitted from the
    JSON entirely when not supplied, rather than emitted as a null, so a caller can test
    for the field's presence rather than its nullness.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Code,
        [Parameter(Mandatory)] [string] $Message,
        $Data
    )

    $err = [ordered]@{ code = $Code; message = $Message }
    if ($PSBoundParameters.ContainsKey('Data')) { $err.data = $Data }
    $err | ConvertTo-Json -Compress -Depth 10
}

function ConvertTo-VaultToolResult {
    <#
    .SYNOPSIS
    Serializes a write-class tool's return object to the Tier-2 JSON text convention
    ratified in Plan-Vault-Tool-Primitives's Return Convention (write, create, delete,
    move, sync, and lint both modes) - the single JSON summary object a write-class tool
    emits as its MCP text content in place of a read-class cognate's native-format output.

    Doubles as a shape guard: a PowerShell scriptblock's return value silently arrayifies
    the moment more than one object reaches the success stream (a stray Write-Output, an
    un-suppressed cmdlet call left in the pipeline), and this throws rather than quietly
    serializing an array where every caller and schema expects a single object - a stray
    success-stream leak fails loudly at serialization instead of shipping malformed JSON.
    .PARAMETER Result
    The single hashtable, [ordered]@{}, or PSCustomObject to serialize.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)] $Result
    )

    process {
        # A scriptblock's return value arrayifies the moment more than one object reaches the
        # success stream; a Hashtable/PSCustomObject never does. [array] is therefore the
        # precise shape of "stray output leaked in", not a stand-in for "any collection" - a
        # tool's return legitimately contains array-valued fields (filesRewritten[]) nested
        # inside the single top-level object, which this check does not touch.
        if ($Result -is [array]) {
            throw (New-VaultToolError -Code 'malformed_op' -Message "Tool returned $($Result.Count) object(s) instead of one Tier-2 result object - stray success-stream output.")
        }
        $Result | ConvertTo-Json -Depth 10
    }
}

function Test-VaultNameConvention {
    <#
    .SYNOPSIS
    True when $Name conforms to the vault's `Title-Case-With-Hyphens` filename/folder-name
    convention (Document-Conventions.md, Vault-Organization.md in vault-primary):
    hyphen-separated segments, each starting with an uppercase letter or a digit (so both
    acronym segments like "MCP" and numeric segments like "2026" are admitted), no spaces
    or underscores, and no empty segments (a leading/trailing/doubled hyphen).

    Consumers warn on a $false result and never fail the operation over it - move reports
    a violation via its `namingWarnings` return field (see Plan-Tool-Move); only
    link-breaking or OS-invalid characters, checked separately, block a move outright.

    A trailing ".md" is stripped (case-insensitively) before checking, so the same check
    serves both file and folder names.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Name
    )

    $bare = $Name
    if ($bare.ToLowerInvariant().EndsWith('.md')) { $bare = $bare.Substring(0, $bare.Length - 3) }

    if ([string]::IsNullOrWhiteSpace($bare)) { return $false }
    if ($bare -match '[ _]') { return $false }
    if ($bare -match '(^-|-$|--)') { return $false }

    foreach ($segment in ($bare -split '-')) {
        if ($segment.Length -eq 0) { return $false }
        if ($segment -cnotmatch '^[A-Z0-9][A-Za-z0-9]*$') { return $false }
    }
    return $true
}

function Get-VaultFilePath {
    <#
    .SYNOPSIS
    Enumerates every file in a vault - not only ".md" notes - as vault-relative,
    forward-slash paths, via ripgrep. This is the basename-census enumeration primitive
    move's ambiguity gate (Plan-Tool-Move) and glob (Plan-Tool-Glob) are designed to
    share, so the two tools always agree on what exists rather than drifting to two
    independent notions of "every file in the vault". Get-VaultNotePath stays the
    note-only (.md) enumeration `read`/`write`/wikilink resolution use; this is the
    file-general counterpart for `move`/`glob`, per Plan-Vault-Tool-Primitives's
    note-vs-file tool split.

    Exclusion rules mirror Plan-Tool-Glob's accepted v1 defaults exactly, since both
    tools shell to the same binary the same way: ripgrep's own defaults respect
    .gitignore and hide dot-directories (.git, .obsidian, .claude, ...), and symlinks are
    never followed (-L/--follow is never passed - the one real vault-escape vector, so
    the guard is simply never opting in). Every result is additionally asserted to
    resolve under the vault root as a second guard, covering Windows junctions and
    reparse points without reasoning about ripgrep's own symlink detection.

    Applies the resolved vault's scope-roots filter the same way Get-VaultNotePath does -
    out-of-scope files are silently dropped, never surfaced as an error.
    .PARAMETER RgPath
    Path to the ripgrep executable. Defaults to resolving "rg" off PATH; throws a
    dependency_unavailable-shaped error (see New-VaultToolError) when it cannot be found,
    naming the path tried, per Plan-Tool-Glob.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots),
        [string] $RgPath
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath '.' -ScopeRoots $flatScopeRoots

    if (-not $RgPath) {
        $cmd = Get-Command -Name 'rg' -CommandType Application -ErrorAction SilentlyContinue
        if (-not $cmd) {
            throw (New-VaultToolError -Code 'dependency_unavailable' -Message 'ripgrep (rg) could not be resolved on PATH.' -Data @{ pathTried = 'rg' })
        }
        $RgPath = $cmd.Source
    }
    elseif (-not (Test-Path -LiteralPath $RgPath -PathType Leaf)) {
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message "ripgrep (rg) could not be resolved at '$RgPath'." -Data @{ pathTried = $RgPath })
    }

    # --files with no -g pattern lists every file rg would ever consider a match candidate -
    # exactly the "everything that exists" set a basename census needs. Argument array, never
    # a composed shell string, so nothing here is shell-interpreted.
    $rgOutput = & $RgPath '--files' '--' $resolved.Root 2>$null
    if ($LASTEXITCODE -gt 1) {
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message "ripgrep exited with code ${LASTEXITCODE} enumerating '$($resolved.Root)'.")
    }

    $rootPrefix = $resolved.Root + [System.IO.Path]::DirectorySeparatorChar
    foreach ($line in $rgOutput) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $full = [System.IO.Path]::GetFullPath($line)
        if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $full).Replace('\', '/')
        if ($flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $relative -ScopeRoots $flatScopeRoots)) { continue }
        $relative
    }
}

function Get-VaultWriteLockName {
    <#
    .SYNOPSIS
    Builds the per-vault write-mutex name: Global\VaultWrite-<32 hex chars of SHA-256 of the
    lowercased, canonicalized vault root path>.

    This name is the cross-process contract. VaultWatcher constructs the identical string to
    contend for the same lock, so any change here is a breaking change for both processes.

    Derived from the root path rather than the vaults.json vault name, so two configs naming
    the same directory contend for the same lock. Lowercased before hashing on every
    platform, so a case-only config difference cannot leave two processes unserialized.
    Hashed because a mutex name is capped at 260 characters and cannot contain a backslash
    outside the reserved prefix. Global\ scopes the mutex across sessions; Windows only.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $normalized = $resolved.Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar).ToLowerInvariant()

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    }
    finally {
        $sha.Dispose()
    }
    $hash = [System.BitConverter]::ToString($bytes).Replace('-', '').Substring(0, 32)

    $prefix = if ($IsWindows) { 'Global\' } else { '' }
    "${prefix}VaultWrite-$hash"
}

function Invoke-VaultWriteLock {
    <#
    .SYNOPSIS
    Runs $ScriptBlock holding the per-vault write mutex, releasing it however the block exits.

    The scriptblock wrapper is the only supported way to take the write lock; there is no
    exported Enter/Exit pair. The try/finally lives here once, so a tool cannot hold the lock
    without releasing it.

    Wrap the whole logical operation, not individual chokepoint calls. A rename issues N+1
    mutations (one Write-VaultNote per backlinked file, then Move-VaultNote), so a lock taken
    inside those functions would acquire and release N+1 times without serializing the
    operation.

    When a process dies holding the mutex, the OS releases it and hands the next waiter an
    AbandonedMutexException - that waiter owns the lock, and a previous owner died mid-write.
    Reported via -Abandoned and a warning; the vault may be torn and git diff is the
    diagnosis. Recovery is therefore signalled rather than inferred from elapsed time.

    -TimeoutSeconds is a liveness guard only: it fails the call rather than hanging the MCP
    request when the lock cannot be acquired. It is not the recovery mechanism, and a hung
    holder never abandons.

    Reentrant for the same thread; each level releases its own hold.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [scriptblock] $ScriptBlock,
        [string] $Vault,
        [int] $TimeoutSeconds = $(if ($env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS) { [int]$env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS } else { 60 }),
        [AllowNull()] [ref] $Abandoned
    )

    $name = Get-VaultWriteLockName -Vault $Vault
    if ($Abandoned) { $Abandoned.Value = $false }

    $mutex = [System.Threading.Mutex]::new($false, $name)
    try {
        $acquired = $false
        try {
            $acquired = $mutex.WaitOne([timespan]::FromSeconds($TimeoutSeconds))
        }
        catch [System.Threading.AbandonedMutexException] {
            # Signals that the previous owner died without releasing; the lock is held now.
            $acquired = $true
            if ($Abandoned) { $Abandoned.Value = $true }
            Write-Warning "Vault write lock was abandoned by a process that died mid-operation (vault root lock '$name'). The vault may be in a partially-written state - check 'git status' and 'git diff'."
        }

        if (-not $acquired) {
            throw "Timed out after ${TimeoutSeconds}s waiting for the vault write lock. Another vault operation is holding it - if nothing else is running, a process may be wedged while holding the lock."
        }

        try {
            & $ScriptBlock
        }
        finally {
            $mutex.ReleaseMutex()
        }
    }
    finally {
        $mutex.Dispose()
    }
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
    'Resolve-VaultScopeRoots'
    'Assert-VaultOperationAllowed'
    'Assert-VaultOpClass'
    'Get-VaultWriteLockName'
    'Invoke-VaultWriteLock'
    'Test-VaultNoteMissingError'
    'New-VaultToolError'
    'ConvertTo-VaultToolResult'
    'Test-VaultNameConvention'
    'Get-VaultFilePath'
)
