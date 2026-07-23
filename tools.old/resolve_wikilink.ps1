<#
.SYNOPSIS
Resolves a wikilink-style or bare-name identifier to its vault-relative note path.
.PARAMETER Link
The identifier to resolve: a bare filename, a path-qualified name, or a full [[wikilink]] -
resolved the same way Resolve-WikiLink resolves any other reference (case-insensitive;
throws if the name is ambiguous).
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: Read
#>
param(
    [Parameter(Mandatory)] [string]$Link,
    [string]$Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# Resolve-WikiLink already throws on ambiguity (listing candidates) - let that surface as an
# MCP tool error, same as rename_note's "Ambiguous wikilink" case. A clean miss is not an
# error condition here, unlike rename_note (which needs a hit to do anything useful) - report
# it as a normal result so callers can branch on "found vs. not found" without catching.
$resolved = Resolve-WikiLink -Link $Link -Vault $Vault
if (-not $resolved) {
    Write-Output "Not found: $Link"
}
else {
    Write-Output $resolved
}
