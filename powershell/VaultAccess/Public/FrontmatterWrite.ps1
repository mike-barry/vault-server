# Public/FrontmatterWrite.ps1 - the validate-and-classify wrapper: the single write path into
# frontmatter, and the agentic outcome diff it classifies on.
# PowerShell-only: this substrate serves the tool-script contract (edit / create / lint) and has
# no C# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.
#
# Every consumer that writes frontmatter goes through Invoke-VaultFrontmatterWrite rather than
# calling the primitives directly. That routing is what makes three properties structural instead
# of merely intended: the fence-introduction guard, scalar hygiene, and the agentic:write
# assertion apply to every frontmatter write in the codebase, including lint's provenance dates.
#
# All in memory. The caller owns the read, the lock, and the write.

function Get-VaultAgenticFrontmatterDiff {
    <#
    .SYNOPSIS
    The agent.* / nav.* keys whose presence or value differs between two versions of a note -
    sorted, @() when the agentic layer is untouched.

    This is the classification predicate for the agentic:write key, and it is deliberately
    OUTCOME-based rather than a read of what the ops nominally targeted. Measuring the result
    closes the injection paths where an op aimed at a non-agentic field mutates the layer anyway.
    Exported in its own right so a consumer can assert the property directly - lint's non-agentic
    guarantee lives in its fix set, and the test for it compares before and after over this
    function rather than depending on a denial that varies with the caller's grant.

    Assign the result; do not wrap the call in @(), which would nest the returned array.
    .PARAMETER BeforeContent
    The note as it was; '' for a note being created.
    .PARAMETER AfterContent
    The note as it would be written.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $BeforeContent,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $AfterContent
    )

    $before = Get-VaultAgenticKeyMap $BeforeContent
    $after = Get-VaultAgenticKeyMap $AfterContent

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($before.Keys) + @($after.Keys)) {
        if ($changed.Contains($key)) { continue }
        $inBefore = $before.ContainsKey($key)
        $inAfter = $after.ContainsKey($key)
        if ($inBefore -ne $inAfter) { $changed.Add($key); continue }
        if ($inBefore -and ($before[$key] -cne $after[$key])) { $changed.Add($key) }
    }
    return , @($changed | Sort-Object)
}

function Invoke-VaultFrontmatterWrite {
    <#
    .SYNOPSIS
    Composes a note's new content from frontmatter ops and/or a new body, validates the result
    against the vault schema, classifies the agentic outcome diff, and asserts agentic:write when
    that diff is non-empty. Returns the composed content and the decision data a caller needs;
    performs no file I/O.

    Three refusals are hard throws, because a caller cannot sensibly proceed past any of them:

      frontmatter_integrity   - an unclosed fence on input or output, or a composed result whose
                                re-extracted frontmatter differs from what the ops produced. The
                                second covers both directions: a body write that introduces a
                                fence on a fence-less note (minting a block no op authored, past
                                schema validation and past the key assertion), and one that
                                destroys or truncates an existing fence.
      malformed_op            - an unknown op, a missing op parameter, or field/value/text
                                hygiene: a bad key name, or a line break, control character, or
                                '"' in caller-supplied text.
      insufficient_capability - the agentic diff is non-empty and the injected op-class set lacks
                                agentic:write. Nothing has been written at that point, and the
                                caller gets no content back, so the denial is zero-mutation. An
                                absent class set (a standalone run) degrades open, per the shared
                                model.

    Schema violations are NOT a throw - they are returned. The caller decides what a violation
    means: edit and create throw schema_validation_failed on a non-empty list, lint renders
    findings from it. A hard throw here would make lint's provenance date fix refuse on exactly
    the notes with pre-existing agent.* problems, which are the ones most needing attention.

    All three consumers reach the same path with no branch of their own, each passing
    -RelativePath:
      edit   - -Content (the note as read), -Ops, and -Body only when it is replacing or patching
               the body facet (it resolves its own old_string/new_string against
               Split-VaultNoteContent's Body first, so the fence guard still catches a fence the
               patch introduced).
      create - -Content '' and -Frontmatter (the caller-supplied block) plus -Body. With no
               original to diff against, every agentic key present counts as a diff, which is the
               degenerate case of the same classification rather than a second rule.
      lint   - -Content and one or two set_field ops on doc.* scalars. Non-agentic by
               construction, so the diff is empty and no key is asserted - defense in depth under
               lint's own fix-set guarantee.
    .PARAMETER Content
    The note's current full content; '' when the note is being created.
    .PARAMETER Ops
    Ordered op objects (hashtables or deserialized JSON objects), @() when there are none.
    Vocabulary: set_field {field, value}, remove_field {field}, add_hint {text},
    add_directive {kind: pinned|use_skill, text | skill + when + note?},
    strip_directive {kind: pinned|use_skill, text (required for pinned)},
    add_nav_entry {key, link|folder, description, load_when?, before?|after?},
    remove_nav_entry {key, link|folder}.
    Order and repetition are semantic and are honoured; each op sees the result of the previous.

    The two nav entry ops take FIELDS, never pre-rendered block text, so scalar hygiene applies
    per field - block text would smuggle line breaks past it. `key` is one of the nav block-list
    keys (Get-VaultNavListKey); `link` and `folder` are exclusive in both directions; entries are
    addressed by their target value rather than by index, because ops are ordered and an index
    would name a different entry once an earlier op shifted the list. Both are idempotent: a
    duplicate add and an absent-target removal report changed: false and mutate nothing. A target
    matching more than one entry is malformed_op, not a guess.
    .PARAMETER Frontmatter
    A whole caller-supplied frontmatter block replacing whatever $Content carried - create's
    alternative to -Ops. Accepted fenced or as bare inner lines; empty means no block at all.
    .PARAMETER Body
    The new body facet. Unbound leaves the body untouched; '' truncates it.
    .PARAMETER RelativePath
    Optional vault-relative path of the note, passed through to the schema validator to enable
    its one path-dependent check (Tasks.md's required vault-tasks directive).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [AllowEmptyCollection()] [AllowNull()] [object[]] $Ops = @(),
        [AllowEmptyString()] [string] $Frontmatter,
        [AllowEmptyString()] [string] $Body,
        [string] $RelativePath
    )

    $hasFrontmatter = $PSBoundParameters.ContainsKey('Frontmatter')
    $hasBody = $PSBoundParameters.ContainsKey('Body')

    # An input fence that never closes is refused up front: every mutator would otherwise read it
    # as "no frontmatter" and stack a second complete block on top of it.
    $originalSplit = Split-VaultNoteContent $Content
    if ($originalSplit.FenceState -eq 'Unclosed') {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message 'The note opens a "---" frontmatter fence that is never closed; it cannot be written until the fence is repaired.' `
                -Data @{ reason = 'unclosed_fence' })
    }

    $working = if ($hasFrontmatter) {
        (New-VaultFrontmatterBlockText -Frontmatter $Frontmatter) + $originalSplit.Body
    }
    else { $Content }

    $records = New-Object System.Collections.Generic.List[psobject]
    $opCount = if ($null -eq $Ops) { 0 } else { $Ops.Count }
    for ($i = 0; $i -lt $opCount; $i++) {
        $applied = Invoke-VaultFrontmatterOp -Content $working -Op $Ops[$i] -OpIndex $i
        $working = $applied.Content
        $records.Add($applied.Record)
    }

    # The frontmatter the ops produced - the byte-exact reference the guard below compares against.
    $afterOps = Split-VaultNoteContent $working

    $final = $working
    if ($hasBody) {
        if ($afterOps.FenceState -eq 'Closed') {
            $prefix = $working.Substring(0, $afterOps.BodyOffset)
            if ($Body -ne '' -and -not $prefix.EndsWith("`n")) {
                $prefix += (Get-VaultLineTerminatorFor -Lines (ConvertTo-VaultLineList $prefix) -Anchor 0)
            }
            $final = $prefix + $Body
        }
        else {
            # No fence to preserve: the body IS the whole note, which is exactly the case the
            # fence-introduction guard below exists for.
            $final = $Body
        }
    }

    $finalSplit = Split-VaultNoteContent $final
    if ($finalSplit.FenceState -ne $afterOps.FenceState -or $finalSplit.Frontmatter -cne $afterOps.Frontmatter) {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message 'The composed content''s frontmatter does not match what the frontmatter ops produced - a body write introduced, altered, or truncated the fence.' `
                -Data @{ reason = 'body_write_changed_frontmatter'; expectedFenceState = $afterOps.FenceState; actualFenceState = $finalSplit.FenceState })
    }
    if ($finalSplit.FenceState -eq 'Unclosed') {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message 'The composed content opens a "---" frontmatter fence that is never closed.' `
                -Data @{ reason = 'unclosed_fence' })
    }

    $validateArgs = @{ Content = $final }
    if (-not [string]::IsNullOrWhiteSpace($RelativePath)) { $validateArgs['RelativePath'] = $RelativePath }
    # Assigned, never @()-wrapped: both of these return a comma-wrapped array so that an empty
    # result survives as an empty array, and wrapping the call would nest it one level deep.
    $violations = Get-VaultFrontmatterSchemaViolation @validateArgs

    # Outcome-based classification, over the diff rather than over what the ops nominally
    # targeted, and asserted before the caller has anything to write.
    $agenticKeysChanged = Get-VaultAgenticFrontmatterDiff -BeforeContent $Content -AfterContent $final
    $capabilities = @()
    if ($agenticKeysChanged.Count -gt 0) {
        Assert-VaultOpClass -OpClass 'agentic:write' -Data @{ requiredClass = 'agentic:write'; agenticKeysChanged = $agenticKeysChanged }
        $capabilities = @('agentic:write')
    }

    return [PSCustomObject]@{
        Content            = $final
        Frontmatter        = $finalSplit.Frontmatter
        Body               = $finalSplit.Body
        Violations         = $violations
        OpsApplied         = @($records)
        FrontmatterChanged = ($originalSplit.Frontmatter -cne $finalSplit.Frontmatter -or $originalSplit.FenceState -ne $finalSplit.FenceState)
        BodyChanged        = ($originalSplit.Body -cne $finalSplit.Body)
        AgenticKeysChanged = $agenticKeysChanged
        CapabilitiesUsed   = $capabilities
    }
}
