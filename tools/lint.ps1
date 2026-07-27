<#
.SYNOPSIS
Runs the vault's deterministic convention checks over a target and reports findings, each carrying
the exact computed payload for whichever tool owns its remedy. Reports by default; `apply` opts in
to the narrow fix set the tool cannot be wrong about - typography, trailing whitespace, and
git-derived provenance dates. Everything else is a finding, never a silent rewrite.

Call this when:
- outside text has just landed in the vault - web content, model output, anything pasted from a
  word processor. That is where curly quotes, non-ASCII dashes, ellipsis characters, and NBSPs
  arrive, and the typography pass is what catches them
- you have finished a batch of writes and want to know what they broke: broken wikilinks,
  unresolvable anchor links, filename-convention violations, invalid agent.*/nav.* frontmatter,
  folder notes that no longer list their own contents
- the user asks to lint, to clean up formatting, or to fix dashes or quotes. Report first, then
  re-call with apply:true, which takes the write lock and asserts the content:write key
- you want a remedy you can dispatch rather than a complaint. Findings carry remedyOptions whose
  computed params target edit, move, or lint_suppressions
- hard-wrapped prose needs attention. That pass is in NO group and runs only when named explicitly

Not for silencing a finding - that is lint_suppressions, behind the lint:manage key; lint reads
suppression state and never writes it. Not for arbitrary text: the passes scan vault files only.
Findings are never errors - a vault with 500 violations returns success with 500 findings, so a
populated report is not a failed call.

Eight passes, ported from the vault-side vault-lint skill scripts plus the filesystem half of
vault-agent-frontmatter's validator. Enumeration is per-pass and deliberately inconsistent - each
pass keeps the scan set its skill-side original had, because unifying them would change coverage -
so filesScanned is reported per pass rather than once for the call. See the Lint corpus doc
(vault-primary) for the full contract.
.PARAMETER passes
Pass names and/or group tokens; every member is validated before any pass runs, so an
unrecognized one refuses the whole call rather than half-running it. Passes:
  typography           - non-ASCII dashes, curly quotes, ellipsis, NBSP. FIXABLE.
  trailing-whitespace  - whitespace before a line ending. FIXABLE.
  provenance           - four subchecks: archive-dates, created, doc-type, stale-use-skill.
                         The two date subchecks are FIXABLE where the recovery chain resolves.
  broken-wikilinks     - link basenames matching no note, annotated from git history.
  anchor-links         - markdown-format fragment links Obsidian will not resolve.
  filename-conventions - spaces, underscores, non-Title-Case filename segments.
  frontmatter-schema   - the shared agent.*/nav.* validator plus lint's own filesystem scan
                         (dead folder: refs, folder-note completeness).
  hard-wrap            - hard-wrapped prose and list lines.
Groups: "default" and "all" are every pass except hard-wrap; "checks" is the report-only
passes; "fixes" is the write-capable ones (typography, trailing-whitespace, provenance).
hard-wrap is in NO group and must be named explicitly - the opt-in expressed in the schema
itself. Matched case-insensitively after a trim; duplicates collapse. Deliberately not a
ValidateSet, so every value reaches the script body and an unrecognized one is refused with
lint's own invalid_argument JSON error rather than a PowerShell binding error.
.PARAMETER apply
Mutation opt-in, default FALSE - assessment is the routine call here and fixing is the
deliberate one. True takes the write lock, asserts the content:write key before the first
write, and applies the fix set; non-fixable passes still run and report either way.
.PARAMETER path
Optional vault-relative file or folder narrowing the scan; omitted means the whole vault. A
missing path is not_found. There is no kind parameter: a file and a folder are both legal, so
there is nothing to assert. Narrowing bypasses each pass's include-folder list the way the
skill scripts' -PathOverride does, and a single file is post-filtered from the pass's
enumeration - a pass whose exclusions drop that file simply scans nothing.
.PARAMETER hard_wrap_inspect
Per-block before/after detail on hard-wrap findings, for a caller deciding block by block.
Refused with apply:true (invalid_argument): inspection is a report-mode affordance and
hard-wrap has no fix to apply.
.PARAMETER vault
The vault to operate in. Defaults to the configured default vault.
.NOTES
OpClass: content:read

The floor is content:read because a lint report is a read. apply:true asserts the content:write
key in-script via Assert-VaultOpClass, inside the lock and before the first write, so a denial
is insufficient_capability with zero mutation. Report mode asserts nothing and takes no lock.

lint NEVER asserts agentic:write, and that guarantee lives in the FIX SET, not in the
capability check: every fix it applies is a body-text transform or a doc.* scalar, so no lint
write can produce an agentic frontmatter diff - which holds for every caller, including one
holding agentic:write. Routing the provenance date fix through Invoke-VaultFrontmatterWrite is
defense in depth on top of that, not the guarantee itself: the wrapper asserts, so it stops a
drifted fix only for callers who lack the key.

There is NO staleness gate, deliberately. The fix set is idempotent and per-file-independent,
so a stale view can only cause a redundant or a newly-warranted write, both of which converge.
Idempotency is the substitute for the gate. It is also what licenses the recorded divergence
from plan-validate-apply: writes STREAM per file, and a failure mid-fix raises partial_state
naming every file already written (Tool-Contracts: Tool Authoring Standards).

Findings are never errors: a vault with 500 violations returns success with 500 findings.
Two degradations stay findings rather than becoming errors - an unreadable suppression list
degrades to no suppression, and an anchor target that cannot be parsed degrades to a
needs-manual-review finding.

Suppression state is read from the configured locations under .vault/state/vault-lint/ and
nowhere else; there is no ignore-file override parameter, because lint_suppressions owns that
state behind the lint:manage key and a second door would go around it.

The return carries the write-class envelope: capabilitiesUsed and filesSkippedUnreadable[]
always ([] in report mode, which drops skips silently per the read-class convention),
lockWarning only when an abandoned mutex was inherited.
#>
[CmdletBinding()]
param(
    [string[]] $passes = @('default'),
    [bool] $apply = $false,
    [string] $path,
    [bool] $hard_wrap_inspect = $false,
    [string] $vault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# ---------------------------------------------------------------------------
# Pass registry. The group tokens are derived from these three lists rather than spelled out
# separately, so a pass added to the inventory cannot fall out of the group it belongs to.
# hard-wrap is deliberately absent from every group: the opt-in is expressed in the schema.
# ---------------------------------------------------------------------------
$script:FixPasses = @('typography', 'trailing-whitespace', 'provenance')
$script:CheckPasses = @('broken-wikilinks', 'anchor-links', 'filename-conventions', 'frontmatter-schema')
$script:OptInPasses = @('hard-wrap')
$script:AllPasses = @($script:FixPasses) + @($script:CheckPasses) + @($script:OptInPasses)
$script:Groups = @{
    'default' = @($script:FixPasses) + @($script:CheckPasses)
    'all'     = @($script:FixPasses) + @($script:CheckPasses)
    'checks'  = @($script:CheckPasses)
    'fixes'   = @($script:FixPasses)
}

# Enumeration config, carried over verbatim from the skill scripts. The .claude/guides tier is
# vault-convention content that lints like any other note; the rest of .claude is harness
# contract. Templates carry literal [[Note-Name]] placeholders that would read as broken links.
$script:LintIncludeFolders = @('Archive', 'Areas', 'Projects', 'Resources', '.claude/guides')
$script:LintExcludeFiles = @('Document-Conventions.md')
$script:LintExcludePaths = @('Resources/Vault/Templates')

# check-provenance.ps1's own set: skill-owned formats and non-authored files, plus the hidden
# content that is not authored vault documents (skill docs are harness contract, .vault is
# state, .myai is external tooling).
$script:ProvenanceExcludeFiles = @('Tasks.md', 'CLAUDE.md', 'Vault-Manifest.md')
$script:ProvenanceExcludePaths = @('Tasks', '.claude/skills', '.myai', '.vault', '.pytest_cache')

$script:StateFolder = '.vault/state/vault-lint'

# Minor words that may stay lowercase in a Title-Case filename, except as the first segment.
# This stays code, not state: it is English title-case convention, not vault-specific judgment.
$script:TitleLowercaseWords = @(
    'a', 'an', 'and', 'as', 'at', 'but', 'by', 'for', 'from', 'if', 'in', 'into',
    'nor', 'of', 'off', 'on', 'onto', 'or', 'per', 'so', 'the', 'to', 'up', 'via',
    'vs', 'with', 'yet'
)

# Standard subfolders a folder note must account for. The ephemeral pair routes via a single
# folder: entry, so a missing one is auto-addable with this boilerplate; Corpus is the contrast
# (per-member links, checked member by member) and anything else is surfaced for a human.
$script:EphemeralBoilerplate = [ordered]@{
    Inbox   = @{ description = 'Active working docs and in-flight content'; load_when = 'working on in-progress tasks or finding a specific artifact' }
    Archive = @{ description = 'Completed and inactive artifacts, date-prefixed'; load_when = 'researching prior artifacts or project history' }
}

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# ---------------------------------------------------------------------------
# Small ports of the skill-side shared helpers. Each is content- or path-only; none of them
# reaches the filesystem except where the original did.
# ---------------------------------------------------------------------------

function ConvertTo-LintComparablePath {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)
    (($Value -replace '\\', '/').Trim('/')).ToLowerInvariant()
}

function Test-LintFolderNote {
    <#
    .SYNOPSIS
    True when a note is its folder's folder note - basename equals the containing folder's name
    (Personal/Personal.md). Never reads content.
    #>
    param([Parameter(Mandatory)] [string] $Rel)
    $segments = $Rel -split '/'
    if ($segments.Count -lt 2) { return $false }
    $base = [System.IO.Path]::GetFileNameWithoutExtension($segments[-1])
    $base -eq $segments[-2]
}

function Test-LintGuideDoc {
    param([Parameter(Mandatory)] [string] $Rel)
    $Rel -match '(^|/)\.claude/guides/'
}

function Test-LintTemplateDoc {
    param([Parameter(Mandatory)] [string] $Rel)
    $Rel -match '(^|/)Resources/Vault/Templates/'
}

function Test-LintTemplaterScript {
    <#
    .SYNOPSIS
    True when the content is, in its entirety, one Templater command block - an insertable
    script with no document to attach doc.* fields to. Content-based, because most files under
    Templates/ are real markdown templates that stay in scope.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)
    $trimmed = $Content.Trim()
    $trimmed.StartsWith('<%*') -and $trimmed.EndsWith('%>')
}

function Test-LintArchivePath {
    param([Parameter(Mandatory)] [string] $Rel)
    $Rel -match '(^|/)Archive(/|$)'
}

function Get-LintFilenameDate {
    <#
    .SYNOPSIS
    The YYYY-MM-DD segment of a leading (YYYY-MM-DD-Title) or trailing (Title-YYYY-MM-DD)
    filename date, or $null when the stem carries none.
    #>
    param([Parameter(Mandatory)] [string] $BaseName)
    if ($BaseName -match '-(\d{4}-\d{2}-\d{2})$') { return $Matches[1] }
    if ($BaseName -match '^(\d{4}-\d{2}-\d{2})-') { return $Matches[1] }
    $null
}

function Remove-LintCodeSpansAndFences {
    <#
    .SYNOPSIS
    Strips fenced code blocks and inline code spans, so a check scanning prose for [[wikilinks]]
    does not match link syntax shown as an example inside backticks. Inline spans are stripped
    per blank-line-bounded paragraph, so an unterminated backtick run cannot hunt for a closing
    match arbitrarily far away and swallow unrelated text.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)
    $stripped = [regex]::Replace($Content, '(?ms)^(```|~~~).*?^\1\s*$', '')
    $sb = New-Object System.Text.StringBuilder
    foreach ($para in [regex]::Split($stripped, '(?:\r?\n){2,}')) {
        [void]$sb.Append([regex]::Replace($para, '(?s)(`+)(?:(?!\1).)*?\1', ''))
        [void]$sb.Append("`n`n")
    }
    $sb.ToString()
}

# ---------------------------------------------------------------------------
# Suppression state. Read from the configured locations only. A missing OR unreadable file
# degrades to no suppression and never becomes an error - that degradation is recorded as
# lint-domain behavior, since a state file a caller cannot fix through lint must not be able to
# fail the whole scan.
# ---------------------------------------------------------------------------

function Read-LintStateLine {
    param([Parameter(Mandatory)] [string] $FileName)
    $full = Join-Path $script:VaultRoot ("$script:StateFolder/$FileName".Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return , @() }
    try {
        return , @([System.IO.File]::ReadAllLines($full))
    }
    catch {
        return , @()
    }
}

function Get-LintSimpleSuppression {
    <#
    .SYNOPSIS
    The entries of a flat (unsectioned) suppression list - "#" starts a comment, blanks drop.
    #>
    param([Parameter(Mandatory)] [string] $FileName)
    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Read-LintStateLine -FileName $FileName)) {
        $text = ($line -split '#', 2)[0].Trim()
        if ($text -ne '') { $entries.Add($text) }
    }
    , $entries.ToArray()
}

function Get-LintBrokenLinkSuppression {
    <#
    .SYNOPSIS
    The sectioned broken-wikilinks list: entries before any [path:...] header are global; a
    header scopes the entries under it to files whose vault-relative path starts with that
    prefix. Matching is case-insensitive throughout, as the writing tool's is.
    #>
    $global = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $scoped = @{}
    $current = $null
    foreach ($line in (Read-LintStateLine -FileName 'known-broken-wikilinks.txt')) {
        if ($line -match '^\s*\[path:(.+)\]\s*$') {
            $current = $Matches[1].Trim().Replace('\', '/').Trim('/')
            if (-not $scoped.ContainsKey($current)) {
                $scoped[$current] = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
            }
            continue
        }
        $entry = ($line -split '#', 2)[0].Trim()
        if ($entry -eq '') { continue }
        if ($null -eq $current) { [void]$global.Add($entry) } else { [void]$scoped[$current].Add($entry) }
    }
    @{ Global = $global; Scoped = $scoped }
}

function Test-LintLinkSuppressed {
    param(
        [Parameter(Mandatory)] $Suppression,
        [Parameter(Mandatory)] [string] $Target,
        [Parameter(Mandatory)] [string] $Rel
    )
    if ($Suppression.Global.Contains($Target)) { return $true }
    foreach ($prefix in $Suppression.Scoped.Keys) {
        if ($Rel.StartsWith($prefix + '/', [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($Suppression.Scoped[$prefix].Contains($Target)) { return $true }
        }
    }
    $false
}

# ---------------------------------------------------------------------------
# Result shaping.
# ---------------------------------------------------------------------------

function New-LintRemedy {
    <#
    .SYNOPSIS
    The shared RemedyOption (Tool-Contracts: The RemedyOption Shape). `requires` names the dotted
    param paths lint structurally cannot fill because they need caller judgment - the same field
    sync uses for commit.message - and is empty on the remedies lint computes end to end.

    THE INVARIANT THIS FILE HOLDS TO: every field in a remedy's `params` is either computed or
    named in `requires`, and an EMPTY-STRING field is the visible slot form of "named in
    requires". A params field that is neither is a pre-rendered hint pretending to be a payload,
    which is exactly what the entryBlock shape was.
    #>
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $Action,
        [Parameter(Mandatory)] $Params,
        [string[]] $Requires = @()
    )
    [ordered]@{ id = $Id; label = $Label; action = $Action; params = $Params; requires = @($Requires) }
}

function New-LintEditPatchRemedy {
    <#
    .SYNOPSIS
    An edit-patch remedy carrying edit's exactly-once anchored patch arguments verbatim.
    #>
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $Rel,
        [Parameter(Mandatory)] [string] $OldString,
        [Parameter(Mandatory)] [string] $NewString
    )
    New-LintRemedy -Id $Id -Label $Label -Action 'edit-patch' -Params ([ordered]@{
            path       = $Rel
            old_string = $OldString
            new_string = $NewString
        })
}

function New-LintFrontmatterRemedy {
    <#
    .SYNOPSIS
    An edit-frontmatter remedy. `params.frontmatter_ops` rides as a REAL JSON ARRAY of op objects,
    not as a pre-encoded JSON string.

    WHY THE STRUCTURE STAYS STRUCTURED. edit's frontmatter_ops parameter is a `[string]` in the
    param() block, but that is a transport detail of the PowerShell binder, not the contract: the
    parameter's `.SCHEMA` side-car declares `type: "array"`, and the host encodes a structured
    argument at the call boundary on exactly that signal (ScriptToolArgumentEncoder). Serializing
    here would put lint on the wrong side of that boundary - the payload would be a string the
    caller has to decode to inspect, re-encode to amend, and get the array-vs-bare-object shape
    right on by hand. Encoding is mechanical and belongs to the dispatcher.

    HOW `requires` ADDRESSES A SLOT. Tool-Contracts spells `requires` as dotted param paths. The
    path syntax is unchanged - `frontmatter_ops.0.description` is still the description slot of the
    first op - but it now resolves by PLAIN OBJECT AND ARRAY WALKING, no decode step anywhere: the
    same algorithm that resolves sync's `commit.message` against its live nested `commit` object,
    with a numeric segment indexing an array. So one resolver serves every tool, and a path naming
    a key the payload does not carry is checkable before dispatch rather than after.

    The numeric segment still reuses the op addressing edit already publishes - malformed_op
    carries {opIndex}, so "the op at position N" is how this system names an op everywhere else.
    The alternative - naming the whole `frontmatter_ops` param - would say the entire computed
    payload is caller-supplied, which is false and throws away the deterministic work `params`
    exists to carry.
    .PARAMETER Ops
    The ordered op objects. Rides verbatim; @() around it keeps a ONE-OP payload an array, which
    is the shape edit requires - the single-element unroll is the trap this file already fell into
    once, from the other direction, when -AsArray double-wrapped every payload it emitted.
    .PARAMETER Requires
    Dotted param paths (see above) the caller must fill before dispatch. Empty means dispatchable
    without caller judgment - the dispatcher still encodes, which needs no judgment at all.
    #>
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $Rel,
        [Parameter(Mandatory)] [object[]] $Ops,
        [string[]] $Requires = @()
    )
    New-LintRemedy -Id $Id -Label $Label -Action 'edit-frontmatter' -Requires $Requires -Params ([ordered]@{
            path            = $Rel
            frontmatter_ops = @($Ops)
        })
}

function New-LintSuppressionRemedy {
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $List,
        [Parameter(Mandatory)] [string] $Entry,
        [string] $ScopePath
    )
    $params = [ordered]@{ action = 'add'; list = $List; entry = $Entry }
    if ($ScopePath) { $params['scope_path'] = $ScopePath }
    New-LintRemedy -Id $Id -Label $Label -Action 'add-suppression' -Params $params
}

function New-LintFinding {
    param(
        [Parameter(Mandatory)] [string] $File,
        $Line,
        [Parameter(Mandatory)] [string] $Disposition,
        [Parameter(Mandatory)] [string] $Issue,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Detail,
        [object[]] $RemedyOptions
    )
    $finding = [ordered]@{
        file        = $File
        line        = $Line
        disposition = $Disposition
        issue       = $Issue
        detail      = $Detail
    }
    if ($RemedyOptions -and $RemedyOptions.Count -gt 0) { $finding['remedyOptions'] = @($RemedyOptions) }
    $finding
}

function New-LintPassResult {
    param(
        [Parameter(Mandatory)] [string] $Pass,
        [Parameter(Mandatory)] [bool] $ReportOnly,
        [Parameter(Mandatory)] [int] $FilesScanned,
        [Parameter(Mandatory)] $Counts,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Findings,
        $Subchecks
    )
    # skipped-not-applicable is the honest answer when narrowing left the pass nothing in its
    # own enumeration - distinct from "scanned and clean", which a caller reads very differently.
    $status = if (@($Findings).Count -gt 0) { 'findings' }
    elseif ($FilesScanned -eq 0) { 'skipped-not-applicable' }
    else { 'clean' }

    $result = [ordered]@{
        pass         = $Pass
        status       = $status
        reportOnly   = $ReportOnly
        filesScanned = $FilesScanned
        counts       = $Counts
        findings     = @($Findings)
    }
    # subchecks is provenance's alone: it is the one pass with independent checks inside it, and
    # there is deliberately no sub-pass addressing on the surface (single entry point).
    if ($null -ne $Subchecks) { $result['subchecks'] = @($Subchecks) }
    $result
}

# ---------------------------------------------------------------------------
# Enumeration. Per-pass and deliberately inconsistent: each pass asks for the include/exclude
# set its skill-side original had. `path` narrowing bypasses the include-folder list the way
# -PathOverride did there, and a single-file target is post-filtered rather than walked for.
# ---------------------------------------------------------------------------

function Test-LintFileAccepted {
    param(
        [Parameter(Mandatory)] [string] $Rel,
        [string[]] $ExcludeFiles,
        [string[]] $ExcludePaths,
        [bool] $ApplyHiddenFilter
    )
    $name = ($Rel -split '/')[-1]
    if ($ExcludeFiles -and ($ExcludeFiles -contains $name)) { return $false }
    if ($ExcludePaths) {
        $relCmp = ConvertTo-LintComparablePath $Rel
        foreach ($ex in $ExcludePaths) {
            $exCmp = ConvertTo-LintComparablePath $ex
            if ($relCmp.StartsWith($exCmp + '/', [System.StringComparison]::Ordinal)) { return $false }
        }
    }
    if ($ApplyHiddenFilter) {
        $segments = $Rel -split '/'
        for ($i = 0; $i -lt $segments.Count - 1; $i++) {
            if ($segments[$i].StartsWith('.')) { return $false }
        }
    }
    if ($script:ScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $Rel -ScopeRoots $script:ScopeRoots)) { return $false }
    $true
}

function Get-LintScanFile {
    <#
    .SYNOPSIS
    The markdown files one pass scans, as @{ Rel; Full; Name; BaseName } records. Out-of-scope
    files are dropped silently, matching the read-class enumeration convention.
    #>
    param(
        [string[]] $IncludeFolders,
        [string[]] $ExcludeFiles,
        [string[]] $ExcludePaths,
        [switch] $IncludeHiddenFolders
    )

    $files = New-Object System.Collections.Generic.List[object]

    # A single-file target is post-filtered from what the pass would have enumerated: it is
    # admitted only if the pass's own exclusions would have kept it. Narrowing puts the scan in
    # override mode, where the include-folder list does not apply and neither does the
    # dot-directory filter - the same semantics the skill scripts' -PathOverride has.
    if ($script:SingleFileRel) {
        if (Test-LintFileAccepted -Rel $script:SingleFileRel -ExcludeFiles $ExcludeFiles -ExcludePaths $ExcludePaths -ApplyHiddenFilter $false) {
            $files.Add((New-LintFileRecord -Rel $script:SingleFileRel))
        }
        return , $files.ToArray()
    }

    $roots = if ($script:OverrideRootFull) { @($script:OverrideRootFull) }
    elseif ($IncludeFolders) { @($IncludeFolders | ForEach-Object { Join-Path $script:VaultRoot ($_ -replace '/', [System.IO.Path]::DirectorySeparatorChar) }) }
    else { @($script:VaultRoot) }

    $applyHiddenFilter = (-not $script:OverrideRootFull) -and (-not $IncludeFolders) -and (-not $IncludeHiddenFolders)

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($item in @(Get-ChildItem -LiteralPath $root -Recurse -Filter '*.md' -File -ErrorAction SilentlyContinue)) {
            $rel = [System.IO.Path]::GetRelativePath($script:VaultRoot, $item.FullName).Replace('\', '/')
            if (-not (Test-LintFileAccepted -Rel $rel -ExcludeFiles $ExcludeFiles -ExcludePaths $ExcludePaths -ApplyHiddenFilter $applyHiddenFilter)) { continue }
            $files.Add((New-LintFileRecord -Rel $rel -Full $item.FullName))
        }
    }
    , $files.ToArray()
}

function New-LintFileRecord {
    param(
        [Parameter(Mandatory)] [string] $Rel,
        [string] $Full
    )
    if (-not $Full) { $Full = Join-Path $script:VaultRoot ($Rel -replace '/', [System.IO.Path]::DirectorySeparatorChar) }
    $name = ($Rel -split '/')[-1]
    [pscustomobject]@{
        Rel      = $Rel
        Full     = $Full
        Name     = $name
        BaseName = [System.IO.Path]::GetFileNameWithoutExtension($name)
    }
}

function Read-LintNote {
    <#
    .SYNOPSIS
    A note's content, or $null when it enumerated but has nothing to read (it vanished, or it is
    a dangling symlink). A file that is PRESENT and genuinely unreadable is a real read_failure,
    never a silent skip - Test-VaultNoteMissingError is the line between the two. Report mode
    drops a skip silently; fix mode records the path.
    #>
    param([Parameter(Mandatory)] $File)
    try {
        return [System.IO.File]::ReadAllText($File.Full, [System.Text.Encoding]::UTF8)
    }
    catch {
        if (Test-VaultNoteMissingError -ErrorRecord $_) {
            if ($script:Applying -and -not $script:FilesSkippedUnreadable.Contains($File.Rel)) {
                [void]$script:FilesSkippedUnreadable.Add($File.Rel)
            }
            return $null
        }
        throw (New-VaultToolError -Code 'read_failure' -Message "Could not read '$($File.Rel)': $($_.Exception.Message)" -Data @{ path = $File.Rel; error = $_.Exception.Message })
    }
}

function Write-LintNote {
    <#
    .SYNOPSIS
    One streaming fix write. UTF-8 with NO BOM, the invariant every vault writer holds. The
    running written-file list is what partial_state must name if a later write throws.
    #>
    param(
        [Parameter(Mandatory)] $File,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content
    )
    [System.IO.File]::WriteAllText($File.Full, $Content, $script:Utf8NoBom)
    if (-not $script:FilesWritten.Contains($File.Rel)) { [void]$script:FilesWritten.Add($File.Rel) }
}

# ---------------------------------------------------------------------------
# Pass: typography
# ---------------------------------------------------------------------------

$script:TypographyDashes = [string][char]0x2013 + [char]0x2014 + [char]0x2015 + [char]0x2012
$script:TypographyLdq = [char]0x201C
$script:TypographyRdq = [char]0x201D
$script:TypographyLsq = [char]0x2018
$script:TypographyRsq = [char]0x2019
$script:TypographyEllipsis = [char]0x2026
$script:TypographyNbsp = [char]0x00A0

function Convert-LintTypography {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)
    $Content `
        -replace "[$script:TypographyDashes]", '-' `
        -replace "[$script:TypographyLdq$script:TypographyRdq]", '"' `
        -replace "[$script:TypographyLsq$script:TypographyRsq]", "'" `
        -replace "$script:TypographyEllipsis", '...' `
        -replace "$script:TypographyNbsp", ' '
}

function Invoke-LintTypographyPass {
    $files = Get-LintScanFile -IncludeFolders $script:LintIncludeFolders -ExcludeFiles $script:LintExcludeFiles -ExcludePaths $script:LintExcludePaths
    $findings = New-Object System.Collections.Generic.List[object]
    $totalReplacements = 0

    foreach ($file in $files) {
        $content = Read-LintNote -File $file
        if ($null -eq $content) { continue }
        $updated = Convert-LintTypography -Content $content
        # Ordinal comparison: the default -ne is culture-sensitive and treats an ellipsis as
        # equal to '...' and an NBSP as equal to ' ', masking exactly the changes made here.
        if ([string]::Equals($content, $updated, [System.StringComparison]::Ordinal)) { continue }

        $counts = [ordered]@{
            dashes           = [regex]::Matches($content, "[$script:TypographyDashes]").Count
            'curly doubles'  = [regex]::Matches($content, "[$script:TypographyLdq$script:TypographyRdq]").Count
            'curly singles'  = [regex]::Matches($content, "[$script:TypographyLsq$script:TypographyRsq]").Count
            ellipses         = [regex]::Matches($content, [regex]::Escape($script:TypographyEllipsis)).Count
            'non-break spaces' = [regex]::Matches($content, [regex]::Escape($script:TypographyNbsp)).Count
        }
        $detail = (@($counts.Keys | Where-Object { $counts[$_] -gt 0 } | ForEach-Object { "$($_): $($counts[$_])" })) -join '; '
        foreach ($key in $counts.Keys) { $totalReplacements += $counts[$key] }

        $disposition = 'fixable'
        if ($script:Applying) {
            Write-LintNote -File $file -Content $updated
            $disposition = 'fixed'
        }
        $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition $disposition -Issue 'typographic-characters' -Detail $detail))
    }

    New-LintPassResult -Pass 'typography' -ReportOnly $false -FilesScanned $files.Count `
        -Counts ([ordered]@{ filesAffected = $findings.Count; replacements = $totalReplacements }) -Findings $findings
}

# ---------------------------------------------------------------------------
# Pass: trailing-whitespace
# ---------------------------------------------------------------------------

function Invoke-LintTrailingWhitespacePass {
    $files = Get-LintScanFile -IncludeFolders $script:LintIncludeFolders -ExcludeFiles $script:LintExcludeFiles -ExcludePaths $script:LintExcludePaths
    $findings = New-Object System.Collections.Generic.List[object]
    $totalLines = 0

    foreach ($file in $files) {
        $content = Read-LintNote -File $file
        if ($null -eq $content) { continue }
        $updated = [regex]::Replace($content, '[ \t]+(?=\r?\n|$)', '')
        if ([string]::Equals($content, $updated, [System.StringComparison]::Ordinal)) { continue }

        $count = [regex]::Matches($content, '[ \t]+(?=\r?\n|$)').Count
        $totalLines += $count

        $disposition = 'fixable'
        if ($script:Applying) {
            Write-LintNote -File $file -Content $updated
            $disposition = 'fixed'
        }
        $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition $disposition -Issue 'trailing-whitespace' -Detail "$count line(s)"))
    }

    New-LintPassResult -Pass 'trailing-whitespace' -ReportOnly $false -FilesScanned $files.Count `
        -Counts ([ordered]@{ filesAffected = $findings.Count; lines = $totalLines }) -Findings $findings
}

# ---------------------------------------------------------------------------
# Pass: provenance
#
# Four subchecks over one vault-wide scan. Only the two date subchecks are fixable, and only
# where the recovery chain resolves; doc.type has no recovery chain (unlike a date there is no
# git-history equivalent to infer it from) and the stale use_skill strip lives in the agentic
# layer, which lint must never write.
# ---------------------------------------------------------------------------

function Invoke-LintGit {
    <#
    .SYNOPSIS
    Runs git and returns its stdout lines, or @() when git is unavailable or the command fails.
    git is an external dependency whose absence must degrade the recovery chain rather than fail
    the pass, so this is the one place the tool's Stop preference is relaxed.
    #>
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -C $script:VaultRoot @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) { return , @() }
        return , @($out)
    }
    catch { return , @() }
    finally { $ErrorActionPreference = $previous }
}

function Get-LintCreatedDate {
    <#
    .SYNOPSIS
    The creation date recovery chain: existing doc.created, then a filename date segment, then
    the git commit that first added the file anywhere (--follow, oldest match across the rename
    chain). $null when none resolve - the caller flags rather than guesses.
    #>
    param(
        [Parameter(Mandatory)] [string] $Rel,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content
    )
    $existing = Get-VaultFrontmatterField -Content $Content -Field 'doc.created'
    if ($existing) { return $existing }

    $fnDate = Get-LintFilenameDate -BaseName ([System.IO.Path]::GetFileNameWithoutExtension($Rel))
    if ($fnDate) { return $fnDate }

    # git log lists commits newest-first; the oldest match is the true first-add date.
    $dates = Invoke-LintGit -Arguments @('log', '--follow', '--diff-filter=A', '--format=%as', '--', $Rel)
    $dates = @($dates | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}$' })
    if ($dates.Count -gt 0) { return $dates[-1] }
    $null
}

function Get-LintArchivedDate {
    <#
    .SYNOPSIS
    The archive date recovery chain: existing doc.archived, then the commit where the file's
    path first crosses from outside any Archive/ folder into one, walking the full --follow
    rename chain oldest to newest. Deliberately NOT "the earliest commit at the current path" -
    a later archive compaction changes the path again and that approach picks up the compaction
    date instead.
    #>
    param(
        [Parameter(Mandatory)] [string] $Rel,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content
    )
    $existing = Get-VaultFrontmatterField -Content $Content -Field 'doc.archived'
    if ($existing) { return $existing }

    $raw = Invoke-LintGit -Arguments @('log', '--follow', '--name-status', '--format=%as', '--', $Rel)
    if ($raw.Count -eq 0) { return $null }

    $commits = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($line in $raw) {
        if ($line -match '^\d{4}-\d{2}-\d{2}$') {
            if ($current) { $commits.Add($current) }
            $current = [pscustomobject]@{ Date = $line; Renames = (New-Object System.Collections.Generic.List[object]) }
        }
        elseif ($current -and $line.Trim() -ne '' -and $line -match '^R\d*\t(.+)\t(.+)$') {
            # Non-rename statuses carry a single path and cannot represent a crossing on their own.
            $current.Renames.Add([pscustomobject]@{ OldPath = $Matches[1]; NewPath = $Matches[2] })
        }
    }
    if ($current) { $commits.Add($current) }

    for ($i = $commits.Count - 1; $i -ge 0; $i--) {
        foreach ($rename in $commits[$i].Renames) {
            if (-not (Test-LintArchivePath -Rel $rename.OldPath) -and (Test-LintArchivePath -Rel $rename.NewPath)) {
                return $commits[$i].Date
            }
        }
    }
    $null
}

function Invoke-LintProvenancePass {
    $files = Get-LintScanFile -ExcludeFiles $script:ProvenanceExcludeFiles -ExcludePaths $script:ProvenanceExcludePaths -IncludeHiddenFolders
    $findings = New-Object System.Collections.Generic.List[object]
    $counters = [ordered]@{ 'archive-dates' = 0; 'created' = 0; 'doc-type' = 0; 'stale-use-skill' = 0 }
    $scanned = 0

    foreach ($file in $files) {
        # Folder notes are structural, not authored documents - excluded from every subcheck.
        if (Test-LintFolderNote -Rel $file.Rel) { continue }
        $content = Read-LintNote -File $file
        if ($null -eq $content) { continue }
        if (Test-LintTemplaterScript -Content $content) { continue }
        $scanned++

        $created = Get-VaultFrontmatterField -Content $content -Field 'doc.created'
        # Tracks whether doc.created will be present after this run, so the vault-wide subcheck
        # does not re-report a file the archive subcheck already resolves.
        $createdEffective = $created

        if (Test-LintArchivePath -Rel $file.Rel) {
            $ops = New-Object System.Collections.Generic.List[object]
            $recoveredLabels = New-Object System.Collections.Generic.List[string]
            $missing = New-Object System.Collections.Generic.List[string]

            if (-not $created) {
                $recovered = Get-LintCreatedDate -Rel $file.Rel -Content $content
                if ($recovered) {
                    $ops.Add(@{ op = 'set_field'; field = 'doc.created'; value = $recovered })
                    $recoveredLabels.Add("doc.created: $recovered")
                    $createdEffective = $recovered
                }
                else { $missing.Add('doc.created') }
            }
            if (-not (Get-VaultFrontmatterField -Content $content -Field 'doc.archived')) {
                $recovered = Get-LintArchivedDate -Rel $file.Rel -Content $content
                if ($recovered) {
                    $ops.Add(@{ op = 'set_field'; field = 'doc.archived'; value = $recovered })
                    $recoveredLabels.Add("doc.archived: $recovered")
                }
                else { $missing.Add('doc.archived') }
            }

            if ($ops.Count -gt 0) {
                $counters['archive-dates']++
                $disposition = 'fixable'
                if ($script:Applying) {
                    # Through the shared validate-and-classify wrapper, never a private YAML
                    # writer: the wrapper is the one write path into frontmatter, and it is the
                    # defense-in-depth layer under lint's own fix-set guarantee. Schema
                    # violations come back as data here rather than throwing, which is why a note
                    # with pre-existing agent.* problems still gets its date repaired.
                    $written = Invoke-VaultFrontmatterWrite -Content $content -Ops $ops.ToArray() -RelativePath $file.Rel
                    Write-LintNote -File $file -Content $written.Content
                    $disposition = 'fixed'
                }
                $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition $disposition `
                            -Issue 'archive-dates' -Detail ($recoveredLabels -join ', ')))
            }
            if ($missing.Count -gt 0) {
                $counters['archive-dates']++
                $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'flagged' `
                            -Issue 'archive-dates-unrecoverable' -Detail "recovery chain exhausted for: $($missing -join ', ')"))
            }

            # Independent of the date repair - a file can be provenance-complete and still carry
            # a stale use_skill entry. Demoted to report-only: the strip is an agentic-layer
            # write, which lint categorically does not make.
            $stripped = Remove-VaultUseSkillAgentInstructions -Content $content
            if ($stripped -cne $content) {
                $counters['stale-use-skill']++
                $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                            -Issue 'stale-archived-use-skill' -Detail 'archived doc still carries a use_skill: agent.instructions entry' `
                            -RemedyOptions @(
                            (New-LintFrontmatterRemedy -Id 'strip-use-skill' -Label 'Strip the stale use_skill: directive (needs agentic:write)' `
                                    -Rel $file.Rel -Ops @([ordered]@{ op = 'strip_directive'; kind = 'use_skill' }))
                        )))
            }
        }

        if (-not $createdEffective -and -not (Test-LintTemplateDoc -Rel $file.Rel)) {
            $counters['created']++
            $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                        -Issue 'missing-doc-created' -Detail "no doc.created, and the recovery chain is only run under Archive/"))
        }

        # Guide docs are structural rather than part of the doc.* schema; template docs carry a
        # leading Templater block the frontmatter parser cannot see past.
        if (-not (Test-LintGuideDoc -Rel $file.Rel) -and -not (Test-LintTemplateDoc -Rel $file.Rel)) {
            if (-not (Get-VaultFrontmatterField -Content $content -Field 'doc.type')) {
                $counters['doc-type']++
                $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                            -Issue 'missing-doc-type' -Detail 'no doc.type, and there is no recovery chain for it - the value is a judgment' `
                            -RemedyOptions @(
                            # The value is a judgment, which is what `requires` is for - the label
                            # said so in prose while the field claimed dispatchable-verbatim.
                            (New-LintFrontmatterRemedy -Id 'set-doc-type' -Label 'Set doc.type (choose the value)' `
                                    -Rel $file.Rel -Ops @([ordered]@{ op = 'set_field'; field = 'doc.type'; value = '' }) `
                                    -Requires @('frontmatter_ops.0.value'))
                        )))
            }
        }
    }

    $subchecks = @($counters.Keys | ForEach-Object { [ordered]@{ subcheck = $_; findings = $counters[$_] } })
    New-LintPassResult -Pass 'provenance' -ReportOnly $false -FilesScanned $scanned `
        -Counts ([ordered]@{ findings = $findings.Count }) -Findings $findings -Subchecks $subchecks
}

# ---------------------------------------------------------------------------
# Pass: broken-wikilinks
# ---------------------------------------------------------------------------

function Invoke-LintBrokenWikilinksPass {
    $files = Get-LintScanFile -IncludeFolders $script:LintIncludeFolders -ExcludeFiles $script:LintExcludeFiles -ExcludePaths $script:LintExcludePaths
    $findings = New-Object System.Collections.Generic.List[object]
    $suppressed = 0

    # Index of every note that exists, ALWAYS vault-wide. `path` narrows which files are
    # SCANNED, never which notes exist: a link out of a narrowed folder into the rest of the
    # vault is a live link, and scoping the index would report every one of them broken.
    # Deliberate divergence from the skill script, whose index followed -PathOverride only to
    # keep its own unit tests hermetic - this suite gets hermeticity from a whole temp vault.
    # Skill docs are included: a cross-file heading wikilink can legitimately point at one.
    $currentNotes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @(Get-ChildItem -LiteralPath $script:VaultRoot -Recurse -Filter '*.md' -File -ErrorAction SilentlyContinue)) {
        [void]$currentNotes.Add($item.BaseName)
    }

    $suppression = Get-LintBrokenLinkSuppression

    # git history: the first rename out of each old name, and every deleted note basename.
    $renameMap = @{}
    foreach ($line in (Invoke-LintGit -Arguments @('log', '--diff-filter=R', '--name-status', '--pretty=format:'))) {
        if ($line -match '^R\d*\t(.+)\t(.+)$') {
            $oldName = [System.IO.Path]::GetFileNameWithoutExtension($Matches[1])
            $newName = [System.IO.Path]::GetFileNameWithoutExtension($Matches[2])
            if (-not $renameMap.ContainsKey($oldName)) { $renameMap[$oldName] = $newName }
        }
    }
    $deletedSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($line in (Invoke-LintGit -Arguments @('log', '--diff-filter=D', '--name-only', '--pretty=format:'))) {
        if ($line -match '\.md$') { [void]$deletedSet.Add([System.IO.Path]::GetFileNameWithoutExtension($line)) }
    }

    $pattern = Get-VaultLinkPattern
    foreach ($file in $files) {
        $content = Read-LintNote -File $file
        if ($null -eq $content) { continue }
        $scanContent = Remove-LintCodeSpansAndFences -Content $content

        foreach ($match in $pattern.Matches($scanContent)) {
            # Embeds are skipped: a transclusion usually targets an image or a PDF, not a note.
            if ($match.Groups['embed'].Value -eq '!') { continue }
            $raw = $match.Groups['target'].Value.Trim()
            if ($raw -eq '') { continue }
            $target = ($raw -split '[/\\]')[-1]

            if ($currentNotes.Contains($target)) { continue }
            if (Test-LintLinkSuppressed -Suppression $suppression -Target $target -Rel $file.Rel) { $suppressed++; continue }

            $remedies = New-Object System.Collections.Generic.List[object]
            $history = if ($renameMap.ContainsKey($target)) {
                $newName = $renameMap[$target]
                # The rewrite is exactly computable: only the target segment changes, and the
                # embed marker, #heading, and |alias ride through verbatim - rebuilt from the
                # grammar's own groups rather than by string surgery on the match text.
                $newLink = "$($match.Groups['embed'].Value)[[$newName$($match.Groups['heading'].Value)$($match.Groups['alias'].Value)]]"
                $remedies.Add((New-LintEditPatchRemedy -Id 'retarget-link' -Label "Retarget the link to [[$newName]]" `
                            -Rel $file.Rel -OldString $match.Value -NewString $newLink))
                "was renamed to '$newName'"
            }
            elseif ($deletedSet.Contains($target)) { 'was deleted' }
            else { 'no history found' }

            $remedies.Add((New-LintSuppressionRemedy -Id 'suppress-global' -Label "Suppress [[$target]] everywhere" `
                        -List 'broken-wikilinks' -Entry $target))
            $remedies.Add((New-LintSuppressionRemedy -Id 'suppress-scoped' -Label "Suppress [[$target]] under this file's folder" `
                        -List 'broken-wikilinks' -Entry $target -ScopePath (Split-Path -Parent $file.Rel).Replace('\', '/')))

            $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                        -Issue 'broken-wikilink' -Detail "[[$target]] - $history" -RemedyOptions $remedies.ToArray()))
        }
    }

    New-LintPassResult -Pass 'broken-wikilinks' -ReportOnly $true -FilesScanned $files.Count `
        -Counts ([ordered]@{ broken = $findings.Count; suppressed = $suppressed }) -Findings $findings
}

# ---------------------------------------------------------------------------
# Pass: anchor-links
#
# Markdown-format fragment links - [Text](#anchor) or [Text](Other-Note#anchor) - that Obsidian
# does not resolve as heading jumps (only wikilinks do). The unambiguous rewrite is computable,
# but applying it is an orchestrator decision, so this pass is report-only and carries the
# rewrite as an edit-patch payload.
# ---------------------------------------------------------------------------

# Group 1 (display) excludes '[' as well as ']' so a stray bracket elsewhere on the line cannot
# swallow everything up to an unrelated later '](...)'; it also excludes newlines so a match can
# never span lines. Group 2 (target) excludes '#' so it captures only the text before the first
# fragment marker.
$script:AnchorLinkPattern = [regex]'\[([^\[\]\r\n]*)\]\(([^)\s#]*)#([^)\s]+)\)'

function ConvertTo-LintSlug {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)
    ([regex]::Replace($Text.ToLowerInvariant(), '[^a-z0-9]+', '-')).Trim('-')
}

function Get-LintLineNumber {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [int] $Index
    )
    if ($Index -le 0) { return 1 }
    1 + ([regex]::Matches($Content.Substring(0, $Index), "`n")).Count
}

function Invoke-LintAnchorLinksPass {
    $files = Get-LintScanFile -ExcludeFiles $script:LintExcludeFiles -ExcludePaths $script:LintExcludePaths -IncludeHiddenFolders
    $findings = New-Object System.Collections.Generic.List[object]
    $rewritable = 0
    $manual = 0

    $contentCache = @{}
    $headingCache = @{}
    $fileIndex = @{}
    foreach ($file in $files) {
        $key = $file.BaseName.ToLowerInvariant()
        if (-not $fileIndex.ContainsKey($key)) { $fileIndex[$key] = New-Object System.Collections.Generic.List[string] }
        $fileIndex[$key].Add($file.Full)
    }

    function Get-LintCachedContent {
        param([Parameter(Mandatory)] [string] $Full)
        if (-not $contentCache.ContainsKey($Full)) {
            $contentCache[$Full] = [System.IO.File]::ReadAllText($Full, [System.Text.Encoding]::UTF8)
        }
        $contentCache[$Full]
    }

    function Get-LintHeadingMap {
        param([Parameter(Mandatory)] [string] $Full)
        if (-not $headingCache.ContainsKey($Full)) {
            $map = @{}
            foreach ($m in [regex]::Matches((Get-LintCachedContent -Full $Full), '(?m)^#{1,6}[ \t]+(.+?)\s*$')) {
                $text = $m.Groups[1].Value.Trim()
                $slug = ConvertTo-LintSlug $text
                if (-not $map.ContainsKey($slug)) { $map[$slug] = New-Object System.Collections.Generic.List[string] }
                $map[$slug].Add($text)
            }
            $headingCache[$Full] = $map
        }
        $headingCache[$Full]
    }

    function Resolve-LintAnchorTarget {
        <#
        .SYNOPSIS
        Resolves a cross-file target: first relative to the linking file's own directory (with
        and without a .md extension, mirroring how a relative markdown link actually resolves),
        then falling back to a vault-wide basename search.
        #>
        param([Parameter(Mandatory)] [string] $Target, [Parameter(Mandatory)] [string] $SourceDir)
        $clean = $Target.Replace('\', '/').Trim()
        $candidates = if ($clean.ToLowerInvariant().EndsWith('.md')) {
            @((Join-Path $SourceDir $clean))
        }
        else {
            @((Join-Path $SourceDir "$clean.md"), (Join-Path $SourceDir $clean))
        }
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return [pscustomobject]@{ Status = 'found'; Path = (Resolve-Path -LiteralPath $candidate).Path }
            }
        }
        $lastSegment = ($clean -split '/')[-1]
        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($lastSegment).ToLowerInvariant()
        if ($fileIndex.ContainsKey($baseName)) {
            $found = $fileIndex[$baseName]
            if ($found.Count -eq 1) { return [pscustomobject]@{ Status = 'found'; Path = $found[0] } }
            return [pscustomobject]@{ Status = 'ambiguous'; Count = $found.Count }
        }
        [pscustomobject]@{ Status = 'notfound' }
    }

    foreach ($file in $files) {
        $content = Read-LintNote -File $file
        if ($null -eq $content) { continue }
        $contentCache[$file.Full] = $content
        $sourceDir = Split-Path -Parent $file.Full

        foreach ($match in $script:AnchorLinkPattern.Matches($content)) {
            $display = $match.Groups[1].Value
            $target = $match.Groups[2].Value
            $fragment = $match.Groups[3].Value
            $line = Get-LintLineNumber -Content $content -Index $match.Index

            # External links (scheme or protocol-relative) are valid as-is.
            if ($target -match '^[a-zA-Z][a-zA-Z0-9+.\-]*:' -or $target -match '^//') { continue }

            # An inline-code-quoted illustration - a backtick touching both the opening '[' and
            # the closing ')' - is the syntax shown as prose, not a live link.
            $beforeChar = if ($match.Index -gt 0) { $content[$match.Index - 1] } else { $null }
            $afterIndex = $match.Index + $match.Length
            $afterChar = if ($afterIndex -lt $content.Length) { $content[$afterIndex] } else { $null }
            if ($beforeChar -eq '`' -and $afterChar -eq '`') { continue }

            $isSamePage = [string]::IsNullOrEmpty($target)
            if ($isSamePage) { $targetPath = $file.Full }
            else {
                $resolved = Resolve-LintAnchorTarget -Target $target -SourceDir $sourceDir
                if ($resolved.Status -eq 'notfound') {
                    $manual++
                    $findings.Add((New-LintFinding -File $file.Rel -Line $line -Disposition 'flagged' `
                                -Issue 'anchor-link-unresolved' -Detail "$($match.Value) -> target file not found: $target" `
                                -RemedyOptions @((New-LintRemedy -Id 'manual-anchor' -Label 'Resolve by hand - the target file does not exist' -Action 'manual' -Params ([ordered]@{ path = $file.Rel; link = $match.Value; reason = 'target-not-found' })))))
                    continue
                }
                if ($resolved.Status -eq 'ambiguous') {
                    $manual++
                    $findings.Add((New-LintFinding -File $file.Rel -Line $line -Disposition 'flagged' `
                                -Issue 'anchor-link-unresolved' -Detail "$($match.Value) -> ambiguous target: $($resolved.Count) files named '$target' in vault" `
                                -RemedyOptions @((New-LintRemedy -Id 'manual-anchor' -Label 'Resolve by hand - the target name is ambiguous' -Action 'manual' -Params ([ordered]@{ path = $file.Rel; link = $match.Value; reason = 'ambiguous-target' })))))
                    continue
                }
                $targetPath = $resolved.Path
            }

            # A target that is present but cannot be parsed degrades to needs-manual-review
            # rather than failing the pass.
            try { $headingMap = Get-LintHeadingMap -Full $targetPath }
            catch {
                $manual++
                $findings.Add((New-LintFinding -File $file.Rel -Line $line -Disposition 'flagged' `
                            -Issue 'anchor-link-unresolved' -Detail "$($match.Value) -> target could not be read for heading extraction" `
                            -RemedyOptions @((New-LintRemedy -Id 'manual-anchor' -Label 'Resolve by hand - the target could not be read' -Action 'manual' -Params ([ordered]@{ path = $file.Rel; link = $match.Value; reason = 'target-unreadable' })))))
                continue
            }

            $fragSlug = ConvertTo-LintSlug $fragment
            if (-not $headingMap.ContainsKey($fragSlug)) {
                $manual++
                $targetRel = [System.IO.Path]::GetRelativePath($script:VaultRoot, $targetPath).Replace('\', '/')
                $findings.Add((New-LintFinding -File $file.Rel -Line $line -Disposition 'flagged' `
                            -Issue 'anchor-link-unresolved' -Detail "$($match.Value) -> no heading matches '#$fragment' in $targetRel" `
                            -RemedyOptions @((New-LintRemedy -Id 'manual-anchor' -Label 'Resolve by hand - no heading matches the fragment' -Action 'manual' -Params ([ordered]@{ path = $file.Rel; link = $match.Value; reason = 'no-matching-heading' })))))
                continue
            }
            $candidates = $headingMap[$fragSlug]
            if ($candidates.Count -gt 1) {
                $manual++
                $findings.Add((New-LintFinding -File $file.Rel -Line $line -Disposition 'flagged' `
                            -Issue 'anchor-link-ambiguous-heading' -Detail "$($match.Value) -> $($candidates.Count) headings match '#$fragment'" `
                            -RemedyOptions @((New-LintRemedy -Id 'pick-heading' -Label 'Pick which heading the link means' -Action 'pick-heading' -Params ([ordered]@{
                                        path      = $file.Rel
                                        link      = $match.Value
                                        headings  = @($candidates)
                                    })))))
                continue
            }

            $headingText = $candidates[0]
            $displayTrim = $display.Trim()
            $sameText = [string]::Equals($displayTrim, $headingText, [System.StringComparison]::OrdinalIgnoreCase)
            $wikilink = if ($isSamePage) {
                if ($sameText) { "[[#$headingText]]" } else { "[[#$headingText|$displayTrim]]" }
            }
            else {
                $noteName = [System.IO.Path]::GetFileNameWithoutExtension($targetPath)
                if ($sameText) { "[[$noteName#$headingText]]" } else { "[[$noteName#$headingText|$displayTrim]]" }
            }

            $rewritable++
            $findings.Add((New-LintFinding -File $file.Rel -Line $line -Disposition 'reported' `
                        -Issue 'markdown-anchor-link' -Detail "$($match.Value) -> $wikilink" `
                        -RemedyOptions @((New-LintEditPatchRemedy -Id 'rewrite-anchor' -Label "Rewrite as $wikilink" -Rel $file.Rel -OldString $match.Value -NewString $wikilink))))
        }
    }

    New-LintPassResult -Pass 'anchor-links' -ReportOnly $true -FilesScanned $files.Count `
        -Counts ([ordered]@{ rewritable = $rewritable; needsManualReview = $manual }) -Findings $findings
}

# ---------------------------------------------------------------------------
# Pass: filename-conventions
# ---------------------------------------------------------------------------

function Invoke-LintFilenameConventionsPass {
    $files = Get-LintScanFile -IncludeFolders $script:LintIncludeFolders -ExcludeFiles $script:LintExcludeFiles -ExcludePaths $script:LintExcludePaths
    $findings = New-Object System.Collections.Generic.List[object]
    $exceptions = Get-LintSimpleSuppression -FileName 'filename-exceptions.txt'

    foreach ($file in $files) {
        $baseName = $file.BaseName
        $issues = New-Object System.Collections.Generic.List[string]
        $badSegments = New-Object System.Collections.Generic.List[string]

        if ($baseName -match ' ') { $issues.Add('contains spaces') }
        if ($baseName -match '_') { $issues.Add('contains underscores') }

        # Case-SENSITIVE operators throughout: the default -match is case-insensitive and would
        # treat every capitalized segment as lowercase. All-caps names (README) and date-prefixed
        # names are skipped.
        if ($baseName -cnotmatch '^[A-Z0-9]+$' -and $baseName -notmatch '^\d{4}-\d{2}') {
            $segments = $baseName -split '-'
            for ($i = 0; $i -lt $segments.Count; $i++) {
                $segment = $segments[$i]
                if ($segment -cmatch '^[a-z]' -and
                    -not ($i -gt 0 -and $script:TitleLowercaseWords -contains $segment) -and
                    -not ($exceptions -contains $segment)) {
                    $badSegments.Add($segment)
                }
            }
            if ($badSegments.Count -gt 0) { $issues.Add("not Title-Case (segments: $($badSegments -join ', '))") }
        }

        if ($issues.Count -eq 0) { continue }

        # The suggested destination is computed deterministically - separators normalized to
        # hyphens, offending segments capitalized - so the move remedy is dispatchable verbatim.
        $suggested = ($baseName -replace '[ _]+', '-')
        foreach ($segment in $badSegments) {
            $suggested = $suggested -creplace "(?<=^|-)$([regex]::Escape($segment))(?=-|$)", ($segment.Substring(0, 1).ToUpperInvariant() + $segment.Substring(1))
        }
        $parent = (Split-Path -Parent $file.Rel).Replace('\', '/')
        $dest = if ($parent) { "$parent/$suggested.md" } else { "$suggested.md" }

        $remedies = New-Object System.Collections.Generic.List[object]
        $remedies.Add((New-LintRemedy -Id 'rename-file' -Label "Rename to $suggested.md" -Action 'move' -Params ([ordered]@{ source = $file.Rel; dest = $dest })))
        foreach ($segment in $badSegments) {
            $remedies.Add((New-LintSuppressionRemedy -Id "except-$segment" -Label "Record '$segment' as a filename exception" -List 'filename-exceptions' -Entry $segment))
        }

        $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                    -Issue 'filename-convention' -Detail ($issues -join '; ') -RemedyOptions $remedies.ToArray()))
    }

    New-LintPassResult -Pass 'filename-conventions' -ReportOnly $true -FilesScanned $files.Count `
        -Counts ([ordered]@{ violations = $findings.Count }) -Findings $findings
}

# ---------------------------------------------------------------------------
# Pass: frontmatter-schema
#
# TWO composed sources presented as one pass: the shared VaultAccess per-note validator
# (Get-VaultFrontmatterSchemaViolation - the schema encoded once for edit, create, and lint) and
# lint's own filesystem scan, which the shared surface deliberately does not carry because it
# enumerates directories and consults suppression state.
# ---------------------------------------------------------------------------

function Get-LintNavLinkBasename {
    <#
    .SYNOPSIS
    The lowercased link basenames a note declares across its nav.* blocks. A folder: entry is
    not a link, so it is skipped here - the completeness scan tracks those separately.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Blocks)
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($block in $Blocks) {
        if ($block.Key -notin @('nav.contents', 'nav.see_also')) { continue }
        foreach ($item in (Get-VaultFrontmatterListItems $block.Children)) {
            if (-not $item.IsMap) { continue }
            if ($item.Fields.Contains('folder') -and $item.Fields['folder']) { continue }
            if (-not $item.Fields.Contains('link')) { continue }
            if ($item.Fields['link'] -match '\[\[([^\]]+)\]\]') {
                $inner = ((($Matches[1] -split '\|')[0]) -split '#')[0].Trim()
                if ($inner) { [void]$set.Add((($inner -replace '\\', '/') -split '/')[-1]) }
            }
        }
    }
    $set
}

function Expand-LintCorpusReach {
    <#
    .SYNOPSIS
    Corpus routing may hop through an intermediate folder note: the note under scan links a
    Corpus subfolder's folder note, whose own nav.contents routes on to the members inside it.
    Only a FOLDER NOTE is stepped through - routing is a folder note's job, and traversing an
    ordinary doc's prose cross-references would let any member satisfy the check for any other.
    #>
    param(
        [Parameter(Mandatory)] $Declared,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Members
    )
    $folderNotes = @{}
    foreach ($member in $Members) {
        if (Test-LintFolderNote -Rel $member.Rel) { $folderNotes[$member.BaseName.ToLowerInvariant()] = $member }
    }
    $reach = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $queue = New-Object System.Collections.Generic.Queue[string]
    foreach ($name in @($Declared)) { if ($reach.Add($name)) { $queue.Enqueue($name) } }
    while ($queue.Count -gt 0) {
        $name = $queue.Dequeue()
        $key = $name.ToLowerInvariant()
        if (-not $folderNotes.ContainsKey($key)) { continue }
        if (-not $visited.Add($key)) { continue }
        $hopContent = Read-LintNote -File $folderNotes[$key]
        if ($null -eq $hopContent) { continue }
        $hopLines = Get-VaultFrontmatterLines $hopContent
        if ($null -eq $hopLines) { continue }
        foreach ($basename in (Get-LintNavLinkBasename -Blocks (Get-VaultFrontmatterBlocks $hopLines))) {
            if ($reach.Add($basename)) { $queue.Enqueue($basename) }
        }
    }
    $reach
}

function Invoke-LintFrontmatterSchemaPass {
    $files = Get-LintScanFile -IncludeFolders $script:LintIncludeFolders -ExcludeFiles $script:LintExcludeFiles -ExcludePaths $script:LintExcludePaths
    $findings = New-Object System.Collections.Generic.List[object]
    $schemaCount = 0
    $filesystemCount = 0

    # Assigned before the pipeline, never piped from the call: the helper comma-wraps its return
    # so an empty list survives as an empty array, and piping the call directly would hand
    # ForEach-Object that whole array as one item.
    $ignoredEntries = Get-LintSimpleSuppression -FileName 'known-ignored-subfolders.txt'
    $ignoredSubfolders = @($ignoredEntries | ForEach-Object { ConvertTo-LintComparablePath $_ })
    $excludedCmp = @($script:LintExcludePaths | ForEach-Object { ConvertTo-LintComparablePath $_ })

    foreach ($file in $files) {
        $content = Read-LintNote -File $file
        if ($null -eq $content) { continue }

        # --- Source 1: the shared per-note validator. Its structured violation list is rendered
        # into findings here; edit and create throw on the same list. Never fixed - the agentic
        # layer is categorically remedy-only for lint. ---
        $violations = Get-VaultFrontmatterSchemaViolation -Content $content -RelativePath $file.Rel
        foreach ($violation in $violations) {
            $schemaCount++
            $detail = if ($null -ne $violation.EntryIndex) { "$($violation.Field) entry #$($violation.EntryIndex): $($violation.Reason)" } else { $violation.Reason }
            $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                        -Issue "schema:$($violation.Rule)" -Detail $detail))
        }

        $fmLines = Get-VaultFrontmatterLines $content
        $blocks = if ($null -ne $fmLines) { Get-VaultFrontmatterBlocks $fmLines } else { @() }
        $byKey = @{}
        foreach ($block in $blocks) { $byKey[$block.Key] = $block }

        # --- Source 2a: a declared folder: path that no longer resolves. Filesystem-dependent,
        # so it is lint's own rather than the shared validator's. ---
        $declaredFolders = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($navKey in @('nav.contents', 'nav.see_also')) {
            if (-not $byKey.ContainsKey($navKey)) { continue }
            $index = 0
            foreach ($item in (Get-VaultFrontmatterListItems $byKey[$navKey].Children)) {
                $index++
                if (-not $item.IsMap) { continue }
                if (-not ($item.Fields.Contains('folder') -and $item.Fields['folder'])) { continue }
                $declared = $item.Fields['folder']
                [void]$declaredFolders.Add((ConvertTo-LintComparablePath $declared))
                $abs = Join-Path $script:VaultRoot ($declared -replace '/', [System.IO.Path]::DirectorySeparatorChar)
                if (-not (Test-Path -LiteralPath $abs -PathType Container)) {
                    $filesystemCount++
                    # Two options, repair first and escape hatch second, the ordering every other
                    # finding in this file uses. The repair is the LIKELY intent - a stale folder:
                    # path is usually a moved folder, not a dead one - so the destructive option
                    # is not what a caller reading remedyOptions[0] finds.
                    #
                    # A repoint is a removal plus an add: there is no update-entry op, deliberately
                    # (see Add-VaultNavEntry). Ordered ops make that one atomic edit call. The
                    # entry's own description and load_when ride along, so the ONLY slot the caller
                    # fills is the new path - lint knows everything about this entry except where
                    # it was supposed to point.
                    $existingDescription = if ($item.Fields.Contains('description')) { "$($item.Fields['description'])" } else { '' }
                    $removeOp = [ordered]@{ op = 'remove_nav_entry'; key = $navKey; folder = $declared }
                    $addOp = [ordered]@{ op = 'add_nav_entry'; key = $navKey; folder = ''; description = $existingDescription }
                    $repointRequires = @('frontmatter_ops.1.folder')
                    # An entry with no usable description cannot be carried forward: add_nav_entry
                    # refuses an empty one, so the slot becomes the caller's too rather than a
                    # payload that would come back malformed_op.
                    if ([string]::IsNullOrWhiteSpace($existingDescription)) { $repointRequires += 'frontmatter_ops.1.description' }
                    if ($item.Fields.Contains('load_when') -and -not [string]::IsNullOrWhiteSpace("$($item.Fields['load_when'])")) {
                        $addOp['load_when'] = "$($item.Fields['load_when'])"
                    }
                    $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                                -Issue 'folder-entry-unresolved' -Detail "$navKey entry #${index}: folder path does not resolve: '$declared'" `
                                -RemedyOptions @(
                                (New-LintFrontmatterRemedy -Id 'repoint-nav-entry' -Label "Repoint the '$declared' entry at its new path (choose the path)" `
                                        -Rel $file.Rel -Ops @($removeOp, $addOp) -Requires $repointRequires),
                                (New-LintFrontmatterRemedy -Id 'remove-nav-entry' -Label "Drop the '$declared' entry - the folder is gone, not moved" `
                                        -Rel $file.Rel -Ops @([ordered]@{ op = 'remove_nav_entry'; key = $navKey; folder = $declared }))
                            )))
                }
            }
        }

        # --- Source 2b: folder-note completeness. A folder note's job is to route to its
        # children, so the subfolders that physically exist are compared against what it
        # declares. ---
        if (-not (Test-LintFolderNote -Rel $file.Rel)) { continue }
        $declaredLinks = Get-LintNavLinkBasename -Blocks $blocks
        $parentDir = Split-Path -Parent $file.Full

        foreach ($childDir in @(Get-ChildItem -LiteralPath $parentDir -Directory -ErrorAction SilentlyContinue | Where-Object { -not $_.Name.StartsWith('.') })) {
            $childRel = [System.IO.Path]::GetRelativePath($script:VaultRoot, $childDir.FullName).Replace('\', '/')
            $childCmp = ConvertTo-LintComparablePath $childRel
            if ($excludedCmp -contains $childCmp) { continue }
            $name = $childDir.Name

            if ($script:EphemeralBoilerplate.Contains($name)) {
                if ($declaredFolders.Contains($childCmp)) { continue }
                $boilerplate = $script:EphemeralBoilerplate[$name]
                $filesystemCount++
                # The one folder-note remedy that is computed end to end: the standard folders
                # carry standard prose, so the description and load_when come from the table
                # rather than from a judgment. `requires` stays empty and this dispatches verbatim.
                $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                            -Issue 'missing-standard-folder-entry' -Detail "standard folder '$name' has no nav.contents entry" `
                            -RemedyOptions @(
                            (New-LintFrontmatterRemedy -Id 'add-nav-entry' -Label "Add the nav.contents folder entry for '$name'" `
                                    -Rel $file.Rel -Ops @([ordered]@{
                                        op          = 'add_nav_entry'
                                        key         = 'nav.contents'
                                        folder      = $childRel
                                        description = $boilerplate.description
                                        load_when   = $boilerplate.load_when
                                    }))
                        )))
            }
            elseif ($name -eq 'Corpus') {
                $members = @(Get-ChildItem -LiteralPath $childDir.FullName -Recurse -Filter '*.md' -File -ErrorAction SilentlyContinue |
                        ForEach-Object { New-LintFileRecord -Rel ([System.IO.Path]::GetRelativePath($script:VaultRoot, $_.FullName).Replace('\', '/')) -Full $_.FullName })
                $reachable = Expand-LintCorpusReach -Declared $declaredLinks -Members $members
                foreach ($member in $members) {
                    if ($reachable.Contains($member.BaseName)) { continue }
                    $filesystemCount++
                    $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                                -Issue 'unlinked-corpus-member' -Detail "'$($member.Rel)' is not reachable from this folder note's nav links" `
                                -RemedyOptions @(
                                # NOT fully computed, and never was - the entryBlock shape just
                                # hid it behind pre-rendered YAML. What a corpus doc is FOR and
                                # when to load it are the author's judgment; lint knows only that
                                # the doc is unreachable. Both slots ride as empty strings and are
                                # named in `requires`, the way sync carries commit.message.
                                (New-LintFrontmatterRemedy -Id 'add-nav-entry' -Label "Add a nav.contents link entry for $($member.BaseName) (write the description and load_when)" `
                                        -Rel $file.Rel -Ops @([ordered]@{
                                            op          = 'add_nav_entry'
                                            key         = 'nav.contents'
                                            link        = "[[$($member.BaseName)]]"
                                            description = ''
                                            load_when   = ''
                                        }) -Requires @('frontmatter_ops.0.description', 'frontmatter_ops.0.load_when'))
                            )))
                }
            }
            else {
                $referenced = $declaredFolders.Contains($childCmp) -or $declaredLinks.Contains($name)
                if ($referenced -or ($ignoredSubfolders -contains $childCmp)) { continue }
                $filesystemCount++
                $findings.Add((New-LintFinding -File $file.Rel -Line $null -Disposition 'reported' `
                            -Issue 'unreferenced-subfolder' -Detail "'$childRel' has no link or folder entry in its parent folder note" `
                            -RemedyOptions @(
                            # Same honest gap as the corpus case: lint found a subfolder nobody
                            # declared, which says nothing about what it holds or when to load it.
                            (New-LintFrontmatterRemedy -Id 'add-nav-entry' -Label "Add a nav entry for '$childRel' (write the description and load_when)" `
                                    -Rel $file.Rel -Ops @([ordered]@{
                                        op          = 'add_nav_entry'
                                        key         = 'nav.contents'
                                        folder      = $childRel
                                        description = ''
                                        load_when   = ''
                                    }) -Requires @('frontmatter_ops.0.description', 'frontmatter_ops.0.load_when')),
                            (New-LintSuppressionRemedy -Id 'ignore-subfolder' -Label "Record '$childRel' as intentionally unreferenced" -List 'ignored-subfolders' -Entry $childRel)
                        )))
            }
        }
    }

    New-LintPassResult -Pass 'frontmatter-schema' -ReportOnly $true -FilesScanned $files.Count `
        -Counts ([ordered]@{ schemaViolations = $schemaCount; filesystemViolations = $filesystemCount }) -Findings $findings
}

# ---------------------------------------------------------------------------
# Pass: hard-wrap
#
# Report-only, and in no group: safe-block joins are heuristic, which puts them in the
# "unsafe fix" tier. The corruption-safety flags below are locked-in behavior from the skill's
# own unit suite and must not drift.
# ---------------------------------------------------------------------------

$script:ListPrefixRegex = '^(\s*(?:[-*+]|\d+[.)])(?:\s+\[[ xX]\])?\s+)'

# A genuinely hard-wrapped line lands close to the wrapping tool's column on every line except
# the last; an intentionally short line (mini-heading, definition-list label) does not. Chosen
# conservatively above observed intentional-short-line lengths and below observed genuine
# mid-paragraph wrap lengths - biased toward NOT joining, since a missed join is a safe no-op
# but a wrong join risks corrupting content.
$script:MinFullLineLength = 70

function Test-LintFullWidthLine {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Line)
    $Line.TrimEnd().Length -ge $script:MinFullLineLength
}

# A `**Label:** value` definition-list item never folds into a preceding sub-block - two
# adjacent bold-label lines are independent record fields, not a wrapped pair.
function Test-LintBoldLabelLine {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Line)
    [bool]($Line -match '^\s*\*\*[^*\n]+:\*\*')
}

# A line that is, in its entirety, a markdown image reference is an intentionally separate
# reference, never a wrap continuation.
function Test-LintImageEmbedLine {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Line)
    [bool]($Line -match '^\s*!\[[^\]]*\]\([^)]*\)\s*$')
}

function Get-LintEligibleChunk {
    <#
    .SYNOPSIS
    Splits lines into blank-line-delimited chunks, excluding frontmatter and fenced code regions
    entirely - those lines never become part of any chunk.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Lines)

    $chunks = New-Object System.Collections.Generic.List[object]
    $n = $Lines.Count
    $i = 0
    if ($n -gt 0 -and $Lines[0].Trim() -eq '---') {
        $i = 1
        while ($i -lt $n -and $Lines[$i].Trim() -ne '---') { $i++ }
        if ($i -lt $n) { $i++ }
    }

    $inFence = $false
    $fenceChar = $null
    $fenceLen = 0
    $current = New-Object System.Collections.Generic.List[string]
    $currentStart = -1

    for (; $i -lt $n; $i++) {
        $line = $Lines[$i]
        if (-not $inFence -and $line -match '^\s*(`{3,}|~{3,})') {
            if ($current.Count -gt 0) {
                $chunks.Add([pscustomobject]@{ StartLine = $currentStart; Lines = $current.ToArray() })
                $current = New-Object System.Collections.Generic.List[string]
            }
            $inFence = $true
            $fenceChar = $Matches[1].Substring(0, 1)
            $fenceLen = $Matches[1].Length
            continue
        }
        if ($inFence) {
            $closeRegex = '^\s*[{0}]{{{1},}}\s*$' -f [regex]::Escape($fenceChar), $fenceLen
            if ($line -match $closeRegex) { $inFence = $false }
            continue
        }
        if ($line.Trim() -eq '') {
            if ($current.Count -gt 0) {
                $chunks.Add([pscustomobject]@{ StartLine = $currentStart; Lines = $current.ToArray() })
                $current = New-Object System.Collections.Generic.List[string]
            }
            continue
        }
        if ($current.Count -eq 0) { $currentStart = $i + 1 }
        $current.Add($line)
    }
    if ($current.Count -gt 0) {
        $chunks.Add([pscustomobject]@{ StartLine = $currentStart; Lines = $current.ToArray() })
    }
    , $chunks.ToArray()
}

function Test-LintChunkDisqualified {
    <#
    .SYNOPSIS
    True for a chunk containing a table row, blockquote/callout, heading, or thematic break -
    fixed-width formatting there may be intentional, or joining it risks corruption.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Lines)
    foreach ($line in $Lines) {
        if ($line -match '^\s*\|') { return $true }
        if ($line -match '^\s*>') { return $true }
        if ($line -match '^\s*#{1,6}(\s|$)') { return $true }
        if ($line -match '^\s*(-{3,}|\*{3,}|_{3,})\s*$') { return $true }
    }
    $false
}

function Split-LintChunkIntoSubBlock {
    <#
    .SYNOPSIS
    Splits a chunk into join-candidate sub-blocks. A list-marker line always starts a new
    sub-block (a nested item must never fold into its parent); a non-marker line folds into the
    current item only when its leading whitespace exactly matches that item's continuation column
    AND the line already accumulated is full-width. Plain text folds under the same full-width
    gate, so an intentionally short line never absorbs whatever follows it.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Lines)

    $subBlocks = New-Object System.Collections.Generic.List[object]
    $current = @()
    $currentIsList = $false
    $currentContCol = -1
    $currentOffset = 0

    for ($k = 0; $k -lt $Lines.Count; $k++) {
        $line = $Lines[$k]
        if ($line -match $script:ListPrefixRegex) {
            if ($current.Count -gt 0) { $subBlocks.Add([pscustomobject]@{ Offset = $currentOffset; Lines = $current }) }
            $current = @($line)
            $currentOffset = $k
            $currentIsList = $true
            $currentContCol = $Matches[1].Length
            continue
        }
        if ($current.Count -eq 0) {
            $current = @($line)
            $currentOffset = $k
            $currentIsList = $false
            continue
        }
        if ($currentIsList) {
            $line -match '^(\s*)' | Out-Null
            $leadingLen = $Matches[1].Length
            if ($leadingLen -eq $currentContCol -and (Test-LintFullWidthLine $current[$current.Count - 1]) -and -not (Test-LintImageEmbedLine $line)) {
                $current += $line
            }
            else {
                $subBlocks.Add([pscustomobject]@{ Offset = $currentOffset; Lines = $current })
                $current = @($line)
                $currentOffset = $k
                $currentIsList = $false
            }
        }
        else {
            if ((Test-LintFullWidthLine $current[$current.Count - 1]) -and -not (Test-LintBoldLabelLine $line) -and -not (Test-LintImageEmbedLine $line)) {
                $current += $line
            }
            else {
                $subBlocks.Add([pscustomobject]@{ Offset = $currentOffset; Lines = $current })
                $current = @($line)
                $currentOffset = $k
                $currentIsList = $false
            }
        }
    }
    if ($current.Count -gt 0) { $subBlocks.Add([pscustomobject]@{ Offset = $currentOffset; Lines = $current }) }
    , $subBlocks.ToArray()
}

function Get-LintJoinedBlockResult {
    <#
    .SYNOPSIS
    Joins a candidate block into one line, tracking backtick and wikilink state across the break
    points. Corruption safety, all locked-in:
      - balanced backticks/wikilinks -> safe, single-space join throughout;
      - a break inside an unclosed note-link TARGET -> no-space join (vault filenames have no
        spaces, so the break inserted a gap rather than removing one);
      - a break inside a note-link ALIAS -> single-space join (ordinary prose);
      - a break inside an EMBED target -> always flagged; attachment filenames can contain
        spaces, so the safe join cannot be assumed;
      - unbalanced backticks, or a wikilink never closed within the block -> flagged.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Lines)

    $prepped = @($Lines[0])
    for ($k = 1; $k -lt $Lines.Count; $k++) { $prepped += ($Lines[$k] -replace '^\s+', '') }

    $backtickOpen = $false
    $linkState = 'None'   # None | Target | Alias
    $isEmbed = $false
    $unsafe = $false
    $reason = $null
    $sb = New-Object System.Text.StringBuilder

    for ($li = 0; $li -lt $prepped.Count; $li++) {
        $line = $prepped[$li]
        $ci = 0
        while ($ci -lt $line.Length) {
            $c = $line[$ci]
            if ($c -eq '`') {
                $backtickOpen = -not $backtickOpen
                [void]$sb.Append($c)
                $ci++
                continue
            }
            if ($linkState -eq 'None' -and $c -eq '[' -and ($ci + 1) -lt $line.Length -and $line[$ci + 1] -eq '[') {
                $prevChar = if ($ci -gt 0) { $line[$ci - 1] } elseif ($sb.Length -gt 0) { $sb[$sb.Length - 1] } else { $null }
                $isEmbed = ($prevChar -eq '!')
                $linkState = 'Target'
                [void]$sb.Append('[[')
                $ci += 2
                continue
            }
            if ($linkState -ne 'None' -and $c -eq ']' -and ($ci + 1) -lt $line.Length -and $line[$ci + 1] -eq ']') {
                [void]$sb.Append(']]')
                $linkState = 'None'
                $isEmbed = $false
                $ci += 2
                continue
            }
            if ($linkState -eq 'Target' -and $c -eq '|') { $linkState = 'Alias' }
            [void]$sb.Append($c)
            $ci++
        }
        if ($li -lt ($prepped.Count - 1)) {
            if ($linkState -ne 'None' -and $isEmbed) {
                $unsafe = $true
                if (-not $reason) { $reason = 'break inside embed link' }
                [void]$sb.Append(' ')
            }
            elseif ($linkState -eq 'Target') {
                # No-space join: a vault note-link target never contains spaces.
            }
            else { [void]$sb.Append(' ') }
        }
    }

    if ($backtickOpen) {
        $unsafe = $true
        if (-not $reason) { $reason = 'unbalanced code span' }
    }
    if ($linkState -ne 'None') {
        $unsafe = $true
        $reason = if ($isEmbed) { 'unbalanced embed link' } else { 'unbalanced wikilink' }
    }

    [pscustomobject]@{ Safe = (-not $unsafe); Text = $sb.ToString(); Reason = $reason }
}

function Get-LintHardWrapFinding {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Lines)
    $found = New-Object System.Collections.Generic.List[object]
    foreach ($chunk in (Get-LintEligibleChunk -Lines $Lines)) {
        if (Test-LintChunkDisqualified -Lines $chunk.Lines) { continue }
        foreach ($sub in (Split-LintChunkIntoSubBlock -Lines $chunk.Lines)) {
            if ($sub.Lines.Count -lt 2) { continue }
            $result = Get-LintJoinedBlockResult -Lines $sub.Lines
            $found.Add([pscustomobject]@{
                    StartLine = $chunk.StartLine + $sub.Offset
                    LineCount = $sub.Lines.Count
                    Safe      = $result.Safe
                    Text      = $result.Text
                    Reason    = $result.Reason
                    Lines     = $sub.Lines
                })
        }
    }
    , $found.ToArray()
}

function Invoke-LintHardWrapPass {
    $files = Get-LintScanFile -IncludeFolders $script:LintIncludeFolders -ExcludeFiles $script:LintExcludeFiles -ExcludePaths $script:LintExcludePaths
    $findings = New-Object System.Collections.Generic.List[object]
    $joinable = 0
    $flagged = 0

    foreach ($file in $files) {
        $content = Read-LintNote -File $file
        if ($null -eq $content) { continue }
        $lines = $content -split "`r?`n"
        foreach ($block in (Get-LintHardWrapFinding -Lines $lines)) {
            $endLine = $block.StartLine + $block.LineCount - 1
            if ($block.Safe) {
                $joinable++
                $detail = "lines $($block.StartLine)-$endLine join safely into one line"
                $remedy = New-LintEditPatchRemedy -Id 'join-block' -Label "Join lines $($block.StartLine)-$endLine" `
                    -Rel $file.Rel -OldString ($block.Lines -join "`n") -NewString $block.Text
                $finding = New-LintFinding -File $file.Rel -Line $block.StartLine -Disposition 'reported' `
                    -Issue 'hard-wrapped-block' -Detail $detail -RemedyOptions @($remedy)
            }
            else {
                $flagged++
                $detail = "lines $($block.StartLine)-$endLine flagged: $($block.Reason)"
                $finding = New-LintFinding -File $file.Rel -Line $block.StartLine -Disposition 'flagged' `
                    -Issue 'hard-wrapped-block-unsafe' -Detail $detail `
                    -RemedyOptions @((New-LintRemedy -Id 'manual-join' -Label 'Join by hand - the automatic join is not safe here' -Action 'manual' -Params ([ordered]@{ path = $file.Rel; startLine = $block.StartLine; endLine = $endLine; reason = $block.Reason })))
            }
            if ($hard_wrap_inspect) {
                $finding['inspect'] = [ordered]@{ before = @($block.Lines); after = $(if ($block.Safe) { $block.Text } else { $null }) }
            }
            $findings.Add($finding)
        }
    }

    New-LintPassResult -Pass 'hard-wrap' -ReportOnly $true -FilesScanned $files.Count `
        -Counts ([ordered]@{ joinableBlocks = $joinable; flaggedBlocks = $flagged }) -Findings $findings
}

# ===========================================================================
# VALIDATE (arguments). Everything here runs before any pass and outside any lock: the full
# pass set is resolved first, so an unrecognized name refuses the call rather than half-running
# it, and neither the inert-flag refusal nor the path miss reads state a lock would protect.
# ===========================================================================

$resolvedVault = Resolve-VaultToolRoot -Vault $Vault
$script:VaultRoot = $resolvedVault.Root

if ($hard_wrap_inspect -and $Apply) {
    throw (New-VaultToolError -Code 'invalid_argument' -Message 'hard_wrap_inspect is a report-mode affordance and is refused with apply:true - hard-wrap has no fix to apply.' -Data @{ param = 'hard_wrap_inspect'; apply = $true })
}

$requested = New-Object System.Collections.Generic.List[string]
foreach ($token in @($Passes)) {
    $normalized = "$token".Trim().ToLowerInvariant()
    if ($normalized -eq '') {
        throw (New-VaultToolError -Code 'invalid_argument' -Message 'passes contains an empty value.' -Data @{ passes = @($Passes) })
    }
    $expanded = if ($script:Groups.ContainsKey($normalized)) { $script:Groups[$normalized] }
    elseif ($script:AllPasses -contains $normalized) { @($normalized) }
    else {
        throw (New-VaultToolError -Code 'invalid_argument' -Message "'$token' is neither a pass nor a group. Passes: $($script:AllPasses -join ', '). Groups: default, checks, fixes, all. hard-wrap is in no group and must be named explicitly." -Data @{ pass = "$token"; knownPasses = $script:AllPasses; knownGroups = @('default', 'checks', 'fixes', 'all') })
    }
    foreach ($pass in $expanded) { if (-not $requested.Contains($pass)) { $requested.Add($pass) } }
}
# Run in inventory order regardless of how the caller listed them, so two callers asking for the
# same set read the same report.
$passesRun = @($script:AllPasses | Where-Object { $requested.Contains($_) })

# --- Scope roots: absent or empty degrade open (a standalone run injects none). Read through
# Get-Variable because under Set-StrictMode an unset global is an error.
#
# Resolve-VaultScopeRoots returns the two unscoped cases as two different things: a null ambient
# map (standalone) exits via a bare `return @()`, which unrolls to nothing in the output stream
# and lands as $null, while an injected-but-empty entry exits comma-wrapped and lands as a real
# empty array. Both mean unscoped, so normalize to a string[] here and let the Count -gt 0 guards
# below (and Get-LintScanFile's) stay null-safe without repeating the check at every site.
#
# NOT @(...) around the call: that defeats the comma idiom on the injected-but-empty path, giving
# a one-element array holding the empty array. The guards then read an unscoped grant as scoped,
# and Test-VaultPathInScope's [string[]] coercion flattens the inner array to an empty-string
# root - which made every call from a real grant die in ConvertTo-VaultScopeSegments with a raw
# "Scope path is empty." before any pass ran. Both grants.json rows resolve to that shape. ---
$ambientScopeRoots = Get-Variable -Name VaultMcpScopeRoots -Scope Global -ValueOnly -ErrorAction SilentlyContinue
$script:ScopeRoots = [string[]] ((Resolve-VaultScopeRoots -Vault $resolvedVault.Name -ScopeRoots $ambientScopeRoots) ?? @())

# --- path narrowing. A missing target is not_found in either mode. ---
$script:OverrideRootFull = $null
$script:SingleFileRel = $null
$normalizedPath = $null
if ($PSBoundParameters.ContainsKey('path') -and -not [string]::IsNullOrWhiteSpace($Path)) {
    $normalizedPath = $Path.Trim().Replace('\', '/').Trim('/')
    try { $fullPath = Resolve-VaultPath -Path $normalizedPath -Vault $Vault }
    catch {
        throw (New-VaultToolError -Code 'invalid_argument' -Message $_.Exception.Message -Data @{ path = $normalizedPath })
    }
    if ($script:ScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $normalizedPath -ScopeRoots $script:ScopeRoots)) {
        throw (New-VaultToolError -Code 'out_of_scope' -Message "Path is outside the grant's scope for vault `"$($resolvedVault.Name)`": $normalizedPath" -Data @{ vault = $resolvedVault.Name; path = $normalizedPath })
    }
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $script:OverrideRootFull = $fullPath
    }
    elseif (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $script:SingleFileRel = $normalizedPath
        $parent = Split-Path -Parent $fullPath
        $script:OverrideRootFull = if ($parent) { $parent } else { $script:VaultRoot }
    }
    else {
        throw (New-VaultToolError -Code 'not_found' -Message "Nothing at path: $normalizedPath" -Data @{ path = $normalizedPath })
    }
}

# ===========================================================================
# RUN.
# ===========================================================================

$script:Applying = $Apply
$script:FilesWritten = New-Object System.Collections.Generic.List[string]
$script:FilesSkippedUnreadable = New-Object System.Collections.Generic.List[string]

$script:PassDispatch = @{
    'typography'           = { Invoke-LintTypographyPass }
    'trailing-whitespace'  = { Invoke-LintTrailingWhitespacePass }
    'provenance'           = { Invoke-LintProvenancePass }
    'broken-wikilinks'     = { Invoke-LintBrokenWikilinksPass }
    'anchor-links'         = { Invoke-LintAnchorLinksPass }
    'filename-conventions' = { Invoke-LintFilenameConventionsPass }
    'frontmatter-schema'   = { Invoke-LintFrontmatterSchemaPass }
    'hard-wrap'            = { Invoke-LintHardWrapPass }
}

function Invoke-LintPassSet {
    $results = New-Object System.Collections.Generic.List[object]
    try {
        foreach ($pass in $passesRun) {
            $results.Add((& $script:PassDispatch[$pass]))
        }
    }
    catch {
        # Re-throw a clean {code, message, data?} error unchanged; raise partial_state only once
        # a write has actually landed. Streaming writes are the licensed divergence here, and the
        # idempotent fix set is what makes the half-applied state safe to converge by rerunning.
        if ($_.Exception.Message -match '^\s*\{.*"code"') { throw }
        if ($script:FilesWritten.Count -gt 0) {
            throw (New-VaultToolError -Code 'partial_state' -Message "lint applied fixes to $($script:FilesWritten.Count) file(s) and then failed: $($_.Exception.Message). The fix set is idempotent - rerunning converges." -Data @{
                    filesRewritten = $script:FilesWritten.ToArray()
                    error          = $_.Exception.Message
                })
        }
        throw
    }
    , $results.ToArray()
}

function New-LintReport {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $PassResults, [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $CapabilitiesUsed)

    $all = @($PassResults | ForEach-Object { $_.findings })
    $summary = [ordered]@{
        findings     = @($all).Count
        fixed        = @($all | Where-Object { $_.disposition -eq 'fixed' }).Count
        fixable      = @($all | Where-Object { $_.disposition -eq 'fixable' }).Count
        flagged      = @($all | Where-Object { $_.disposition -eq 'flagged' }).Count
        reported     = @($all | Where-Object { $_.disposition -eq 'reported' }).Count
        filesWritten = $script:FilesWritten.ToArray()
    }
    [ordered]@{
        applied                = $Apply
        path                   = $normalizedPath
        passesRun              = @($passesRun)
        passes                 = @($PassResults)
        # Write-class envelope: capabilitiesUsed and filesSkippedUnreadable are always present,
        # [] when empty. lockWarning is the deliberate exception, attached post-lock only when it
        # fires - so it never appears in report mode, which takes no lock at all.
        capabilitiesUsed       = @($CapabilitiesUsed)
        filesSkippedUnreadable = $script:FilesSkippedUnreadable.ToArray()
        summary                = $summary
    }
}

if (-not $Apply) {
    # Report mode takes NO lock and asserts NOTHING: it reads, and a preview that locked would
    # serialize every reader against the vault's one writer for no gain.
    ConvertTo-VaultToolResult -Result (New-LintReport -PassResults (Invoke-LintPassSet) -CapabilitiesUsed @())
    return
}

$lockAbandoned = $false
$result = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$lockAbandoned) -ScriptBlock {
    # Required: the script-scope preference does not reach cmdlets invoked from inside a
    # module-function scriptblock, and without it a failed mutation is non-terminating and gets
    # reported as success.
    $ErrorActionPreference = 'Stop'

    # The key assertion is the first statement after the preference: validate-phase, before the
    # first byte is written, so a denial is insufficient_capability with zero mutation.
    Assert-VaultOpClass -OpClass 'content:write' -Data @{ requiredClass = 'content:write'; passes = @($passesRun) }

    New-LintReport -PassResults (Invoke-LintPassSet) -CapabilitiesUsed @('content:write')
}

# lockWarning is present ONLY when the write inherited an abandoned mutex and is omitted entirely
# otherwise - the envelope's deliberate exception to always-present (Tool-Contracts: Return
# Convention): it is a warning, not a status field, and an always-present null would read as a
# checked-and-clear signal.
if ($lockAbandoned) {
    $result['lockWarning'] = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
