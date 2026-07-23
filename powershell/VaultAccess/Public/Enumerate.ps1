# Public/Enumerate.ps1 - vault enumeration and wikilink resolution.
# Mirrored core: Resolve-WikiLink <-> VaultAccess.ResolveWikilink, and Get-VaultNotePath's
# enumeration <-> VaultAccess.EnumerateNotes (private C# support for ResolveWikilink). Changes
# to wikilink-resolution semantics land as a shared tests/cases/*.json case. Get-VaultFilePath
# is PowerShell-only (the ripgrep file census for move/glob) with no C# counterpart.
# See ../VaultAccess.psm1 for the full mirror policy.

function Get-VaultNotePath {
    <#
    .SYNOPSIS
    Enumerates all notes in a vault as vault-relative, forward-slash paths, skipping
    dot-directories (.git, .obsidian, ...) and - when the vault has a non-empty scoped
    grant - any note outside those folder-prefix roots (Test-VaultPathInScope). Out-of-scope
    notes are silently dropped from the result, not surfaced as an error - a search or
    wikilink resolution built on this enumeration (Resolve-WikiLink) sees a naturally
    scope-filtered result set.

    $ScopeRoots is the grant's per-vault map (hashtable keyed by vault name, or $null); the
    entry for the resolved vault is picked out via Resolve-VaultScopeRoots before use, so a
    scope root configured for a different vault the grant can access never applies here.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath '.' -ScopeRoots $flatScopeRoots

    Get-ChildItem -LiteralPath $resolved.Root -Filter '*.md' -File -Recurse -Force |
        ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $_.FullName).Replace('\', '/')
            $segments = $relative -split '/'
            $dirSegments = if ($segments.Count -gt 1) { $segments[0..($segments.Count - 2)] } else { @() }
            if ($dirSegments | Where-Object { $_.StartsWith('.') }) { return }
            if ($flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $relative -ScopeRoots $flatScopeRoots)) { return }
            $relative
        }
}

function Resolve-WikiLink {
    <#
    .SYNOPSIS
    Resolves a wikilink to a vault-relative note path. Accepts every convention form:
    [[Name]], bare Name, [[Folder/Name]] (path-qualified, matched on trailing path
    segments), and either with #heading and/or |display suffixes, which are ignored.
    A trailing .md extension on the link itself is also stripped and ignored.
    Returns $null when nothing matches; throws when the link is ambiguous.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Link,
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots)
    )

    $target = $Link.Trim()
    if ($target.StartsWith('[[') -and $target.EndsWith(']]')) { $target = $target.Substring(2, $target.Length - 4) }
    $target = ($target -split '\|')[0]
    $target = ($target -split '#')[0]
    $target = $target.Trim().Replace('\', '/').Trim('/')
    if ($target.ToLowerInvariant().EndsWith('.md')) { $target = $target.Substring(0, $target.Length - 3) }
    if (-not $target) { throw "Empty wikilink: $Link" }

    $qualified = $target.Contains('/')
    $found = @(Get-VaultNotePath -Vault $Vault -ScopeRoots $ScopeRoots | Where-Object {
        $noteNoExt = $_.Substring(0, $_.Length - 3)
        if ($qualified) {
            $noteNoExt -ieq $target -or $noteNoExt.EndsWith("/$target", [System.StringComparison]::OrdinalIgnoreCase)
        }
        else {
            ($noteNoExt -split '/')[-1] -ieq $target
        }
    })

    switch ($found.Count) {
        0 { return $null }
        1 { return $found[0] }
        default { throw "Ambiguous wikilink [[$target]]: $($found -join ', ')" }
    }
}

function Get-VaultFilePath {
    <#
    .SYNOPSIS
    Enumerates every file in a vault - not only ".md" notes - as vault-relative,
    forward-slash paths, via ripgrep. This is the basename-census enumeration primitive
    move's ambiguity gate (Plan-Tool-Move) and glob (Plan-Tool-Glob) are designed to
    share, so the two tools always agree on what exists rather than drifting to two
    independent notions of "every file in the vault". Get-VaultNotePath stays the
    note-only (.md) enumeration `read`/`write`/wikilink resolution use; this is the
    file-general counterpart for `move`/`glob`, per Plan-Vault-Tool-Primitives's
    note-vs-file tool split.

    Exclusion rules mirror Plan-Tool-Glob's accepted v1 defaults exactly, since both
    tools shell to the same binary the same way: ripgrep's own defaults respect
    .gitignore and hide dot-directories (.git, .obsidian, .claude, ...), and symlinks are
    never followed (-L/--follow is never passed - the one real vault-escape vector, so
    the guard is simply never opting in). Every result is additionally asserted to
    resolve under the vault root as a second guard, covering Windows junctions and
    reparse points without reasoning about ripgrep's own symlink detection.

    Applies the resolved vault's scope-roots filter the same way Get-VaultNotePath does -
    out-of-scope files are silently dropped, never surfaced as an error.
    .PARAMETER RgPath
    Path to the ripgrep executable. Defaults to resolving "rg" off PATH; throws a
    dependency_unavailable-shaped error (see New-VaultToolError) when it cannot be found,
    naming the path tried, per Plan-Tool-Glob.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots),
        [string] $RgPath
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath '.' -ScopeRoots $flatScopeRoots

    if (-not $RgPath) {
        $cmd = Get-Command -Name 'rg' -CommandType Application -ErrorAction SilentlyContinue
        if (-not $cmd) {
            throw (New-VaultToolError -Code 'dependency_unavailable' -Message 'ripgrep (rg) could not be resolved on PATH.' -Data @{ pathTried = 'rg' })
        }
        $RgPath = $cmd.Source
    }
    elseif (-not (Test-Path -LiteralPath $RgPath -PathType Leaf)) {
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message "ripgrep (rg) could not be resolved at '$RgPath'." -Data @{ pathTried = $RgPath })
    }

    # --files with no -g pattern lists every file rg would ever consider a match candidate -
    # exactly the "everything that exists" set a basename census needs. Argument array, never
    # a composed shell string, so nothing here is shell-interpreted.
    $rgOutput = & $RgPath '--files' '--' $resolved.Root 2>$null
    if ($LASTEXITCODE -gt 1) {
        throw (New-VaultToolError -Code 'dependency_unavailable' -Message "ripgrep exited with code ${LASTEXITCODE} enumerating '$($resolved.Root)'.")
    }

    $rootPrefix = $resolved.Root + [System.IO.Path]::DirectorySeparatorChar
    foreach ($line in $rgOutput) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $full = [System.IO.Path]::GetFullPath($line)
        if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $full).Replace('\', '/')
        if ($flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $relative -ScopeRoots $flatScopeRoots)) { continue }
        $relative
    }
}
