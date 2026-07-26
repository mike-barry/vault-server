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
