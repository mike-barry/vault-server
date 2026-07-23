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

Runs under the per-vault write lock as plan -> validate -> apply. Everything checkable is
checked before the first mutation, so missing source, taken destination, and unwritable
targets leave the vault untouched. A mutation that fails after that names every file
already rewritten, so partial state is declared rather than discovered.
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
# The lock spans resolution as well as mutation: Resolve-WikiLink and the
# ambiguity check read link-graph state the rewrite pass depends on, so a
# concurrent rename between resolution and rewrite would leave this run acting
# on a stale graph. One acquisition covers the whole operation rather than the
# N+1 individual chokepoint mutations it issues.
# ---------------------------------------------------------------------------

$lockAbandoned = $false
$summary = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$lockAbandoned) -ScriptBlock {

    # Required: the script-scope preference does not reach cmdlets invoked from inside a
    # module-function scriptblock, and without it a failed mutation is non-terminating and
    # never reaches the phase 3 catch.
    $ErrorActionPreference = 'Stop'

    # =======================================================================
    # PHASE 1 - PLAN. Read-only: resolve the target, compute the destination,
    # and build the complete set of edits. Nothing is written in this phase.
    # =======================================================================

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

    # Ambiguity check - a bare [[$oldBasename]] link is only safe to rewrite if
    # $oldBasename is unique across the vault; otherwise leave bare links alone
    # and only rewrite path-qualified links, mirroring Resolve-WikiLink's own
    # semantics.
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

    # Scan for every wikilink referencing $oldBasename and record the intended
    # replacement content per file. Matches [[Name]], [[Name#anchor]],
    # [[Name|display]], [[Name#anchor|display]], and path-qualified
    # [[Some/Path/Name...]].
    $plan = [System.Collections.Generic.List[object]]::new()
    $updatedLinks = 0
    $skippedLinks = 0
    $missingNotes = 0

    if (-not $pureRename) {
        $escapedBasename = [regex]::Escape($oldBasename)
        $wikilinkPattern = [regex]::new(
            "\[\[([^\[\]#|]*/)?(${escapedBasename})(#[^\]|]*)?(\|[^\]]+)?\]\]",
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

        foreach ($notePath in $allNotes) {
            # Skip-missing: a note with no readable content has no links to rewrite, so it is
            # skipped rather than fatal. Anything that is not a genuine "nothing to read"
            # still throws and cannot be masked as a silent skip.
            $content = $null
            $vanished = $false
            try {
                $content = Read-VaultNote -Path $notePath -Vault $Vault
            }
            catch {
                if (Test-VaultNoteMissingError -ErrorRecord $_) { $vanished = $true } else { throw }
            }
            if ($vanished) {
                $missingNotes++
                continue
            }

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
                $plan.Add([pscustomobject]@{ Path = $notePath; Content = $newContent })
            }
        }
    }

    # =======================================================================
    # PHASE 2 - VALIDATE. Everything checkable is checked before the first
    # mutation, so ordinary failures cost zero written files. Move-VaultNote's
    # own source/destination checks run last, after every backlink has been
    # rewritten, so they are too late to prevent a torn write on their own.
    # =======================================================================

    $fullOld = Resolve-VaultPath -Path $oldPath -Vault $Vault
    $fullNew = Resolve-VaultPath -Path $newPath -Vault $Vault

    if (-not (Test-Path -LiteralPath $fullOld -PathType Leaf)) {
        throw "Source note not found: $oldPath"
    }
    if (Test-Path -LiteralPath $fullNew) {
        throw "Destination already exists: $newPath"
    }

    $unwritable = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $plan) {
        $full = Resolve-VaultPath -Path $entry.Path -Vault $Vault
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            # Vanished since the scan; dropped from the plan, consistent with skip-missing.
            $missingNotes++
            continue
        }
        if ((Get-Item -LiteralPath $full -Force).IsReadOnly) {
            $unwritable.Add($entry.Path)
        }
    }
    if ((Get-Item -LiteralPath $fullOld -Force).IsReadOnly) {
        $unwritable.Add($oldPath)
    }
    if ($unwritable.Count -gt 0) {
        throw "Cannot rename: $($unwritable.Count) file(s) are read-only and would fail partway through. Nothing was modified. Read-only: $($unwritable -join ', ')"
    }

    $applyPlan = @($plan | Where-Object {
        Test-Path -LiteralPath (Resolve-VaultPath -Path $_.Path -Vault $Vault) -PathType Leaf
    })

    # =======================================================================
    # PHASE 3 - APPLY. Backlinks first, the move last. Any failure reports the
    # exact set of files already rewritten.
    # =======================================================================

    $written = [System.Collections.Generic.List[string]]::new()
    try {
        foreach ($entry in $applyPlan) {
            Write-VaultNote -Path $entry.Path -Vault $Vault -Content $entry.Content
            $written.Add($entry.Path)
        }
        Move-VaultNote -Path $oldPath -NewPath $newPath -Vault $Vault
    }
    catch {
        $detail = "rename_note failed partway through: $($_.Exception.Message)"
        if ($written.Count -gt 0) {
            $detail += " PARTIAL STATE - $($written.Count) file(s) already had backlinks rewritten from '$oldBasename' to '$newBasename', and the note itself was NOT moved (still at $oldPath). Rewritten: $($written -join ', '). Recover with 'git checkout -- <files>', or re-run once the cause is resolved."
        }
        else {
            $detail += ' No files were modified.'
        }
        throw $detail
    }

    $result = "Renamed $oldPath to $newPath. Wikilinks updated: $updatedLinks in $($written.Count) file(s)."
    if ($skippedLinks -gt 0) {
        $result += " Skipped $skippedLinks ambiguous bare link(s) - review manually."
    }
    if ($missingNotes -gt 0) {
        $result += " Skipped $missingNotes unreadable note(s) - missing or a broken link."
    }
    $result
}

if ($lockAbandoned) {
    # This rename completed; the warning is about an earlier operation whose process died
    # mid-write and may have left the vault partially written.
    $summary += ' Warning: the write lock had been abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

Write-Output $summary
