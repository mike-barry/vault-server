<#
.SYNOPSIS
Reads a vault note's content by identifier - a bare filename, a path-qualified name, or a
full [[wikilink]].
.PARAMETER Note
The note to read, resolved the same way Resolve-WikiLink resolves any other reference
(case-insensitive; throws if the name is ambiguous, throws if it cannot be found).
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.PARAMETER FrontmatterOnly
When set, returns only the note's YAML frontmatter block (the content between the leading
`---` delimiters) instead of the full note content. A note with no frontmatter returns an
empty string.
.NOTES
OpClass: Read
#>
param(
    [Parameter(Mandatory)] [string]$Note,
    [string]$Vault,
    [switch]$FrontmatterOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

$path = Resolve-WikiLink -Link $Note -Vault $Vault
if (-not $path) {
    throw "Note not found: $Note"
}

$content = Read-VaultNote -Path $path -Vault $Vault

if (-not $FrontmatterOnly) {
    Write-Output $content
    return
}

# Vault convention: YAML frontmatter is a block delimited by a "---" line at the very start
# of the file and a matching "---" line that closes it. Hand-rolled extraction rather than a
# YAML parser - the vault has no YAML tooling in use elsewhere in this repo, and the tool only
# needs the raw block text, not parsed key/value data.
$frontmatterPattern = [regex]::new('^---\r?\n(.*?)\r?\n---\r?\n?', [System.Text.RegularExpressions.RegexOptions]::Singleline)
$match = $frontmatterPattern.Match($content)
if ($match.Success) {
    Write-Output $match.Groups[1].Value
}
else {
    Write-Output ''
}
