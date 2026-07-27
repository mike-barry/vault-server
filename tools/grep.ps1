<#
.SYNOPSIS
Searches file CONTENT across the vault: takes a regex pattern and returns matching lines, files, or
per-file counts as Tier-1 plain text, one result per line, in ripgrep's order. The vault's
content-search surface (successor to search, whose filename half moved to glob).

Call this when:
- you are looking for notes that MENTION something and do not already know which notes those are
- you need the exact text and line of a phrase - to build an old_string anchor for edit, or to
  find every note referencing a term before renaming it
- you want a file list rather than lines, or a per-file tally. Ask for the files_with_matches or
  count output mode instead of scanning content and counting yourself
- you want an existence check over content. Zero matches is empty output, never an error

Not for finding a file by its name or path - that is glob, and grepping for a filename is the
common mis-selection between the two. Not for reading a note you can already name - that is read.
Narrow a search with the path and glob parameters rather than by calling grep repeatedly.

A query tool: any result count is a result. Zero matches is empty output, many is many lines,
neither is an error - it never raises a not-found or ambiguity error. grep does not implement
matching; it delegates to ripgrep through the shared Search-VaultContent surface (rooted at the
vault, so pattern semantics are native-Grep regex and results stay vault-relative and confined to
the vault), so grep, glob, and move share one notion of what exists. It matches any text file rg
reaches, not only ".md" notes. See the Grep spec (Plan-Tool-Grep, vault-primary) for the full
contract.
.PARAMETER Pattern
Content regex, ripgrep syntax (native Grep semantics). Deliberately not [Parameter(Mandatory)]:
a Mandatory [string] rejects an empty value at binding time with a PowerShell error, but the
spec requires an empty or whitespace-only pattern to surface as grep's own invalid_argument
{code} JSON, so the value must reach the script body for in-script validation (matching glob.ps1
and move.ps1's Kind).
.PARAMETER Path
Optional vault-relative folder to search under, narrowing the rg walk. A rooted or vault-escaping
value is a malformed operand - invalid_argument. Omit for the whole vault.
.PARAMETER Glob
Optional file-filter glob passed as rg's -g (e.g. *.md, Projects/**), case-insensitive like the
glob tool's matching.
.PARAMETER OutputMode
content (path:line:text per matching line, the default), files_with_matches (one path per file
with a match), or count (path:count per file). Deliberately not a ValidateSet: an unrecognized
value must surface as grep's own invalid_argument JSON, not a PowerShell binding error, so any
value reaches the script body for in-script validation.
.PARAMETER IgnoreCase
Opt-in case-insensitive matching (rg -i). Default is case-sensitive, matching the native cognate.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:read

grep asserts no additional op class ("key") beyond its content:read floor: every path through it
is a content read, agentic-layer frontmatter included - grep never mutates, and discloses only
in-scope lines (out-of-scope matches are silently dropped by Search-VaultContent's scope
post-filter, so broader results require a broader grant, not a stronger class). Its only
capability denial is the dispatcher's pre-flight floor refusal. A read-class tool: it takes no
write lock. Standalone (`pwsh -File`) has no injected scope and resolves unscoped, exactly as the
shared search already degrades. See Plan-Tool-Grep (vault-primary).
#>
[CmdletBinding()]
param(
    [string] $Pattern,
    [string] $Path,
    [string] $Glob,
    [string] $OutputMode,
    [bool] $IgnoreCase,
    [string] $Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# --- pattern: the one argument invariant grep owns unconditionally. Everything else (malformed
# regex, missing ripgrep, per-file read errors) is classified inside Search-VaultContent and
# surfaces as its own {code} error. ---
if ([string]::IsNullOrWhiteSpace($Pattern)) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message 'pattern is empty or whitespace-only.' -Data @{ pattern = $Pattern })
}

# --- output_mode: the one enum grep owns. Empty means the default (content). ---
$mode = if ($OutputMode) { $OutputMode.Trim().ToLowerInvariant() } else { 'content' }
if ($mode -notin @('content', 'files_with_matches', 'count')) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "output_mode '$OutputMode' is not a recognized value - grep accepts 'content', 'files_with_matches', or 'count'." -Data @{ outputMode = $OutputMode })
}

# Vault resolution before the path check and the search: both Resolve-VaultPath and
# Search-VaultContent resolve the vault internally through the mirrored-core Resolve-VaultRoot,
# which raises plain text - and the path check would additionally misfile a vault failure as
# invalid_argument. Resolving here refuses an unconfigured name as unknown_vault and a
# configured-but-root-missing vault as vault_unavailable (Plan-Vault-Tool-Primitives: Error
# Convention).
[void](Resolve-VaultToolRoot -Vault $Vault)

# --- path: an optional search-narrowing folder. A rooted or vault-escaping value is a malformed
# operand (invalid_argument), validated via Resolve-VaultPath the same way read.ps1 validates its
# target; existence is not required (a subtree with no matches is an empty result, not an error).
# Separators fold to "/" and a leading "/" (vault-root spelling) is trimmed; empty after that is
# the whole-vault default. ---
$searchTarget = ''
if (-not [string]::IsNullOrWhiteSpace($Path)) {
    $searchTarget = $Path.Trim().Replace('\', '/').Trim('/')
    if (-not [string]::IsNullOrWhiteSpace($searchTarget)) {
        try {
            [void](Resolve-VaultPath -Path $searchTarget -Vault $Vault)
        }
        catch {
            throw (New-VaultToolError -Code 'invalid_argument' -Message $_.Exception.Message -Data @{ path = $Path })
        }
    }
}

# Tier-1 plain text: matching lines/paths/counts, vault-relative forward-slash paths, in
# ripgrep's order (no sorting). Search-VaultContent shells to rg, classifies the exit code,
# normalizes and scope-filters the output, and applies the scope post-filter; no matches yields
# no output, per the query-tool convention. Scope roots default to the ambient host-injected set,
# so no runspace context (standalone) degrades open.
Search-VaultContent -Pattern $Pattern -Path $searchTarget -Glob $Glob -Mode $mode -IgnoreCase:$IgnoreCase -Vault $Vault
