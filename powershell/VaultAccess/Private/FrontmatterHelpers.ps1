# Private/FrontmatterHelpers.ps1 - internal machinery for the frontmatter substrate: the fence
# locator, the YAML parse/emit pair every mutation runs through, the composer that is the module's
# only fence writer, op-text hygiene, and the shared nav/op accessors.
# PowerShell-only: the frontmatter substrate serves the tool-script contract and has no C#
# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.
#
# The split this file rests on: locating the block is a line-level question with a grammar small
# enough to state in a sentence, while READING it is where blank lines, nesting, indentation,
# quoting, and escaping live - so powershell-yaml owns the second and nothing here hand-rolls it.
# Every mutation is a dictionary or list operation over a parsed tree that is re-emitted whole.
# Nothing here splices lines, which is why nothing here reasons about terminators: a composed
# note is written LF.
#
# The locator's grammar is not ours to invent, and Frontmatter.Tests.ps1 pins it differentially
# against Markdig's YamlFrontMatterBlock - the standard's implementation - over every note in the
# vault plus a table of degenerate shapes. Markdig is the authority on what a frontmatter block
# is; it exercises that authority at test time rather than on every call, so the substrate carries
# no runtime dependency on a library PowerShell bundles without declaring.

function Get-VaultFrontmatterSpan {
    <#
    .SYNOPSIS
    Locates a note's frontmatter block: @{ InnerStart; InnerLength; BodyOffset } in character
    indices into $Content, or $null when the note has no block.

    InnerStart/InnerLength delimit the block's inner text - what lies between the opening fence
    line's terminator and the closing fence line, with the last inner line's terminator excluded.
    BodyOffset is where the body begins, one line terminator past the closing fence.

    The grammar, in full: the opening fence is the note's FIRST line and is exactly '---' once
    trailing whitespace is discounted, so a leading space or a fourth dash disqualifies it; the
    closing fence is a later line that is exactly '---' or '...' under the same rule. An opening
    fence nothing closes is not a block - the standard reads it as a thematic break followed by a
    paragraph - and neither is a pair with nothing between them, which is two thematic breaks.

    That last case is the one place this deliberately does NOT follow Markdig, because Markdig
    does not follow itself: it reads '---' / '---' as two thematic breaks under LF and as a real
    block under CRLF. Empty is empty under either terminator here.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    if ([string]::IsNullOrEmpty($Content)) { return $null }

    $firstBreak = $Content.IndexOf("`n")
    if ($firstBreak -lt 0) { return $null }
    if ($Content.Substring(0, $firstBreak).TrimEnd(" `t`r") -ne '---') { return $null }

    $innerStart = $firstBreak + 1
    $pos = $innerStart
    while ($pos -le $Content.Length) {
        $next = $Content.IndexOf("`n", $pos)
        $lineEnd = if ($next -lt 0) { $Content.Length } else { $next }
        $line = $Content.Substring($pos, $lineEnd - $pos).TrimEnd(" `t`r")
        if ($line -eq '---' -or $line -eq '...') {
            # The inner region stops before the terminator that ends the last inner line, and that
            # terminator is not part of it. A lone "`r" is not a terminator - it is an ordinary
            # character inside a line - so only a "`r" immediately preceding the "`n" comes off.
            $innerEnd = $pos
            if ($innerEnd -gt $innerStart) {
                $innerEnd--
                if ($innerEnd -gt $innerStart -and $Content[$innerEnd - 1] -eq "`r") { $innerEnd-- }
            }
            if ($innerEnd -le $innerStart) { return $null }
            # Taken from the terminator rather than from the fence text, so trailing whitespace on
            # the closing line belongs to the fence. Derived the other way it leaks into the body
            # as a " `n" fragment - and the body facet is what `edit` matches a patch against and
            # `read part: body` hands back.
            $bodyOffset = if ($next -lt 0) { $Content.Length } else { $next + 1 }
            return @{ InnerStart = $innerStart; InnerLength = $innerEnd - $innerStart; BodyOffset = $bodyOffset }
        }
        if ($next -lt 0) { break }
        $pos = $next + 1
    }
    return $null
}

function ConvertFrom-VaultFrontmatterText {
    <#
    .SYNOPSIS
    Parses a frontmatter block's inner text into the mutable ordered dictionary every mutator
    works against. An empty or whitespace-only block gives an empty dictionary.

    Ordered because key order is the note's, not ours: a reserialize that reshuffled doc.* and
    nav.* would rewrite every note the first time any field was touched.

    Refuses with frontmatter_integrity when the located block will not parse as a YAML mapping.
    Only the write side calls this - a read hands back the raw block text and never parses it - so
    an unparseable block makes a note unwritable rather than unreadable.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [AllowNull()] [string] $Text,
        [Parameter(Mandatory)] [string] $Operation
    )

    if ([string]::IsNullOrWhiteSpace($Text)) { return [ordered]@{} }

    try { $parsed = ConvertFrom-Yaml -Yaml $Text -Ordered }
    catch {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message "$Operation refused: the note's frontmatter block is not valid YAML - $($_.Exception.Message)" `
                -Data @{ reason = 'frontmatter_unparsed' })
    }
    if ($null -eq $parsed) { return [ordered]@{} }
    if ($parsed -isnot [System.Collections.IDictionary]) {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message "$Operation refused: the note's frontmatter block is not a mapping of top-level keys." `
                -Data @{ reason = 'frontmatter_not_a_map' })
    }
    return $parsed
}

function ConvertTo-VaultFrontmatterText {
    <#
    .SYNOPSIS
    Emits a frontmatter dictionary as block inner text, LF-terminated; '' for an empty dictionary,
    which is what makes an emptying removal drop the fence rather than leave a '---' / '---' pair.

    WithIndentedSequences reproduces the vault's existing list indentation exactly, so the
    reserialize diff is quote style alone. ConvertTo-Yaml emits CRLF unconditionally and the
    normalization here is the substrate's whole line-ending policy in one place.
    #>
    [CmdletBinding()]
    param([AllowNull()] $Map)

    if ($null -eq $Map -or $Map.Count -eq 0) { return '' }
    $text = ConvertTo-Yaml -Data $Map -Options WithIndentedSequences
    $text = $text -replace "`r`n", "`n"
    if (-not $text.EndsWith("`n")) { $text += "`n" }
    return $text
}

function Join-VaultNoteContent {
    <#
    .SYNOPSIS
    Composes a note from a frontmatter dictionary and a body, LF throughout. The module's ONLY
    fence writer, which is what makes two guarantees structural rather than remembered: an empty
    dictionary emits no fence at all, and no mutator can emit a shape the emitter would not.
    #>
    [CmdletBinding()]
    param([AllowNull()] $Map, [Parameter(Mandatory)] [AllowEmptyString()] [string] $Body)

    $normalizedBody = $Body -replace "`r`n", "`n"
    $yaml = ConvertTo-VaultFrontmatterText -Map $Map
    if ($yaml -eq '') { return $normalizedBody }
    return "---`n" + $yaml + "---`n" + $normalizedBody
}

function Resolve-VaultFrontmatterKey {
    <#
    .SYNOPSIS
    The dictionary's own spelling of the key matching $Field, or $null when it carries no such
    key. Callers resolve through this before writing so a caller's casing updates the existing key
    rather than minting a second one beside it - the same reason the nav ops canonicalize.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Map, [Parameter(Mandatory)] [string] $Field)

    foreach ($k in @($Map.Keys)) { if ("$k" -ieq $Field) { return "$k" } }
    return $null
}

function Assert-VaultFrontmatterCreatable {
    <#
    .SYNOPSIS
    Refuses to prepend a fresh block to a note whose first line already looks like a fence.

    Markdig reads an unclosed '---' as a thematic break, so such a note is all body and a mutator
    would happily write a second, complete block above the user's stranded metadata. The state is
    no longer a parse outcome, so this is a write-side nicety rather than the old three-way fence
    classification - and it carries its own reason, not the retired unclosed_fence.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content, [Parameter(Mandatory)] [string] $Operation)

    $firstLine = ($Content -split "`n", 2)[0]
    if ($firstLine.Trim() -eq '---') {
        throw (New-VaultToolError -Code 'frontmatter_integrity' `
                -Message "$Operation refused: the note's first line is a '---' fence that no frontmatter block closes, so a new block would strand the text below it." `
                -Data @{ reason = 'apparent_fence' })
    }
}

function Get-VaultFrontmatterKeyViolation {
    <#
    .SYNOPSIS
    Returns a reason string when $Field is not a legal top-level frontmatter key name, else
    $null. Holds the line against a tool minting arbitrary keys until the key registry lands.
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
    Returns a reason string when caller-supplied scalar text carries a control character, else
    $null.

    One gate, and it is a policy about content rather than an artifact of a format. A YAML emitter
    escapes whatever it is handed - a line break becomes a block scalar, a double quote needs no
    handling at all - so the refusals that existed only because a line-splicing writer could not
    escape went with it. What is left is that frontmatter values are agent-facing text rendered
    into terminals and logs, and ANSI escapes are control characters.

    It REFUSES rather than strips. Silently rewriting a caller's value would report success while
    changing what they asked for - worst on agent.instructions, where it would quietly edit a
    directive future agents obey.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Value, [string] $Label = 'value')

    if ($null -eq $Value) { return $null }
    if ($Value -match '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]') { return "$Label contains a control character" }
    return $null
}

function Remove-VaultFrontmatterKey {
    <#
    .SYNOPSIS
    Drops one top-level key, returning the content unchanged when the key or the block is absent
    and dropping the fence when that empties the block.

    The single removal both public removal primitives delegate to. They stay a pair because the
    ROUTING is the policy - it is what lets a flat-scalar op be refused against a block field
    before it reaches a removal - but there is one removal underneath, so the two cannot drift.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Field,
        [Parameter(Mandatory)] [string] $Operation
    )

    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { return $Content }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation $Operation

    $key = Resolve-VaultFrontmatterKey -Map $map -Field $Field
    if ($null -eq $key) { return $Content }
    $map.Remove($key)
    return (Join-VaultNoteContent -Map $map -Body $split.Body)
}

function Remove-VaultAgentInstructionItem {
    <#
    .SYNOPSIS
    The list filter behind both agent.instructions strippers: removes every entry of the given
    kind (or only the first, under -StopAfterFirst) and drops the whole agent.instructions key
    when nothing would be left. Internal - the two named strippers are the surface, because "which
    entries may be removed how" is the invariant, not the filter.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [ValidateSet('UseSkill', 'Pinned')] [string] $Kind,
        [AllowEmptyString()] [string] $Text,
        [switch] $StopAfterFirst
    )

    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { return $Content }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation 'agent.instructions strip'

    $entries = Get-VaultFrontmatterEntryList -Map $map -Field 'agent.instructions'
    if ($null -eq $entries -or $entries.Count -eq 0) { return $Content }

    $kept = New-Object 'System.Collections.Generic.List[object]'
    $removed = 0
    foreach ($entry in $entries) {
        $hit = $false
        if ($Kind -eq 'UseSkill') {
            $hit = $null -ne (Get-VaultEntryFieldValue -Entry $entry -Name 'use_skill')
        }
        else {
            $pinned = Get-VaultEntryFieldValue -Entry $entry -Name 'pinned'
            # Ordinally: a pinned rule is the user's own text, so a case fold is not ours to apply.
            $hit = $null -ne $pinned -and $pinned -ceq $Text
        }
        if ($hit -and -not ($StopAfterFirst -and $removed -gt 0)) { $removed++; continue }
        $kept.Add($entry)
    }
    if ($removed -eq 0) { return $Content }

    $key = Resolve-VaultFrontmatterKey -Map $map -Field 'agent.instructions'
    if ($kept.Count -eq 0) {
        # Nothing would be left under the key, so the key goes too rather than being left as an
        # empty list, which the schema validator reports as an empty block.
        $map.Remove($key)
    }
    else { $map[$key] = $kept }
    return (Join-VaultNoteContent -Map $map -Body $split.Body)
}

function New-VaultFrontmatterBlockText {
    <#
    .SYNOPSIS
    Normalizes a caller-supplied frontmatter block into LF-terminated fenced block text, ready to
    have a body appended. Returns '' for an empty or whitespace-only block (a note with
    deliberately no frontmatter).

    Accepts the block either already fenced or as bare inner lines. Two refusals, both
    frontmatter_integrity: an opening fence that never closes, and content after the closing
    fence - the second would let body text ride in through the frontmatter parameter, past the
    body facet's own guards.

    These two survive the retirement of the note-level unclosed state deliberately. There the file
    is whatever the standard says it is; here the caller has DECLARED "this text is frontmatter",
    so text that will not hold as such is a caller error rather than a reading of the file.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Frontmatter)

    if ([string]::IsNullOrWhiteSpace($Frontmatter)) { return '' }

    $lines = @(($Frontmatter -replace "`r`n", "`n") -split "`n")
    if ($lines[0].Trim() -eq '---') {
        $close = -1
        for ($i = 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -eq '---') { $close = $i; break }
        }
        if ($close -lt 0) {
            throw (New-VaultToolError -Code 'frontmatter_integrity' `
                    -Message 'Supplied frontmatter opens a "---" fence that is never closed.' `
                    -Data @{ reason = 'unclosed_fence' })
        }
        $trailing = ($lines[($close + 1)..($lines.Count - 1)] -join "`n")
        if ($close -lt $lines.Count - 1 -and $trailing.Trim() -ne '') {
            throw (New-VaultToolError -Code 'frontmatter_integrity' `
                    -Message 'Supplied frontmatter carries content after its closing "---" fence; body content belongs in the body facet.' `
                    -Data @{ reason = 'content_after_fence' })
        }
        return (($lines[0..$close] -join "`n") + "`n")
    }

    $inner = ($lines -join "`n")
    if (-not $inner.EndsWith("`n")) { $inner += "`n" }
    return "---`n" + $inner + "---`n"
}

function Get-VaultAgenticKeyMap {
    <#
    .SYNOPSIS
    Maps a note's agent.* / nav.* top-level keys to a canonical text form of their values, for the
    outcome diff. Canonical (re-emitted) because this is a comparison, never a write: the question
    is only "did the layer move", so a reformat that changed quoting must not read as a change.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Content)

    $map = @{}
    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { return $map }
    # Tolerant on purpose: the diff runs on both sides of every write, including against a note
    # the caller has not asked to repair, so an unparseable block reports "no agentic layer"
    # rather than failing the classification the mutators will refuse on anyway.
    try { $parsed = ConvertFrom-Yaml -Yaml $split.Frontmatter -Ordered } catch { return $map }
    if ($parsed -isnot [System.Collections.IDictionary]) { return $map }

    foreach ($key in @($parsed.Keys)) {
        if ("$key" -notmatch '^(agent|nav)\.') { continue }
        $one = [ordered]@{}
        $one["$key"] = $parsed[$key]
        $map["$key"] = (ConvertTo-VaultFrontmatterText -Map $one)
    }
    return $map
}

function Resolve-VaultNavListKey {
    <#
    .SYNOPSIS
    The canonically-spelled nav block-list key matching $Key case-insensitively, or $null when it
    is not one. Callers normalize through this before writing: the allow-list check is
    case-insensitive (PowerShell's -contains is), so without it a caller's 'Nav.Contents' would
    pass the check and then be reported back under the caller's own spelling.
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
    Every entry in a note's $Key nav block, as @{ Index; Link; Folder } - an empty list when the
    note has no block, the key is absent, or the key carries no entries.

    Index is the entry's position in the parsed list, which is all a mutator needs now that
    entries are spliced as list elements rather than as line ranges. Link and Folder are the
    entry's discriminator values, $null when the entry does not carry that discriminator.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Content,
        [Parameter(Mandatory)] [string] $Key
    )

    $out = New-Object System.Collections.Generic.List[psobject]
    $split = Split-VaultNoteContent $Content
    if ($split.FenceState -ne 'Closed') { return , $out }
    $map = ConvertFrom-VaultFrontmatterText -Text $split.Frontmatter -Operation "Nav entry read '$Key'"

    $entries = Get-VaultFrontmatterEntryList -Map $map -Field $Key
    if ($null -eq $entries) { return , $out }

    $i = 0
    foreach ($entry in $entries) {
        $out.Add([PSCustomObject]@{
                Index  = $i
                Link   = (Get-VaultEntryFieldValue -Entry $entry -Name 'link')
                Folder = (Get-VaultEntryFieldValue -Entry $entry -Name 'folder')
            })
        $i++
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
