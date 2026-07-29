# Public/FrontmatterSchema.ps1 - the per-note agent.* / nav.* schema check.
# PowerShell-only: this substrate serves the tool-script contract (edit / create / lint) and has
# no C# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.
#
# Ported from the vault-side skill script
# (.claude/skills/vault-agent-frontmatter/scripts/validate-frontmatter.ps1), which stays live for
# its current consumers. The schema is the vault's, encoded once here so three tools cannot fork
# it into three copies that drift.
#
# SCOPE: the per-note half only - everything decidable in memory from one note's frontmatter.
# The skill script's filesystem-dependent checks stay there for `lint` to absorb as its own pass:
# `folder:` path resolution (Test-Path against a declared directory) and folder-note completeness
# (parent-directory enumeration plus the known-ignored-subfolders suppression list). Pulling them
# in would give `edit` vault enumeration it never calls and make this a filesystem operation.
#
# The return is a structured violation LIST, never a boolean: `lint` renders findings and remedy
# payloads from it, while `edit` and `create` throw schema_validation_failed on a non-empty list.
# An empty list means valid.

function Get-VaultFrontmatterSchemaViolation {
    <#
    .SYNOPSIS
    Checks a note's agent.* / nav.* frontmatter against the vault schema and returns one
    structured violation object per finding - @() when the note is clean.

    Each violation carries:
      Rule       - machine-usable identifier, the field consumers branch on
      Field      - the top-level frontmatter key the violation is about
      EntryIndex - 1-based index of the offending list entry, $null when the violation is about
                   the key rather than one of its entries
      Detail     - the offending sub-field name or inline value, $null when not applicable
      Reason     - human/model-readable explanation

    Keys outside the agent.* / nav.* namespace (doc.* and any other lifecycle field) are not in
    scope and are skipped, exactly as the skill script skips them.

    Assign the result; do not wrap the call in @(). The array is returned comma-wrapped so an
    empty result survives as an empty array rather than collapsing to $null, and @(...) around
    the call would nest that array one level deep.

    Rules emitted: unknown_key, empty_block, empty_placeholder, inline_scalar, entry_not_object,
    link_folder_both, link_folder_neither, missing_description, unknown_entry_field,
    both_discriminators, no_discriminator, unexpected_pinned_field, use_skill_missing_when,
    unexpected_use_skill_field, tasks_missing_use_skill.
    .PARAMETER Content
    Full note content. A note with no frontmatter block is vacuously clean.
    .PARAMETER RelativePath
    Optional vault-relative path of the note. Supplying it enables the one check that needs the
    note's name rather than its content - a file named Tasks.md outside an Archive/ folder must
    carry a `use_skill: vault-tasks` directive. Omitted, that check is skipped and every other
    check runs unchanged; no filesystem access happens either way.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [string] $RelativePath
    )

    # The complete agent.* / nav.* schema, encoded once (the vault's source of truth is
    # Frontmatter-Schema.md, which this conforms to).
    # The nav list is read from Get-VaultNavListKey rather than restated, so the schema and the
    # nav entry ops' allow-list cannot drift into disagreeing about what a nav block list is.
    $navKeys     = Get-VaultNavListKey
    $knownKeys   = @('agent.instructions') + $navKeys
    $entryFields = @('link', 'folder', 'description', 'load_when')
    $pinnedFields = @('pinned')
    $skillFields = @('use_skill', 'when', 'note')

    $violations = New-Object System.Collections.Generic.List[psobject]
    function New-Violation {
        param([string] $Rule, [string] $Field, $EntryIndex, $Detail, [string] $Reason)
        $violations.Add([PSCustomObject]@{
                Rule       = $Rule
                Field      = $Field
                EntryIndex = $EntryIndex
                Detail     = $Detail
                Reason     = $Reason
            })
    }

    # Tolerant parse, single-sourced in Get-VaultFrontmatterMap: this reports on notes rather than
    # gating them, and lint runs it over every file in the vault, so a block that will not parse
    # reads as no block here. The write path refuses that same note outright, which is where an
    # unparseable block is actionable.
    $fm = Get-VaultFrontmatterMap -Content $Content

    # Skill-owned Tasks.md must carry its vault-tasks use_skill: directive, keyed off the literal
    # filename - absence is a violation here, unlike every other field where absence is valid.
    # Archived Tasks.md is excluded: use_skill: entries are stripped at archive time, so a
    # stripped directive there is correct.
    if ($PSBoundParameters.ContainsKey('RelativePath') -and -not [string]::IsNullOrWhiteSpace($RelativePath)) {
        $relFwd = $RelativePath -replace '\\', '/'
        if (($relFwd -split '/')[-1] -ieq 'Tasks.md' -and $relFwd -notmatch '(^|/)Archive(/|$)') {
            $ok = $false
            # Assigned before enumerating, never @()-wrapped: the entry list is returned
            # comma-wrapped so an empty one survives, and wrapping the call would nest it.
            $directives = Get-VaultFrontmatterEntryList -Map $fm -Field 'agent.instructions'
            foreach ($it in $directives) {
                $skill = Get-VaultEntryFieldValue -Entry $it -Name 'use_skill'
                if ($null -ne $skill -and $skill -match 'vault-tasks') { $ok = $true; break }
            }
            if (-not $ok) {
                New-Violation -Rule 'tasks_missing_use_skill' -Field 'agent.instructions' -EntryIndex $null -Detail 'vault-tasks' `
                    -Reason "Tasks.md missing its agent.instructions 'use_skill: vault-tasks' directive"
            }
        }
    }

    foreach ($rawKey in @($fm.Keys)) {
        $key = "$rawKey"
        # Only the agent.*/nav.* namespace is in scope; lifecycle/top-level fields are ignored.
        if ($key -notmatch '^(agent|nav)\.') { continue }
        if ($knownKeys -notcontains $key) {
            New-Violation -Rule 'unknown_key' -Field $key -EntryIndex $null -Detail $null `
                -Reason "Unknown frontmatter key '$key' (not in the agent.*/nav.* schema)"
            continue
        }

        # Presence means content: every known key is a non-empty block list. A valueless key, an
        # empty-marker value ([] or ''), or a stray scalar are all malformed.
        $value = $fm[$rawKey]
        $items = $null
        if ($null -eq $value) {
            New-Violation -Rule 'empty_block' -Field $key -EntryIndex $null -Detail $null `
                -Reason "Empty '$key' - omit the field instead (presence means content)"
            continue
        }
        if ($value -is [string]) {
            if ($value -eq '') {
                New-Violation -Rule 'empty_placeholder' -Field $key -EntryIndex $null -Detail "''" `
                    -Reason "Empty placeholder '$($key): ''' - omit the field instead (presence means content)"
            }
            else {
                New-Violation -Rule 'inline_scalar' -Field $key -EntryIndex $null -Detail $value `
                    -Reason "'$key' should be a block list, found inline value '$value'"
            }
            continue
        }
        $items = Get-VaultFrontmatterEntryList -Map $fm -Field $key
        if ($null -eq $items) {
            New-Violation -Rule 'inline_scalar' -Field $key -EntryIndex $null -Detail "$value" `
                -Reason "'$key' should be a block list, found inline value '$value'"
            continue
        }
        if (@($items).Count -eq 0) {
            New-Violation -Rule 'empty_placeholder' -Field $key -EntryIndex $null -Detail '[]' `
                -Reason "Empty placeholder '$($key): []' - omit the field instead (presence means content)"
            continue
        }

        if ($navKeys -contains $key) {
            $idx = 0
            foreach ($it in $items) {
                $idx++
                if ($it -isnot [System.Collections.IDictionary]) {
                    New-Violation -Rule 'entry_not_object' -Field $key -EntryIndex $idx -Detail $null `
                        -Reason "$key entry #$idx is a bare string, expected a link/folder object"
                    continue
                }
                $hasLink = $it.Contains('link')
                $hasFolder = $it.Contains('folder')
                if ($hasLink -and $hasFolder) {
                    New-Violation -Rule 'link_folder_both' -Field $key -EntryIndex $idx -Detail $null `
                        -Reason "$key entry #$idx has both 'link' and 'folder' (exactly one required)"
                }
                elseif (-not $hasLink -and -not $hasFolder) {
                    New-Violation -Rule 'link_folder_neither' -Field $key -EntryIndex $idx -Detail $null `
                        -Reason "$key entry #$idx has neither 'link' nor 'folder'"
                }
                if ([string]::IsNullOrWhiteSpace((Get-VaultEntryFieldValue -Entry $it -Name 'description'))) {
                    New-Violation -Rule 'missing_description' -Field $key -EntryIndex $idx -Detail 'description' `
                        -Reason "$key entry #$idx is missing 'description'"
                }
                foreach ($f in @($it.Keys)) {
                    if ($entryFields -notcontains "$f") {
                        New-Violation -Rule 'unknown_entry_field' -Field $key -EntryIndex $idx -Detail "$f" `
                            -Reason "$key entry #$idx has unknown field '$f'"
                    }
                }
                # The declared folder: path's resolution against the filesystem is deliberately
                # NOT checked here - it is lint's, along with folder-note completeness.
            }
        }
        elseif ($key -eq 'agent.instructions') {
            # Each entry is a bare string (revisable hint) or a discriminated object:
            # pinned: (human, frozen) or use_skill:/when:/note: (skill-authored, frozen).
            $idx = 0
            foreach ($it in $items) {
                $idx++
                if ($it -isnot [System.Collections.IDictionary]) { continue }
                $hasPinned = $it.Contains('pinned')
                $hasSkill = $it.Contains('use_skill')
                if ($hasPinned -and $hasSkill) {
                    New-Violation -Rule 'both_discriminators' -Field $key -EntryIndex $idx -Detail $null `
                        -Reason "agent.instructions entry #$idx has both 'pinned' and 'use_skill' (exactly one discriminator allowed)"
                }
                elseif ($hasPinned) {
                    foreach ($f in @($it.Keys)) {
                        if ($pinnedFields -notcontains "$f") {
                            New-Violation -Rule 'unexpected_pinned_field' -Field $key -EntryIndex $idx -Detail "$f" `
                                -Reason "agent.instructions entry #$idx (pinned) has unexpected field '$f'"
                        }
                    }
                }
                elseif ($hasSkill) {
                    if ([string]::IsNullOrWhiteSpace((Get-VaultEntryFieldValue -Entry $it -Name 'when'))) {
                        New-Violation -Rule 'use_skill_missing_when' -Field $key -EntryIndex $idx -Detail 'when' `
                            -Reason "agent.instructions entry #$idx (use_skill) is missing 'when'"
                    }
                    foreach ($f in @($it.Keys)) {
                        if ($skillFields -notcontains "$f") {
                            New-Violation -Rule 'unexpected_use_skill_field' -Field $key -EntryIndex $idx -Detail "$f" `
                                -Reason "agent.instructions entry #$idx (use_skill) has unexpected field '$f'"
                        }
                    }
                }
                else {
                    New-Violation -Rule 'no_discriminator' -Field $key -EntryIndex $idx -Detail $null `
                        -Reason "agent.instructions entry #$idx is an object but has neither 'pinned' nor 'use_skill'"
                }
            }
        }
    }

    return , $violations.ToArray()
}
