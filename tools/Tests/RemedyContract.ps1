# tools/Tests/RemedyContract.ps1 - the RemedyOption payload-validation walker, shared by
# lint.Tests.ps1 and sync.Tests.ps1. NOT a Pester file: Invoke-Pester discovers *.Tests.ps1 only,
# and ScriptToolLoader scans tools/*.ps1 top-directory-only, so a plain .ps1 here is loaded by
# neither. Dot-sourced from each suite's top-level BeforeAll.
#
# WHAT THIS CLOSES. A remedy's `params` is a payload FOR another tool, named by the remedy's own
# `tool` field. Nothing used to check that the keys in that payload were parameters the named tool
# actually declares, and the cost of that gap is on the record: `state_token` and `on_conflict`
# rode in sync's directive remedies for as long as they existed without binding to anything.
# Shape assertions did not catch it because a shape assertion asserts the shape it was written
# against. This walks what the suite EMITS and checks it against what the target tool DECLARES, so
# neither side can drift without the other noticing.

# ---------------------------------------------------------------------------
# What a tool advertises.
# ---------------------------------------------------------------------------

$script:VaultAdvertisedParameterCache = @{}

function Get-VaultToolAdvertisedParameter {
    <#
    .SYNOPSIS
    The parameter names a tool script ADVERTISES, read the way the server reads them: a static AST
    parse of the `param()` block, one name per ParameterAst, exactly ScriptToolLoader.LoadOne's
    `ast.ParamBlock?.Parameters` walk.

    WHY NOT (Get-Command $path).Parameters. Every tool script now declares [CmdletBinding()], so
    Get-Command reports the eleven common parameters (Verbose, ErrorAction, OutVariable, ...)
    alongside the declared ones. Those are precisely what ScriptToolDispatcher's argument-name
    check refuses - the param() block is the entire client-facing argument surface - so a
    Get-Command-backed check would pass a `params` key the real dispatcher would reject as
    invalid_argument. The AST parse is the faithful mirror; Get-Command is a superset of the
    contract. It also needs no compile of the script and no module state.
    #>
    param([Parameter(Mandatory)] [string] $ToolsDir, [Parameter(Mandatory)] [string] $Tool)

    $path = Join-Path $ToolsDir "$Tool.ps1"
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $full = (Resolve-Path -LiteralPath $path).Path
    if ($script:VaultAdvertisedParameterCache.ContainsKey($full)) { return $script:VaultAdvertisedParameterCache[$full] }

    $ast = [System.Management.Automation.Language.Parser]::ParseFile($full, [ref] $null, [ref] $null)
    $names = @()
    if ($null -ne $ast.ParamBlock) {
        $names = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
    }
    $script:VaultAdvertisedParameterCache[$full] = $names
    $names
}

# ---------------------------------------------------------------------------
# What the tools construct (static), and what they emit (runtime).
# ---------------------------------------------------------------------------

function Get-VaultRemedyConstructionSite {
    <#
    .SYNOPSIS
    Every call to a remedy builder in a tool script, read off the AST: its literal `-Action`, its
    literal `-Tool`, and whether `-Tool` was passed at all. Static counterpart to the runtime walk
    below - it sees construction sites no fixture happens to reach, which is how a newly added
    remedy is caught before it can ride untested.
    #>
    param([Parameter(Mandatory)] [string] $Path, [Parameter(Mandatory)] [string] $Builder)

    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $Path).Path, [ref] $null, [ref] $null)
    $calls = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq $Builder
        }, $true)

    foreach ($call in $calls) {
        $action = $null
        $tool = $null
        $hasTool = $false
        $elements = @($call.CommandElements)
        for ($i = 0; $i -lt $elements.Count; $i++) {
            $element = $elements[$i]
            if ($element -isnot [System.Management.Automation.Language.CommandParameterAst]) { continue }
            # `-Tool $null` parses as a parameter with a separate argument element; `-Tool:'edit'`
            # would carry it inline. Both forms are read.
            $value = if ($null -ne $element.Argument) { $element.Argument }
            elseif ($i + 1 -lt $elements.Count) { $elements[$i + 1] }
            else { $null }
            switch ($element.ParameterName) {
                'Action' { if ($value -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $action = $value.Value } }
                'Tool' {
                    $hasTool = $true
                    if ($value -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $tool = $value.Value }
                }
            }
        }
        [pscustomobject] @{
            Action  = $action
            Tool    = $tool
            HasTool = $hasTool
            Line    = $call.Extent.StartLineNumber
        }
    }
}

function Get-VaultRemedyOption {
    <#
    .SYNOPSIS
    Every RemedyOption anywhere in a parsed tool result, found STRUCTURALLY.

    HOW `Add-SyncAction`'s ENTRIES STAY OUT. sync carries a second, unrelated `action` vocabulary:
    Add-SyncAction appends {repo, action, detail} entries to actions[] / plannedActions[] naming
    what the tool did or will do (commit, merge, push, fast-forward, reattach, ...). Those are not
    RemedyOptions. The walk excludes them by SHAPE, never by field name or by container name: a
    RemedyOption carries `id`, `label`, `action` and `params`, and an Add-SyncAction entry carries
    none of the first, second or fourth. Matching on `action` alone - or on "things under
    actions[]" - would either sweep them in or hard-code the very container names that make the
    check brittle.

    `tool` is deliberately NOT part of the predicate even though it is part of the shape. If it
    were, a remedy that forgot to set it would simply stop being recognised as a remedy and the
    walk would skip it in silence - the check would go green on exactly the drift it exists to
    catch. Presence of `tool` is asserted by the caller instead.

    HOW THE `action` COLLISION STAYS STRAIGHT. add-suppression's `params` carries a key named
    `action` (lint_suppressions' own wire parameter, valued 'add') inside a remedy whose top-level
    field is also `action`. Both spellings are correct. Nothing here flattens a remedy: the walk
    STOPS at the `params` boundary and never descends into it, so a payload key can never be read
    as a remedy field, a nested payload can never be mistaken for a remedy, and every consumer
    below reads `$remedy.action` and `$remedy.params.action` as the two separate things they are.
    #>
    param($Node)

    if ($null -eq $Node) { return }
    if ($Node -is [string] -or $Node -is [System.ValueType]) { return }

    if ($Node -is [System.Collections.IList]) {
        foreach ($item in $Node) { Get-VaultRemedyOption -Node $item }
        return
    }
    if ($Node.PSObject.TypeNames[0] -ne 'System.Management.Automation.PSCustomObject') { return }

    $names = @($Node.PSObject.Properties.Name)
    $isRemedy = ($names -contains 'id') -and ($names -contains 'label') -and
                ($names -contains 'action') -and ($names -contains 'params')

    if ($isRemedy) { $Node }
    foreach ($property in $Node.PSObject.Properties) {
        # The payload boundary. See above.
        if ($isRemedy -and $property.Name -eq 'params') { continue }
        Get-VaultRemedyOption -Node $property.Value
    }
}

function Test-VaultRemedyContract {
    <#
    .SYNOPSIS
    Checks one RemedyOption against the contract and returns a violation string per breach (none
    means it holds). Returns rather than asserts so a failing suite names every offending remedy at
    once instead of stopping at the first.
    .PARAMETER ToolContract
    action -> tool name, or action -> $null for the advisory actions that dispatch nothing. This is
    the SPEC half of the check and is deliberately hand-written; what must never be hand-written is
    the ENUMERATION of remedies, which comes from Get-VaultRemedyOption over real output.
    #>
    param(
        [Parameter(Mandatory)] $Remedy,
        [Parameter(Mandatory)] [hashtable] $ToolContract,
        [Parameter(Mandatory)] [string] $ToolsDir
    )

    $id = $Remedy.id
    $action = $Remedy.action
    $where = "remedy '$id' (action '$action')"

    if (-not (@($Remedy.PSObject.Properties.Name) -contains 'tool')) {
        "$where carries no 'tool' field at all"
        return
    }
    if (-not $ToolContract.ContainsKey($action)) {
        "$where has an action the contract table does not classify"
        return
    }

    $expected = $ToolContract[$action]
    $tool = $Remedy.tool
    if ($null -eq $expected) {
        if ($null -ne $tool) { "$where is advisory and must carry tool:null, but carries '$tool'" }
        return
    }
    if ($tool -cne $expected) {
        "$where must dispatch to '$expected' but carries tool '$tool'"
        return
    }

    $advertised = Get-VaultToolAdvertisedParameter -ToolsDir $ToolsDir -Tool $tool
    if ($null -eq $advertised) {
        "$where names tool '$tool', which is not a tool script"
        return
    }
    foreach ($key in @($Remedy.params.PSObject.Properties.Name)) {
        # Ordinal, not case-insensitive. The binder would reconcile `Path` with `path`, but the
        # one-name-per-argument rule is that a params key is spelled verbatim as the param() block
        # spells it; a case-insensitive check would let a second spelling in.
        if ($advertised -cnotcontains $key) {
            "$where carries params key '$key', which is not a parameter of '$tool' ($($advertised -join ', '))"
        }
    }
}
