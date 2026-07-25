# Public/ToolResult.ps1 - tool-script error/result JSON shaping, structured vault resolution,
# and the name-convention check.
# PowerShell-only: these serve the tool-script contract (Plan-Vault-Tool-Primitives) and have
# no C# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.

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

function Resolve-VaultToolRoot {
    <#
    .SYNOPSIS
    Tool-facing vault resolution: returns Resolve-VaultRoot's @{ Name; Root } object, and
    converts its two failure modes into the structured `{code, message, data?}` errors
    Plan-Vault-Tool-Primitives's Error Convention requires - `unknown_vault` for a name no
    vaults.json entry configures, `vault_unavailable` for a configured vault whose root
    directory was missing at load and was therefore skipped (Initialize-VaultAccess). Both
    codes are cross-tool core codes, so every tool script calls this instead of
    Resolve-VaultRoot and a resolution refusal is machine-routable under the dispatcher and
    under a standalone `pwsh -File` run alike.

    Resolve-VaultRoot itself keeps throwing plain text - it is mirrored core - and its
    message is carried through verbatim as `message`, so both sides still say the same
    thing and only the envelope differs.

    Config-load failures (missing or malformed vaults.json) are not vault resolution and
    carry no code here - they propagate from Get-VaultAccessConfig unchanged.
    .PARAMETER Vault
    Vault name to resolve; empty or omitted resolves the configured default vault, exactly
    as Resolve-VaultRoot.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault
    )

    # The config is read before the attempt so the failure branch classifies from the config
    # state (which map holds the name) rather than by matching Resolve-VaultRoot's message
    # text - the two codes stay distinct even if that wording changes.
    $config = Get-VaultAccessConfig
    $name = if ([string]::IsNullOrWhiteSpace($Vault)) { $config.DefaultVault } else { $Vault }

    try {
        return Resolve-VaultRoot -Vault $Vault
    }
    catch {
        if ($config.SkippedVaults.ContainsKey($name)) {
            throw (New-VaultToolError -Code 'vault_unavailable' -Message $_.Exception.Message -Data @{ vault = $name; reason = $config.SkippedVaults[$name] })
        }
        throw (New-VaultToolError -Code 'unknown_vault' -Message $_.Exception.Message -Data @{ vault = $name; configuredVaults = @($config.Vaults.Keys) })
    }
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
