# e2e-smoke.ps1
# Deployment-seam smoke test: builds the server, starts the real build against this
# machine's real config (appsettings.json, gitignored grants.json), and runs a fixed
# set of live probes over HTTP. Not a second test matrix - the xUnit integration
# tests already cover behavioral HTTP semantics in-process; this covers only what
# they fake: real process, real config, real port.
#
# Read-only by design: probes touch vault "test" (the committed test-vault/ fixture)
# only, and never change a byte of it. The write path is probed through an edit whose
# outcome is what the note already says and through refusals that fire before anything
# is written, so the committed fixture stays clean and this script needs no sandbox.
# grants.json content is never echoed - assertions read parsed non-secret fields only.
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
$script:skipped = 0
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

# A probe whose subject does not exist yet. Reported rather than removed: a deleted probe
# leaves a full-looking pass count with a hole in it, and this prints the hole on every run.
function Skip {
    param([string]$Name, [string]$Reason)
    Write-Host "  SKIP  $Name" -ForegroundColor Yellow
    Write-Host "        $Reason" -ForegroundColor DarkGray
    $script:skipped++
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
# project emits no apphost (UseAppHost=false in VaultMcpServer.csproj).
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
    Check 'is_fatally_degraded is false' ($null -ne $health -and $health.is_fatally_degraded -eq $false)
    Check 'zero problems (real grants.json parses clean)' ($null -ne $health -and @($health.problems).Count -eq 0) "problems=$(if ($health) { @($health.problems).Count })"

    Write-Host 'Probe: read on vault "test" with selector token (selector -> default binding)'
    $r = Invoke-McpCall -Tool 'read' -Arguments @{ vault = 'test'; path = 'Resources/Gizmo-Reference.md' } -Token $selectorToken
    Check 'a read succeeds with content' ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text) "status=$($r.StatusCode) isError=$($r.IsError) text=$($r.Text)"
    # The content tier is plain text, not an envelope: what a model reads comes back in the shape it
    # already consumes.
    Check 'content is the note text rather than a wrapper' ($null -ne $r.Text -and $r.Text -like '*Gizmo*') "text=$($r.Text)"

    # The one content-tier result carrying structure. Asserted here because the hash is what a later
    # write chains on, and a read that returned one describing other bytes is unrecoverable above it.
    Write-Host 'Probe: read with a content hash (the staleness guard the write path chains on)'
    $r = Invoke-McpCall -Tool 'read' -Arguments @{ vault = 'test'; path = 'Resources/Gizmo-Reference.md'; part = 'body'; with_content_hash = $true } -Token $selectorToken
    $read = if ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text) { try { $r.Text | ConvertFrom-Json } catch { $null } } else { $null }
    Check 'returns content and content_hash together' ($null -ne $read -and $read.content -and $read.content_hash) "text=$($r.Text)"
    Check 'the hash is lowercase hex SHA-256 of the whole file' ($null -ne $read -and $read.content_hash -cmatch '^[0-9a-f]{64}$') "hash=$(if ($read) { $read.content_hash })"

    Write-Host 'Probe: glob and grep on vault "test" (the two search surfaces)'
    $r = Invoke-McpCall -Tool 'glob' -Arguments @{ vault = 'test'; pattern = 'Resources/**' } -Token $selectorToken
    Check 'glob returns vault-relative note paths' ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text -like '*Resources/Gizmo-Reference.md*') "text=$($r.Text)"
    $r = Invoke-McpCall -Tool 'grep' -Arguments @{ vault = 'test'; pattern = 'Gizmo'; granularity = 'files' } -Token $selectorToken
    Check 'grep returns the notes containing a match' ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text -like '*.md*') "text=$($r.Text)"
    # Zero matches is an empty result, never a failure - an existence check must not require a caller
    # to catch.
    $r = Invoke-McpCall -Tool 'grep' -Arguments @{ vault = 'test'; pattern = 'zzz-no-such-string-zzz' } -Token $selectorToken
    Check 'no match is an empty result rather than a failure' ($r.StatusCode -eq 200 -and -not $r.IsError -and -not $r.Text) "isError=$($r.IsError) text=$($r.Text)"

    # The one mutating tool, probed without mutating. An edit setting a scalar to the value the note
    # already carries runs the whole path - the write floor, the lock, the vault's declaration, the
    # operations, the authority classification - and composes bytes identical to what is stored, so
    # nothing is written and this script's read-only posture holds. What it proves is that the
    # declaration the committed vaults.json names actually loaded: with none, every one of these
    # refuses frontmatter_unavailable.
    Write-Host 'Probe: edit on vault "test" (the write path, over a note it leaves unchanged)'
    $note = 'Inbox/Note-Loose-Thought.md'
    $r = Invoke-McpCall -Tool 'read' -Arguments @{ vault = 'test'; path = $note; with_content_hash = $true } -Token $selectorToken
    $before = if ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text) { try { $r.Text | ConvertFrom-Json } catch { $null } } else { $null }
    $r = Invoke-McpCall -Tool 'edit' -Arguments @{
        vault = 'test'
        path = $note
        operations = @(@{ op = 'set'; key = 'doc.type'; value = 'Note' })
    } -Token $selectorToken
    $edit = if ($r.StatusCode -eq 200 -and -not $r.IsError -and $r.Text) { try { $r.Text | ConvertFrom-Json } catch { $null } } else { $null }
    Check 'an edit dispatches and answers the mutation envelope' ($null -ne $edit -and $null -ne $edit.content_hash) "isError=$($r.IsError) text=$($r.Text)"
    Check 'the value it already carried is a no-op, so nothing applied' ($null -ne $edit -and @($edit.applied_operations).Count -eq 0 -and $edit.frontmatter_changed -eq $false) "text=$($r.Text)"
    # The whole point of the hash: what a mutation answers is what a read of the same file returns,
    # so a chain of writes needs no interleaved read.
    Check 'the hash chains from the read probe''s hash' ($null -ne $edit -and $null -ne $before -and $edit.content_hash -eq $before.content_hash) "edit=$(if ($edit) { $edit.content_hash }) read=$(if ($before) { $before.content_hash })"

    # The guard on the one operation that can destroy content the caller never saw. Refused before
    # anything is written, and carrying no payload - returning the current hash would let a caller
    # retry into content it never read.
    Write-Host 'Probe: edit with a stale content hash (the staleness guard)'
    $r = Invoke-McpCall -Tool 'edit' -Arguments @{
        vault = 'test'
        path = $note
        body = @{ content = "# Never written`n" }
        expected_content_hash = ('0' * 64)
    } -Token $selectorToken
    $stale = if ($r.Text) { try { $r.Text | ConvertFrom-Json } catch { $null } } else { $null }
    Check 'a stale hash refuses with content_changed and no payload' ($r.IsError -and $null -ne $stale -and $stale.code -eq 'content_changed' -and -not $stale.PSObject.Properties['data']) "isError=$($r.IsError) text=$($r.Text)"

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
    Check 'is a coded tool error (authority_required)' ($r.StatusCode -eq 200 -and $r.IsError -and $null -ne $elevErr -and $elevErr.code -eq 'authority_required') "status=$($r.StatusCode) isError=$($r.IsError) text=$($r.Text)"
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
        Check 'minted token cannot mint content:write (authority_required on the op_class dimension)' ($r.IsError -and $null -ne $mintErr -and $mintErr.code -eq 'authority_required' -and $mintErr.data.argument -eq 'op_class') "isError=$($r.IsError) text=$($r.Text)"
    } else {
        Check 'minted token cannot mint content:write (authority_required on the op_class dimension)' $false 'skipped: mint failed'
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
$skipNote = if ($script:skipped -gt 0) { ", $($script:skipped) skipped" } else { '' }
Write-Host "`n-- Smoke result: $($script:passed)/$total passed$skipNote --" -ForegroundColor $color
exit ($script:failed -gt 0 ? 1 : 0)
