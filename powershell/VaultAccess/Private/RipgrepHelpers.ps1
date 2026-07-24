# Private/RipgrepHelpers.ps1 - shared ripgrep binary resolution and invocation, not exported
# (not in VaultAccess.psd1). PowerShell-only host plumbing with no C# counterpart. Three tools
# shell to ripgrep - move's basename census, glob, and later grep - and share binary resolution
# (Resolve-VaultRgPath) and invocation (Invoke-VaultRipgrep) here, but not output shape, so
# Get-VaultFilePath and glob build their result-shaping on top of these two primitives.
# See ../VaultAccess.psm1 for the module header and mirror policy, and Plan-Tool-Glob
# (vault-primary) for the shared-ripgrep-layer design.

function Resolve-VaultRgPath {
    <#
    .SYNOPSIS
    Resolves the ripgrep executable for every rg consumer in the module. Precedence: an explicit
    -RgPath override (validated as a real file), then the configured server setting, then PATH.
    The configured setting is the VAULT_MCP_RG_PATH environment variable, surfaced from the
    RipgrepPath appsetting the same way vaults.json's path is surfaced via VAULT_MCP_VAULTS_JSON;
    when unset, resolution falls back to "rg" on PATH - a standalone `pwsh -File` run with no host
    has no configured setting and degrades to PATH exactly as path-scope degrades when unscoped.

    Throws the shared `dependency_unavailable` error naming the path tried when nothing resolves,
    so a missing binary surfaces as a clean tool error up front rather than an opaque exec failure
    (Plan-Tool-Glob: Dependency).
    .PARAMETER RgPath
    Optional explicit executable path. When supplied it wins over the configured setting and PATH,
    and must be an existing file or the call fails `dependency_unavailable`. Used by tests and any
    caller that already knows the binary; omit it for normal server/standalone resolution.
    #>
    [CmdletBinding()]
    param(
        [string] $RgPath
    )

    # Explicit override: highest precedence, validated as a real file so a bad path fails as a
    # clean dependency_unavailable rather than an exec error later.
    if (-not [string]::IsNullOrWhiteSpace($RgPath)) {
        if (-not (Test-Path -LiteralPath $RgPath -PathType Leaf)) {
            throw (New-VaultToolError -Code 'dependency_unavailable' -Message "ripgrep (rg) could not be resolved at '$RgPath'." -Data @{ pathTried = $RgPath })
        }
        return $RgPath
    }

    # Configured server setting: the RipgrepPath appsetting surfaced as VAULT_MCP_RG_PATH, the
    # same env-var mechanism vaults.json's path uses. Validated the same way as an explicit path.
    $configured = [System.Environment]::GetEnvironmentVariable('VAULT_MCP_RG_PATH')
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        if (-not (Test-Path -LiteralPath $configured -PathType Leaf)) {
            throw (New-VaultToolError -Code 'dependency_unavailable' -Message "ripgrep (rg) could not be resolved at the configured path '$configured' (VAULT_MCP_RG_PATH)." -Data @{ pathTried = $configured })
        }
        return $configured
    }

    # PATH fallback: no configured setting (standalone run, or a host that left RipgrepPath unset).
    $cmd = Get-Command -Name 'rg' -CommandType Application -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message 'ripgrep (rg) could not be resolved: VAULT_MCP_RG_PATH is unset and "rg" is not on PATH.' -Data @{ pathTried = 'rg' })
    }
    $cmd.Source
}

function Invoke-VaultRipgrep {
    <#
    .SYNOPSIS
    Invokes ripgrep with a caller-supplied argument vector and returns stdout lines, stderr text,
    and the exit code as a structured object. The argument array is passed straight to the binary
    with nothing shell-interpreted, so a glob pattern can never be shell-expanded (Plan-Tool-Glob:
    Dependency). Callers classify the exit code themselves - 0/1 is match/no-match, 2 with a
    caller glob is an invalid pattern, anything else a real failure - so this never discards stderr
    the way a `2>$null` redirect would, which threw diagnostics away and misfiled real rg failures.
    .PARAMETER RgPath
    The already-resolved ripgrep executable (Resolve-VaultRgPath).
    .PARAMETER Arguments
    The full rg argument vector after the binary (e.g. --files, -g <pattern>, --, <searchPath>).
    .PARAMETER WorkingDirectory
    Directory to run rg from. rg matches globs against paths relative to its search argument, so
    rooting the process here and searching "." makes patterns match vault-relative paths - the
    native Glob semantics glob promises. Push/Pop is scoped to this call and only shifts the
    runspace's own $PWD (which PowerShell hands the child process as its working directory), never
    the process-global current directory, so concurrent pooled runspaces do not collide.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RgPath,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Arguments,
        [string] $WorkingDirectory
    )

    # Disable native non-zero-exit-to-terminating-error mapping for this call: rg's exit 1
    # (no matches) and exit 2 (bad pattern) are results we classify, not failures to throw on.
    # Harmless where the preference variable does not exist (older PowerShell).
    $PSNativeCommandUseErrorActionPreference = $false

    $stderrPath = [System.IO.Path]::GetTempFileName()
    $pushed = $false
    try {
        if ($WorkingDirectory) { Push-Location -LiteralPath $WorkingDirectory; $pushed = $true }
        # Argument array, never a composed shell string - nothing here is shell-interpreted.
        # stderr is redirected to a file so it can be captured and classified rather than discarded.
        $stdout = & $RgPath @Arguments 2> $stderrPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($pushed) { Pop-Location }
    }

    $stderr = if (Test-Path -LiteralPath $stderrPath) { [System.IO.File]::ReadAllText($stderrPath) } else { '' }
    Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue

    [pscustomobject]@{
        Stdout   = @($stdout)
        Stderr   = if ($null -eq $stderr) { '' } else { $stderr }
        ExitCode = $exitCode
    }
}
