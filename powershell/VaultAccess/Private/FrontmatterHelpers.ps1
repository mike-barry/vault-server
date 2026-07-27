# Private/FrontmatterHelpers.ps1 - internal machinery for the frontmatter substrate: the
# terminator-preserving line model, fence bounds, scalar unquoting, op-text hygiene, and the
# agent.instructions item-range splitter.
# PowerShell-only: the frontmatter substrate serves the tool-script contract and has no C#
# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.
#
# The line model is the invariant this whole file exists for: a mutation changes only the
# lines it targets, and every untouched byte - line terminators included - survives verbatim.
# Splitting into (Text, Terminator) pairs and splicing the pair list is what makes that true;
# a `-split "\r?\n"` / `-join "\n"` round trip silently rewrites every terminator in the file.

function ConvertTo-VaultLineList {
    <#
    .SYNOPSIS
    Splits content into an ordered list of @{ Text; Terminator } records, where Text carries no
    terminator and Terminator is the exact "`r`n" / "`n" that followed it ('' at end of input).
    Concatenating Text+Terminator over the list reproduces the input byte for byte, so a mixed
    line-ending file stays mixed through any splice.

    A lone "`r" is NOT a terminator - it is an ordinary character inside a line, matching the
    `\r?\n` split the skill-side substrate used. The list always ends with a final record
    (Text possibly '') so the index model matches that split's trailing-empty-element behavior.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    $lines = New-Object System.Collections.Generic.List[psobject]
    $start = 0
    for ($i = 0; $i -lt $Content.Length; $i++) {
        if ($Content[$i] -ne "`n") { continue }
        $end = $i
        $term = "`n"
        if ($end -gt $start -and $Content[$end - 1] -eq "`r") { $end--; $term = "`r`n" }
        $lines.Add([PSCustomObject]@{ Text = $Content.Substring($start, $end - $start); Terminator = $term })
        $start = $i + 1
    }
    $lines.Add([PSCustomObject]@{ Text = $Content.Substring($start); Terminator = '' })
    return , $lines
}

function ConvertFrom-VaultLineList {
    <#
    .SYNOPSIS
    Reassembles a line list produced by ConvertTo-VaultLineList back into content, terminators
    verbatim.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyCollection()] $Lines)

    $sb = New-Object System.Text.StringBuilder
    foreach ($l in $Lines) { [void]$sb.Append($l.Text); [void]$sb.Append($l.Terminator) }
    $sb.ToString()
}

function Get-VaultFenceBounds {
    <#
    .SYNOPSIS
    Locates the frontmatter fence in a line list: @{ State = 'None'|'Closed'|'Unclosed'; End }.
    End is the index of the closing '---' line, -1 when there is no closed fence.

    'Unclosed' is a distinct state on purpose. The skill-side Get-FrontmatterLines collapsed it
    into $null alongside 'None', which let a set-field call prepend a second complete block onto
    a note that already had a dangling fence; consumers raise frontmatter_integrity on it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Lines)

    if ($Lines.Count -lt 1 -or $Lines[0].Text.Trim() -ne '---') { return @{ State = 'None'; End = -1 } }
    for ($i = 1; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Text.Trim() -eq '---') { return @{ State = 'Closed'; End = $i } }
    }
    return @{ State = 'Unclosed'; End = -1 }
}

function Get-VaultLineTerminatorFor {
    <#
    .SYNOPSIS
    The terminator to give a line inserted next to $Anchor: the anchor's own terminator when it
    has one, else the first terminator present anywhere in the list, else "`n". Keeps an
    insertion into a CRLF file CRLF, and an insertion into a mixed file locally consistent.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Lines, [int] $Anchor = -1)

    if ($Anchor -ge 0 -and $Anchor -lt $Lines.Count -and $Lines[$Anchor].Terminator -ne '') {
        return $Lines[$Anchor].Terminator
    }
    foreach ($l in $Lines) { if ($l.Terminator -ne '') { return $l.Terminator } }
    return "`n"
}

function Assert-VaultFenceMutable {
    <#
    .SYNOPSIS
    Refuses a mutation against a note whose opening '---' never closes. Without this the flat
    setters treat a dangling fence as "no frontmatter" and prepend a second complete block,
    stacking two fences and burying the original keys.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Bounds, [Parameter(Mandatory)] [string] $Operation)

    if ($Bounds.State -eq 'Unclosed') {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message "$Operation refused: the note opens a '---' frontmatter fence that is never closed." `
                -Data @{ reason = 'unclosed_fence' })
    }
}

function Remove-VaultFrontmatterQuote {
    <#
    .SYNOPSIS
    Strips a single layer of matching surrounding quotes from a scalar value. Does NOT unescape:
    there is no escape processing anywhere on the parse side, which is why the write side refuses
    quote characters outright rather than emitting escapes it could not read back.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Value)

    $v = "$Value".Trim()
    if ($v.Length -ge 2 -and
        (($v[0] -eq '"' -and $v[$v.Length - 1] -eq '"') -or
         ($v[0] -eq "'" -and $v[$v.Length - 1] -eq "'"))) {
        return $v.Substring(1, $v.Length - 2)
    }
    return $v
}

function Get-VaultFrontmatterKeyViolation {
    <#
    .SYNOPSIS
    Returns a reason string when $Field is not a legal top-level frontmatter key name, else
    $null. The pattern is the one the block parser recognizes as a top-level key; a name outside
    it would be written but never parsed back as a key.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Field)

    if ([string]::IsNullOrEmpty($Field)) { return 'field name is empty' }
    if ($Field -notmatch '^[A-Za-z0-9_.]+$') {
        return "field name '$Field' is not a legal frontmatter key (allowed: letters, digits, '_', '.')"
    }
    return $null
}

function Get-VaultFrontmatterTextViolation {
    <#
    .SYNOPSIS
    Returns a reason string when caller-supplied scalar text cannot be written into frontmatter
    byte-preservingly, else $null.

    Three refusals, all protecting the same invariant - a single-line insertion must never mint a
    top-level key the caller did not author:
      - newline: ends the line and starts a new one, which the block parser reads as a new key;
      - control characters: unrepresentable in the line-oriented format;
      - the '"' character: directive entries are emitted as `- pinned: "TEXT"` with hardcoded
        quotes and no escaping, so a quote closes the literal early and the rest of the text
        lands as raw YAML. Escaping is not the fix - the parse side strips one quote layer
        without unescaping, so an escaped write would not read back as written.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Value, [string] $Label = 'value')

    if ($null -eq $Value) { return $null }
    if ($Value -match "[`r`n]") { return "$Label contains a line break" }
    if ($Value -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') { return "$Label contains a control character" }
    if ($Value.Contains('"')) { return "$Label contains a '`"' character, which frontmatter scalars cannot carry (no escaping is supported on the read side)" }
    return $null
}

function Get-VaultFrontmatterBlockRange {
    <#
    .SYNOPSIS
    Locates one top-level key's block inside a fenced line list: @{ KeyLine; BlockEnd }, or $null
    when the key is absent. BlockEnd is the last child line's index (== KeyLine when the key has
    no children).

    One scan, shared by every block-aware mutator - the whole-block remover, the
    agent.instructions insert/strip pair, and the nav entry pair. Three hand-copies of "find the
    key line, then advance to the next top-level key" is how they drift.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Lines,
        [Parameter(Mandatory)] $Bounds,
        [Parameter(Mandatory)] [string] $Field
    )

    if ($Bounds.State -ne 'Closed') { return $null }
    $keyLine = -1
    for ($i = 1; $i -lt $Bounds.End; $i++) {
        if ($Lines[$i].Text -match "^$([regex]::Escape($Field)):") { $keyLine = $i; break }
    }
    if ($keyLine -lt 0) { return $null }

    $blockEnd = $keyLine
    for ($i = $keyLine + 1; $i -lt $Bounds.End; $i++) {
        if ($Lines[$i].Text -match '^[A-Za-z0-9_.]+:') { break }
        $blockEnd = $i
    }
    return @{ KeyLine = $keyLine; BlockEnd = $blockEnd }
}

function Get-VaultNavListKey {
    <#
    .SYNOPSIS
    The nav block-list keys an entry op may address. A collection, not a signature: admitting a
    third nav list is one entry here, and the schema validator reads the same list, so the op
    vocabulary and the schema cannot drift into disagreeing about what a nav list is.
    #>
    [CmdletBinding()]
    param()

    return , @('nav.contents', 'nav.see_also')
}

function Get-VaultBlockItemRange {
    <#
    .SYNOPSIS
    Splits a child-line index range into per-item ranges - a new list item starts at each
    top-level '  - ' line. Returns a list of @{ Start; End } in absolute line indices, so a
    mutator can splice whole items out without reserializing the ones it keeps.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Lines, [int] $Start, [int] $End)

    $ranges = New-Object System.Collections.Generic.List[psobject]
    $curStart = -1
    for ($i = $Start; $i -le $End; $i++) {
        if ($Lines[$i].Text -match '^\s*-\s') {
            if ($curStart -ge 0) { $ranges.Add([PSCustomObject]@{ Start = $curStart; End = $i - 1 }) }
            $curStart = $i
        }
    }
    if ($curStart -ge 0) { $ranges.Add([PSCustomObject]@{ Start = $curStart; End = $End }) }
    return , $ranges
}

function Remove-VaultAgentInstructionItem {
    <#
    .SYNOPSIS
    The line-range splicer behind both agent.instructions strippers: removes every list item of
    the given kind (or only the first, under -StopAfterFirst) and drops the whole
    agent.instructions key when nothing would be left. Internal - the two named strippers are the
    surface, because "which entries may be removed how" is the invariant, not the splice.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [ValidateSet('UseSkill', 'Pinned')] [string] $Kind,
        [AllowEmptyString()] [string] $Text,
        [switch] $StopAfterFirst
    )

    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    Assert-VaultFenceMutable -Bounds $bounds -Operation 'agent.instructions strip'
    if ($bounds.State -ne 'Closed') { return $Content }

    $block = Get-VaultFrontmatterBlockRange -Lines $lines -Bounds $bounds -Field 'agent.instructions'
    if ($null -eq $block) { return $Content }
    if ($block.BlockEnd -le $block.KeyLine) { return $Content }

    $ranges = Get-VaultBlockItemRange -Lines $lines -Start ($block.KeyLine + 1) -End $block.BlockEnd
    $doomed = New-Object System.Collections.Generic.List[psobject]
    foreach ($r in $ranges) {
        $first = $lines[$r.Start].Text
        $hit = $false
        if ($Kind -eq 'UseSkill') {
            $hit = $first -match '^\s*-\s*use_skill:'
        }
        elseif ($first -match '^\s*-\s*pinned:\s*(.*)$') {
            # Compared against the parsed (one quote layer stripped) value, which is the same
            # form Add-VaultAgentInstructionEntry -Type Pinned takes, and ordinally: a pinned
            # rule is the user's own text, so a case fold is not ours to apply.
            $hit = (Remove-VaultFrontmatterQuote $Matches[1]) -ceq $Text
        }
        if ($hit) {
            $doomed.Add($r)
            if ($StopAfterFirst) { break }
        }
    }
    if ($doomed.Count -eq 0) { return $Content }

    if ($doomed.Count -eq $ranges.Count) {
        # Nothing would be left under the key, so the key goes too rather than being left as an
        # empty list, which the schema validator reports as an empty block.
        for ($i = $block.BlockEnd; $i -ge $block.KeyLine; $i--) { $lines.RemoveAt($i) }
        return (ConvertFrom-VaultLineList $lines)
    }

    # Last range first so the earlier ranges' indices stay valid as lines are spliced out.
    for ($j = $doomed.Count - 1; $j -ge 0; $j--) {
        for ($i = $doomed[$j].End; $i -ge $doomed[$j].Start; $i--) { $lines.RemoveAt($i) }
    }
    return (ConvertFrom-VaultLineList $lines)
}

function New-VaultFrontmatterBlockText {
    <#
    .SYNOPSIS
    Normalizes a caller-supplied frontmatter block into fenced block text ending in a line
    terminator, ready to have a body appended. Returns '' for an empty or whitespace-only block
    (a note with deliberately no frontmatter).

    Accepts the block either already fenced or as bare inner lines. Two refusals, both
    frontmatter_integrity: an opening fence that never closes, and content after the closing
    fence - the second would let body text ride in through the frontmatter parameter, past the
    body facet's own guards.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Frontmatter)

    if ([string]::IsNullOrWhiteSpace($Frontmatter)) { return '' }

    $lines = ConvertTo-VaultLineList $Frontmatter
    $bounds = Get-VaultFenceBounds $lines
    $term = Get-VaultLineTerminatorFor -Lines $lines -Anchor 0

    if ($bounds.State -eq 'Unclosed') {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message 'Supplied frontmatter opens a "---" fence that is never closed.' `
                -Data @{ reason = 'unclosed_fence' })
    }

    if ($bounds.State -eq 'Closed') {
        $sb = New-Object System.Text.StringBuilder
        for ($i = 0; $i -le $bounds.End; $i++) { [void]$sb.Append($lines[$i].Text); [void]$sb.Append($lines[$i].Terminator) }
        $trailing = ''
        for ($i = $bounds.End + 1; $i -lt $lines.Count; $i++) { $trailing += $lines[$i].Text + $lines[$i].Terminator }
        if ($trailing.Trim() -ne '') {
            throw (New-VaultToolError -Code 'frontmatter_integrity' `
                    -Message 'Supplied frontmatter carries content after its closing "---" fence; body content belongs in the body facet.' `
                    -Data @{ reason = 'content_after_fence' })
        }
        $text = $sb.ToString()
        if (-not $text.EndsWith("`n")) { $text += $term }
        return $text
    }

    $text = '---' + $term + $Frontmatter
    if (-not $text.EndsWith("`n")) { $text += $term }
    return $text + '---' + $term
}

function Get-VaultAgenticKeyMap {
    <#
    .SYNOPSIS
    Maps a note's agent.* / nav.* top-level keys to a normalized text form of their values, for
    the outcome diff. Normalized (terminators collapsed) because this is a comparison, never a
    write - the write side preserves bytes; this side only needs to answer "did the layer move".
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    $map = @{}
    $fmLines = Get-VaultFrontmatterLines $Content
    if ($null -eq $fmLines) { return $map }
    foreach ($b in (Get-VaultFrontmatterBlocks $fmLines)) {
        if ($b.Key -notmatch '^(agent|nav)\.') { continue }
        $map[$b.Key] = (@("$($b.Key): $($b.Inline)") + @($b.Children)) -join "`n"
    }
    return $map
}

function Resolve-VaultNavListKey {
    <#
    .SYNOPSIS
    The canonically-spelled nav block-list key matching $Key case-insensitively, or $null when it
    is not one. Callers normalize through this before writing: the allow-list check is
    case-insensitive (PowerShell's -contains is), so without it a caller's 'Nav.Contents' would
    pass the check and then be WRITTEN as a second, differently-cased key line.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Key)

    # Assigned before piping: the return is comma-wrapped, and piping the call directly would
    # hand the whole array to the filter as one item.
    $navKeys = Get-VaultNavListKey
    return ($navKeys | Where-Object { $_ -ieq $Key } | Select-Object -First 1)
}

function Get-VaultNavTargetViolation {
    <#
    .SYNOPSIS
    Returns a reason string when a nav entry op's addressing parameters are not usable, else
    $null: an unrecognized key, or a link/folder pair that is not exactly one.

    Single-sourced because both entry primitives and both ops check it - the ops so the refusal
    carries an opIndex the caller can act on, the primitives so a direct caller gets the same
    answer. The link/folder rules restate schema violations (link_folder_both /
    link_folder_neither) at op level deliberately: the validator would report them a step later,
    against a note the op has already written into.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()] [AllowNull()] [string] $Key,
        [AllowEmptyString()] [AllowNull()] [string] $Link,
        [AllowEmptyString()] [AllowNull()] [string] $Folder
    )

    $navKeys = Get-VaultNavListKey
    if ($navKeys -notcontains $Key) {
        return "'$Key' is not a nav block-list key (expected one of: $($navKeys -join ', '))"
    }
    $hasLink = -not [string]::IsNullOrWhiteSpace($Link)
    $hasFolder = -not [string]::IsNullOrWhiteSpace($Folder)
    if ($hasLink -and $hasFolder) { return "'link' and 'folder' are mutually exclusive - a nav entry carries exactly one" }
    if (-not $hasLink -and -not $hasFolder) { return "a nav entry requires 'link' or 'folder'" }
    return $null
}

function Get-VaultNavEntry {
    <#
    .SYNOPSIS
    Every entry in a note's $Key nav block, as @{ Start; End; Link; Folder } in ABSOLUTE line
    indices into ConvertTo-VaultLineList $Content - an empty list when the note has no closed
    fence, the key is absent, or the key carries no entries.

    Indices are absolute and content-derived, so a caller that rebuilds the line list from the
    same content gets the same indices; that is what lets the entry mutators splice by range
    instead of reserializing the entries they keep.

    Link and Folder are the entry's parsed (one quote layer stripped) discriminator values, $null
    when the entry does not carry that discriminator. Parsing goes through
    Get-VaultFrontmatterListItems rather than a second regex, so "what an entry is" has one
    definition shared with the schema validator.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Key
    )

    $out = New-Object System.Collections.Generic.List[psobject]
    $lines = ConvertTo-VaultLineList $Content
    $bounds = Get-VaultFenceBounds $lines
    if ($bounds.State -ne 'Closed') { return , $out }
    $block = Get-VaultFrontmatterBlockRange -Lines $lines -Bounds $bounds -Field $Key
    if ($null -eq $block -or $block.BlockEnd -le $block.KeyLine) { return , $out }

    foreach ($r in (Get-VaultBlockItemRange -Lines $lines -Start ($block.KeyLine + 1) -End $block.BlockEnd)) {
        $texts = @()
        for ($i = $r.Start; $i -le $r.End; $i++) { $texts += $lines[$i].Text }
        # Assigned before indexing: the parser's return is comma-wrapped.
        $items = Get-VaultFrontmatterListItems $texts
        $fields = if ($items.Count -gt 0 -and $items[0].IsMap) { $items[0].Fields } else { $null }
        $out.Add([PSCustomObject]@{
                Start  = $r.Start
                End    = $r.End
                Link   = $(if ($null -ne $fields -and $fields.Contains('link')) { $fields['link'] } else { $null })
                Folder = $(if ($null -ne $fields -and $fields.Contains('folder')) { $fields['folder'] } else { $null })
            })
    }
    return , $out
}

function Select-VaultNavEntry {
    <#
    .SYNOPSIS
    The entries in a Get-VaultNavEntry list that a caller's target addresses. -Link and -Folder
    match that discriminator only; -Target matches either, which is the form the before/after
    anchors take (a caller naming an existing entry knows its value, not its discriminator).

    Matching is case-INSENSITIVE, unlike the pinned-entry stripper's -ceq. A pinned entry's text
    is the user's own prose, so a case fold there is not ours to apply; a nav target is a
    denotation - a wikilink or a folder path, both of which the vault and the filesystem resolve
    case-insensitively - so treating '[[Foo]]' and '[[foo]]' as different entries would let a
    duplicate through the add and let a removal silently miss.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] $Entries,
        [string] $Link,
        [string] $Folder,
        [string] $Target
    )

    $hits = New-Object System.Collections.Generic.List[psobject]
    foreach ($e in $Entries) {
        $hit = $false
        if ($PSBoundParameters.ContainsKey('Target')) {
            $hit = ($null -ne $e.Link -and $e.Link -eq $Target) -or ($null -ne $e.Folder -and $e.Folder -eq $Target)
        }
        elseif (-not [string]::IsNullOrEmpty($Link)) { $hit = ($null -ne $e.Link -and $e.Link -eq $Link) }
        elseif (-not [string]::IsNullOrEmpty($Folder)) { $hit = ($null -ne $e.Folder -and $e.Folder -eq $Folder) }
        if ($hit) { $hits.Add($e) }
    }
    return , $hits
}

function Get-VaultOpValue {
    <#
    .SYNOPSIS
    Reads a named member from an op object, accepting either a hashtable (PowerShell callers) or
    a PSCustomObject (a deserialized JSON ops array). Member names match case-insensitively;
    $null when absent.
    #>
    [CmdletBinding()]
    param($Op, [Parameter(Mandatory)] [string] $Name)

    if ($null -eq $Op) { return $null }
    if ($Op -is [System.Collections.IDictionary]) {
        foreach ($k in $Op.Keys) { if ("$k" -ieq $Name) { return $Op[$k] } }
        return $null
    }
    $prop = $Op.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($prop) { return $prop.Value }
    return $null
}
