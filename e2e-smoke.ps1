# e2e-smoke.ps1
# Deployment-seam smoke test: builds the server, starts the real build against this
# machine's real config (appsettings.json, gitignored grants.json), and runs a fixed
# set of live probes over HTTP. Not a second test matrix - the xUnit integration
# tests already cover behavioral HTTP semantics in-process; this covers only what
# they fake: real process, real config, real port.
#
# Read-only by design: probes touch vault "test" (the committed test-vault/ fixture)
# only, and never write. grants.json content is never echoed - assertions read
# parsed non-secret fields only.
#
# Usage: .\e2e-smoke.ps1 [-Port 5111] [-StartTimeoutSec 30] [-SkipBuild]
# Exit code: 0 all probes passed, 1 otherwise.

param(
    [int]$Port = 5111,
    [int]$StartTimeoutSec = 30,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src\VaultMcpServer'
$dllPath = Join-Path $projectDir 'bin\Release\net10.0\VaultMcpServer.dll'
$baseUrl = "http://localhost:$Port"
$selectorToken = 'vault-mcp-localhost-selector'
$bogusToken = 'e2e-smoke-bogus-token'

$script:passed = 0
$script:failed = 0
$script:rpcId = 0

function Check {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        Write-Host "  PASS  $Name" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" -ForegroundColor DarkGray }
        $script:failed++
    }
}

function Test-PortListening {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        return $client.ConnectAsync('localhost', $Port).Wait(500) -and $client.Connected
    } catch { return $false } finally { $client.Dispose() }
}

# One stateless JSON-RPC tools/call. The MCP endpoint requires an Accept header
# naming BOTH application/json and text/event-stream, and answers as a single SSE
# "data:" event whose payload must be stripped before JSON parsing.
function Invoke-McpCall {
    param(
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Arguments = @{},
        [string]$Token
    )
    $headers = @{ Accept = 'application/json, text/event-stream' }
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $body = @{
        jsonrpc = '2.0'
        id = ++$script:rpcId
        method = 'tools/call'
        params = @{ name = $Tool; arguments = $Arguments }
    } | ConvertTo-Json -Depth 6 -Compress

    $resp = Invoke-WebRequest -Uri "$baseUrl/mcp" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body $body -SkipHttpErrorCheck -TimeoutSec 15

    $out = [ordered]@{
        StatusCode = [int]$resp.StatusCode
        RawBody = [string]$resp.Content
        IsError = $false
        Text = $null
    }
    if ($out.StatusCode -eq 200) {
        $dataLines = ($out.RawBody -split "`r?`n") |
            Where-Object { $_ -like 'data:*' } |
            ForEach-Object { $_.Substring(5).TrimStart() }
        $json = ($dataLines -join "`n") | ConvertFrom-Json
        $result = $json.result
        if ($result.PSObject.Properties['isError'] -and $result.isError) { $out.IsError = $true }
        $textBlock = @($result.content) | Where-Object { $_.type -eq 'text' } | Select-Object -First 1
        if ($textBlock) { $out.Text = $textBlock.text }
    }
    return [pscustomobject]$out
}

# ---------------------------------------------------------------------------
# Preflight + build + start
# ---------------------------------------------------------------------------

if (Test-PortListening) {
    Write-Host "Port $Port is already in use - stop the running server first (this script starts its own instance)." -ForegroundColor Red
    exit 1
}

if (-not $SkipBuild) {
    Write-Host "Building (Release)..."
    $buildOutput = & dotnet build $projectDir -c Release --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($buildOutput -join "`n")
        Write-Host 'Build failed.' -ForegroundColor Red
        exit 1
    }
}
if (-not (Test-Path $dllPath)) {
    Write-Host "Built assembly not found at $dllPath" -ForegroundColor Red
    exit 1
}

# Launched through the shared `dotnet` host, the same way `dotnet run` starts it: the
# project emits no apphost (see the Application Control note in the Dev-Setup corpus doc).
# The host runs the assembly in-process rather than spawning a child, so the teardown
# below owns the whole server by killing this one process.
#
# Working directory = project dir, so the content root matches `dotnet run` and the
# ../../ config paths in appsettings.json resolve to this repo's real config files.
Write-Host "Starting $dllPath ..."
$proc = Start-Process -FilePath 'dotnet' -ArgumentList $dllPath -WorkingDirectory $projectDir -PassThru -WindowStyle Hidden

try {
    $deadline = (Get-Date).AddSeconds($StartTimeoutSec)
    while (-not (Test-PortListening)) {
        if ($proc.HasExited) {
            Write-Host "Server process exited immediately (exit code $($proc.ExitCode)) - check the newest logs\vault-mcp-server-*.log." -ForegroundColor Red
            exit 1
        }
        if ((Get-Date) -ge $deadline) {
            Write-Host "Server did not open port $Port within $StartTimeoutSec seconds." -ForegroundColor Red
            exit 1
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Server is up on port $Port.`n"

    # -----------------------------------------------------------------------
    # Probes
    # -----------------------------------------------------------------------

    Write-Host 'Probe: health_check with selector token'
    $r = Invoke-McpCall -Tool 'health_check' -Token $selectorToken
    Check 'returns HTTP 200, no tool error' ($r.StatusCode -eq 200 -and -not $r.IsError) "status=$($r.StatusCode)"
    $health = if ($r.Text) { $r.Text | ConvertFrom-Json } else { $null }
    Check 'isFatallyDegraded is false' ($null -ne $health -and $health.isFatallyDegraded -eq $false)
    Check 'zero problems (real grants.json parses clean)' ($null -ne $health -and @($health.problems).Count -eq 0) "problems=$(if ($health) { @($health.problems).Count })"

    Write-Host 'Probe: read on vault "test" with selector token (selector -> default binding)'
    $r = Invoke-McpCall -Tool 'read' -Arguments @{ path = 'Resources/Gizmo-Reference.md'; vault = 'test' } -Token $selectorToken
    Check 'succeeds with content' ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text) "status=$($r.StatusCode) isError=$($r.IsError)"

    # The middleware's vault gate matches the vault argument's property name
    # case-insensitively. The schema now spells it "vault" (param() blocks are snake_case),
    # so probe an off-schema casing too and catch a regression to a case-sensitive peek.
    Write-Host 'Probe: read on vault "test" with bogus token, schema-cased args (middleware vault gate)'
    $r = Invoke-McpCall -Tool 'read' -Arguments @{ path = 'Resources/Gizmo-Reference.md'; vault = 'test' } -Token $bogusToken
    Check 'is not a 401 (floor supersedes 401 on localhost)' ($r.StatusCode -ne 401) "status=$($r.StatusCode)"
    Check 'is HTTP 403 naming the minimum-access vault scope' ($r.StatusCode -eq 403 -and $r.RawBody -like '*Grant "minimum-access" is not scoped to vault "test"*') "status=$($r.StatusCode) body=$($r.RawBody)"

    Write-Host 'Probe: read on vault "test" with bogus token, off-schema casing (same gate, case-insensitive peek)'
    $r = Invoke-McpCall -Tool 'read' -Arguments @{ Path = 'Resources/Gizmo-Reference.md'; Vault = 'test' } -Token $bogusToken
    Check 'is HTTP 403 naming the minimum-access vault scope' ($r.StatusCode -eq 403 -and $r.RawBody -like '*Grant "minimum-access" is not scoped to vault "test"*') "status=$($r.StatusCode) body=$($r.RawBody)"

    Write-Host 'Probe: health_check with bogus token (diagnostics as a right of connection)'
    $r = Invoke-McpCall -Tool 'health_check' -Token $bogusToken
    Check 'still succeeds on the floored grant' ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text) "status=$($r.StatusCode) isError=$($r.IsError)"

    # The server-intrinsic tools refuse through the shared {code, message, data?} envelope, the same
    # shape a tool script's own error carries - so these probe the code, not the prose beside it.
    Write-Host 'Probe: request_elevation with bogus token (auto-deny stub)'
    $r = Invoke-McpCall -Tool 'request_elevation' -Arguments @{ grant = 'admin' } -Token $bogusToken
    $elevErr = if ($r.Text) { try { $r.Text | ConvertFrom-Json } catch { $null } } else { $null }
    Check 'is a coded tool error (insufficient_capability)' ($r.StatusCode -eq 200 -and $r.IsError -and $null -ne $elevErr -and $elevErr.code -eq 'insufficient_capability') "status=$($r.StatusCode) isError=$($r.IsError) text=$($r.Text)"
    Check 'message still names the denied elevation' ($null -ne $elevErr -and $elevErr.message -like '*Elevation to "admin" denied*') "text=$($r.Text)"

    Write-Host 'Probe: attenuation-only minting'
    $mintedToken = $null
    $r = Invoke-McpCall -Tool 'mint_token' -Arguments @{ scope_roots = @('test'); op_class = @('content:read', 'grants:manage') } -Token $selectorToken
    $mint = if ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text) { $r.Text | ConvertFrom-Json } else { $null }
    if ($mint) { $mintedToken = $mint.token }
    Check 'selector mints a content:read+grants:manage grant on vault "test"' ($null -ne $mint -and $mint.token) "status=$($r.StatusCode) isError=$($r.IsError)"
    if ($mintedToken) {
        $r = Invoke-McpCall -Tool 'mint_token' -Arguments @{ scope_roots = @('test'); op_class = @('content:write') } -Token $mintedToken
        $mintErr = if ($r.Text) { try { $r.Text | ConvertFrom-Json } catch { $null } } else { $null }
        # Attenuation-only: a mint exceeding the minter's authority is refused, not intersected, so
        # the refusal is a capability denial rather than an argument error.
        Check 'minted token cannot mint content:write (insufficient_capability on the op_class dimension)' ($r.IsError -and $null -ne $mintErr -and $mintErr.code -eq 'insufficient_capability' -and $mintErr.data.argument -eq 'op_class') "isError=$($r.IsError) text=$($r.Text)"
    } else {
        Check 'minted token cannot mint content:write (insufficient_capability on the op_class dimension)' $false 'skipped: mint failed'
    }

    Write-Host 'Probe: audit log hygiene'
    $log = Get-ChildItem -Path (Join-Path $repoRoot 'logs') -Filter 'vault-mcp-server-*.log' -File |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($log) {
        $logText = Get-Content -Path $log.FullName -Raw
        Check 'audit lines present' ($logText -match 'Audit: grant=') $log.Name
        Check 'bogus token value never logged' (-not $logText.Contains($bogusToken))
        if ($mintedToken) {
            Check 'minted token value never logged' (-not $logText.Contains($mintedToken))
        }
    } else {
        Check 'newest log file found' $false "no vault-mcp-server-*.log under $repoRoot\logs"
    }
} finally {
    # -----------------------------------------------------------------------
    # Teardown: kill the server, verify the port actually closed
    # -----------------------------------------------------------------------
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        $proc.WaitForExit(5000) | Out-Null
    }
    Start-Sleep -Milliseconds 500
    if (Test-PortListening) {
        Write-Host "`nTeardown: port $Port is STILL listening after kill - a stray server process remains." -ForegroundColor Red
        $script:failed++
    } else {
        Write-Host "`nTeardown: server stopped, port $Port closed."
    }
}

$total = $script:passed + $script:failed
$color = if ($script:failed -eq 0) { 'Green' } else { 'Red' }
Write-Host "`n-- Smoke result: $($script:passed)/$total passed --" -ForegroundColor $color
exit ($script:failed -gt 0 ? 1 : 0)
