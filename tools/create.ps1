<#
.SYNOPSIS
Brings one new item into the vault in a single write-locked operation: a markdown note
(caller-supplied frontmatter plus body) or a bare directory. The target must NOT already exist -
the mirror image of edit's exists-contract, and the reason there is no expected_sha parameter
here (there is no prior state to assert; already_exists is the guard). See the Create corpus doc
(vault-primary) for the full contract.
.PARAMETER Kind
Required discriminator over two disjoint parameter surfaces - unlike the intent-assertion `kind`
on other tools, this one does real work, so it is mandatory. Members:
  note   - creates a markdown note from Frontmatter + Body. Path must end in ".md". A missing
           parent directory is refused (parent_not_found); use kind "folder" first.
  folder - creates a bare directory, intermediates included (mkdir-p). Frontmatter and Body are
           inert for this kind and supplying either is invalid_argument.
Matched case-insensitively after a trim. Deliberately not a ValidateSet, so every value reaches
the script body and an unrecognized one is refused with create's own invalid_argument JSON error
rather than a PowerShell binding error.
.PARAMETER Path
Vault-relative path of the item to create. Constructed, not resolved: it names something that
does not yet exist, so the resolve-to-exactly-one rule does not apply. Separators fold to "/" and
a leading "/" (vault-root spelling) is trimmed; nothing else is inferred. ".md" is REQUIRED for
kind "note" and is never appended - an extensionless or non-".md" note path is invalid_argument
naming the ".md" form, so create and a following edit can never disagree about what the operand
was. Link-breaking ("[", "]", "#", "^", "|") and OS-invalid characters in the basename are
refused; a basename that does not follow Title-Case-With-Hyphens is allowed and reported through
namingWarnings.
.PARAMETER Frontmatter
kind "note" only, and required for it: the caller-supplied frontmatter block, accepted either
already fenced or as bare inner lines, and empty for a note deliberately carrying no block. An
opening fence that never closes, or content trailing the closing fence, is frontmatter_integrity.
The block is schema-validated through the shared VaultAccess validator; a violation is
schema_validation_failed. Keys under the agent.* / nav.* prefixes assert the agentic:write op
class (see .NOTES). There is no server-side templating: a template is read with `read` and filled
in by the caller, which is why this parameter takes finished text.
.PARAMETER Body
kind "note" only: the note's body, everything after the frontmatter block's closing fence.
Defaults to empty. Body text that opens its own "---" fence is refused frontmatter_integrity - a
frontmatter block can only be authored through Frontmatter, where it is validated and classified.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:write

create asserts agentic:write in-script when the supplied frontmatter carries any agent.* / nav.*
key. That is the degenerate case of edit's outcome-based classification rather than a second
rule: with no original to diff against, every agentic key present counts as a change, and it
falls out of the shared wrapper's diff with no branch here. Classification is validate-phase, so
a denial is insufficient_capability with zero disk change. kind "folder" never asserts the key.
Standalone (no injected class set) degrades open; an injected-but-empty set denies.

Runs under the per-vault write lock as validate -> apply, with the existence guard inside the
lock so the check and the write cannot be interleaved by another writer. There is no `apply`
parameter: create is single-target and takes caller-supplied content, so a preview would echo the
request back (Tool-Contracts: Assess-Before-Mutate Convention). The return carries the write-class
envelope: capabilitiesUsed and filesSkippedUnreadable[] always ([] when empty), lockWarning only
when an abandoned mutex was inherited. filesSkippedUnreadable is structurally always [] - create
is single-target and reads nothing it could skip.
#>
param(
    [Parameter(Mandatory)] [string] $Kind,
    [Parameter(Mandatory)] [string] $Path,
    [string] $Frontmatter,
    [string] $Body,
    [string] $Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# Parameter presence is decided here, in script scope, from $PSBoundParameters - the only way to
# tell an unsupplied -Frontmatter (missing, and required for kind note) from -Frontmatter ''
# (a note deliberately carrying no block). A parameter bound to an explicit JSON null counts as
# unsupplied: the dispatcher converts JSON null to $null, and "present but null" is not a value
# the caller asked to write.
$script:FrontmatterBound = $PSBoundParameters.ContainsKey('Frontmatter') -and $null -ne $Frontmatter
$script:BodyBound = $PSBoundParameters.ContainsKey('Body') -and $null -ne $Body

# Link-breaking characters Obsidian itself forbids in filenames ([, ], #, ^, |) plus
# Windows-invalid filename characters. Checked against the basename only - never the full path,
# which legitimately contains "/".
$script:InvalidBasenameCharsPattern = '[\[\]#\^\|<>:"/\\\?\*\x00-\x1F]'

function Test-VaultCreateBasenameValid {
    <#
    .SYNOPSIS
    True when $Basename contains none of the link-breaking or OS-invalid characters and does not
    end in a dot or space (both invalid trailing characters on Windows).
    #>
    param([Parameter(Mandatory)] [string] $Basename)
    (-not ($Basename -match $script:InvalidBasenameCharsPattern)) -and ($Basename -notmatch '[. ]$')
}

function Get-VaultCreateSha {
    <#
    .SYNOPSIS
    SHA-256 over a file's raw bytes as lowercase hex - byte-identical to what `read part: sha`
    and edit's resultingSha return, which is what lets a caller chain straight from a create into
    an edit's expected_sha without a read. Takes bytes rather than a path or a string so no
    decoding, newline translation, or encoding choice can enter the digest.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]] $Bytes)

    [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($Bytes)).Replace('-', '').ToLowerInvariant()
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
    # VALIDATE. Argument shape, then the constructed path's shape, then the target's state -
    # every invariant checked before anything is brought into existence. The existence guard
    # runs inside the lock, so no other writer can occupy the target between the check and the
    # write.
    # =======================================================================

    # --- kind: the discriminator, so it is validated first - every later rule is conditional on
    # it. Matched case-insensitively after a trim; an unrecognized value is create's own coded
    # refusal rather than a binding error. ---
    $normalizedKind = $Kind.Trim().ToLowerInvariant()
    if ($normalizedKind -notin @('note', 'folder')) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "kind '$Kind' is not a recognized value - create accepts 'note' or 'folder'. Project and area scaffolding is composed from folder and note creates, not a kind of its own." -Data @{ kind = $Kind })
    }

    # --- Conditional requiredness and inert-param fail-fast. The derived MCP schema cannot
    # express either (Tool-Contracts: Argument Convention), and an inert parameter is refused
    # rather than ignored: silently dropping a body the caller supplied is how content goes
    # missing without a signal. ---
    if ($normalizedKind -eq 'folder') {
        $inertParam = if ($script:FrontmatterBound) { 'frontmatter' } elseif ($script:BodyBound) { 'body' } else { '' }
        if ($inertParam) {
            throw (New-VaultToolError -Code 'invalid_argument' -Message "$inertParam is inert for kind 'folder' - a folder create makes a bare directory and takes no content. Create the folder, then create the note inside it." -Data @{ kind = 'folder'; param = $inertParam })
        }
    }
    else {
        if (-not $script:FrontmatterBound) {
            throw (New-VaultToolError -Code 'invalid_argument' -Message "frontmatter is required for kind 'note' - pass an empty string for a note deliberately carrying no frontmatter block." -Data @{ kind = 'note'; param = 'frontmatter' })
        }
    }

    # --- Path construction (move's dest posture): separators fold to "/", a leading "/" (vault
    # root spelling) is trimmed. Verbatim beyond that - nothing is inferred or appended. The
    # path names something that does not yet exist, so this never resolves it to a candidate. ---
    $targetPath = $Path.Trim().Replace('\', '/').Trim('/')
    if ([string]::IsNullOrWhiteSpace($targetPath)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'path is empty.' -Data @{ path = $Path })
    }
    $basename = ($targetPath -split '/')[-1]

    # --- Link-breaking / OS-invalid characters in the basename. Unlike a naming-convention
    # violation, these block outright: a "#" or "|" in a filename cannot be linked to at all. ---
    if (-not (Test-VaultCreateBasenameValid -Basename $basename)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "path basename contains a link-breaking or OS-invalid character: $basename" -Data @{ path = $targetPath; basename = $basename })
    }

    # --- ".md" is required for kind note and is NEVER inferred (Tool-Contracts: Path Convention).
    # The message names the ".md" form so the miss is loud and retryable in one call, and so a
    # create and a following edit can never disagree about what the operand was. A non-".md"
    # extension is refused for the same reason rather than converted: create is note-semantic and
    # produces content, so it gates convention. ---
    if ($normalizedKind -eq 'note') {
        $extension = [System.IO.Path]::GetExtension($basename)
        if ($extension -ine '.md') {
            $suggested = if ([string]::IsNullOrEmpty($extension)) { "$targetPath.md" } else { [System.IO.Path]::ChangeExtension($targetPath, '.md') }
            $has = if ([string]::IsNullOrEmpty($extension)) { 'no extension' } else { "extension '$extension'" }
            throw (New-VaultToolError -Code 'invalid_argument' -Message "kind 'note' requires a '.md' path and create never infers one: '$basename' has $has. Retry with '$suggested'." -Data @{ path = $targetPath; suggestedPath = $suggested })
        }
    }

    # --- A rooted or vault-escaping path is a malformed operand, not a miss. ---
    try {
        $fullPath = Resolve-VaultPath -Path $targetPath -Vault $Vault
    }
    catch {
        throw (New-VaultToolError -Code 'invalid_argument' -Message $_.Exception.Message -Data @{ path = $targetPath })
    }

    # --- Scope membership. MANDATORY here and the only thing that produces out_of_scope: the
    # Write-VaultNote chokepoint calls Assert-VaultOperationAllowed, which throws a PLAIN STRING
    # and is deliberately not one of the coded chokepoints - and kind 'folder' does not reach
    # that chokepoint at all, so for a folder create this check is the whole of scope
    # enforcement. Absent or empty roots degrade open (a standalone run injects no
    # VaultMcpScopeRoots at all); read through Get-Variable because under Set-StrictMode an unset
    # global is an error, and standalone is exactly the case where it is unset. ---
    $ambientScopeRoots = Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolvedVault.Name -ScopeRoots $ambientScopeRoots
    if ($null -ne $flatScopeRoots -and $flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $targetPath -ScopeRoots $flatScopeRoots)) {
        throw (New-VaultToolError -Code 'out_of_scope' -Message "Path is outside the grant's scope for vault `"$($resolvedVault.Name)`": $targetPath" -Data @{ vault = $resolvedVault.Name; path = $targetPath })
    }

    # --- Naming-convention check: warn only, never fail the operation (move's posture). Only the
    # character check above blocks a name outright. ---
    $namingWarnings = @()
    if (-not (Test-VaultNameConvention -Name $basename)) {
        $namingWarnings += "Name '$basename' does not follow the vault's Title-Case-With-Hyphens naming convention."
    }

    # --- The existence guard, and create's whole contract: the target must not exist. Tested
    # without -PathType so a folder occupying a note's path (and the reverse) is reported as the
    # collision it is rather than falling through to a confusing write failure. ---
    if (Test-Path -LiteralPath $fullPath) {
        throw (New-VaultToolError -Code 'already_exists' -Message "Target already exists: $targetPath" -Data @{ path = $targetPath })
    }

    if ($normalizedKind -eq 'folder') {
        # --- mkdir-p. Every ancestor that does not yet exist is created along with the target and
        # reported in created[]: making folder structure exist is the whole job, so refusing
        # intermediates would only force N calls. The ancestor walk runs before any mutation, both
        # to compute created[] and to catch a path component that is a FILE - New-Item would fail
        # on it partway through, leaving some intermediates behind. ---
        $created = New-Object System.Collections.Generic.List[string]
        $accumulated = ''
        foreach ($segment in ($targetPath -split '/')) {
            $accumulated = if ($accumulated) { "$accumulated/$segment" } else { $segment }
            $accumulatedFull = Resolve-VaultPath -Path $accumulated -Vault $Vault
            if (Test-Path -LiteralPath $accumulatedFull -PathType Container) { continue }
            if (Test-Path -LiteralPath $accumulatedFull -PathType Leaf) {
                throw (New-VaultToolError -Code 'parent_not_found' -Message "Path component '$accumulated' is a file, not a folder, so '$targetPath' cannot be created under it." -Data @{ path = $targetPath; parent = $accumulated })
            }
            $created.Add($accumulated)
        }

        # =======================================================================
        # APPLY (folder). One mkdir-p; -Force creates the intermediates the walk above named.
        # =======================================================================
        New-Item -ItemType Directory -Path $fullPath -Force -ErrorAction Stop | Out-Null

        return [ordered]@{
            kind                   = 'folder'
            path                   = $targetPath
            created                = $created.ToArray()
            # A folder has no content to digest; null rather than an empty string so a caller
            # cannot mistake it for the hash of an empty file.
            sha                    = $null
            namingWarnings         = $namingWarnings
            # Write-class envelope: always present, [] when empty. A folder create never asserts
            # the agentic key - there is no frontmatter to classify.
            capabilitiesUsed       = @()
            filesSkippedUnreadable = @()
        }
    }

    # --- kind 'note'. The single write path into frontmatter: normalizes the supplied block,
    # runs the fence-introduction guard (which is what stops a body that opens its own "---" from
    # minting a block no validation saw), applies scalar hygiene, validates against the vault
    # schema, and diffs the agent.*/nav.* keys against the empty original - so every agentic key
    # present is a change and agentic:write is asserted here, in the validate phase, before
    # anything exists on disk. Integrity, hygiene, and the capability assertion are its throws;
    # schema violations come back as a list because lint needs them as data, and create (like
    # edit) treats a violation as a refusal. ---
    $bodyValue = if ($script:BodyBound) { $Body } else { '' }
    $write = Invoke-VaultFrontmatterWrite -Content '' -Frontmatter $Frontmatter -Body $bodyValue -RelativePath $targetPath

    if ($write.Violations.Count -gt 0) {
        $first = $write.Violations[0]
        throw (New-VaultToolError -Code 'schema_validation_failed' -Message "The supplied frontmatter violates the agent.*/nav.* schema ($($write.Violations.Count) violation(s), first reported): $($first.Reason)" -Data @{
                field      = $first.Field
                entryIndex = $first.EntryIndex
                reason     = $first.Reason
                rule       = $first.Rule
            })
    }

    # =======================================================================
    # APPLY (note). Validation is complete. One atomic write of one composed string, UTF-8 with
    # no BOM - so there is no partial state for a single note by construction, which is why
    # create has no partial_state code. A missing parent directory is refused STRUCTURALLY here:
    # Write-VaultNote never brings a directory into existence and raises parent_not_found itself,
    # so the refusal does not depend on create remembering to pre-check it. The code surfaces
    # unchanged - it is a coded chokepoint and is not caught and reshaped.
    # =======================================================================
    Write-VaultNote -Path $targetPath -Content $write.Content -Vault $Vault

    # Digested from the bytes on disk rather than from the composed string, so sha is the token a
    # caller hands straight to a following edit's expected_sha with no interleaved read.
    $sha = Get-VaultCreateSha -Bytes ([System.IO.File]::ReadAllBytes($fullPath))

    [ordered]@{
        kind                   = 'note'
        path                   = $targetPath
        # Single-target: a note create brings exactly one thing into existence, since a missing
        # parent is refused rather than created.
        created                = @($targetPath)
        sha                    = $sha
        namingWarnings         = $namingWarnings
        # Write-class envelope: always present, [] when empty. filesSkippedUnreadable can never
        # be populated here - create is single-target and reads nothing it could skip - but the
        # caller still reads one shape.
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
