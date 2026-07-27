<#
.SYNOPSIS
Finds files by NAME or PATH: takes a glob pattern and returns the matching vault-relative paths,
one per line, in ripgrep's order. The vault's only filename-search surface and the discovery step
that turns a name into a path (successor to resolve_wikilink).

Call this when:
- you know a note's name but not where it lives. Glob `**/Note-Name.md` first, then pass the
  returned path to read, edit, move, or delete - those tools take a path and never a bare name
- you are resolving a wikilink. `[[Note-Name]]` is not a path: strip the brackets and glob for
  `**/Note-Name.md`
- you need to enumerate what exists under a folder or matching a shape (`Projects/**/*.md`)
- you want an existence check. Zero matches is empty output, never an error, so no try/catch

Not for searching inside files - that is grep, which matches file CONTENT, while glob only ever
looks at the path. Grepping for a filename is the common mis-selection between the two. Not for
reading a note once you hold its path - that is read.

A query tool: any result count is a result. Zero matches is empty output, many is many lines,
neither is an error - it never raises an ambiguity or not-found error. glob does not implement
glob matching; it delegates to ripgrep through the shared Get-VaultFilePath enumeration (rooted at
the vault, so pattern semantics match the native Glob tool and results stay confined to the vault),
so glob and move can never drift to independent notions of what exists. See the Glob spec
(Plan-Tool-Glob, vault-primary) for the full contract.
.PARAMETER Pattern
Glob pattern with native Glob semantics (e.g. `**/Note-Name.md`, `Projects/**/*.md`). Expresses
location as well as name. Matching is case-insensitive on Windows, like the native Glob tool.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:read

glob declares the content:read floor and asserts no additional op class ("key"): it mutates
nothing and discloses only paths inside the caller's scope - out-of-scope matches are silently
dropped by Get-VaultFilePath's scope post-filter, so broader results require a broader grant, not
a stronger class. Its only capability denial is the dispatcher's pre-flight floor refusal. A
read-class tool: it takes no write lock. Standalone (`pwsh -File`) has no injected scope and
resolves unscoped, exactly as the shared enumeration already degrades.
#>
# Pattern is deliberately not [Parameter(Mandatory)]: a Mandatory [string] rejects an empty
# string at binding time with a PowerShell error, but the spec requires an empty or
# whitespace-only pattern to surface as glob's own invalid_argument {code} JSON, so the value
# must reach the script body for in-script validation (the same reason move.ps1's Kind is not
# Mandatory).
[CmdletBinding()]
param(
    [string] $Pattern,
    [string] $Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# Non-empty pattern is the one argument invariant glob owns; everything else (malformed pattern,
# missing ripgrep) is classified inside Get-VaultFilePath and surfaces as its own {code} error.
if ([string]::IsNullOrWhiteSpace($Pattern)) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message 'pattern is empty or whitespace-only.' -Data @{ pattern = $Pattern })
}

# Vault resolution before enumeration: Get-VaultFilePath resolves the vault internally through
# the mirrored-core Resolve-VaultRoot, which raises plain text, so glob resolves first to refuse
# an unconfigured name as unknown_vault and a configured-but-root-missing vault as
# vault_unavailable (Plan-Vault-Tool-Primitives: Error Convention).
[void](Resolve-VaultToolRoot -Vault $Vault)

# Tier-1 plain text: one vault-relative, forward-slash path per line, in ripgrep's order (no
# sorting). Get-VaultFilePath applies the scope post-filter and the vault-root containment guard;
# no matches yields no output, per the native Glob convention. Scope roots default to the ambient
# host-injected set, so no runspace context (standalone) degrades open.
Get-VaultFilePath -Pattern $Pattern -Vault $Vault
