# Public/Frontmatter.ps1 - the in-memory frontmatter primitives: facet split, field shape, flat
# scalar get/set/remove, block removal, the agent.instructions directive insert/strip pair, and
# the nav.* key accessor plus entry insert/remove pair.
# PowerShell-only: this substrate serves the tool-script contract (edit / create / lint) and has
# no C# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.
#
# Every mutator here reads the block to an object, changes the object, and re-emits the whole
# block. Two consequences that callers depend on:
#   1. Body CONTENT is preserved verbatim - every byte except line terminators, which are
#      normalized to LF along with the rest of the note. The frontmatter block's own bytes are
#      not preserved; only its meaning is.
#   2. An idempotent no-op returns the caller's content unchanged rather than a reserialization
#      of it, so nothing reformats on a write that changed nothing.
#
# NOTHING here touches the filesystem: every function takes and returns an in-memory content
# string. The read/lock/write envelope belongs to the calling tool.

function Split-VaultNoteContent {
    <#
    .SYNOPSIS
    Splits a note into its two facets: @{ FenceState; Frontmatter; Body; BodyOffset }.

    `Frontmatter` is the block's inner text, raw and never parsed, with the fence lines and the
    last inner line's terminator removed - byte-identical to what `read part: frontmatter`
    returns, and '' for a note with no block. `Body` is everything after the closing fence line,
    verbatim, and is the whole content for a note with no block - byte-identical to
    `read part: body`. `BodyOffset` is the character index in $Content where Body begins, so a
    caller can splice a new body in without recomposing the block it did not touch - which is what
    keeps a body-only edit from reformatting the frontmatter and churning the note's hash.

    `FenceState` is 'Closed' or 'None': a block is present or it is not. The third state the old
    scanner reported for a dangling '---' is gone, because to the standard that note has no
    frontmatter at all - see Get-VaultFrontmatterSpan for the grammar and for the differential
    test that holds it to the standard's own implementation.

    This is the body facet `edit` matches its old_string/new_string patch against; confining the
    patch to it is what keeps frontmatter unreachable by patch. This function is also where the
    block-text shape is defined - `read part: frontmatter` and `edit`'s resultingFrontmatter both
    delegate here, so the convention runs outward from here.
    .PARAMETER Content
    Full note content.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    $span = Get-VaultFrontmatterSpan $Content
    if ($null -eq $span) {
        return @{ FenceState = 'None'; Frontmatter = ''; Body = $Content; BodyOffset = 0 }
    }

    return @{
        FenceState  = 'Closed'
        Frontmatter = $Content.Substring($span.InnerStart, $span.InnerLength)
        Body        = $Content.Substring($span.BodyOffset)
        BodyOffset  = $span.BodyOffset
    }
}

function Get-VaultFrontmatterMap {
    <#
    .SYNOPSIS
    A note's frontmatter as the parsed key/value tree - an empty ordered dictionary when the note
    has no block or its block will not parse.

    The READ-side entry point, and the counterpart to the write side's own parse: this one is
    deliberately TOLERANT, because its callers report on notes rather than gating them and they
    run over every file in the vault, so a block that will not parse reads as no block. The write
    path refuses that same note outright with frontmatter_integrity, which is where an unparseable
    block is actionable.

    Together with Get-VaultFrontmatterEntryList and Get-VaultEntryFieldValue this is the whole
    surface a consumer needs to ask a note's frontmatter a question. It replaces the hand-rolled
    lines/blocks/list-items trio, and the replacement is one-for-one on purpose: a consumer that
    walked key/children/list-item shapes by hand was reimplementing a parser, and the defects this
    substrate was ported to remove all lived in that walk.
    .PARAMETER Content
    Full note content.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    $split = Split-VaultNoteContent $Content
    $map = $null
    if ($split.FenceState -eq 'Closed') {
        try { $map = ConvertFrom-Yaml -Yaml $split.Frontmatter -Ordered } catch { $map = $null }
    }
    if ($map -isnot [System.Collections.IDictionary]) { $map = [ordered]@{} }
    return $map
}

function Get-VaultFrontmatterEntryList {
    <#
    .SYNOPSIS
    A top-level key's entries as an array, or $null when the key is absent or holds something that
    is not a block sequence. Shared by the directive and nav entry mutators, the schema validator,
    and lint's own nav scans, so "this key holds entries" has one definition.

    $null and an empty array are different answers - "not a list" versus "a list with nothing in
    it" - so assign the result and test it for $null; the array is returned comma-wrapped to keep
    an empty one from collapsing into the first of those.
    .PARAMETER Map
    A frontmatter tree, from Get-VaultFrontmatterMap.
    .PARAMETER Field
    Top-level field name, matched case-insensitively.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Map, [Parameter(Mandatory)] [string] $Field)

    $key = Resolve-VaultFrontmatterKey -Map $Map -Field $Field
    if ($null -eq $key) { return $null }
    $value = $Map[$key]
    if ($value -is [string] -or $value -is [System.Collections.IDictionary]) { return $null }
    if ($value -isnot [System.Collections.IEnumerable]) { return $null }

    $out = New-Object 'System.Collections.Generic.List[object]'
    foreach ($e in $value) { $out.Add($e) }
    return , $out.ToArray()
}

function Get-VaultEntryFieldValue {
    <#
    .SYNOPSIS
    A list entry's named sub-field as text, or $null when the entry is not a map or does not carry
    it. The one reader for entry discriminators, so the nav ops, the directive strippers, the
    schema validator, and lint cannot drift on what an entry field is.

    An entry that is a bare string answers $null to every name, which is what lets a caller test a
    discriminator instead of first testing the entry's shape.
    .PARAMETER Entry
    One element of a Get-VaultFrontmatterEntryList result.
    .PARAMETER Name
    Sub-field name.
    #>
    [CmdletBinding()]
    param([AllowNull()] $Entry, [Parameter(Mandatory)] [string] $Name)

    if ($Entry -isnot [System.Collections.IDictionary]) { return $null }
    if (-not $Entry.Contains($Name)) { return $null }
    $v = $Entry[$Name]
    if ($null -eq $v) { return $null }
    return "$v"
}

function Get-VaultFrontmatterFieldShape {
    <#
    .SYNOPSIS
    Detects a top-level field's shape: 'Absent', 'Scalar', or 'Block'. This is the detection the
    two-removal-primitive split rests on - consumers route a removal by shape and refuse a
    flat-scalar set against a block field with field_shape_mismatch, so a caller cannot replace a
    whole nav list with a string by naming the wrong op.

    A type test now, not an inline/children split: a value that is a sequence or a nested mapping
    is 'Block', and everything else - including a key declared with no value at all - is 'Scalar'.
    A valueless key has nothing to orphan, so a flat set on it is not the path the guard exists to
    stop. (The schema validator reports such a key separately as an empty block.)
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

    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { return 'Absent' }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation "Field shape '$Field'"

    $key = Resolve-VaultFrontmatterKey -Map $map -Field $Field
    if ($null -eq $key) { return 'Absent' }
    $value = $map[$key]
    if ($null -eq $value -or $value -is [string]) { return 'Scalar' }
    if ($value -is [System.Collections.IEnumerable]) { return 'Block' }
    return 'Scalar'
}

function Get-VaultFrontmatterField {
    <#
    .SYNOPSIS
    Reads a flat top-level scalar field (e.g. doc.created, doc.archived) from frontmatter.

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

    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { return $null }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation "Field read '$Field'"

    $key = Resolve-VaultFrontmatterKey -Map $map -Field $Field
    if ($null -eq $key) { return $null }
    $value = $map[$key]
    if ($null -eq $value -or $value -isnot [string]) { return $null }
    if ($value -eq '') { return $null }
    return $value
}

function Set-VaultFrontmatterField {
    <#
    .SYNOPSIS
    Writes a flat top-level scalar field into frontmatter, returning the updated content. Updates
    the field in place when present - keeping its position, and the note's existing key order with
    it - appends it after the last key when the block exists but the field does not, or creates a
    block at the top of the file when there is none.

    Flat-scalar only: against a block-list field this replaces the whole value. Callers route by
    Get-VaultFrontmatterFieldShape and refuse that path with field_shape_mismatch.
    .PARAMETER Content
    Full note content.
    .PARAMETER Field
    Top-level field name.
    .PARAMETER Value
    Scalar value. The emitter quotes and escapes it as YAML requires, so it needs no shape of its
    own beyond the key's registry value pattern; control characters are refused outright.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Field,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Value
    )

    # The registry, not a character class: an unrecognized key is unwritable, and a recognized
    # one's value must match its declared pattern. Both checks live here as well as at the op
    # layer, so the primitive cannot be reached around to mint a key or fill a legitimate one with
    # arbitrary text.
    $reason = Get-VaultFrontmatterRegistryViolation -Field $Field -Value $Value
    if ($reason) { throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot set frontmatter field: $reason" -Data @{ reason = $reason }) }

    $operation = "Set-VaultFrontmatterField '$Field'"
    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { Assert-VaultFrontmatterCreatable -Content $Content -Operation $operation }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation $operation

    # Through the resolver so a caller's casing updates the existing key rather than adding a
    # second one beside it.
    $key = Resolve-VaultFrontmatterKey -Map $map -Field $Field
    if ($null -eq $key) { $key = $Field }
    $map[$key] = $Value
    return (Join-VaultNoteContent -Map $map -Body $split.Body)
}

function Remove-VaultFrontmatterField {
    <#
    .SYNOPSIS
    Removes a flat top-level scalar field from frontmatter, returning the content unchanged when
    the field or the block is absent. Drops the fence entirely when that empties the block.

    Paired with Remove-VaultFrontmatterBlock, which callers route to by shape. The two now do the
    same thing to the tree - the split survives because the ROUTING is the policy: it is what lets
    a flat-scalar op be refused against a block field before it ever reaches a removal.
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

    Remove-VaultFrontmatterKey -Content $Content -Field $Field -Operation "Remove-VaultFrontmatterField '$Field'"
}

function Remove-VaultFrontmatterBlock {
    <#
    .SYNOPSIS
    Removes an entire block-list field - its key and every entry under it - returning the content
    unchanged when the field or the block is absent. Drops the fence entirely when that empties
    the block.

    The block-aware counterpart to Remove-VaultFrontmatterField in the op vocabulary; see there
    for why the pair survives the move to an object model.
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

    Remove-VaultFrontmatterKey -Content $Content -Field $Field -Operation "Remove-VaultFrontmatterBlock '$Field'"
}

function Add-VaultAgentInstructionEntry {
    <#
    .SYNOPSIS
    Inserts one discriminated entry into a note's agent.instructions block, creating the block
    (and the frontmatter fence) when absent. Returns the updated content.

    Idempotent: an entry identical to one already present - same Bare/Pinned text, or same
    UseSkill + When - is a no-op, so a backfill caller can invoke it unconditionally. A different
    `when` is a distinct entry, not a duplicate.
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

    $newEntry = switch ($Type) {
        'Bare' { $Instruction }
        'Pinned' { [ordered]@{ pinned = $Instruction } }
        'UseSkill' {
            $e = [ordered]@{ use_skill = $UseSkill; when = $When }
            if (-not [string]::IsNullOrWhiteSpace($Note)) { $e['note'] = $Note }
            $e
        }
    }

    $operation = 'Add-VaultAgentInstructionEntry'
    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { Assert-VaultFrontmatterCreatable -Content $Content -Operation $operation }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation $operation

    $key = Resolve-VaultFrontmatterKey -Map $map -Field 'agent.instructions'
    $entries = Get-VaultFrontmatterEntryList -Map $map -Field 'agent.instructions'
    if ($null -eq $entries) { $entries = New-Object 'System.Collections.Generic.List[object]' }

    foreach ($entry in $entries) {
        $dup = switch ($Type) {
            'Bare' { $entry -is [string] -and $entry -eq $Instruction }
            'Pinned' { (Get-VaultEntryFieldValue -Entry $entry -Name 'pinned') -eq $Instruction }
            'UseSkill' {
                (Get-VaultEntryFieldValue -Entry $entry -Name 'use_skill') -eq $UseSkill -and
                (Get-VaultEntryFieldValue -Entry $entry -Name 'when') -eq $When
            }
        }
        if ($dup) { return $Content }
    }

    $updated = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $entries) { $updated.Add($entry) }
    $updated.Add($newEntry)
    # A key that was absent lands after the last existing key, which is where the old line splicer
    # put a new field: just before the closing fence.
    if ($null -eq $key) { $key = 'agent.instructions' }
    $map[$key] = $updated
    return (Join-VaultNoteContent -Map $map -Body $split.Body)
}

function Get-VaultNavListKey {
    <#
    .SYNOPSIS
    The nav block-list keys an entry op may address - the registry's NavList-flagged entries, read
    rather than restated, so the op vocabulary, the schema validator, and lint's scans cannot drift
    into disagreeing about what a nav list is. Admitting a third nav list is one entry in
    Get-VaultFrontmatterKeyRegistry.

    Exported because it is the published legal-value set for the -Key parameter of
    Add-VaultNavEntry / Remove-VaultNavEntry, and because out-of-module consumers (tools/lint.ps1
    and edit.ps1's .SCHEMA guard) must read it rather than restate it.
    #>
    [CmdletBinding()]
    param()

    $registry = Get-VaultFrontmatterKeyRegistry
    return , @(@($registry.Keys) | Where-Object { $registry[$_].NavList })
}

function Add-VaultNavEntry {
    <#
    .SYNOPSIS
    Inserts one entry into a note's nav.contents / nav.see_also block list, creating the key (and
    the frontmatter fence) when absent. Returns the updated content.

    STRUCTURED, never pre-rendered block text. The caller supplies fields and this function builds
    the entry, so each field is a value in the tree rather than text spliced into a line - which
    is what makes "a description cannot become a top-level key" a property of the emitter instead
    of a check the caller has to be trusted to have passed.

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
    # Canonical spelling, so a caller's casing cannot mint a second differently-cased key.
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
    $newEntry = [ordered]@{ $discriminator = $target; description = $Description }
    if (-not [string]::IsNullOrWhiteSpace($LoadWhen)) { $newEntry['load_when'] = $LoadWhen }

    $operation = "Add-VaultNavEntry '$Key'"
    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { Assert-VaultFrontmatterCreatable -Content $Content -Operation $operation }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation $operation

    $mapKey = Resolve-VaultFrontmatterKey -Map $map -Field $Key
    $existing = Get-VaultFrontmatterEntryList -Map $map -Field $Key
    $entries = New-Object 'System.Collections.Generic.List[object]'
    if ($null -ne $existing) { foreach ($e in $existing) { $entries.Add($e) } }

    # An anchor cannot be honoured against a list that does not exist yet.
    if ($entries.Count -eq 0 -and ($hasBefore -or $hasAfter)) {
        $anchor = if ($hasBefore) { $Before } else { $After }
        throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: no entry with target '$anchor' under '$Key' to position against" `
                -Data @{ reason = "no entry with target '$anchor' under '$Key'" })
    }

    # Assigned before filtering: Get-VaultNavEntry's return is comma-wrapped, so a direct pipe
    # would hand the whole list to the filter as one item.
    $addressable = Get-VaultNavEntry -Content $Content -Key $Key
    $dup = Select-VaultNavEntry -Entries $addressable -Link $Link -Folder $Folder
    if ($dup.Count -gt 0) { return $Content }

    $insertAt = $entries.Count
    if ($hasBefore -or $hasAfter) {
        $anchorTarget = if ($hasBefore) { $Before } else { $After }
        $hits = Select-VaultNavEntry -Entries $addressable -Target $anchorTarget
        if ($hits.Count -eq 0) {
            throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: no entry with target '$anchorTarget' under '$Key' to position against" `
                    -Data @{ reason = "no entry with target '$anchorTarget' under '$Key'" })
        }
        if ($hits.Count -gt 1) {
            throw (New-VaultToolError -Code 'malformed_op' -Message "Cannot add a nav entry: '$anchorTarget' matches $($hits.Count) entries under '$Key', so the position is ambiguous" `
                    -Data @{ reason = "'$anchorTarget' matches $($hits.Count) entries under '$Key'" })
        }
        $insertAt = if ($hasBefore) { $hits[0].Index } else { $hits[0].Index + 1 }
    }
    elseif ($discriminator -eq 'link') {
        # End of the link run: before the first folder: entry, which is where every folder note in
        # the vault keeps its Inbox/Archive entries.
        $firstFolder = $addressable | Where-Object { $null -ne $_.Folder } | Select-Object -First 1
        if ($null -ne $firstFolder) { $insertAt = $firstFolder.Index }
    }

    $entries.Insert($insertAt, $newEntry)
    if ($null -eq $mapKey) { $mapKey = $Key }
    $map[$mapKey] = $entries
    return (Join-VaultNoteContent -Map $map -Body $split.Body)
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

    $operation = "Remove-VaultNavEntry '$Key'"
    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { return $Content }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation $operation

    $existing = Get-VaultFrontmatterEntryList -Map $map -Field $Key
    if ($null -eq $existing) { return $Content }

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

    $kept = New-Object 'System.Collections.Generic.List[object]'
    $i = 0
    foreach ($e in $existing) {
        if ($i -ne $hits[0].Index) { $kept.Add($e) }
        $i++
    }

    $mapKey = Resolve-VaultFrontmatterKey -Map $map -Field $Key
    if ($kept.Count -eq 0) {
        # Nothing would be left under the key, so the key goes too - the same call the
        # agent.instructions strippers make, and for the same reason.
        $map.Remove($mapKey)
    }
    else { $map[$mapKey] = $kept }
    return (Join-VaultNoteContent -Map $map -Body $split.Body)
}

function Remove-VaultUseSkillAgentInstructions {
    <#
    .SYNOPSIS
    Removes EVERY use_skill:-typed entry from a note's agent.instructions block, leaving bare and
    pinned: entries untouched. When that empties the block, the whole agent.instructions key is
    dropped rather than left as an empty list. A no-op when there is no block or it carries no
    use_skill: entries.

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
    first occurrence. Text is matched CASE-SENSITIVELY against the entry's parsed value, which is
    the same form Add-VaultAgentInstructionEntry -Type Pinned takes.
    .PARAMETER Content
    Full note content.
    .PARAMETER Text
    The entry's exact text.
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
