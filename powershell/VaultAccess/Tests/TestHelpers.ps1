# Shared Pester fixture for the VaultAccess module test suite. NOT a test file (no
# .Tests.ps1 suffix, so Pester does not discover it). Each test file dot-sources this
# in its BeforeAll and calls Initialize-VaultAccessTestFixture; AfterAll calls
# Remove-VaultAccessTestFixture.
#
# Run with: Invoke-Pester powershell/VaultAccess/Tests
#
# The wikilink and path cases load from tests/cases/*.json, shared with the xUnit suite
# (tests/VaultMcpServer.Tests) so the mirrored chokepoint semantics are exercised
# identically on both sides. Files here live in Tests/ (one level below the module root),
# so repo-relative paths resolve from $PSScriptRoot '../../..'.

function Import-VaultAccessModule {
    Import-Module (Join-Path $PSScriptRoot '../VaultAccess.psd1') -Force
}

function Initialize-VaultAccessTestFixture {
    # Builds the temp-root fixture (test + scratch copies of test-vault, injected
    # .obsidian/junk.md, A/Dupe.md, B/Dupe.md), writes vaults.json, and initializes
    # VaultAccess against it. Returns the temp-root path; callers assign it to
    # $script:TempRoot so It blocks can reference it.
    Import-VaultAccessModule

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vault-mcp-pester-' + [guid]::NewGuid().ToString('N'))
    $source = Join-Path $repoRoot 'test-vault'
    $test = Join-Path $tempRoot 'test'
    $scratch = Join-Path $tempRoot 'scratch'
    Copy-Item -LiteralPath $source -Destination $test -Recurse
    Copy-Item -LiteralPath $source -Destination $scratch -Recurse

    New-Item -ItemType Directory -Path (Join-Path $test '.obsidian'), (Join-Path $test 'A'), (Join-Path $test 'B') | Out-Null
    Set-Content -LiteralPath (Join-Path $test '.obsidian/junk.md') -Value '# Hidden'
    Set-Content -LiteralPath (Join-Path $test 'A/Dupe.md') -Value '# Dupe'
    Set-Content -LiteralPath (Join-Path $test 'B/Dupe.md') -Value '# Dupe'

    $configPath = Join-Path $tempRoot 'vaults.json'
    @{ default_vault = 'test'; vaults = @{ test = $test; scratch = $scratch } } | ConvertTo-Json | Set-Content -LiteralPath $configPath
    Initialize-VaultAccess -ConfigPath $configPath

    return $tempRoot
}

function Remove-VaultAccessTestFixture {
    param([string] $TempRoot)
    Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
