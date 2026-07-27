<#
.SYNOPSIS
Returns the content of the note at a vault-relative path (successor of read_note). Tier-1 return:
the raw text of the requested part (a hex digest for part "sha"), no line numbers, no path echo.

Call this when:
- you hold a vault-relative path and need the note's text. Holding only a name, resolve it to a
  path with glob first - the path is taken verbatim after separator normalization, with no ".md"
  inference (Plan-Vault-Tool-Primitives: Path Convention)
- you are about to edit. Read the note before patching so an old_string anchor is exact, and read
  part "sha" for the expected_sha token that makes the write staleness-checked
- you need only the frontmatter or only the body. Part "frontmatter" and part "body" avoid pulling
  a whole large note back through the caller
- you want to re-check a digest you saw earlier, without re-reading the content

Not for locating a note - that is glob for a name or a path shape, grep for content. Not for a
folder or an existing non-".md" file: read is note-semantic and refuses both as kind_mismatch.
There is no recursive or multi-path form - glob the folder, then read the paths you actually want.

An operate-on tool: exactly one existing target, and a miss is not_found.
.PARAMETER Path
Vault-relative path of the note to read. An operate-on tool requires exactly one existing
target: a miss errors not_found. An extensionless path is refused as invalid_argument naming
the ".md" form rather than guessed at. read is note-semantic: a path resolving to a folder or
to an existing non-".md" file is kind_mismatch.
.PARAMETER Part
Which part of the note to return: "full" (default) is the whole content, "frontmatter" is
the raw YAML block between the leading "---" fences (raw block text by ratified contract,
never parsed; empty for a note with no frontmatter), "body" is everything after the closing
fence, verbatim (the whole content for a note with no frontmatter), "sha" is the SHA-256
digest of the whole file's bytes on disk as lowercase hex - the staleness token a caller
passes to edit as expected_sha, covering the entire file no matter which part was read.
Deliberately not a ValidateSet: an unrecognized value must surface as read's own
invalid_argument JSON error, not a PowerShell binding error, so any value reaches the script
body for in-script validation.
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
[CmdletBinding()]
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
if ($normalizedPart -notin @('full', 'frontmatter', 'body', 'sha')) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "part '$Part' is not a recognized value - read accepts 'full', 'frontmatter', 'body', or 'sha'." -Data @{ part = $Part })
}

# --- Path normalization: separators fold to "/", a leading "/" (vault-root spelling) is
# trimmed. Taken verbatim beyond that - no ".md" inference (Plan-Tool-Read). ---
$notePath = $Path.Trim().Replace('\', '/').Trim('/')
if ([string]::IsNullOrWhiteSpace($notePath)) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message 'path is empty.' -Data @{ path = $Path })
}

# Vault resolution before any path work: an unconfigured name refuses unknown_vault and a
# configured-but-root-missing vault refuses vault_unavailable, in the shared {code, message,
# data?} shape (Plan-Vault-Tool-Primitives: Error Convention). Resolving here also keeps a
# vault failure from reaching the invalid_argument wrapper below, which Resolve-VaultPath
# would otherwise trip on the same condition.
$resolvedVault = Resolve-VaultToolRoot -Vault $Vault

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

# --- Operand shape: read is note-semantic, so an existing folder or non-.md file is a
# kind_mismatch, not a miss. The folder probe runs before the extension rule so an existing
# folder is named as a folder rather than suggested a ".md" sibling that is not where its
# folder note lives; an extensionless path that resolves to nothing is invalid_argument
# naming the ".md" form, so the miss is loud and retryable in one call rather than an
# ambiguous not_found (Tool-Contracts: Path Convention). "Extension" is the last
# dot-delimited segment of the last path segment - the contract's own definition - so the
# two rules below read one notion of it and cannot drift apart. The read itself
# re-classifies a vanished-since-probe target below, so the leaf probe here is a courtesy
# shape check, not the existence authority. ---
if (Test-Path -LiteralPath $fullPath -PathType Container) {
    throw (New-VaultToolError -Code 'kind_mismatch' -Message "Path is a folder, not a note: $notePath" -Data @{ requiredKind = 'note'; resolvedKind = 'folder' })
}
$noteExtension = [System.IO.Path]::GetExtension(($notePath -split '/')[-1])
if ([string]::IsNullOrEmpty($noteExtension)) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "path has no extension and read never infers one: retry with '$notePath.md' if that is the note you mean." -Data @{ path = $notePath; suggestedPath = "$notePath.md" })
}
if ($noteExtension -ine '.md' -and (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw (New-VaultToolError -Code 'kind_mismatch' -Message "Path is not a markdown note: $notePath" -Data @{ requiredKind = 'note'; resolvedKind = 'file' })
}

# --- The read. Errors classify off the error record, never a Test-Path probe (racy, and a
# dangling symlink passes -PathType Leaf while being unreadable): nothing-to-read is
# not_found; present-but-unreadable (permissions, sharing, I/O) is read_failure - a missing
# sole target is a real error, never a silent skip. ---
try {
    if ($normalizedPart -eq 'sha') {
        # sha reads the file as bytes rather than through the text read: the digest must
        # identify the file exactly as stored, so no decoding, newline translation, or
        # encoding choice can enter into it. Same try/catch, so the classification below is
        # the one error surface for every part.
        $fileBytes = [System.IO.File]::ReadAllBytes($fullPath)
    }
    else {
        $content = Read-VaultNote -Path $notePath -Vault $Vault
    }
}
catch {
    if (Test-VaultNoteMissingError -ErrorRecord $_) {
        throw (New-VaultToolError -Code 'not_found' -Message "No note at path: $notePath" -Data @{ path = $notePath })
    }
    throw (New-VaultToolError -Code 'read_failure' -Message "Note exists but could not be read: $($_.Exception.Message)" -Data @{ path = $notePath; error = $_.Exception.Message })
}
# --- part: sha. SHA-256 over the whole file's bytes, emitted as lowercase hex (64 chars).
# The digest covers the entire file regardless of which part a caller read, so a caller that
# read one slice still holds a token identifying the whole on-disk state - the invariant
# edit's expected_sha check depends on. Returns before the content branches: $content is
# unassigned on this path. ---
if ($normalizedPart -eq 'sha') {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha256.ComputeHash($fileBytes)
    }
    finally {
        $sha256.Dispose()
    }
    Write-Output ([System.BitConverter]::ToString($digest).Replace('-', '').ToLowerInvariant())
    return
}

if ($null -eq $content) { $content = '' }

if ($normalizedPart -eq 'full') {
    Write-Output $content
    return
}

# Vault convention: YAML frontmatter is a block delimited by a "---" line at the very start
# of the file and a matching "---" line that closes it. The split itself is DELEGATED to the
# substrate's Split-VaultNoteContent - the line-based authority for this exact split - rather
# than restated here: "frontmatter" is its .Frontmatter (the fenced block's inner text, raw by
# ratified contract, never parsed), "body" is its .Body (everything after the closing fence
# line, verbatim). A note with no closed fence has an empty frontmatter and is its own body.
# Delegating is what holds read and `edit` to one definition of the split: `edit` matches its
# patch against the substrate's body facet, so a caller can read part "body", anchor a patch
# in what it got back, and have that patch resolve.
$split = Split-VaultNoteContent -Content $content
if ($normalizedPart -eq 'frontmatter') {
    Write-Output $split.Frontmatter
}
else {
    Write-Output $split.Body
}
