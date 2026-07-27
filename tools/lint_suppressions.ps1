<#
.SYNOPSIS
Manages the lint suppression lists - the vault-lint state files under .vault/state/vault-lint/ that
silence a lint finding. Three lists, one CRUD surface: list, add, remove.

Call this when:
- a lint finding is a deliberate exception rather than a defect - a wikilink to a note that will
  never exist, a brand name that is not Title-Case, a subfolder a folder note should not list - and
  it should stay silent on every future run
- you are auditing what is already silenced, or retiring a suppression that has outlived its
  reason. Action "list" takes no lock and asserts no op class
- lint handed you an add-suppression remedy, with the entry and the list already computed

Not for FIXING a finding - that is lint with apply:true. Suppressing something you could fix buries
it permanently, which is why the mutations sit behind lint:manage rather than content:write: an
agent trusted to fix a curly quote is not thereby trusted to bury a finding forever. Not for
reaching the state files through a content primitive - edit is note-semantic and cannot write an
opaque ".txt" under .vault/state/, and create refuses a path that already exists - so this tool is
the only door. Its subject is one tool's configuration, not vault content.

See the Lint corpus doc (vault-primary) for the full contract.
.PARAMETER Action
What to do to the list. Members:
  list   - returns the list's current entries. Takes no lock and asserts no op class.
  add    - appends one entry. Asserts lint:manage.
  remove - deletes one entry. Asserts lint:manage.
There is no "update": entries are atomic values, so an update is a remove followed by an add.
Matched case-insensitively after a trim. Deliberately not a ValidateSet, so every value reaches
the script body and an unrecognized one is refused with this tool's own invalid_argument JSON
error rather than a PowerShell binding error.
.PARAMETER List
Which suppression list to operate on. Members:
  broken-wikilinks    - .vault/state/vault-lint/known-broken-wikilinks.txt. Suppresses
                        broken-wikilinks findings. SECTIONED: entries before any [path:...]
                        header are global; a [path:prefix] header scopes the entries under it to
                        files whose vault-relative path starts with that prefix. ScopePath
                        selects the section.
  ignored-subfolders  - .vault/state/vault-lint/known-ignored-subfolders.txt. Suppresses
                        unreferenced-subfolder findings in the folder-note completeness scan.
  filename-exceptions - .vault/state/vault-lint/filename-exceptions.txt. Suppresses Title-Case
                        findings for brand names and literal extension references.
Matched case-insensitively after a trim; deliberately not a ValidateSet, same reasoning as Action.
.PARAMETER Entry
The suppression entry to add or remove - required for those actions and inert for "list"
(supplying it there is invalid_argument rather than silently ignored). One line's worth of
value: it is trimmed, and a value carrying a newline, a "#" (which would start a comment and
truncate it on the next read), or the [path:...] section-header spelling is refused, since any
of the three would mean the file no longer round-trips what was written. Matching against
existing entries is case-INSENSITIVE, which is how all three consuming checks match.
.PARAMETER ScopePath
broken-wikilinks only - any other list is invalid_argument. Vault-relative path prefix naming a
[path:...] section; omitted means the global (pre-header) section. Separators fold to "/" and a
leading or trailing "/" is trimmed. On "add" a named section that does not exist yet is CREATED,
appended at the end of the file - there is no separate create-section action, so an add is the
only way a section comes into being. On "list" and "remove" a section that does not exist is
not_found, so "the section you named is not there" is one answer rather than an empty result a
caller could read as an existing-but-empty section. Global and scoped namespaces are distinct: a
target may be listed globally AND under a section without either being a duplicate.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:read

The floor is content:read because listing suppressions is a read. "add" and "remove" assert the
lint:manage key in-script via Assert-VaultOpClass, before the first byte is written, so a denial
is insufficient_capability with zero mutation. lint:manage rather than content:write is the
point of the class: a suppression add is how an unattended agent silences its own findings, so
the mutation is gated by the class naming exactly that authority - an agent trusted to fix a
curly quote is not thereby trusted to bury a finding forever. Standalone (no injected class set)
degrades open; an injected-but-empty set denies.

NOT scope-bound, deliberately. Suppressions are vault state, not vault content - the same
posture sync takes for its git classes - so no entry and no state-file path is checked against
the grant's scopeRoots. Gating suppression management by scope roots would make suppressions
unmanageable exactly for the constrained agents they exist to serve.

Mutations run under the per-vault write lock as validate -> apply, with the read of the existing
file inside the lock so the duplicate/absence check and the rewrite cannot be interleaved by
another writer. "list" takes no lock: it neither mutates nor depends on a state a concurrent
write could make its answer wrong about. There is no `apply` parameter - a mutation is one
atomic value against one named list, so a preview would echo the request back (Tool-Contracts:
Assess-Before-Mutate Convention).

Every return carries the write-class envelope - capabilitiesUsed and filesSkippedUnreadable[]
always ([] when empty), lockWarning only when an abandoned mutex was inherited. That includes
"list": this is one tool with one return convention (Tier 2), and teaching callers which action
carries which fields is the cost the envelope exists to avoid. filesSkippedUnreadable is
structurally always [] - the tool reads one named file and a missing one is not a skip.

Rewrites preserve the file byte-for-byte outside the touched line: each line keeps its own
original terminator, comments and blank lines are untouched, and the file is written UTF-8 with
no BOM. A file that does not exist yet is created by "add" (with its parent directory) and reads
as empty for "list".
#>
param(
    [Parameter(Mandatory)] [string] $Action,
    [Parameter(Mandatory)] [string] $List,
    [string] $Entry,
    [string] $ScopePath,
    [string] $Vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# Parameter presence is decided here, in script scope, from $PSBoundParameters - the only way to
# tell an unsupplied -Entry from -Entry '' (which is a refusal, not an absence). A parameter
# bound to an explicit JSON null counts as unsupplied: the dispatcher converts JSON null to
# $null, and "present but null" is not a value the caller asked to write.
$script:EntryBound = $PSBoundParameters.ContainsKey('Entry') -and $null -ne $Entry
$script:ScopePathBound = $PSBoundParameters.ContainsKey('ScopePath') -and $null -ne $ScopePath

# list name -> file under .vault/state/vault-lint/, plus whether that file carries [path:...]
# sections. Sectioned is a property of the file format, not of the tool, so ScopePath's
# admissibility is read from here rather than from a name comparison scattered through the body.
$script:ListFiles = @{
    'broken-wikilinks'    = @{ File = 'known-broken-wikilinks.txt'; Sectioned = $true }
    'ignored-subfolders'  = @{ File = 'known-ignored-subfolders.txt'; Sectioned = $false }
    'filename-exceptions' = @{ File = 'filename-exceptions.txt'; Sectioned = $false }
}

$script:StateFolder = '.vault/state/vault-lint'

# A [path:prefix] section header, the one line shape that is structure rather than an entry.
$script:SectionHeaderPattern = '^\s*\[path:(.+)\]\s*$'

function ConvertTo-VaultSuppressionLines {
    <#
    .SYNOPSIS
    Splits raw file text into per-line records that carry their OWN terminator, so a rewrite can
    reproduce every untouched line byte-for-byte - including a file with mixed or absent
    terminators. A plain -split plus a join would normalize the whole file to one convention,
    which is a reformat of lines the caller never asked to touch.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)

    $lines = [System.Collections.Generic.List[hashtable]]::new()
    # Every return is comma-wrapped: a List written to the output stream is unrolled, and an
    # empty one would come back as nothing at all rather than as an empty list.
    if ($Text.Length -eq 0) { return , $lines }

    # Split keeping the separators: parts alternate content, separator, content, separator, ...
    # and always end with a content element (empty when the text ended with a terminator).
    $parts = [regex]::Split($Text, '(\r\n|\n|\r)')
    for ($i = 0; $i -lt $parts.Count; $i += 2) {
        $eol = if (($i + 1) -lt $parts.Count) { $parts[$i + 1] } else { '' }
        # A trailing empty content element after the final terminator is not a line - it is the
        # end of the file. Dropping it here is what makes a rewrite preserve (rather than double)
        # the final newline.
        if ($eol -eq '' -and $parts[$i] -eq '' -and $i -gt 0) { continue }
        $lines.Add(@{ Text = $parts[$i]; Eol = $eol })
    }
    , $lines
}

function ConvertFrom-VaultSuppressionLines {
    <#
    .SYNOPSIS
    Reassembles the per-line records into the exact text to write back.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] $Lines)

    $sb = [System.Text.StringBuilder]::new()
    foreach ($line in $Lines) {
        [void]$sb.Append($line.Text).Append($line.Eol)
    }
    $sb.ToString()
}

function Get-VaultSuppressionEntryText {
    <#
    .SYNOPSIS
    The entry value a line carries, or '' for a blank line, a comment line, or a section header.
    Mirrors how the consuming checks read these files: everything from a '#' onward is a trailing
    comment, and what remains is trimmed.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $LineText)

    if ($LineText -match $script:SectionHeaderPattern) { return '' }
    ($LineText -split '#', 2)[0].Trim()
}

function ConvertTo-VaultSuppressionScope {
    <#
    .SYNOPSIS
    Normalizes a section prefix the same way check-broken-wikilinks.ps1 does when it reads a
    [path:...] header - separators folded to "/", surrounding slashes trimmed - so a scope
    written one way and named another still matches.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Prefix)

    $Prefix.Trim().Replace('\', '/').Trim('/')
}

function Get-VaultSuppressionDefaultEol {
    <#
    .SYNOPSIS
    The terminator a newly inserted line takes: the file's own first terminator, so an insert
    into a CRLF file stays CRLF. Falls back to "`n" for a file with no terminator at all (a
    single unterminated line, or a file being created).
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] $Lines)

    foreach ($line in $Lines) {
        if ($line.Eol -ne '') { return $line.Eol }
    }
    "`n"
}

# Vault resolution before the write lock: Invoke-VaultWriteLock derives its mutex name from the
# resolved root, so it resolves the vault itself and would raise the mirrored core's plain text
# before the locked scriptblock ever runs. Resolving here refuses an unconfigured name as
# unknown_vault and a configured-but-root-missing vault as vault_unavailable (Tool-Contracts:
# Error Convention), with nothing locked.
$resolvedVault = Resolve-VaultToolRoot -Vault $Vault

# =======================================================================
# VALIDATE (argument shape). Runs OUTSIDE the lock and before any file is touched: none of it
# reads vault state, so locking to reject a misspelled action would be ceremony. The state-
# dependent checks - duplicate, absence, missing section - all run inside the lock below.
# =======================================================================

# --- action: the discriminator, so it is validated first - whether Entry is required and
# whether a lock and a key are needed are all conditional on it. ---
$normalizedAction = $Action.Trim().ToLowerInvariant()
if ($normalizedAction -notin @('list', 'add', 'remove')) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "action '$Action' is not a recognized value - lint_suppressions accepts 'list', 'add', or 'remove'. An update is a remove followed by an add: entries are atomic values." -Data @{ action = $Action })
}

$normalizedList = $List.Trim().ToLowerInvariant()
if (-not $script:ListFiles.ContainsKey($normalizedList)) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "list '$List' is not a recognized value - lint_suppressions manages 'broken-wikilinks', 'ignored-subfolders', and 'filename-exceptions'." -Data @{ list = $List })
}
$listSpec = $script:ListFiles[$normalizedList]

# --- ScopePath is a property of the sectioned file format, so it is refused rather than ignored
# on a flat list: silently dropping it would let a caller believe an entry was scoped when it
# was written globally, which is precisely the mistake path-scoped sections exist to prevent. ---
if ($script:ScopePathBound -and -not $listSpec.Sectioned) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message "scope_path applies only to the sectioned 'broken-wikilinks' list - '$normalizedList' has no [path:...] sections." -Data @{ list = $normalizedList; scopePath = $ScopePath })
}
$normalizedScope = if ($script:ScopePathBound) { ConvertTo-VaultSuppressionScope -Prefix $ScopePath } else { $null }
if ($script:ScopePathBound -and [string]::IsNullOrEmpty($normalizedScope)) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message 'scope_path is empty - omit it to target the global section.' -Data @{ scopePath = $ScopePath })
}

# --- Conditional requiredness and the inert-parameter refusal, neither expressible in the
# derived MCP schema (Tool-Contracts: Argument Convention). ---
$normalizedEntry = if ($script:EntryBound) { $Entry.Trim() } else { '' }
if ($normalizedAction -eq 'list') {
    if ($script:EntryBound) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "entry is inert for action 'list' - a list returns every entry. Use 'add' or 'remove' to name one." -Data @{ action = 'list'; param = 'entry' })
    }
}
else {
    if (-not $script:EntryBound -or [string]::IsNullOrEmpty($normalizedEntry)) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "entry is required for action '$normalizedAction'." -Data @{ action = $normalizedAction; param = 'entry' })
    }
    # --- Characters that would stop the file round-tripping what was written. A newline would
    # split one entry into two; a '#' would be read back as a trailing comment and truncate the
    # entry; the [path:...] spelling would be read back as a section header rather than an
    # entry. All three are refusals rather than escapes: these files are line-oriented by
    # design and there is no escaping convention to add one to. ---
    if ($normalizedEntry -match '[\r\n]') {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'entry contains a line break - a suppression entry is one line.' -Data @{ entry = $Entry })
    }
    if ($normalizedEntry.Contains('#')) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "entry contains '#', which these files read as the start of a trailing comment - the entry would be truncated on the next read." -Data @{ entry = $Entry })
    }
    if ($normalizedEntry -match $script:SectionHeaderPattern) {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'entry has the [path:...] section-header spelling and would be read back as a section rather than an entry - pass it as scope_path instead.' -Data @{ entry = $Entry })
    }
}

$script:StateFileRelative = "$script:StateFolder/$($listSpec.File)"
$script:StateFileFull = Join-Path $resolvedVault.Root ($script:StateFileRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))

function Read-VaultSuppressionFile {
    <#
    .SYNOPSIS
    The state file's per-line records, empty for a file that does not exist yet. Read through
    ReadAllText with the standard UTF8 encoding, which accepts a file with or without a BOM;
    only writes pin no-BOM.
    #>
    if (-not (Test-Path -LiteralPath $script:StateFileFull -PathType Leaf)) {
        $empty = ConvertTo-VaultSuppressionLines -Text ''
        return , $empty
    }
    $read = ConvertTo-VaultSuppressionLines -Text ([System.IO.File]::ReadAllText($script:StateFileFull, [System.Text.Encoding]::UTF8))
    , $read
}

function Get-VaultSuppressionSectionMap {
    <#
    .SYNOPSIS
    Walks the line records once and returns, per line index, the section that line belongs to -
    $null for the global (pre-header) region, otherwise the normalized prefix. One walk is what
    keeps the global/scoped namespaces genuinely distinct: every membership question below is
    answered against this map rather than against a re-parse that could disagree with it.
    Also returns the set of sections that exist and, per section, the index its entries end at,
    which is where an insert goes.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] $Lines)

    $lineScope = [System.Collections.Generic.List[object]]::new()
    $sectionEnd = @{}          # scope key ('' for global) -> index one past its last line
    $sections = [System.Collections.Generic.List[string]]::new()
    $current = $null

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Text -match $script:SectionHeaderPattern) {
            $current = ConvertTo-VaultSuppressionScope -Prefix $Matches[1]
            if ($sections -notcontains $current) { $sections.Add($current) }
        }
        $lineScope.Add($current)
        $key = if ($null -eq $current) { '' } else { $current }
        $sectionEnd[$key] = $i + 1
    }
    if (-not $sectionEnd.ContainsKey('')) { $sectionEnd[''] = 0 }

    @{ LineScope = $lineScope; SectionEnd = $sectionEnd; Sections = $sections }
}

function Test-VaultSuppressionScopeMatch {
    <#
    .SYNOPSIS
    True when a line's section equals the targeted scope. Section names are paths, so they match
    case-insensitively - the same rule check-broken-wikilinks.ps1 applies when it tests a file's
    path against a section prefix.
    #>
    param($LineScope, $TargetScope)

    if ($null -eq $TargetScope) { return $null -eq $LineScope }
    ($null -ne $LineScope) -and ($LineScope -ieq $TargetScope)
}

if ($normalizedAction -eq 'list') {
    # --- No lock and no key assertion: listing is a read of one file, gated by the tool's
    # content:read floor alone. ---
    $lines = Read-VaultSuppressionFile
    $map = Get-VaultSuppressionSectionMap -Lines $lines

    if ($script:ScopePathBound -and ($map.Sections -notcontains $normalizedScope)) {
        # Named-section semantics are operate-on rather than query: the caller asked about one
        # section, so its absence is not_found rather than an empty result the caller could read
        # as an existing-but-empty section.
        throw (New-VaultToolError -Code 'not_found' -Message "No [path:$normalizedScope] section in $script:StateFileRelative." -Data @{ list = $normalizedList; scopePath = $normalizedScope })
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $text = Get-VaultSuppressionEntryText -LineText $lines[$i].Text
        if ($text -eq '') { continue }
        $scope = $map.LineScope[$i]
        if ($script:ScopePathBound -and -not (Test-VaultSuppressionScopeMatch -LineScope $scope -TargetScope $normalizedScope)) { continue }
        if ($listSpec.Sectioned) {
            # scope is carried only by the sectioned list, and is null for a global entry - the
            # concept does not exist on a flat list, so the field does not appear there either.
            $entries.Add([ordered]@{ entry = $text; scope = $scope })
        }
        else {
            $entries.Add([ordered]@{ entry = $text })
        }
    }

    ConvertTo-VaultToolResult -Result ([ordered]@{
            list                   = $normalizedList
            entries                = $entries.ToArray()
            # Write-class envelope on a read-shaped action too: one tool, one return convention.
            capabilitiesUsed       = @()
            filesSkippedUnreadable = @()
        })
    return
}

$lockAbandoned = $false
$result = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$lockAbandoned) -ScriptBlock {
    # Required: the script-scope preference does not reach cmdlets invoked from inside a
    # module-function scriptblock, and without it a failed mutation is non-terminating and gets
    # reported as success.
    $ErrorActionPreference = 'Stop'

    # =======================================================================
    # VALIDATE (state). The capability assertion comes first, so a caller without the key is
    # refused before the tool has even read the list - classification is validate-phase and a
    # denial leaves zero mutation. Then the file's own state: the section must exist (remove),
    # and the entry must be absent (add) or present (remove) within the targeted scope.
    # =======================================================================
    Assert-VaultOpClass -OpClass 'lint:manage' -Data @{ requiredClass = 'lint:manage'; action = $normalizedAction; list = $normalizedList }

    $lines = Read-VaultSuppressionFile
    $map = Get-VaultSuppressionSectionMap -Lines $lines
    $sectionExists = (-not $script:ScopePathBound) -or ($map.Sections -contains $normalizedScope)

    if ($normalizedAction -eq 'remove' -and -not $sectionExists) {
        throw (New-VaultToolError -Code 'not_found' -Message "No [path:$normalizedScope] section in $script:StateFileRelative." -Data @{ list = $normalizedList; scopePath = $normalizedScope })
    }

    # Membership is answered within the targeted scope only. Global and scoped sections are
    # distinct namespaces: the same target may be suppressed globally AND under a section, and
    # neither is a duplicate of the other, because they suppress different things.
    $matchIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (-not (Test-VaultSuppressionScopeMatch -LineScope $map.LineScope[$i] -TargetScope $normalizedScope)) { continue }
        $text = Get-VaultSuppressionEntryText -LineText $lines[$i].Text
        # Case-insensitive, matching how all three consuming checks compare.
        if ($text -ne '' -and $text -ieq $normalizedEntry) { $matchIndex = $i; break }
    }

    $scopeLabel = if ($script:ScopePathBound) { "the [path:$normalizedScope] section of" } else { 'the global section of' }
    if ($normalizedAction -eq 'add' -and $matchIndex -ge 0) {
        throw (New-VaultToolError -Code 'already_exists' -Message "'$normalizedEntry' is already in $scopeLabel $script:StateFileRelative." -Data @{ list = $normalizedList; entry = $normalizedEntry; scopePath = $normalizedScope })
    }
    if ($normalizedAction -eq 'remove' -and $matchIndex -lt 0) {
        throw (New-VaultToolError -Code 'not_found' -Message "'$normalizedEntry' is not in $scopeLabel $script:StateFileRelative." -Data @{ list = $normalizedList; entry = $normalizedEntry; scopePath = $normalizedScope })
    }

    # =======================================================================
    # APPLY. One line is inserted or deleted and every other line - comments, blank lines,
    # section headers, and its own terminator - is carried through untouched.
    # =======================================================================
    $defaultEol = Get-VaultSuppressionDefaultEol -Lines $lines
    $sectionCreated = $false

    if ($normalizedAction -eq 'remove') {
        $lines.RemoveAt($matchIndex)
    }
    elseif ($script:ScopePathBound -and -not $sectionExists) {
        # A named section that does not exist is created by the add, appended at the end of the
        # file: there is no create-section action, so an add is the only way a section comes into
        # being. Appending (rather than inserting) keeps every existing section's contents where
        # they are, and a header at the end scopes only what follows it.
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Eol -eq '') { $lines[$lines.Count - 1].Eol = $defaultEol }
        $lines.Add(@{ Text = "[path:$normalizedScope]"; Eol = $defaultEol })
        $lines.Add(@{ Text = $normalizedEntry; Eol = $defaultEol })
        $sectionCreated = $true
    }
    else {
        # Insert at the end of the targeted section, which for the global section means before
        # the first [path:...] header - an entry appended past a header would silently become
        # part of that section instead.
        $key = if ($null -eq $normalizedScope) { '' } else { $normalizedScope }
        $insertAt = if ($map.SectionEnd.ContainsKey($key)) { $map.SectionEnd[$key] } else { $lines.Count }
        if ($insertAt -ge $lines.Count) {
            if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Eol -eq '') { $lines[$lines.Count - 1].Eol = $defaultEol }
            $lines.Add(@{ Text = $normalizedEntry; Eol = $defaultEol })
        }
        else {
            $lines.Insert($insertAt, @{ Text = $normalizedEntry; Eol = $defaultEol })
        }
    }

    $stateDirectory = Split-Path -Parent $script:StateFileFull
    if (-not (Test-Path -LiteralPath $stateDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $stateDirectory -Force -ErrorAction Stop | Out-Null
    }
    # UTF-8 with NO BOM, the invariant every vault writer holds - a BOM on a line-oriented state
    # file would ride into the first entry on the next read.
    [System.IO.File]::WriteAllText($script:StateFileFull, (ConvertFrom-VaultSuppressionLines -Lines $lines), (New-Object System.Text.UTF8Encoding $false))

    $summary = [ordered]@{
        list   = $normalizedList
        action = $normalizedAction
        entry  = $normalizedEntry
    }
    # scope appears only when the mutation targeted a section - the same posture lockWarning
    # takes, rather than an always-present null that would read as "global" on a flat list.
    if ($script:ScopePathBound) {
        $summary['scope'] = $normalizedScope
        $summary['scopeCreated'] = $sectionCreated
    }
    $summary['file'] = $script:StateFileRelative
    # Write-class envelope: always present. capabilitiesUsed is never empty on a mutation - the
    # lint:manage assertion above is the whole reason this branch is separate from list.
    $summary['capabilitiesUsed'] = @('lint:manage')
    $summary['filesSkippedUnreadable'] = @()
    $summary
}

# lockWarning is present ONLY when the write inherited an abandoned mutex and is omitted entirely
# otherwise - the envelope's deliberate exception to always-present (Tool-Contracts: Return
# Convention): it is a warning, not a status field, and an always-present null would read as a
# checked-and-clear signal.
if ($lockAbandoned) {
    $result['lockWarning'] = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
