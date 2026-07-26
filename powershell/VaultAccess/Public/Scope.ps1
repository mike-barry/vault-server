# Public/Scope.ps1 - path-scope containment and grant checks.
# Mirrored core: Test-VaultPathInScope <-> VaultAccess.IsPathInScope, Resolve-VaultScopeRoots
# <-> VaultAccess.ResolveVaultScopeRoots, Assert-VaultOperationAllowed <-> EnsureAllowed.
# Changes to scope-containment semantics land as a shared tests/cases/*.json case.
# Assert-VaultOpClass is PowerShell-only: the C# host enforces op-class in its dispatcher, not
# in VaultAccess.cs. See ../VaultAccess.psm1 for the full mirror policy.

function Test-VaultPathInScope {
    <#
    .SYNOPSIS
    True if $Path falls under any of $ScopeRoots - folder-prefix containment matched by path
    segment, not raw string prefix, so a root of "Projects/Foo" does not match
    "Projects/Foo-Other/...". Empty $ScopeRoots means unscoped (always in scope). Every root
    (and the path itself) is normalized before any match is attempted, so one malformed root
    makes the whole check throw regardless of whether a different root would have matched -
    an unscopeable query errors rather than silently degrading. Mirrors the C# side's
    VaultAccess.IsPathInScope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [AllowNull()] [string[]] $ScopeRoots
    )

    if ($null -eq $ScopeRoots -or $ScopeRoots.Count -eq 0) { return $true }

    $pathSegments = ConvertTo-VaultScopeSegments -Path $Path
    $rootSegmentsList = @($ScopeRoots | ForEach-Object { , (ConvertTo-VaultScopeSegments -Path $_) })

    foreach ($rootSegments in $rootSegmentsList) {
        if ($rootSegments.Count -gt $pathSegments.Count) { continue }
        $isMatch = $true
        for ($i = 0; $i -lt $rootSegments.Count; $i++) {
            if ($rootSegments[$i] -ine $pathSegments[$i]) { $isMatch = $false; break }
        }
        if ($isMatch) { return $true }
    }
    return $false
}

function Resolve-VaultScopeRoots {
    <#
    .SYNOPSIS
    Resolves a grant's per-vault $ScopeRoots hashtable down to the flat, already-vault-scoped
    root list for one specific $Vault - the shape every chokepoint function (and
    Assert-VaultOperationAllowed, Test-VaultPathInScope) actually operates on. $ScopeRoots
    being $null means "no grant in play at all" (direct/no-grant callers) - unscoped for every
    vault, returns an empty array. A non-null $ScopeRoots missing an entry for $Vault (matched
    case-insensitively) is a grant configuration invariant violation - throws, mirroring the
    C# side's VaultAccess.ResolveVaultScopeRoots, which throws InvalidOperationException for
    the same case: every vault a grant can access must have an explicit entry (an empty set for
    full access). This is where "a scope root configured for one vault must not leak into
    another vault the same grant can access" is enforced: only the entry keyed to $Vault is
    ever consulted.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Vault,
        [AllowNull()] $ScopeRoots
    )

    if ($null -eq $ScopeRoots) { return @() }
    $key = @($ScopeRoots.Keys) | Where-Object { $_ -ieq $Vault } | Select-Object -First 1
    if ($null -eq $key) {
        throw "Grant configuration invariant violated: vault '$Vault' has no scope-roots entry in a non-null scope-roots map. Every vault a grant can access must have an explicit entry (an empty set for full access) - this should be impossible for any grant built through GrantStore.Load or GrantStore.Mint."
    }
    , @($ScopeRoots[$key])
}

function Assert-VaultOperationAllowed {
    <#
    .SYNOPSIS
    Grant-check hook: checks a resolved vault-relative path against the grant's folder-prefix
    $ScopeRoots before a read, write, or enumeration proceeds. Op-class enforcement happens
    separately in the C# host's ScriptToolDispatcher (grants.json); this hook is path scope
    only, distinct from which vault a grant may touch at all (Resolve-VaultRoot).

    Unlike the other chokepoint functions, $ScopeRoots here is already the flat, resolved root
    list for $Vault (see Resolve-VaultScopeRoots) - callers look it up once and reuse it for
    both this check and any per-note filtering they do themselves (e.g. Get-VaultNotePath).
    $ScopeRoots null or empty means the caller's grant is unscoped for this vault: full access,
    so this is a no-op. The literal path "." - used by Get-VaultNotePath to gate the listing
    operation itself, not any one note - is always allowed here even under a scoped grant;
    per-note filtering happens inside Get-VaultNotePath after listing, via Test-VaultPathInScope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Vault,
        [Parameter(Mandatory)] [string] $RelativePath,
        [AllowNull()] [string[]] $ScopeRoots
    )

    if ($null -eq $ScopeRoots -or $ScopeRoots.Count -eq 0) { return }
    if ($RelativePath -eq '.') { return }
    if (-not (Test-VaultPathInScope -Path $RelativePath -ScopeRoots $ScopeRoots)) {
        throw "Path is outside the grant's scope for vault `"$Vault`": $RelativePath"
    }
}

function Assert-VaultOpClass {
    <#
    .SYNOPSIS
    In-script op-class floor check: throws unless the calling grant holds $OpClass. Lets a tool
    whose declared floor op-class (the class the C# dispatcher already gated the call on via
    grant.Allows(tool.OpClass)) admit it, still require a *higher* class before an elevated
    operation - e.g. a content:read tool that performs a content:write mutation only on some
    branch asserts content:write there, so a content:read-only grant is denied in-script rather
    than at listing time.

    $GrantClasses defaults to the host-injected $Global:VaultMcpOpClasses (set by
    ScriptToolRunner right before invoking a tool script, in the same runspace - see its
    InvokeAsync doc comment), mirroring how the path-scope chokepoints default -ScopeRoots to the
    injected VaultMcpScopeRoots global. The $null-vs-empty distinction is load-bearing and matches
    how Test-VaultPathInScope treats an unscoped ($null) grant:

      - $GrantClasses is $null  -> no host context was injected (a standalone `pwsh -File` run, or
        a Pester test calling the function directly). Degrade open: return silently, exactly as an
        unscoped scope-roots map is treated as "no restriction in play".
      - $GrantClasses is an empty array -> host context WAS injected but the grant holds no op
        classes (e.g. the minimum-access floor). This is a real, authenticated "holds nothing"
        state and must DENY every asserted class - it is not the same as absent context.
      - $OpClass is a member (matched case-insensitively) -> pass (return).
      - otherwise -> throw insufficient_capability naming the missing class.

    Matching is case-insensitive by design: op-class names are case-insensitive everywhere -
    the C# OpClassRegistry canonicalizes lookups case-insensitively - and this gate follows
    the same rule via `-contains`' case-insensitive default.
    .PARAMETER OpClass
    The op class this operation requires beyond the tool's dispatcher-gated floor.
    .PARAMETER GrantClasses
    The classes the calling grant holds; defaults to the host-injected $Global:VaultMcpOpClasses.
    .PARAMETER Data
    Optional structured payload attached to the refusal's `data` member - retry material such as
    `{opIndex, requiredClass}`. Omitted, the thrown JSON is byte-identical to a `{code, message}`
    refusal with no data, so existing callers and tests are unaffected. It exists because the
    error convention forbids a caller catching this refusal and re-shaping it to add context: the
    context has to be attachable at the throw.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $OpClass,
        $GrantClasses = $Global:VaultMcpOpClasses,
        $Data
    )

    # $null means "no injected host context at all" - degrade open, same posture as an unscoped
    # scope-roots map. An empty (non-null) set is distinct: an injected but empty key ring denies.
    if ($null -eq $GrantClasses) { return }

    if ($GrantClasses -contains $OpClass) { return }

    # Built through the shared New-VaultToolError (Public/ToolResult.ps1) so this refusal carries
    # the same { code, message, data? } shape every other coded refusal does - the code is the
    # stable contract, the message is human-facing and names the missing class.
    $message = "Grant does not hold the required op class '$OpClass' for this operation."
    if ($PSBoundParameters.ContainsKey('Data')) {
        throw (New-VaultToolError -Code 'insufficient_capability' -Message $message -Data $Data)
    }
    throw (New-VaultToolError -Code 'insufficient_capability' -Message $message)
}
