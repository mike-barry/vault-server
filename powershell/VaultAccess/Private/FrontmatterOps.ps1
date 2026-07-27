# Private/FrontmatterOps.ps1 - the frontmatter op vocabulary: one op object in, an updated
# content string and an applied/changed record out.
# PowerShell-only. See ../VaultAccess.psm1 for the full mirror policy.
#
# Each op maps 1:1 onto a substrate primitive; the guards around the dispatch are what stop the
# three ways a byte-preserving line insertion can be turned into something it is not:
#   - hygiene, so op-supplied text can never mint a top-level key the caller did not author;
#   - the protected-target refusal, so pinned:/use_skill: entries cannot be dropped by a generic
#     field op instead of the explicit strip op that names them;
#   - shape routing, so a flat-scalar op never orphans a block field's children.
#
# The two nav entry ops add a fourth: an allow-list of nav block-list keys (Get-VaultNavListKey),
# so entry insertion reaches the lists it was written for and no other block key. They take
# structured FIELDS rather than pre-rendered block text for the same reason hygiene binds
# directive text - block text would carry line breaks past the per-field check.

function New-VaultOpError {
    # malformed_op with the {opIndex, reason} retry material the error convention requires.
    [CmdletBinding()]
    param([int] $OpIndex, [Parameter(Mandatory)] [string] $Reason)

    New-VaultToolError -Code 'malformed_op' -Message "Op #$($OpIndex): $Reason" -Data @{ opIndex = $OpIndex; reason = $Reason }
}

function Assert-VaultOpText {
    # Hygiene on every caller-supplied scalar an op carries. Bound to ALL text-bearing ops, not
    # just the field ops: directive entries are emitted with hardcoded quotes and no escaping, so
    # unchecked directive text smuggles top-level keys exactly as an unchecked field value would -
    # and a smuggled non-agentic key (doc.type, say) is invisible to both the agentic diff and the
    # schema validator, which only look at agent.*/nav.*.
    [CmdletBinding()]
    param([int] $OpIndex, [AllowEmptyString()] [AllowNull()] [string] $Value, [Parameter(Mandatory)] [string] $Label)

    $reason = Get-VaultFrontmatterTextViolation -Value $Value -Label $Label
    if ($reason) { throw (New-VaultOpError -OpIndex $OpIndex -Reason $reason) }
}

function Invoke-VaultFrontmatterOp {
    <#
    .SYNOPSIS
    Applies one frontmatter op to a content string, returning @{ Content; Record }. Record is
    @{ Op; Field; Kind; Applied; Changed } - Changed is $false for the substrate's idempotent
    no-ops (a duplicate add_directive, an absent-field remove, an unmatched pinned strip).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [AllowNull()] $Op,
        [Parameter(Mandatory)] [int] $OpIndex
    )

    $name = "$(Get-VaultOpValue -Op $Op -Name 'op')".Trim().ToLowerInvariant()
    if ($name -eq '') { throw (New-VaultOpError -OpIndex $OpIndex -Reason "missing 'op' member") }

    $field = Get-VaultOpValue -Op $Op -Name 'field'
    $kind = "$(Get-VaultOpValue -Op $Op -Name 'kind')".Trim().ToLowerInvariant()
    $before = $Content
    $result = $Content

    switch ($name) {
        'set_field' {
            if ([string]::IsNullOrEmpty($field)) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "set_field requires 'field'") }
            $value = Get-VaultOpValue -Op $Op -Name 'value'
            if ($null -eq $value) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "set_field requires 'value'") }
            $reason = Get-VaultFrontmatterKeyViolation $field
            if ($reason) { throw (New-VaultOpError -OpIndex $OpIndex -Reason $reason) }
            Assert-VaultOpText -OpIndex $OpIndex -Value "$value" -Label 'value'
            Assert-VaultOpTarget -OpIndex $OpIndex -Field $field -Content $Content -RequireScalar
            $result = Set-VaultFrontmatterField -Content $Content -Field $field -Value "$value"
        }
        'remove_field' {
            if ([string]::IsNullOrEmpty($field)) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "remove_field requires 'field'") }
            $reason = Get-VaultFrontmatterKeyViolation $field
            if ($reason) { throw (New-VaultOpError -OpIndex $OpIndex -Reason $reason) }
            Assert-VaultOpTarget -OpIndex $OpIndex -Field $field -Content $Content
            # Routed by detected shape - the whole reason two removal primitives exist.
            $result = switch (Get-VaultFrontmatterFieldShape -Content $Content -Field $field) {
                'Block' { Remove-VaultFrontmatterBlock -Content $Content -Field $field }
                'Scalar' { Remove-VaultFrontmatterField -Content $Content -Field $field }
                default { $Content }
            }
        }
        'add_hint' {
            $text = Get-VaultOpValue -Op $Op -Name 'text'
            if ([string]::IsNullOrWhiteSpace($text)) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_hint requires 'text'") }
            Assert-VaultOpText -OpIndex $OpIndex -Value "$text" -Label 'text'
            $result = Add-VaultAgentInstructionEntry -Content $Content -Type Bare -Instruction "$text"
        }
        'add_directive' {
            switch ($kind) {
                'pinned' {
                    $text = Get-VaultOpValue -Op $Op -Name 'text'
                    if ([string]::IsNullOrWhiteSpace($text)) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_directive kind 'pinned' requires 'text'") }
                    Assert-VaultOpText -OpIndex $OpIndex -Value "$text" -Label 'text'
                    $result = Add-VaultAgentInstructionEntry -Content $Content -Type Pinned -Instruction "$text"
                }
                'use_skill' {
                    $skill = Get-VaultOpValue -Op $Op -Name 'skill'
                    $when = Get-VaultOpValue -Op $Op -Name 'when'
                    $note = Get-VaultOpValue -Op $Op -Name 'note'
                    if ([string]::IsNullOrWhiteSpace($skill)) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_directive kind 'use_skill' requires 'skill'") }
                    if ([string]::IsNullOrWhiteSpace($when)) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_directive kind 'use_skill' requires 'when'") }
                    Assert-VaultOpText -OpIndex $OpIndex -Value "$skill" -Label 'skill'
                    Assert-VaultOpText -OpIndex $OpIndex -Value "$when" -Label 'when'
                    Assert-VaultOpText -OpIndex $OpIndex -Value "$note" -Label 'note'
                    $addArgs = @{ Content = $Content; Type = 'UseSkill'; UseSkill = "$skill"; When = "$when" }
                    if (-not [string]::IsNullOrWhiteSpace($note)) { $addArgs['Note'] = "$note" }
                    $result = Add-VaultAgentInstructionEntry @addArgs
                }
                default { throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_directive requires 'kind' of 'pinned' or 'use_skill'") }
            }
        }
        'add_nav_entry' {
            # Structured fields, not pre-rendered block text: the substrate renders the YAML so
            # hygiene applies per field. Raw block text would carry newlines straight past it.
            $key = "$(Get-VaultOpValue -Op $Op -Name 'key')".Trim()
            $link = "$(Get-VaultOpValue -Op $Op -Name 'link')"
            $folder = "$(Get-VaultOpValue -Op $Op -Name 'folder')"
            $description = "$(Get-VaultOpValue -Op $Op -Name 'description')"
            $loadWhen = "$(Get-VaultOpValue -Op $Op -Name 'load_when')"
            $navBefore = "$(Get-VaultOpValue -Op $Op -Name 'before')"
            $navAfter = "$(Get-VaultOpValue -Op $Op -Name 'after')"

            $reason = Get-VaultNavTargetViolation -Key $key -Link $link -Folder $folder
            if ($reason) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_nav_entry: $reason") }
            # Canonical spelling before anything reads or reports the key.
            $key = Resolve-VaultNavListKey -Key $key
            if ([string]::IsNullOrWhiteSpace($description)){ throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_nav_entry requires a non-empty 'description'") }
            if (-not [string]::IsNullOrWhiteSpace($navBefore) -and -not [string]::IsNullOrWhiteSpace($navAfter)) {
                throw (New-VaultOpError -OpIndex $OpIndex -Reason "add_nav_entry: 'before' and 'after' are mutually exclusive")
            }
            foreach ($pair in @(@('link', $link), @('folder', $folder), @('description', $description), @('load_when', $loadWhen), @('before', $navBefore), @('after', $navAfter))) {
                Assert-VaultOpText -OpIndex $OpIndex -Value $pair[1] -Label $pair[0]
            }

            $addArgs = @{ Content = $Content; Key = $key; Description = $description }
            if (-not [string]::IsNullOrWhiteSpace($link)) { $addArgs['Link'] = $link }
            if (-not [string]::IsNullOrWhiteSpace($folder)) { $addArgs['Folder'] = $folder }
            if (-not [string]::IsNullOrWhiteSpace($loadWhen)) { $addArgs['LoadWhen'] = $loadWhen }
            if (-not [string]::IsNullOrWhiteSpace($navBefore)) { $addArgs['Before'] = $navBefore }
            if (-not [string]::IsNullOrWhiteSpace($navAfter)) { $addArgs['After'] = $navAfter }
            # The nav key IS this op's field, so it reports as one in opsApplied.
            $field = $key
            $result = Add-VaultNavEntry @addArgs
        }
        'remove_nav_entry' {
            $key = "$(Get-VaultOpValue -Op $Op -Name 'key')".Trim()
            $link = "$(Get-VaultOpValue -Op $Op -Name 'link')"
            $folder = "$(Get-VaultOpValue -Op $Op -Name 'folder')"

            $reason = Get-VaultNavTargetViolation -Key $key -Link $link -Folder $folder
            if ($reason) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "remove_nav_entry: $reason") }
            $key = Resolve-VaultNavListKey -Key $key
            Assert-VaultOpText -OpIndex $OpIndex -Value $link -Label 'link'
            Assert-VaultOpText -OpIndex $OpIndex -Value $folder -Label 'folder'

            # Ambiguity is checked here rather than left to the primitive so the refusal carries
            # the opIndex - which op in an ordered list is the actionable half of the answer.
            $target = if ([string]::IsNullOrWhiteSpace($link)) { $folder } else { $link }
            $hits = Select-VaultNavEntry -Entries (Get-VaultNavEntry -Content $Content -Key $key) -Link $link -Folder $folder
            if ($hits.Count -gt 1) {
                throw (New-VaultOpError -OpIndex $OpIndex -Reason "remove_nav_entry: '$target' matches $($hits.Count) entries under '$key' - name a target that addresses exactly one entry, or repair the duplicate first")
            }

            $removeArgs = @{ Content = $Content; Key = $key }
            if (-not [string]::IsNullOrWhiteSpace($link)) { $removeArgs['Link'] = $link }
            if (-not [string]::IsNullOrWhiteSpace($folder)) { $removeArgs['Folder'] = $folder }
            $field = $key
            $result = Remove-VaultNavEntry @removeArgs
        }
        'strip_directive' {
            switch ($kind) {
                'use_skill' {
                    # Every use_skill entry at once - the substrate's behavior, and the deliberate
                    # asymmetry with pinned below.
                    $result = Remove-VaultUseSkillAgentInstructions -Content $Content
                }
                'pinned' {
                    $text = Get-VaultOpValue -Op $Op -Name 'text'
                    if ([string]::IsNullOrEmpty($text)) { throw (New-VaultOpError -OpIndex $OpIndex -Reason "strip_directive kind 'pinned' requires 'text'") }
                    Assert-VaultOpText -OpIndex $OpIndex -Value "$text" -Label 'text'
                    $result = Remove-VaultPinnedAgentInstruction -Content $Content -Text "$text"
                }
                default { throw (New-VaultOpError -OpIndex $OpIndex -Reason "strip_directive requires 'kind' of 'pinned' or 'use_skill'") }
            }
        }
        default { throw (New-VaultOpError -OpIndex $OpIndex -Reason "unknown op '$name'") }
    }

    return @{
        Content = $result
        Record  = [PSCustomObject]@{
            Op      = $name
            Field   = $(if ([string]::IsNullOrEmpty($field)) { $null } else { "$field" })
            Kind    = $kind
            Applied = $true
            Changed = ($result -cne $before)
        }
    }
}

function Assert-VaultOpTarget {
    # Refuses the two targets a generic field op must never reach.
    #
    # Precedence is deliberate: protected_directive_violation (routing - use strip_directive)
    # wins over field_shape_mismatch when both would fire on agent.instructions, because naming
    # the right op is the more actionable answer.
    [CmdletBinding()]
    param(
        [int] $OpIndex,
        [Parameter(Mandatory)] [string] $Field,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [switch] $RequireScalar
    )

    if ($Field -ieq 'agent.instructions') {
        throw (New-VaultToolError -Code 'protected_directive_violation' `
                -Message "Op #$($OpIndex): 'agent.instructions' holds pinned: and use_skill: entries, which a generic field op must not drop - use strip_directive, which names the entry." `
                -Data @{ opIndex = $OpIndex; field = $Field })
    }
    if ($RequireScalar -and (Get-VaultFrontmatterFieldShape -Content $Content -Field $Field) -eq 'Block') {
        throw (New-VaultToolError -Code 'field_shape_mismatch' `
                -Message "Op #$($OpIndex): '$Field' is a block-list field; a flat-scalar op would replace its key line and orphan its children." `
                -Data @{ field = $Field; opIndex = $OpIndex })
    }
}
