# Public/Frontmatter.ps1 - the in-memory frontmatter primitives: facet split, fence state,
# block/list parsing, flat-scalar get/set/remove, block removal, the agent.instructions
# directive insert/strip pair, and the nav.* entry insert/remove pair.
# PowerShell-only: this substrate serves the tool-script contract (edit / create / lint) and has
# no C# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.
#
# Ported from the vault-side skill substrate (.claude/skills/_shared/vault-frontmatter.ps1),
# which stays live for its own consumers. Two invariants this port adds and holds:
#   1. Byte preservation. A mutation rewrites only the lines it targets; every other byte, line
#      terminators included, survives verbatim, so a CRLF or mixed-ending note keeps its endings
#      and a content-hash chain over successive edits stays stable.
#   2. Unclosed fences are refused, not absorbed. A dangling '---' is a distinct state, so no
#      mutator can prepend a second block on top of one.
#
# NOTHING here touches the filesystem: every function takes and returns an in-memory content
# string. The read/lock/write envelope belongs to the calling tool.

function Get-VaultNoteFenceState {
    <#
    .SYNOPSIS
    Classifies a note's frontmatter fence as 'None' (no leading '---'), 'Closed' (a complete
    '---' ... '---' block), or 'Unclosed' (an opening fence with no closing one).

    The three-way answer is the point: the skill-side substrate reported 'None' and 'Unclosed'
    identically as $null, which is what let a set-field call on a dangling fence stack a second
    block. Consumers raise frontmatter_integrity on 'Unclosed'.
    .PARAMETER Content
    Full note content.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    (Get-VaultFenceBounds (ConvertTo-VaultLineList $Content)).State
}

function Split-VaultNoteContent {
    <#
    .SYNOPSIS
    Splits a note into its two facets: @{ FenceState; Frontmatter; Body; BodyOffset }.

    `Frontmatter` is the fenced block's inner text, raw and never parsed, with the fence lines
    and the last inner line's terminator removed - byte-identical to what `read part: frontmatter`
    returns, and '' for a note with no closed fence. `Body` is everything after the closing fence
    line, verbatim, and is the whole content for a note with no closed fence - byte-identical to
    `read part: body`. `BodyOffset` is the character index in $Content where Body begins, so a
    caller can splice a new body in without recomposing the block it did not touch.

    This is the body facet `edit` matches its old_string/new_string patch against; confining the
    patch to it is what keeps frontmatter unreachable by patch.
    .PARAMETER Content
    Full note content.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    if ($bounds.State -ne 'Closed') {
        return @{ FenceState = $bounds.State; Frontmatter = ''; Body = $Content; BodyOffset = 0 }
    }

    $bodyOffset = 0
    for ($i = 0; $i -le $bounds.End; $i++) { $bodyOffset += $lines[$i].Text.Length + $lines[$i].Terminator.Length }

    $sb = New-Object System.Text.StringBuilder
    for ($i = 1; $i -lt $bounds.End; $i++) {
        [void]$sb.Append($lines[$i].Text)
        # The final inner line's terminator is dropped - the block text is the fence's inner
        # content, not the line ending that closes it; every interior terminator is kept verbatim.
        # This function is where that shape is defined: `read part: frontmatter` and `edit`'s
        # resultingFrontmatter both delegate here, so the convention runs outward from this line.
        if ($i -lt $bounds.End - 1) { [void]$sb.Append($lines[$i].Terminator) }
    }

    return @{
        FenceState  = 'Closed'
        Frontmatter = $sb.ToString()
        Body        = $Content.Substring($bodyOffset)
        BodyOffset  = $bodyOffset
    }
}

function Get-VaultFrontmatterLines {
    <#
    .SYNOPSIS
    The lines between the opening '---' fence and the next '---', or $null when the content has
    no closed frontmatter block. Terminators are stripped - this is the text view the block and
    list-item parsers consume; use Split-VaultNoteContent when raw block text is wanted.

    $null and an empty array are different answers - no block at all, versus a block with no
    fields - so assign the result and test it for $null; do not wrap the call in @(), which
    erases the distinction and nests the array.
    .PARAMETER Content
    Full note content.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    if ($bounds.State -ne 'Closed') { return $null }

    $out = New-Object System.Collections.Generic.List[string]
    for ($i = 1; $i -lt $bounds.End; $i++) { $out.Add($lines[$i].Text) }
    return , $out.ToArray()
}

function Get-VaultFrontmatterBlocks {
    <#
    .SYNOPSIS
    Splits frontmatter lines into top-level blocks: @{ Key; Inline; Children = @(raw indented
    lines) }. The constrained-shape parser for the frontmatter the schema actually uses -
    dot-namespaced top-level keys whose values are block sequences of bare strings or nested
    objects. PowerShell has no built-in YAML and the vault takes no external module dependency.

    Shared by the schema validator (read), the field-shape detector (routing), and the agentic
    diff (classification), so all three consume one parse.
    .PARAMETER FrontmatterLines
    The lines between the fences, as returned by Get-VaultFrontmatterLines.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowNull()] [string[]] $FrontmatterLines)

    $blocks = @()
    $cur = $null
    foreach ($line in $FrontmatterLines) {
        if ($null -eq $line) { continue }
        if ($line -match '^[A-Za-z0-9_.]' -and $line -match '^([A-Za-z0-9_.]+):(.*)$') {
            if ($cur) { $blocks += $cur }
            $cur = @{ Key = $Matches[1]; Inline = $Matches[2].Trim(); Children = @() }
        }
        elseif ($null -ne $cur -and $line.Trim() -ne '') {
            $cur.Children += $line
        }
    }
    if ($cur) { $blocks += $cur }
    return , $blocks
}

function Get-VaultFrontmatterListItems {
    <#
    .SYNOPSIS
    Parses a block's child lines into list items. Each item is either a bare scalar
    (@{ IsMap = $false; Scalar }) or a nested object (@{ IsMap = $true; Fields = [ordered] }).
    Scalar values arrive with one layer of surrounding quotes stripped and no unescaping.
    .PARAMETER Children
    The block's raw child lines, as returned by Get-VaultFrontmatterBlocks.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowNull()] [string[]] $Children)

    $items = @()
    $cur = $null
    foreach ($line in $Children) {
        if ($null -eq $line) { continue }
        if ($line -match '^\s*-\s*(.*)$') {
            if ($cur) { $items += $cur }
            $rest = $Matches[1]
            if ($rest -match '^([A-Za-z0-9_]+):\s*(.*)$') {
                $cur = @{ IsMap = $true; Fields = [ordered]@{} }
                $cur.Fields[$Matches[1]] = (Remove-VaultFrontmatterQuote $Matches[2])
            }
            else {
                $cur = @{ IsMap = $false; Scalar = (Remove-VaultFrontmatterQuote $rest) }
            }
        }
        elseif ($null -ne $cur -and $cur.IsMap -and $line -match '^\s+([A-Za-z0-9_]+):\s*(.*)$') {
            $cur.Fields[$Matches[1]] = (Remove-VaultFrontmatterQuote $Matches[2])
        }
    }
    if ($cur) { $items += $cur }
    return , $items
}

function Get-VaultFrontmatterFieldShape {
    <#
    .SYNOPSIS
    Detects a top-level field's shape: 'Absent', 'Scalar', or 'Block'. This is the detection the
    two-removal-primitive split rests on - a flat-scalar op against a block field replaces or
    deletes the key line and orphans its indented children, so consumers route a removal by shape
    and refuse the corrupting write with field_shape_mismatch.

    Detection reads the block parser's Inline/Children, never Get-VaultFrontmatterField: that
    function returns $null for a block key exactly as it does for a missing one, so it cannot
    tell the two apart.

    A key declared with no inline value AND no children reports 'Scalar': there are no children
    to orphan, so a flat set on it is not the corrupting path the guard exists to stop. (The
    schema validator reports such a key separately as an empty block.)
    .PARAMETER Content
    Full note content.
    .PARAMETER Field
    Top-level field name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Field
    )

    $fmLines = Get-VaultFrontmatterLines $Content
    if ($null -eq $fmLines) { return 'Absent' }
    # Assigned before filtering, never piped: these collection returns are comma-wrapped so an
    # empty result stays an empty array, which means a direct pipe would hand the whole array to
    # the filter as one item.
    $blocks = Get-VaultFrontmatterBlocks $fmLines
    $block = $blocks | Where-Object { $_.Key -eq $Field } | Select-Object -First 1
    if ($null -eq $block) { return 'Absent' }
    if ($block.Inline -ne '') { return 'Scalar' }
    if (@($block.Children).Count -gt 0) { return 'Block' }
    return 'Scalar'
}

function Get-VaultFrontmatterField {
    <#
    .SYNOPSIS
    Reads a flat top-level scalar field (e.g. doc.created, doc.archived) from frontmatter,
    with one layer of surrounding quotes stripped.

    Returns $null when the field, its value, or the frontmatter block itself is absent or empty -
    so this cannot be used as a presence test (an empty value and a missing key are the same
    answer, as are a block key and a missing key). Use Get-VaultFrontmatterFieldShape for
    presence and shape.
    .PARAMETER Content
    Full note content.
    .PARAMETER Field
    Top-level field name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Field
    )

    $fmLines = Get-VaultFrontmatterLines $Content
    if ($null -eq $fmLines) { return $null }
    foreach ($line in $fmLines) {
        if ($line -match "^$([regex]::Escape($Field)):\s*(.*)$") {
            $v = Remove-VaultFrontmatterQuote $Matches[1]
            if ($v -eq '') { return $null }
            return $v
        }
    }
    return $null
}

function Set-VaultFrontmatterField {
    <#
    .SYNOPSIS
    Writes a flat top-level scalar field into frontmatter, returning the updated content. Updates
    the field in place when present, inserts it just before the closing fence when the block
    exists but the field does not, or creates a new block at the top of the file when there is
    none. Existing fields and their order are preserved.

    Flat-scalar only: against a block-list field this replaces the key line and orphans its
    children. Callers route by Get-VaultFrontmatterFieldShape and refuse that path.

    Byte-preserving: an in-place update rewrites one line's text and keeps its terminator; an
    insertion borrows the neighbouring line's terminator. No other byte moves. An unclosed fence
    is refused with frontmatter_integrity rather than being treated as "no block" and prepended
    over.
    .PARAMETER Content
    Full note content.
    .PARAMETER Field
    Top-level field name.
    .PARAMETER Value
    Scalar value, written verbatim after "field: ".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Field,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Value
    )

    $reason = Get-VaultFrontmatterKeyViolation $Field
    if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot set frontmatter field: $reason" -Data @{ reason = $reason }) }
    $reason = Get-VaultFrontmatterTextViolation -Value $Value -Label 'value'
    if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot set frontmatter field '$Field': $reason" -Data @{ reason = $reason }) }

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    Assert-VaultFenceMutable -Bounds $bounds -Operation "Set-VaultFrontmatterField '$Field'"

    if ($bounds.State -eq 'Closed') {
        for ($i = 1; $i -lt $bounds.End; $i++) {
            if ($lines[$i].Text -match "^$([regex]::Escape($Field)):\s*(.*)$") {
                $lines[$i] = [PSCustomObject]@{ Text = "$($Field): $Value"; Terminator = $lines[$i].Terminator }
                return (ConvertFrom-VaultLineList $lines)
            }
        }
        $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor ($bounds.End - 1)
        $lines.Insert($bounds.End, [PSCustomObject]@{ Text = "$($Field): $Value"; Terminator = $term })
        return (ConvertFrom-VaultLineList $lines)
    }

    $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor 0
    $lines.Insert(0, [PSCustomObject]@{ Text = '---'; Terminator = $term })
    $lines.Insert(1, [PSCustomObject]@{ Text = "$($Field): $Value"; Terminator = $term })
    $lines.Insert(2, [PSCustomObject]@{ Text = '---'; Terminator = $term })
    return (ConvertFrom-VaultLineList $lines)
}

function Remove-VaultFrontmatterField {
    <#
    .SYNOPSIS
    Removes a flat top-level scalar field from frontmatter, returning the updated content
    unchanged when the field or the block is absent.

    Flat-scalar only: against a block-list field this deletes the key line and leaves its
    now-unkeyed children behind. Use Remove-VaultFrontmatterBlock for those; callers route by
    Get-VaultFrontmatterFieldShape.

    Byte-preserving line-range removal - the surrounding lines keep their own terminators. An
    unclosed fence is refused with frontmatter_integrity: the skill-side original computed a
    reversed index range in that case and corrupted the content instead of removing the field.
    .PARAMETER Content
    Full note content.
    .PARAMETER Field
    Top-level field name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Field
    )

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    Assert-VaultFenceMutable -Bounds $bounds -Operation "Remove-VaultFrontmatterField '$Field'"
    if ($bounds.State -ne 'Closed') { return $Content }

    for ($i = 1; $i -lt $bounds.End; $i++) {
        if ($lines[$i].Text -match "^$([regex]::Escape($Field)):\s*(.*)$") {
            $lines.RemoveAt($i)
            return (ConvertFrom-VaultLineList $lines)
        }
    }
    return $Content
}

function Remove-VaultFrontmatterBlock {
    <#
    .SYNOPSIS
    Removes an entire block-list field - its key line plus all indented children - returning the
    content unchanged when the field or the block is absent. The block-aware counterpart to
    Remove-VaultFrontmatterField, which handles single-line flat scalars only and would leave a
    block field's children orphaned.

    Byte-preserving line-range removal. An unclosed fence is refused with frontmatter_integrity.
    .PARAMETER Content
    Full note content.
    .PARAMETER Field
    Top-level field name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Field
    )

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    Assert-VaultFenceMutable -Bounds $bounds -Operation "Remove-VaultFrontmatterBlock '$Field'"
    if ($bounds.State -ne 'Closed') { return $Content }

    $block = Get-VaultFrontmatterBlockRange -Lines $lines -Bounds $bounds -Field $Field
    if ($null -eq $block) { return $Content }

    for ($i = $block.BlockEnd; $i -ge $block.KeyLine; $i--) { $lines.RemoveAt($i) }
    return (ConvertFrom-VaultLineList $lines)
}

function Add-VaultAgentInstructionEntry {
    <#
    .SYNOPSIS
    Inserts one discriminated entry into a note's agent.instructions block, creating the block
    (and the frontmatter fence) when absent. Returns the updated content.

    Idempotent: an entry identical to one already present - same Bare/Pinned text, or same
    UseSkill + When - is a no-op, so a backfill caller can invoke it unconditionally. A different
    `when` is a distinct entry, not a duplicate.

    Entry text is refused when it carries a line break, a control character, or a '"'. The entry
    is emitted with hardcoded quotes and no escaping, so any of those would close the literal
    early and let the remainder land as a real top-level key.
    .PARAMETER Content
    Full note content.
    .PARAMETER Type
    Bare (a revisable hint), Pinned (the user's own binding rule), or UseSkill (a routing directive).
    .PARAMETER Instruction
    Entry text; required for Bare and Pinned.
    .PARAMETER UseSkill
    Skill name; required for UseSkill.
    .PARAMETER When
    Condition the directive applies under; required for UseSkill.
    .PARAMETER Note
    Optional extra detail on a UseSkill entry.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [ValidateSet('Bare', 'Pinned', 'UseSkill')] [string] $Type,
        [string] $Instruction,
        [string] $UseSkill,
        [string] $When,
        [string] $Note
    )

    if ($Type -in @('Bare', 'Pinned') -and [string]::IsNullOrWhiteSpace($Instruction)) {
        throw (New-VaultToolError -Code 'malformed_op' -Message "Add-VaultAgentInstructionEntry: -Instruction is required for -Type $Type" -Data @{ reason = 'missing Instruction' })
    }
    if ($Type -eq 'UseSkill' -and ([string]::IsNullOrWhiteSpace($UseSkill) -or [string]::IsNullOrWhiteSpace($When))) {
        throw (New-VaultToolError -Code 'malformed_op' -Message 'Add-VaultAgentInstructionEntry: -UseSkill and -When are required for -Type UseSkill' -Data @{ reason = 'missing UseSkill or When' })
    }
    foreach ($pair in @(@('instruction', $Instruction), @('use_skill', $UseSkill), @('when', $When), @('note', $Note))) {
        $reason = Get-VaultFrontmatterTextViolation -Value $pair[1] -Label $pair[0]
        if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add agent.instructions entry: $reason" -Data @{ reason = $reason }) }
    }

    $newTexts = switch ($Type) {
        'Bare' { , @("  - `"$Instruction`"") }
        'Pinned' { , @("  - pinned: `"$Instruction`"") }
        'UseSkill' {
            $l = @("  - use_skill: `"$UseSkill`"", "    when: `"$When`"")
            if (-not [string]::IsNullOrWhiteSpace($Note)) { $l += "    note: `"$Note`"" }
            , $l
        }
    }

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    Assert-VaultFenceMutable -Bounds $bounds -Operation 'Add-VaultAgentInstructionEntry'

    if ($bounds.State -eq 'None') {
        $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor 0
        $prelude = @('---', 'agent.instructions:') + $newTexts + @('---')
        for ($i = 0; $i -lt $prelude.Count; $i++) {
            $lines.Insert($i, [PSCustomObject]@{ Text = $prelude[$i]; Terminator = $term })
        }
        return (ConvertFrom-VaultLineList $lines)
    }

    $block = Get-VaultFrontmatterBlockRange -Lines $lines -Bounds $bounds -Field 'agent.instructions'
    if ($null -ne $block) {
        $childTexts = @()
        for ($i = $block.KeyLine + 1; $i -le $block.BlockEnd; $i++) { $childTexts += $lines[$i].Text }
        $items = Get-VaultFrontmatterListItems $childTexts
        $dup = $items | Where-Object {
            if ($Type -eq 'Bare') {
                -not $_.IsMap -and $_.Scalar -eq $Instruction
            }
            elseif ($Type -eq 'Pinned') {
                $_.IsMap -and $_.Fields.Contains('pinned') -and $_.Fields['pinned'] -eq $Instruction
            }
            else {
                $_.IsMap -and $_.Fields.Contains('use_skill') -and $_.Fields['use_skill'] -eq $UseSkill `
                    -and $_.Fields.Contains('when') -and $_.Fields['when'] -eq $When
            }
        }
        if ($dup) { return $Content }

        $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor $block.BlockEnd
        for ($i = 0; $i -lt $newTexts.Count; $i++) {
            $lines.Insert($block.BlockEnd + 1 + $i, [PSCustomObject]@{ Text = $newTexts[$i]; Terminator = $term })
        }
        return (ConvertFrom-VaultLineList $lines)
    }

    $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor ($bounds.End - 1)
    $insert = @('agent.instructions:') + $newTexts
    for ($i = 0; $i -lt $insert.Count; $i++) {
        $lines.Insert($bounds.End + $i, [PSCustomObject]@{ Text = $insert[$i]; Terminator = $term })
    }
    return (ConvertFrom-VaultLineList $lines)
}

function Add-VaultNavEntry {
    <#
    .SYNOPSIS
    Inserts one entry into a note's nav.contents / nav.see_also block list, creating the key (and
    the frontmatter fence) when absent. Returns the updated content.

    STRUCTURED, never pre-rendered block text. The caller supplies fields and this function
    renders the YAML, so scalar hygiene applies per field. Accepting raw block text would let a
    caller smuggle line breaks - and therefore top-level keys - past the same guard that stopped
    the skill-side directive-quoting bug.

    Idempotent, keyed on the target value: an entry whose link (or folder) already appears under
    that key is a no-op, so a backfill caller can invoke it unconditionally. The description and
    load_when of an existing entry are NOT updated by a duplicate add - "already there" is the
    answer, and rewriting an entry is a removal plus an add.

    Default position is a pure function of the existing list, reproducing the convention every
    folder note in the vault follows: a link entry goes at the end of the link run (immediately
    before the first folder: entry, or at the end when there is none), and a folder entry goes at
    the end. -Before / -After override it by naming an existing entry's target value.
    .PARAMETER Content
    Full note content.
    .PARAMETER Key
    The nav block-list key - one of Get-VaultNavListKey.
    .PARAMETER Link
    Wikilink target, e.g. '[[Tasks]]'. Exactly one of -Link / -Folder.
    .PARAMETER Folder
    Folder path, e.g. 'Inbox'. Exactly one of -Link / -Folder.
    .PARAMETER Description
    Required, non-empty: the entry's description. Absence is a schema violation, so refusing it
    here keeps the op from writing a note it knows the validator will report.
    .PARAMETER LoadWhen
    Optional load condition; omitted when empty (the schema treats load_when as optional).
    .PARAMETER Before
    Insert immediately before the existing entry with this target value (link or folder).
    .PARAMETER After
    Insert immediately after the existing entry with this target value (link or folder).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Key,
        [string] $Link,
        [string] $Folder,
        [string] $Description,
        [string] $LoadWhen,
        [string] $Before,
        [string] $After
    )

    $reason = Get-VaultNavTargetViolation -Key $Key -Link $Link -Folder $Folder
    if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: $reason" -Data @{ reason = $reason }) }
    # Canonical spelling, so a caller's casing cannot mint a second differently-cased key line.
    $Key = Resolve-VaultNavListKey -Key $Key
    if ([string]::IsNullOrWhiteSpace($Description)) {
        throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: 'description' is required" -Data @{ reason = "'description' is required" })
    }
    $hasBefore = -not [string]::IsNullOrWhiteSpace($Before)
    $hasAfter = -not [string]::IsNullOrWhiteSpace($After)
    if ($hasBefore -and $hasAfter) {
        throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: 'before' and 'after' are mutually exclusive" -Data @{ reason = "'before' and 'after' are mutually exclusive" })
    }
    foreach ($pair in @(@('link', $Link), @('folder', $Folder), @('description', $Description), @('load_when', $LoadWhen), @('before', $Before), @('after', $After))) {
        $reason = Get-VaultFrontmatterTextViolation -Value $pair[1] -Label $pair[0]
        if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: $reason" -Data @{ reason = $reason }) }
    }

    $discriminator = if ([string]::IsNullOrWhiteSpace($Link)) { 'folder' } else { 'link' }
    $target = if ($discriminator -eq 'link') { $Link } else { $Folder }
    $newTexts = @("  - $($discriminator): `"$target`"", "    description: `"$Description`"")
    if (-not [string]::IsNullOrWhiteSpace($LoadWhen)) { $newTexts += "    load_when: `"$LoadWhen`"" }

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    Assert-VaultFenceMutable -Bounds $bounds -Operation "Add-VaultNavEntry '$Key'"

    # No block at all: the entry brings the fence and the key with it, exactly as the directive
    # inserter does. An anchor cannot be honoured against a list that does not exist.
    if ($bounds.State -eq 'None') {
        if ($hasBefore -or $hasAfter) {
            $anchor = if ($hasBefore) { $Before } else { $After }
            throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: no entry with target '$anchor' under '$Key' to position against" `
                    -Data @{ reason = "no entry with target '$anchor' under '$Key'" })
        }
        $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor 0
        $prelude = @('---', "$($Key):") + $newTexts + @('---')
        for ($i = 0; $i -lt $prelude.Count; $i++) {
            $lines.Insert($i, [PSCustomObject]@{ Text = $prelude[$i]; Terminator = $term })
        }
        return (ConvertFrom-VaultLineList $lines)
    }

    $block = Get-VaultFrontmatterBlockRange -Lines $lines -Bounds $bounds -Field $Key
    if ($null -eq $block) {
        if ($hasBefore -or $hasAfter) {
            $anchor = if ($hasBefore) { $Before } else { $After }
            throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: no entry with target '$anchor' under '$Key' to position against" `
                    -Data @{ reason = "no entry with target '$anchor' under '$Key'" })
        }
        # The completeness case: a folder note carrying no nav.contents at all. The key line goes
        # in just before the closing fence, where Set-VaultFrontmatterField puts a new field.
        $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor ($bounds.End - 1)
        $insert = @("$($Key):") + $newTexts
        for ($i = 0; $i -lt $insert.Count; $i++) {
            $lines.Insert($bounds.End + $i, [PSCustomObject]@{ Text = $insert[$i]; Terminator = $term })
        }
        return (ConvertFrom-VaultLineList $lines)
    }

    # Indices are absolute over the same content, so they line up with $lines above.
    $entries = Get-VaultNavEntry -Content $Content -Key $Key
    $dup = Select-VaultNavEntry -Entries $entries -Link $Link -Folder $Folder
    if ($dup.Count -gt 0) { return $Content }

    $insertAt = $block.BlockEnd + 1
    $anchorLine = $block.BlockEnd
    if ($hasBefore -or $hasAfter) {
        $anchorTarget = if ($hasBefore) { $Before } else { $After }
        $hits = Select-VaultNavEntry -Entries $entries -Target $anchorTarget
        if ($hits.Count -eq 0) {
            throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: no entry with target '$anchorTarget' under '$Key' to position against" `
                    -Data @{ reason = "no entry with target '$anchorTarget' under '$Key'" })
        }
        if ($hits.Count -gt 1) {
            throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: '$anchorTarget' matches $($hits.Count) entries under '$Key', so the position is ambiguous" `
                    -Data @{ reason = "'$anchorTarget' matches $($hits.Count) entries under '$Key'" })
        }
        if ($hasBefore) { $insertAt = $hits[0].Start; $anchorLine = $hits[0].Start }
        else { $insertAt = $hits[0].End + 1; $anchorLine = $hits[0].End }
    }
    elseif ($discriminator -eq 'link') {
        # End of the link run: before the first folder: entry, which is where every folder note in
        # the vault keeps its Inbox/Archive entries.
        $firstFolder = $entries | Where-Object { $null -ne $_.Folder } | Select-Object -First 1
        if ($null -ne $firstFolder) { $insertAt = $firstFolder.Start; $anchorLine = $firstFolder.Start }
    }

    $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor $anchorLine
    for ($i = 0; $i -lt $newTexts.Count; $i++) {
        $lines.Insert($insertAt + $i, [PSCustomObject]@{ Text = $newTexts[$i]; Terminator = $term })
    }
    return (ConvertFrom-VaultLineList $lines)
}

function Remove-VaultNavEntry {
    <#
    .SYNOPSIS
    Removes ONE entry from a note's nav.contents / nav.see_also block list, addressed by its
    target value - never by index. When the removal empties the list, the whole key is dropped
    rather than left as an empty block (which the schema validator reports). An absent target is
    a no-op, matching the removal primitives' absent-field semantics.

    Addressed by target because ops are ordered and repetition is semantic: an index would
    silently name a different entry once an earlier op in the same call shifted the list, while a
    target self-verifies.

    A target matching MORE than one entry is refused with malformed_op rather than guessed at -
    the same stance as edit's patch_ambiguous. The note is already malformed in that case (two
    entries pointing at one target), and picking one would be a coin flip the caller cannot see.

    Line-range based, so every surviving entry keeps its exact bytes and terminators.
    .PARAMETER Content
    Full note content.
    .PARAMETER Key
    The nav block-list key - one of Get-VaultNavListKey.
    .PARAMETER Link
    Wikilink target of the entry to remove. Exactly one of -Link / -Folder.
    .PARAMETER Folder
    Folder path of the entry to remove. Exactly one of -Link / -Folder.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Key,
        [string] $Link,
        [string] $Folder
    )

    $reason = Get-VaultNavTargetViolation -Key $Key -Link $Link -Folder $Folder
    if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot remove a nav entry: $reason" -Data @{ reason = $reason }) }
    $Key = Resolve-VaultNavListKey -Key $Key
    foreach ($pair in @(@('link', $Link), @('folder', $Folder))) {
        $reason = Get-VaultFrontmatterTextViolation -Value $pair[1] -Label $pair[0]
        if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot remove a nav entry: $reason" -Data @{ reason = $reason }) }
    }

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    Assert-VaultFenceMutable -Bounds $bounds -Operation "Remove-VaultNavEntry '$Key'"
    if ($bounds.State -ne 'Closed') { return $Content }

    $block = Get-VaultFrontmatterBlockRange -Lines $lines -Bounds $bounds -Field $Key
    if ($null -eq $block) { return $Content }

    $entries = Get-VaultNavEntry -Content $Content -Key $Key
    $hits = Select-VaultNavEntry -Entries $entries -Link $Link -Folder $Folder
    if ($hits.Count -eq 0) { return $Content }
    if ($hits.Count -gt 1) {
        $target = if ([string]::IsNullOrWhiteSpace($Link)) { $Folder } else { $Link }
        $reason = "'$target' matches $($hits.Count) entries under '$Key'"
        throw (New-VaultToolError -Code 'malformed_op' `
                -Message "Cannot remove a nav entry: $reason - name a target that addresses exactly one entry, or repair the duplicate first." `
                -Data @{ reason = $reason })
    }

    if ($entries.Count -eq 1) {
        # Nothing would be left under the key, so the key goes too - the same call the
        # agent.instructions strippers make, and for the same reason.
        for ($i = $block.BlockEnd; $i -ge $block.KeyLine; $i--) { $lines.RemoveAt($i) }
        return (ConvertFrom-VaultLineList $lines)
    }

    for ($i = $hits[0].End; $i -ge $hits[0].Start; $i--) { $lines.RemoveAt($i) }
    return (ConvertFrom-VaultLineList $lines)
}

function Remove-VaultUseSkillAgentInstructions {
    <#
    .SYNOPSIS
    Removes EVERY use_skill:-typed entry from a note's agent.instructions block, leaving bare and
    pinned: entries untouched. When that empties the block, the whole agent.instructions key is
    dropped rather than left as an empty list. A no-op when there is no block or it carries no
    use_skill: entries.

    Line-range based, never a reserialize from parsed items, so surviving entries keep their exact
    bytes and terminators.

    The all-at-once granularity is deliberate and asymmetric with the pinned stripper: a use_skill:
    entry is skill-authored routing, while a pinned: entry is the user's own binding rule and is
    named one at a time.
    .PARAMETER Content
    Full note content.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    Remove-VaultAgentInstructionItem -Content $Content -Kind UseSkill
}

function Remove-VaultPinnedAgentInstruction {
    <#
    .SYNOPSIS
    Removes ONE pinned: entry from a note's agent.instructions block, matched by its exact text.
    When that empties the block, the whole agent.instructions key is dropped. A miss is a no-op,
    matching the absent-field semantics of the removal primitives.

    Granularity is by matched text, one entry: a pinned: entry is the user's own binding rule
    (sensitivity, no-exfil), so removing one has to name it, and a repeated text removes only the
    first occurrence. Text is matched against the parsed entry value - one layer of surrounding
    quotes stripped - which is the same form Add-VaultAgentInstructionEntry -Type Pinned takes.

    Line-range based like the use_skill stripper, so every surviving entry keeps its exact bytes.
    .PARAMETER Content
    Full note content.
    .PARAMETER Text
    The entry's exact text, unquoted.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Text
    )

    $reason = Get-VaultFrontmatterTextViolation -Value $Text -Label 'text'
    if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot strip a pinned entry: $reason" -Data @{ reason = $reason }) }

    Remove-VaultAgentInstructionItem -Content $Content -Kind Pinned -Text $Text -StopAfterFirst
}
