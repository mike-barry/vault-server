<#
.SYNOPSIS
Searches the vault for notes whose filename or content contains a query substring, returning
the matching notes' vault-relative paths with a short context snippet per hit.
.PARAMETER Query
The text to search for - a plain-text, case-insensitive substring match against each note's
filename and content. No wildcard or regex support.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: Read
#>
param(
    [Parameter(Mandatory)] [string]$Query,
    [string]$Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

if ([string]::IsNullOrWhiteSpace($Query)) {
    throw 'Query is empty.'
}

# Vault sizes here are personal-notes scale, not web scale - a per-note read-and-scan loop
# over the existing enumerate/read chokepoint primitives is simple and fast enough, so this
# needs no new chokepoint search operation.
$results = @()
foreach ($notePath in (Get-VaultNotePath -Vault $Vault)) {
    $filenameHit = $notePath -imatch [regex]::Escape($Query)

    # Skip-missing: a note with no readable content has nothing to search, so it is skipped
    # rather than fatal. Dropped silently, matching the silent drop enumeration already
    # applies to out-of-scope notes. This tool is Read-class and takes no write lock, so it
    # stays exposed to concurrent mutation the mutex cannot cover.
    $content = $null
    $vanished = $false
    try {
        $content = Read-VaultNote -Path $notePath -Vault $Vault
    }
    catch {
        if (Test-VaultNoteMissingError -ErrorRecord $_) { $vanished = $true } else { throw }
    }
    if ($vanished) { continue }

    $snippet = $null
    $contentHit = $false
    foreach ($line in ($content -split "`r?`n")) {
        if ($line -imatch [regex]::Escape($Query)) {
            $contentHit = $true
            $snippet = $line.Trim()
            break
        }
    }

    if ($filenameHit -or $contentHit) {
        $results += if ($snippet) { "${notePath}: $snippet" } else { $notePath }
    }
}

if ($results.Count -eq 0) {
    Write-Output "No matches for: $Query"
}
else {
    Write-Output $results
}
