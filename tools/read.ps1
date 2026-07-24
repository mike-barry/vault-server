<#
.SYNOPSIS
Returns the content of the note at a vault-relative path (successor of read_note). Tier-1
return: the raw text of the requested part, no line numbers, no path echo. Callers holding
only a name resolve it to a path with glob first (Plan-Vault-Tool-Primitives: Path
Convention); the path is taken verbatim after separator normalization - no ".md" inference.
.PARAMETER Path
Vault-relative path of the note to read. An operate-on tool requires exactly one existing
target: a miss errors not_found. A path that resolves to a folder or a non-.md file errors
kind_mismatch - read is note-semantic.
.PARAMETER Part
Which part of the note to return: "full" (default) is the whole content, "frontmatter" is
the raw YAML block between the leading "---" fences (raw block text by ratified contract,
never parsed; empty for a note with no frontmatter), "body" is everything after the closing
fence, verbatim (the whole content for a note with no frontmatter). Deliberately not a
ValidateSet: an unrecognized value must surface as read's own invalid_argument JSON error,
not a PowerShell binding error, so any value reaches the script body for in-script
validation.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:read

read asserts no additional op class ("key") beyond its content:read floor: every path
through it is a content read, agentic-layer frontmatter included - reading agent.*/nav.*
is how an agent navigates at all; only mutating that layer is agentic:write. Read-only,
so no write lock is taken. See Plan-Tool-Read (vault-primary).

There is no Kind parameter: read's only legal value would be "note", and the tool enforces
note-ness on the target regardless - the assertion would be inert (Plan-Tool-Read).
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [string] $Part,
    [string] $Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# --- part: the one enum read owns. Empty means the default (full). ---
$normalizedPart = if ($Part) { $Part.Trim().ToLowerInvariant() } else { 'full' }
if ($normalizedPart -notin @('full', 'frontmatter', 'body')) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "part '$Part' is not a recognized value - read accepts 'full', 'frontmatter', or 'body'." -Data @{ part = $Part })
}

# --- Path normalization: separators fold to "/", a leading "/" (vault-root spelling) is
# trimmed. Taken verbatim beyond that - no ".md" inference (Plan-Tool-Read). ---
$notePath = $Path.Trim().Replace('\', '/').Trim('/')
if ([string]::IsNullOrWhiteSpace($notePath)) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message 'path is empty.' -Data @{ path = $Path })
}

# Vault resolution stays outside the JSON convention by design: the dispatcher's
# vault-membership pre-flight owns that refusal server-side, and a standalone run surfaces
# Resolve-VaultRoot's plain message (Plan-Tool-Read: Errors).
$resolvedVault = Resolve-VaultRoot -Vault $Vault

# A rooted or vault-escaping path is a malformed operand, not a miss.
try {
    $fullPath = Resolve-VaultPath -Path $notePath -Vault $Vault
}
catch {
    throw (New-VaultToolError -Code 'invalid_argument' -Message $_.Exception.Message -Data @{ path = $notePath })
}

# --- Scope membership: the ambient grant's roots for this vault; absent or empty degrades
# open, matching every chokepoint (a standalone run injects no VaultMcpScopeRoots at all). ---
$ambientScopeRoots = Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue
$flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolvedVault.Name -ScopeRoots $ambientScopeRoots
if ($null -ne $flatScopeRoots -and $flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $notePath -ScopeRoots $flatScopeRoots)) {
    throw (New-VaultToolError -Code 'out_of_scope' -Message "Path is outside the grant's scope for vault `"$($resolvedVault.Name)`": $notePath" -Data @{ vault = $resolvedVault.Name; path = $notePath })
}

# --- Shape check: read is note-semantic, so an existing folder or non-.md file is a
# kind_mismatch, not a miss. The read itself re-classifies a vanished-since-probe target
# below, so this probe is a courtesy shape check, not the existence authority. ---
if (Test-Path -LiteralPath $fullPath -PathType Container) {
    throw (New-VaultToolError -Code 'kind_mismatch' -Message "Path is a folder, not a note: $notePath" -Data @{ requiredKind = 'note'; resolvedKind = 'folder' })
}
if (-not $notePath.ToLowerInvariant().EndsWith('.md') -and (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw (New-VaultToolError -Code 'kind_mismatch' -Message "Path is not a markdown note: $notePath" -Data @{ requiredKind = 'note'; resolvedKind = 'file' })
}

# --- The read. Errors classify off the error record, never a Test-Path probe (racy, and a
# dangling symlink passes -PathType Leaf while being unreadable): nothing-to-read is
# not_found; present-but-unreadable (permissions, sharing, I/O) is read_failure - a missing
# sole target is a real error, never a silent skip. ---
try {
    $content = Read-VaultNote -Path $notePath -Vault $Vault
}
catch {
    if (Test-VaultNoteMissingError -ErrorRecord $_) {
        throw (New-VaultToolError -Code 'not_found' -Message "No note at path: $notePath" -Data @{ path = $notePath })
    }
    throw (New-VaultToolError -Code 'read_failure' -Message "Note exists but could not be read: $($_.Exception.Message)" -Data @{ path = $notePath; error = $_.Exception.Message })
}
if ($null -eq $content) { $content = '' }

if ($normalizedPart -eq 'full') {
    Write-Output $content
    return
}

# Vault convention: YAML frontmatter is a block delimited by a "---" line at the very start
# of the file and a matching "---" line that closes it. The one regex serves both remaining
# parts: "frontmatter" is the fenced block's inner text (raw by ratified contract, never
# parsed), "body" is everything after the closing fence's line ending, verbatim. A note
# with no frontmatter has an empty frontmatter and is its own body.
$frontmatterPattern = [regex]::new('^---\r?\n(.*?)\r?\n---\r?\n?', [System.Text.RegularExpressions.RegexOptions]::Singleline)
$match = $frontmatterPattern.Match($content)
if ($normalizedPart -eq 'frontmatter') {
    if ($match.Success) { Write-Output $match.Groups[1].Value } else { Write-Output '' }
}
else {
    if ($match.Success) { Write-Output $content.Substring($match.Length) } else { Write-Output $content }
}
