<#
.SYNOPSIS
Renames or moves a vault note and rewrites every wikilink that references it, across the
whole vault, to point at the new name.
.PARAMETER Note
The note to rename: a bare filename, a path-qualified name, or a full [[wikilink]] -
resolved the same way Resolve-WikiLink resolves any other reference (case-insensitive;
throws if the name is ambiguous).
.PARAMETER NewName
The new name for the note. A bare name (no "/") keeps the note in its current folder and
renames only the basename. A name containing "/" is treated as a full vault-relative
destination path. The ".md" extension is optional either way.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: Write
#>
param(
    [Parameter(Mandatory)] [string]$Note,
    [Parameter(Mandatory)] [string]$NewName,
    [string]$Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# ---------------------------------------------------------------------------
# Refuse under a scoped grant - rename rewrites the link graph vault-wide, so
# scope-filtered enumeration would either silently mis-rewrite a bare link
# meant for an out-of-scope twin or leave out-of-scope backlinks dangling with
# no signal. See Plan-Grant-Tool-Trust-Model's "Scoped-grant mutation safety".
# The ambient scope map is the same VaultMcpScopeRoots global runspace
# variable Get-VaultAccessAmbientScopeRoots reads (that helper isn't exported
# from the module, so it's read directly here); Resolve-VaultScopeRoots - the
# exported chokepoint primitive - does all of the actual map resolution
# (null-map/no-grant handling, per-vault lookup, the missing-entry invariant
# check), so nothing about scope semantics is hand-rolled here.
# ---------------------------------------------------------------------------

$resolvedVault = Resolve-VaultRoot -Vault $Vault
$ambientScopeRoots = Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue
$vaultScopeRoots = Resolve-VaultScopeRoots -Vault $resolvedVault.Name -ScopeRoots $ambientScopeRoots
if ($null -ne $vaultScopeRoots -and $vaultScopeRoots.Count -gt 0) {
    throw "rename_note requires an unscoped Write grant for vault `"$($resolvedVault.Name)`" - the link graph is vault-wide."
}

# ---------------------------------------------------------------------------
# Resolve the target note and the destination path
# ---------------------------------------------------------------------------

$oldPath = Resolve-WikiLink -Link $Note -Vault $Vault
if (-not $oldPath) {
    throw "Note not found: $Note"
}

$oldBasename = [System.IO.Path]::GetFileNameWithoutExtension($oldPath)

$newNameTrimmed = $NewName.Trim()
if ([string]::IsNullOrWhiteSpace($newNameTrimmed)) {
    throw 'NewName is empty.'
}

if ($newNameTrimmed -match '[\\/]') {
    $newPath = $newNameTrimmed.Replace('\', '/').Trim('/')
    if (-not $newPath.ToLowerInvariant().EndsWith('.md')) {
        $newPath = "$newPath.md"
    }
}
else {
    $newBasenameOnly = if ($newNameTrimmed.ToLowerInvariant().EndsWith('.md')) {
        $newNameTrimmed.Substring(0, $newNameTrimmed.Length - 3)
    }
    else {
        $newNameTrimmed
    }
    $slashIndex = $oldPath.LastIndexOf('/')
    $parent = if ($slashIndex -ge 0) { $oldPath.Substring(0, $slashIndex) } else { '' }
    $newPath = if ($parent) { "$parent/$newBasenameOnly.md" } else { "$newBasenameOnly.md" }
}

if ($newPath -eq $oldPath) {
    throw "New path is the same as the current path: $oldPath"
}

$newBasename = [System.IO.Path]::GetFileNameWithoutExtension($newPath)
$pureRename = $oldBasename -ieq $newBasename

# ---------------------------------------------------------------------------
# Ambiguity check - a bare [[$oldBasename]] link is only safe to rewrite if
# $oldBasename is unique across the vault; otherwise leave bare links alone and
# only rewrite path-qualified links, mirroring Resolve-WikiLink's own semantics.
# ---------------------------------------------------------------------------

$allNotes = Get-VaultNotePath -Vault $Vault
$ambiguous = $false
if (-not $pureRename) {
    $sameBasename = $allNotes | Where-Object {
        $_ -ne $oldPath -and ([System.IO.Path]::GetFileNameWithoutExtension($_) -ieq $oldBasename)
    }
    if ($sameBasename) {
        $ambiguous = $true
    }
}

# ---------------------------------------------------------------------------
# Rewrite pass - find every wikilink referencing $oldBasename and update it to
# $newBasename, across every note in the vault. Matches [[Name]], [[Name#anchor]],
# [[Name|display]], [[Name#anchor|display]], and path-qualified [[Some/Path/Name...]].
# ---------------------------------------------------------------------------

$updatedLinks = 0
$skippedLinks = 0
$updatedFiles = 0

if (-not $pureRename) {
    $escapedBasename = [regex]::Escape($oldBasename)
    $wikilinkPattern = [regex]::new(
        "\[\[([^\[\]#|]*/)?(${escapedBasename})(#[^\]|]*)?(\|[^\]]+)?\]\]",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    foreach ($notePath in $allNotes) {
        $content = Read-VaultNote -Path $notePath -Vault $Vault
        $linkMatches = $wikilinkPattern.Matches($content)
        if ($linkMatches.Count -eq 0) {
            continue
        }

        $replacements = @()
        foreach ($m in $linkMatches) {
            $pathPrefix = $m.Groups[1].Value
            $anchor = $m.Groups[3].Value
            $display = $m.Groups[4].Value
            $isBare = [string]::IsNullOrEmpty($pathPrefix)

            if ($ambiguous -and $isBare) {
                $skippedLinks++
                continue
            }

            $newLink = "[[${pathPrefix}${newBasename}${anchor}${display}]]"
            $replacements += [pscustomobject]@{ Old = $m.Value; New = $newLink }
            $updatedLinks++
        }

        if ($replacements.Count -gt 0) {
            $newContent = $content
            foreach ($r in $replacements) {
                $newContent = $newContent.Replace($r.Old, $r.New)
            }
            Write-VaultNote -Path $notePath -Vault $Vault -Content $newContent
            $updatedFiles++
        }
    }
}

# ---------------------------------------------------------------------------
# Move the file itself, last - after every back-reference has already been
# rewritten to point at the new name.
# ---------------------------------------------------------------------------

Move-VaultNote -Path $oldPath -NewPath $newPath -Vault $Vault

$summary = "Renamed $oldPath to $newPath. Wikilinks updated: $updatedLinks in $updatedFiles file(s)."
if ($skippedLinks -gt 0) {
    $summary += " Skipped $skippedLinks ambiguous bare link(s) - review manually."
}
Write-Output $summary
