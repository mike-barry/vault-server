# Public/FrontmatterRegistry.ps1 - the frontmatter registry: the single declaration of which
# top-level keys the vault's tooling recognizes, what each non-agentic key's value must look like,
# which keys are agentic and therefore gated behind agentic:write, and which paths carry
# frontmatter the vault's tooling does not own at all.
# PowerShell-only: this substrate serves the tool-script contract (edit / create / lint) and has
# no C# counterpart in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.
#
# The registry is a POSITIVE ALLOWLIST and it fails closed. An unrecognized key is an error rather
# than something implicitly classified as safe, which is the inversion that matters: agentic:write
# is required by a key's MEMBERSHIP in the agentic set, never by a pattern match against its name.
# The prefix scan this replaces classified anything it did not recognize as non-agentic, so a key
# the parser could not see was silently waved through - the bypass this file exists to close.
#
# Single-sourced on purpose. Extending the schema is one edit here: the nav entry ops' key
# allow-list (Get-VaultNavListKey), the agentic classification behind the outcome diff
# (Get-VaultAgenticKeyMap), create's whole-block validation, and edit's per-op validation all read
# this table rather than restating any part of it.
#
# TWO TOOLS, TWO EXTENTS, and the asymmetry is deliberate. `create` validates the whole block - its
# caller supplies every key, so all of it is new and all of it is checked. `edit` validates only
# the keys an op TOUCHES, and passes an unrecognized key already in the note through untouched.
# Every path by which a key ENTERS the vault is still closed, because create admits only registry
# keys and edit can only write them. Do not "tighten" edit to whole-block: that would make the six
# Tasks/ notes carrying Obsidian's own obsidianUIMode unwritable until the key was stripped, which
# is the outcome the split exists to avoid.

function Get-VaultFrontmatterKeyRegistry {
    <#
    .SYNOPSIS
    The key registry as an ordered dictionary of canonical key name to
    @{ Key; Agentic; NavList; Pattern; Expectation }.

      Agentic     - membership in the agentic set. A write touching one requires agentic:write.
      NavList     - the key is a nav block list, so the nav entry ops may address it.
      Pattern     - the value pattern a non-agentic key's scalar value must match; $null for an
                    agentic key, whose value shape is the schema validator's business.
      Expectation - human/model-readable statement of that pattern, for the refusal message.

    Derived from a powershell-yaml parse of every frontmatter-bearing note across the vault and the
    server's test-vault (345 files, 319 with a block, measured 2026-07-30), not from memory.

    doc.type is a CHARACTER CLASS, not an enum. The vault has no authoritative list of document
    types - Document-Conventions.md defers to a Known-Types table that does not exist - and the 13
    values in use are a mix of established types and one-offs nothing can tell apart from the
    outside. A curated list is data rather than a value pattern; the character class is what stops
    a doc.type carrying arbitrary smuggled text in the meantime, and it keeps lint's set-doc-type
    remedy usable, which an enum guessed at now would break on exactly the notes it is meant to fix.

    `scope` is allowlisted bare, as it is spelled today. Renaming it to tasks.scope for consistency
    with the other namespaced keys is follow-on work, not this registry's.

    Three key groups found in the corpus are deliberately NOT here, and none needs grandfathering -
    because edit validates only the keys an op touches, the notes carrying them stay writable and
    the keys survive untouched; they simply cannot be written through the tooling:
      obsidianUIMode                              - Obsidian's own UI preference, not vault metadata
      status / title                              - a superseded convention nothing will author again
      name / description / disable-model-invocation - Claude Code's skill schema, under .claude/skills
    #>
    [CmdletBinding()]
    param()

    # A date STRING. The build parses YAML under the 1.2 core schema, so 2026-07-28 arrives as a
    # string rather than a date - this is a format check on text, not a type test.
    $date = '^\d{4}-\d{2}-\d{2}$'

    return [ordered]@{
        'doc.created'        = @{ Key = 'doc.created'; Agentic = $false; NavList = $false; Pattern = $date; Expectation = 'a YYYY-MM-DD date' }
        'doc.archived'       = @{ Key = 'doc.archived'; Agentic = $false; NavList = $false; Pattern = $date; Expectation = 'a YYYY-MM-DD date' }
        'doc.type'           = @{ Key = 'doc.type'; Agentic = $false; NavList = $false; Pattern = '^[A-Za-z-]{1,20}$'; Expectation = '1 to 20 letters and dashes' }
        'scope'              = @{ Key = 'scope'; Agentic = $false; NavList = $false; Pattern = '^[\s\S]*$'; Expectation = 'a string' }
        'agent.instructions' = @{ Key = 'agent.instructions'; Agentic = $true; NavList = $false; Pattern = $null; Expectation = 'a block list of directives' }
        'nav.contents'       = @{ Key = 'nav.contents'; Agentic = $true; NavList = $true; Pattern = $null; Expectation = 'a block list of nav entries' }
        'nav.see_also'       = @{ Key = 'nav.see_also'; Agentic = $true; NavList = $true; Pattern = $null; Expectation = 'a block list of nav entries' }
    }
}

function Resolve-VaultFrontmatterRegistryKey {
    <#
    .SYNOPSIS
    The registry's canonical spelling of the key matching $Field case-insensitively, or $null when
    the registry does not carry it.

    Case-insensitive because the vault's key casing is canonical, not the caller's: a caller
    naming 'DOC.Type' means doc.type and gets doc.type back, so a write updates the existing key
    rather than minting a second one beside it.
    .PARAMETER Field
    A top-level frontmatter key name.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Field)

    if ([string]::IsNullOrEmpty($Field)) { return $null }
    foreach ($k in (Get-VaultFrontmatterKeyRegistry).Keys) { if ("$k" -ieq $Field) { return "$k" } }
    return $null
}

function Test-VaultFrontmatterKeyAgentic {
    <#
    .SYNOPSIS
    Whether a top-level key belongs to the agentic layer - the layer a write may only touch with
    agentic:write.

    Registry membership is the CLASSIFIER. The agent.* / nav.* prefix survives only as a backstop
    that can escalate, never as one that can clear: a key outside the registry that nonetheless
    sits in those namespaces is treated as agentic, so a foreign agent.* key already in a note
    still counts as agentic layer for the outcome diff. Nothing is classified as non-agentic by
    failing to match a pattern - that is precisely the bypass this registry replaced.
    .PARAMETER Field
    A top-level frontmatter key name.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $Field)

    if ([string]::IsNullOrEmpty($Field)) { return $false }
    $key = Resolve-VaultFrontmatterRegistryKey -Field $Field
    if ($null -ne $key) { return [bool](Get-VaultFrontmatterKeyRegistry)[$key].Agentic }
    return ($Field -match '^(agent|nav)\.')
}

function Get-VaultFrontmatterRegistryViolation {
    <#
    .SYNOPSIS
    Returns a reason string when a top-level key - and, when -Value is supplied, its value - is not
    something the tooling may write, else $null.

    Two rules, in order:
      1. An UNRECOGNIZED key is a violation. This replaces the old character-class test, which
         admitted any name matching a charset and so let a tool mint unauthored keys.
      2. A recognized NON-AGENTIC key's value must match its registry pattern, and must be a scalar
         rather than a list or a map. Enforcing the value and not just the name is the point: a
         caller blocked from inventing a key could otherwise put arbitrary text into a legitimate
         one, which is the same smuggling in a narrower channel.

    An agentic key has no value pattern applied here - its gate is agentic:write and its shape is
    the schema validator's business - so a caller checking one gets $null from a bound -Value.

    -Value unbound checks the key alone, which is what a removal needs: it names a key without
    supplying a value.
    .PARAMETER Field
    Top-level frontmatter key name.
    .PARAMETER Value
    The value being written. Bind it to check the value pattern as well as the key.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()] [AllowNull()] [string] $Field,
        [AllowEmptyString()] [AllowNull()] $Value
    )

    if ([string]::IsNullOrEmpty($Field)) { return 'field name is empty' }
    $key = Resolve-VaultFrontmatterRegistryKey -Field $Field
    if ($null -eq $key) {
        $known = ((Get-VaultFrontmatterKeyRegistry).Keys -join ', ')
        return "'$Field' is not a frontmatter key this vault's tooling recognizes (known keys: $known)"
    }
    if (-not $PSBoundParameters.ContainsKey('Value')) { return $null }

    $entry = (Get-VaultFrontmatterKeyRegistry)[$key]
    if ($entry.Agentic) { return $null }

    if ($Value -is [System.Collections.IDictionary]) { return "'$key' expects $($entry.Expectation), found a mapping" }
    if ($Value -isnot [string] -and $Value -is [System.Collections.IEnumerable]) { return "'$key' expects $($entry.Expectation), found a list" }

    $text = if ($null -eq $Value) { '' } else { "$Value" }
    $reason = Get-VaultFrontmatterTextViolation -Value $text -Label "'$key' value"
    if ($reason) { return $reason }
    if ($text -notmatch $entry.Pattern) {
        return "'$key' expects $($entry.Expectation), found '$text'"
    }
    return $null
}

function Assert-VaultFrontmatterRegistry {
    <#
    .SYNOPSIS
    Validates a WHOLE parsed frontmatter tree against the registry and asserts agentic:write when
    the tree carries any agentic key. Throws; returns nothing.

    This is create's extent - every key the caller supplied - and it runs BEFORE the tree is
    emitted, so a refusal happens with nothing written and nothing returned. The tree that passes
    is the tree that gets serialized, which is what removes the second reading a smuggled key used
    to hide in: there is no longer a caller byte string that one reader parses and another does not.
    .PARAMETER Map
    A parsed frontmatter tree.
    .PARAMETER Operation
    Operation name, for the refusal message.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowNull()] $Map,
        [Parameter(Mandatory)] [string] $Operation
    )

    if ($null -eq $Map) { return }

    $agentic = New-Object System.Collections.Generic.List[string]
    foreach ($rawKey in @($Map.Keys)) {
        $key = "$rawKey"
        if (Test-VaultFrontmatterKeyAgentic -Field $key) { $agentic.Add($key) }
        $reason = Get-VaultFrontmatterRegistryViolation -Field $key -Value $Map[$rawKey]
        if ($reason) {
            throw (New-VaultToolError -Code 'malformed_op' -Message "$Operation refused: $reason" -Data @{ field = $key; reason = $reason })
        }
    }

    if ($agentic.Count -gt 0) {
        # Asserted here as well as on the outcome diff at the end of the write. The diff is the
        # authority on what actually changed; this one is the registry's own statement that a
        # declared agentic key was supplied, and it fires before the tree is serialized.
        Assert-VaultOpClass -OpClass 'agentic:write' -Data @{ requiredClass = 'agentic:write'; agenticKeysChanged = @($agentic | Sort-Object) }
    }
}

function Get-VaultExcludedFrontmatterPath {
    <#
    .SYNOPSIS
    The vault-relative path prefixes whose frontmatter the vault's tooling does not touch, as
    forward-slashed, lower-cased, trailing-slash-free strings.

    One list, one member. `.claude/skills/` carries CLAUDE CODE's frontmatter schema rather than
    the vault's - `name` / `description` / `disable-model-invocation` in place of `doc.*` - so none
    of it is in the key registry above and every file under it would refuse on the first key
    anyway. The reason the exclusion exists as its own rule is shape rather than schema: three
    skills carry a `description: >` folded scalar, and a reserialization re-emits the fold as a
    literal block whose paragraphs are single 200-435 character lines (measured 2026-07-30 across
    all 18 skill files). Semantically identical - folding is defined to produce one string - and
    exactly the shape those blocks exist for, destroyed.

    `.claude/guides/` is deliberately NOT here. Those are vault notes that happen to live under a
    dot directory, and they carry the vault's own frontmatter.

    Deliberately not a config surface and not an abstraction. Test-VaultPathInScope is the server's
    only existing scoping facility and it is per-grant and allow-only, so there is nothing here to
    reuse; generalizing one is follow-on work rather than this list's business.
    #>
    [CmdletBinding()]
    param()

    return , @('.claude/skills')
}

function Test-VaultFrontmatterPathExcluded {
    <#
    .SYNOPSIS
    True when a vault-relative path sits under one of Get-VaultExcludedFrontmatterPath's prefixes.

    Matched CASE-INSENSITIVELY, on a path normalized first: separators folded to '/', and '.' and
    '..' segments resolved away. Both matter as anti-evasion rather than as convenience - the
    server runs on Windows where '\' and '/' are interchangeable, and `Areas/../.claude/skills/x.md`
    names the same file as `.claude/skills/x.md`. A caller must not reach an excluded file by
    spelling it differently.

    A prefix match is on whole segments: '.claude/skillset/x.md' does not match '.claude/skills'.
    .PARAMETER RelativePath
    The note's vault-relative path.
    #>
    [CmdletBinding()]
    param([AllowEmptyString()] [AllowNull()] [string] $RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath)) { return $false }

    $segments = New-Object System.Collections.Generic.List[string]
    foreach ($segment in (($RelativePath -replace '\\', '/') -split '/')) {
        if ($segment -eq '' -or $segment -eq '.') { continue }
        if ($segment -eq '..') {
            # A '..' that would climb above the vault root is kept as a literal segment rather than
            # dropped: such a path is out of contract, and silently rebasing it at the root is how a
            # normalizer invents a match - or an escape - the caller never wrote.
            if ($segments.Count -gt 0 -and $segments[$segments.Count - 1] -ne '..') { $segments.RemoveAt($segments.Count - 1) }
            else { $segments.Add('..') }
            continue
        }
        $segments.Add($segment)
    }
    $normalized = ($segments -join '/').ToLowerInvariant()

    foreach ($prefix in (Get-VaultExcludedFrontmatterPath)) {
        if ($normalized -eq $prefix -or $normalized.StartsWith($prefix + '/', [System.StringComparison]::Ordinal)) { return $true }
    }
    return $false
}
