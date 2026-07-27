<#
.SYNOPSIS
Mutates an EXISTING note in one write-locked plan-validate-apply call: ordered frontmatter ops plus
at most one body facet - a whole-body replacement, or a single Edit-style old_string/new_string
patch.

Call this when:
- you are changing the content or the frontmatter of a note that already exists. The note must
  exist - a miss is not_found, and creation is create's job
- you are setting, removing, or reordering frontmatter: doc.* fields, agent.instructions entries,
  nav.contents and nav.see_also entries. The frontmatter ops are the only supported way in, and
  authoring a "---" block through the body facet is refused frontmatter_integrity
- you are changing one span of prose. Prefer the old_string/new_string patch over a whole-body
  replacement: it must match exactly once, so it refuses (patch_target_not_found or
  patch_ambiguous) rather than clobbering
- you are replacing the whole body. That facet REQUIRES expected_sha, so read part "sha" first
- a lint finding handed you an edit-frontmatter or edit-patch remedy with computed params

Not for a path that does not exist yet - that is create. Not for renaming or relocating a note -
that is move, which also rewrites every reference to it; edit cannot change a path. Not for
removing a note - that is delete. Not for lint suppression state, which lives in opaque files
under .vault/state/ - that is lint_suppressions. edit is note-semantic: a folder or an existing
non-".md" file is kind_mismatch.

Every op applies to an in-memory string and the result is written once, so a validation failure
means zero mutation and there is no partial-op disk state. See the Edit corpus doc (vault-primary)
for the full contract, including the documented "read part: sha then read part: full" staleness
flow.
.PARAMETER Path
Vault-relative path of the note to edit, per the Path Convention: taken verbatim after
separator normalization (backslashes fold to "/", a leading "/" is trimmed), with no ".md"
inference. An extensionless path is refused as invalid_argument naming the ".md" form rather
than guessed at. edit is note-semantic: a path resolving to a folder or to an existing
non-".md" file is kind_mismatch.
.PARAMETER FrontmatterOps
A JSON string holding an ORDERED array of frontmatter op objects, deserialized and validated
in-script (the derived MCP schema cannot express an array of typed objects). Order and
repetition are semantic - each op sees the result of the previous one. Vocabulary:
  {"op":"set_field","field":"doc.type","value":"Corpus"}      - flat top-level scalar set
  {"op":"remove_field","field":"doc.type"}                    - removal, routed to the scalar or
                                                                block primitive by detected shape
  {"op":"add_hint","text":"..."}                              - bare (revisable) agent.instructions entry
  {"op":"add_directive","kind":"pinned","text":"..."}         - the user's own binding rule
  {"op":"add_directive","kind":"use_skill","skill":"vault-docs","when":"editing this file","note":"..."}
  {"op":"strip_directive","kind":"pinned","text":"..."}       - removes exactly the named entry
  {"op":"strip_directive","kind":"use_skill"}                 - removes every use_skill entry
  {"op":"add_nav_entry","key":"nav.contents","link":"[[Note]]","description":"...","load_when":"..."}
                                                              - optional before/after positions the
                                                                entry against one named by target
  {"op":"remove_nav_entry","key":"nav.see_also","folder":"Inbox"}  - removes the addressed entry,
                                                                dropping the key if that empties it
An unknown op, a missing op parameter, or text carrying a line break, a control character, or a
'"' is malformed_op with {opIndex, reason}. agent.instructions is reachable only through the
directive ops - a generic set_field/remove_field against it is protected_directive_violation.
.PARAMETER Body
Whole-body replacement: everything after the frontmatter block's closing fence. Unset leaves the
body untouched; an empty string truncates it. Mutually exclusive with the OldString/NewString
patch pair, and REQUIRES ExpectedSha - a whole-body replace is the one facet that can destroy
content the caller never saw.
.PARAMETER OldString
Edit-style patch anchor. Matched against the BODY facet only, so frontmatter is unreachable by
patch, and it must match exactly once: zero matches is patch_target_not_found, more than one is
patch_ambiguous (widen the anchor). Must be supplied together with NewString, and is mutually
exclusive with Body. One patch per call.
.PARAMETER NewString
Replacement for the span OldString matched; an empty string deletes it. Must be supplied
together with OldString.
.PARAMETER ExpectedSha
The whole file's SHA-256 as the caller last saw it - from "read part: sha" or a prior edit's
resultingSha - compared in the validate phase, so a stale write is refused with zero mutation.
A mismatch is state_mismatch; recovery is a re-read, never a retry with the reported digest.
Required with Body, optional on every other facet (a patch self-verifies through its
exactly-once anchor, and frontmatter ops are surgical).
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:write

edit asserts agentic:write in-script when the resulting frontmatter differs from the original
over any agent.* / nav.* key - classification is outcome-based, measured over the composed diff
rather than over what the ops nominally targeted, and it happens in the validate phase, so a
denial is insufficient_capability with zero disk change. Standalone (no injected class set)
degrades open. The assertion and the diff live in Invoke-VaultFrontmatterWrite, the single write
path into frontmatter, which also owns the fence-introduction guard, scalar hygiene, and schema
validation; edit throws schema_validation_failed on the violation list that wrapper returns.

Runs under the per-vault write lock as plan -> validate -> apply. There is no `apply` parameter:
edit is a single-target tool taking caller-supplied content, so it has nothing to discover and a
preview would be ceremony (Tool-Contracts: Assess-Before-Mutate Convention). The return carries
the write-class envelope: capabilitiesUsed and filesSkippedUnreadable[] always ([] when empty),
lockWarning only when an abandoned mutex was inherited. filesSkippedUnreadable is structurally
always [] - edit is single-target, so a missing or unreadable sole target is not_found /
read_failure, never a skip.
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [string] $FrontmatterOps,
    [string] $Body,
    [string] $OldString,
    [string] $NewString,
    [string] $ExpectedSha,
    [string] $Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# Facet presence is decided here, in script scope, from $PSBoundParameters - the only way to tell
# an unset -Body (leave the body alone) from -Body '' (truncate it). A parameter bound to an
# explicit JSON null counts as unsupplied: the dispatcher converts JSON null to $null, and
# "present but null" is not a facet the caller asked to mutate.
$script:BodyBound = $PSBoundParameters.ContainsKey('Body') -and $null -ne $Body
$script:OldStringBound = $PSBoundParameters.ContainsKey('OldString') -and $null -ne $OldString
$script:NewStringBound = $PSBoundParameters.ContainsKey('NewString') -and $null -ne $NewString
$script:ExpectedShaBound = $PSBoundParameters.ContainsKey('ExpectedSha') -and -not [string]::IsNullOrWhiteSpace($ExpectedSha)

function Get-VaultEditSha {
    <#
    .SYNOPSIS
    SHA-256 over a file's raw bytes as lowercase hex - byte-identical to what `read part: sha`
    returns, which is what makes expected_sha and resultingSha one interchangeable token. Takes
    bytes rather than a path or a string so no decoding, newline translation, or encoding choice
    can enter the digest.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]] $Bytes)

    [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($Bytes)).Replace('-', '').ToLowerInvariant()
}

function Measure-VaultEditPatchMatch {
    <#
    .SYNOPSIS
    Counts ordinal, non-overlapping occurrences of $Anchor in $Text, stopping at two, and returns
    @{ Count; Index } where Index is the first match's offset (-1 when there is none). Two is all
    the caller needs: zero is patch_target_not_found and anything above one is patch_ambiguous, so
    counting the rest of a large body buys nothing.
    #>
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Text,
        [Parameter(Mandatory)] [string] $Anchor
    )

    $count = 0
    $firstIndex = -1
    $searchIndex = 0
    while ($searchIndex -le $Text.Length) {
        $i = $Text.IndexOf($Anchor, $searchIndex, [System.StringComparison]::Ordinal)
        if ($i -lt 0) { break }
        $count++
        if ($count -eq 1) { $firstIndex = $i }
        if ($count -gt 1) { break }
        $searchIndex = $i + $Anchor.Length
    }
    @{ Count = $count; Index = $firstIndex }
}

# Vault resolution before the write lock: Invoke-VaultWriteLock derives its mutex name from the
# resolved root, so it resolves the vault itself and would raise the mirrored core's plain text
# before the locked scriptblock ever runs. Resolving here refuses an unconfigured name as
# unknown_vault and a configured-but-root-missing vault as vault_unavailable (Tool-Contracts:
# Error Convention), with nothing locked.
$resolvedVault = Resolve-VaultToolRoot -Vault $Vault

$lockAbandoned = $false
$result = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$lockAbandoned) -ScriptBlock {
    # Required: the script-scope preference does not reach cmdlets invoked from inside a
    # module-function scriptblock, and without it a failed mutation is non-terminating and gets
    # reported as success.
    $ErrorActionPreference = 'Stop'

    # =======================================================================
    # VALIDATE. Argument shape, then the operand's shape, then the file's state - every
    # invariant checked before a single character of the note is mutated in memory, and the
    # note is only ever mutated in memory. Ordered: the FIRST failure is reported, because
    # {opIndex, ...} is retry material for one op at a time, not a batch report.
    # =======================================================================

    # --- Path normalization: separators fold to "/", a leading "/" (vault-root spelling) is
    # trimmed. Verbatim beyond that - no ".md" inference. ---
    $notePath = $Path.Trim().Replace('\', '/').Trim('/')
    if ([string]::IsNullOrWhiteSpace($notePath)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'path is empty.' -Data @{ path = $Path })
    }

    # --- frontmatter_ops: a JSON-string parameter, so the contract lives in .PARAMETER and the
    # deserialization is the script's own. -NoEnumerate is load-bearing: without it a
    # single-element ops array is unrolled to a bare object and becomes indistinguishable from a
    # caller who sent one op object instead of the required array. ---
    $ops = @()
    if (-not [string]::IsNullOrWhiteSpace($FrontmatterOps)) {
        try {
            $parsedOps = ConvertFrom-Json -InputObject $FrontmatterOps -NoEnumerate
        }
        catch {
            throw (New-VaultToolError -Code 'malformed_op' -Message "frontmatter_ops is not valid JSON: $($_.Exception.Message)" -Data @{ reason = 'frontmatter_ops is not valid JSON' })
        }
        if ($parsedOps -isnot [System.Object[]]) {
            throw (New-VaultToolError -Code 'malformed_op' -Message 'frontmatter_ops must be a JSON array of op objects, e.g. [{"op":"set_field","field":"doc.type","value":"Corpus"}].' -Data @{ reason = 'frontmatter_ops is not a JSON array' })
        }
        $ops = $parsedOps
    }

    # --- Facet-form rules. One body form per call, at least one facet overall, and the
    # whole-body replace carries a mandatory staleness guard. Enforced in-script because the
    # derived schema cannot express conditional requiredness. ---
    $hasOps = $ops.Count -gt 0
    $hasPatch = $OldStringBound -and $NewStringBound

    if ($BodyBound -and ($OldStringBound -or $NewStringBound)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'body and the old_string/new_string patch pair are mutually exclusive - one body form per call.' -Data @{ path = $notePath })
    }
    if ($OldStringBound -xor $NewStringBound) {
        $supplied = if ($OldStringBound) { 'old_string' } else { 'new_string' }
        $missing = if ($OldStringBound) { 'new_string' } else { 'old_string' }
        throw (New-VaultToolError -Code 'invalid_argument' -Message "$supplied was supplied without $missing - a patch is the pair, not half of it." -Data @{ path = $notePath })
    }
    if ($hasPatch -and $OldString -eq '') {
        # An empty anchor matches at every position, so it can neither locate a span nor
        # self-verify against a stale view - the two things the patch facet exists to do.
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'old_string is empty - a patch anchor must be a non-empty span of the body.' -Data @{ path = $notePath })
    }
    if (-not $hasOps -and -not $BodyBound -and -not $hasPatch) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'Nothing to do: supply frontmatter_ops, body, or an old_string/new_string patch.' -Data @{ path = $notePath })
    }
    if ($BodyBound -and -not $ExpectedShaBound) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'body (whole-body replace) requires expected_sha - it is the only facet that can destroy content the caller never saw. Read "part: sha", then "part: full", then edit.' -Data @{ path = $notePath })
    }

    # --- A rooted or vault-escaping path is a malformed operand, not a miss. ---
    try {
        $fullPath = Resolve-VaultPath -Path $notePath -Vault $Vault
    }
    catch {
        throw (New-VaultToolError -Code 'invalid_argument' -Message $_.Exception.Message -Data @{ path = $notePath })
    }

    # --- Scope membership. MANDATORY here and the only thing that produces out_of_scope: the
    # Write-VaultNote chokepoint calls Assert-VaultOperationAllowed, which throws a PLAIN STRING
    # and is deliberately not one of the coded chokepoints, so without this pre-check a scope
    # refusal would reach the caller uncoded. It also keeps the refusal ahead of any in-memory
    # mutation. Absent or empty roots degrade open (a standalone run injects no
    # VaultMcpScopeRoots at all); read through Get-Variable because under Set-StrictMode an
    # unset global is an error, and standalone is exactly the case where it is unset. ---
    $ambientScopeRoots = Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolvedVault.Name -ScopeRoots $ambientScopeRoots
    if ($null -ne $flatScopeRoots -and $flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $notePath -ScopeRoots $flatScopeRoots)) {
        throw (New-VaultToolError -Code 'out_of_scope' -Message "Path is outside the grant's scope for vault `"$($resolvedVault.Name)`": $notePath" -Data @{ vault = $resolvedVault.Name; path = $notePath })
    }

    # --- Operand shape. edit is note-semantic. The folder probe runs before the extension rule
    # so an existing folder is named as a folder rather than suggested a ".md" sibling that is
    # not where its folder note lives; an extensionless path that resolves to nothing is
    # invalid_argument naming the ".md" form, so the miss is loud and retryable in one call
    # rather than an ambiguous not_found (Tool-Contracts: Path Convention). ---
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        throw (New-VaultToolError -Code 'kind_mismatch' -Message "Path is a folder, not a note: $notePath" -Data @{ requiredKind = 'note'; resolvedKind = 'folder' })
    }
    $noteExtension = [System.IO.Path]::GetExtension(($notePath -split '/')[-1])
    if ([string]::IsNullOrEmpty($noteExtension)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "path has no extension and edit never infers one: retry with '$notePath.md' if that is the note you mean." -Data @{ path = $notePath; suggestedPath = "$notePath.md" })
    }
    if ($noteExtension -ine '.md' -and (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw (New-VaultToolError -Code 'kind_mismatch' -Message "Path is not a markdown note: $notePath" -Data @{ requiredKind = 'note'; resolvedKind = 'file' })
    }

    # --- The read. Classified off the error record, never a Test-Path probe (racy, and a
    # dangling symlink satisfies -PathType Leaf while being unreadable): nothing to read is
    # not_found, present-but-unreadable is read_failure. A missing sole target is a real error
    # and is never skipped. The bytes are read alongside the text because the staleness token is
    # a digest of the file exactly as stored. ---
    try {
        $fileBytes = [System.IO.File]::ReadAllBytes($fullPath)
        $content = Read-VaultNote -Path $notePath -Vault $Vault
    }
    catch {
        if (Test-VaultNoteMissingError -ErrorRecord $_) {
            throw (New-VaultToolError -Code 'not_found' -Message "No note at path: $notePath" -Data @{ path = $notePath })
        }
        throw (New-VaultToolError -Code 'read_failure' -Message "Note exists but could not be read: $($_.Exception.Message)" -Data @{ path = $notePath; error = $_.Exception.Message })
    }
    if ($null -eq $content) { $content = '' }

    # --- Staleness guard, in the validate phase so a stale write mutates nothing. The data
    # diagnoses and deliberately does not invite a retry with the actual digest - that would
    # clobber without re-reading. Recovery is a re-read. ---
    if ($ExpectedShaBound) {
        $actualSha = Get-VaultEditSha -Bytes $fileBytes
        if ($ExpectedSha.Trim().ToLowerInvariant() -cne $actualSha) {
            throw (New-VaultToolError -Code 'state_mismatch' -Message "The note changed since expected_sha was taken - read it again before editing: $notePath" -Data @{ expectedSha = $ExpectedSha.Trim().ToLowerInvariant(); actualSha = $actualSha; path = $notePath })
        }
    }

    # --- Facet split. An unclosed fence is refused before the patch runs rather than after: with
    # a dangling fence the body facet is the WHOLE file, so patch matching would reach the
    # frontmatter the body-confinement guard exists to keep it out of. The wrapper refuses the
    # same state, but only once the patch has already been resolved against it. ---
    $split = Split-VaultNoteContent -Content $content
    if ($split.FenceState -eq 'Unclosed') {
        throw (New-VaultToolError -Code 'frontmatter_integrity' -Message "The note opens a `"---`" frontmatter fence that is never closed; it cannot be edited until the fence is repaired: $notePath" -Data @{ reason = 'unclosed_fence'; path = $notePath })
    }

    $writeArgs = @{ Content = $content; Ops = $ops; RelativePath = $notePath }
    if ($BodyBound) {
        $writeArgs['Body'] = $Body
    }
    elseif ($hasPatch) {
        # Body-confined and exactly-once. Matching the body facet alone is what makes frontmatter
        # unreachable by patch, so every frontmatter mutation flows through the schema-validated,
        # capability-classified ops path. The patched body then goes to the wrapper like any
        # other body facet, so the fence-introduction guard still catches a fence a patch created.
        $probe = Measure-VaultEditPatchMatch -Text $split.Body -Anchor $OldString
        if ($probe.Count -eq 0) {
            throw (New-VaultToolError -Code 'patch_target_not_found' -Message 'old_string does not appear in the note body - the view it was taken from may be stale, or the anchor may span the frontmatter, which a patch cannot reach.' -Data @{ path = $notePath; oldString = $(if ($OldString.Length -gt 200) { $OldString.Substring(0, 200) } else { $OldString }) })
        }
        if ($probe.Count -gt 1) {
            throw (New-VaultToolError -Code 'patch_ambiguous' -Message 'old_string matches the note body more than once - widen the anchor with surrounding context until it is unique.' -Data @{ path = $notePath; matchCount = $probe.Count })
        }
        $writeArgs['Body'] = $split.Body.Substring(0, $probe.Index) + $NewString + $split.Body.Substring($probe.Index + $OldString.Length)
    }

    # --- The single write path into frontmatter: composes the ops, runs the fence-introduction
    # guard and scalar hygiene, validates against the vault schema, diffs the agent.*/nav.* keys,
    # and asserts agentic:write when that diff is non-empty. Integrity, hygiene, and the
    # capability assertion are its throws; schema violations come back as a list because lint
    # needs them as data. edit is the consumer that treats a violation as a refusal. ---
    $write = Invoke-VaultFrontmatterWrite @writeArgs

    if ($write.Violations.Count -gt 0) {
        $first = $write.Violations[0]
        throw (New-VaultToolError -Code 'schema_validation_failed' -Message "The resulting frontmatter violates the agent.*/nav.* schema ($($write.Violations.Count) violation(s), first reported): $($first.Reason)" -Data @{
                field      = $first.Field
                entryIndex = $first.EntryIndex
                reason     = $first.Reason
                rule       = $first.Rule
            })
    }

    # =======================================================================
    # APPLY. Validation is complete. Every op applied to one in-memory string, so this is a
    # single write - there is no partial-op disk state for a single note by construction, which
    # is why edit has no partial_state code.
    # =======================================================================
    Write-VaultNote -Path $notePath -Content $write.Content -Vault $Vault

    # Digested from the bytes on disk rather than from the composed string, so resultingSha is
    # the token a caller can hand straight back as the next edit's expected_sha and a chain of
    # edits needs no interleaved read.
    $resultingSha = Get-VaultEditSha -Bytes ([System.IO.File]::ReadAllBytes($fullPath))

    # resultingFrontmatter is contracted to match `read part: frontmatter` byte for byte, and it
    # keeps that promise by delegating to the same authority read does: the substrate's
    # Split-VaultNoteContent, whose .Frontmatter is the fenced block's inner text and '' for a
    # note with no closed fence. Re-split from $write.Content rather than reusing the pre-op
    # $split, because the ops are exactly what moved the block.
    $resultingFrontmatter = (Split-VaultNoteContent -Content $write.Content).Frontmatter

    $opsApplied = @(foreach ($record in $write.OpsApplied) {
            $entry = [ordered]@{ op = $record.Op }
            # field and kind are per-op optional: emitted only where the op has one, so a caller
            # never reads a null it has to interpret.
            if (-not [string]::IsNullOrEmpty($record.Field)) { $entry['field'] = $record.Field }
            if (-not [string]::IsNullOrEmpty($record.Kind)) { $entry['kind'] = $record.Kind }
            # changed:false records the substrate's idempotent no-ops - a duplicate
            # add_directive, an absent-field remove, an unmatched pinned strip.
            $entry['changed'] = [bool]$record.Changed
            $entry
        })

    [ordered]@{
        path                   = $notePath
        opsApplied             = $opsApplied
        frontmatterChanged     = [bool]$write.FrontmatterChanged
        bodyChanged            = [bool]$write.BodyChanged
        resultingFrontmatter   = $resultingFrontmatter
        resultingSha           = $resultingSha
        # Write-class envelope: always present, [] when empty. filesSkippedUnreadable can never
        # be populated here - edit is single-target, so a missing or unreadable sole target is
        # not_found / read_failure, never a skip - but the caller still reads one shape.
        capabilitiesUsed       = @($write.CapabilitiesUsed)
        filesSkippedUnreadable = @()
    }
}

# lockWarning is present ONLY when the write inherited an abandoned mutex and is omitted entirely
# otherwise - the envelope's deliberate exception to always-present (Tool-Contracts: Return
# Convention): it is a warning, not a status field, and an always-present null would read as a
# checked-and-clear signal.
if ($lockAbandoned) {
    $result['lockWarning'] = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
