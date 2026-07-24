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
    Enumerates files in a vault - not only ".md" notes - as vault-relative, forward-slash
    paths, via ripgrep. With no -Pattern this is the "everything that exists" census the
    basename-uniqueness pass in move (Plan-Tool-Move) shares with glob (Plan-Tool-Glob), so
    the two tools always agree on what exists rather than drifting to two independent notions
    of "every file in the vault". With -Pattern it is glob's filename-search surface: rg's
    glob matcher filters the walk, so pattern semantics match the native Glob tool by
    construction. Get-VaultNotePath stays the note-only (.md) enumeration `read`/`write`/
    wikilink resolution use; this is the file-general counterpart for `move`/`glob`, per
    Plan-Vault-Tool-Primitives's note-vs-file tool split.

    Binary resolution and invocation are the shared Resolve-VaultRgPath / Invoke-VaultRipgrep
    privates, so every rg consumer resolves the binary and shells out the same way.

    Exclusion rules mirror Plan-Tool-Glob's accepted v1 defaults exactly, since both tools
    shell to the same binary the same way: ripgrep's own defaults respect .gitignore and hide
    dot-directories (.git, .obsidian, .claude, ...), and symlinks are never followed
    (-L/--follow is never passed - the one real vault-escape vector, so the guard is simply
    never opting in). rg runs rooted at the vault directory searching ".", which both makes
    glob patterns match vault-relative paths (native Glob semantics) and structurally confines
    results to the subtree; every result is additionally asserted to resolve under the vault
    root as a second guard, covering Windows junctions and reparse points without reasoning
    about ripgrep's own symlink detection.

    Applies the resolved vault's scope-roots filter the same way Get-VaultNotePath does -
    out-of-scope files are silently dropped, never surfaced as an error.
    .PARAMETER Pattern
    Optional glob pattern (native Glob semantics, e.g. `**/Note-Name.md`, `Projects/**/*.md`).
    When supplied, `-g <pattern> --glob-case-insensitive` is appended so matching is
    case-insensitive on Windows like the native Glob tool. rg exit 2 (a malformed pattern) is
    surfaced as `invalid_pattern` carrying rg's stderr; without a pattern, exit 2 is a real rg
    failure. Omit for the full-vault census.
    .PARAMETER RgPath
    Optional explicit ripgrep executable path (Resolve-VaultRgPath), highest precedence over
    the VAULT_MCP_RG_PATH setting and PATH; throws `dependency_unavailable` naming the path
    tried when it does not resolve.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots),
        [string] $Pattern,
        [string] $RgPath
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath '.' -ScopeRoots $flatScopeRoots

    $rg = Resolve-VaultRgPath -RgPath $RgPath

    # rg runs rooted at the vault directory and searches ".", so a -g pattern matches
    # vault-relative paths (native Glob semantics) rather than the absolute path, and results
    # are structurally confined to the subtree. --files with no -g lists every candidate file -
    # the census set; -g <pattern> --glob-case-insensitive filters it for glob.
    $hasPattern = $PSBoundParameters.ContainsKey('Pattern')
    $rgArgs = @('--files')
    if ($hasPattern) { $rgArgs += @('--glob-case-insensitive', '-g', $Pattern) }
    $rgArgs += @('--', '.')

    $rgResult = Invoke-VaultRipgrep -RgPath $rg -Arguments $rgArgs -WorkingDirectory $resolved.Root

    # Exit-code classification (Plan-Tool-Glob: Shared Ripgrep Layers). 0 = matches follow;
    # 1 = no matches, an empty result, not an error; 2 with a caller pattern = a malformed glob
    # (invalid_pattern, carrying rg's stderr); exit 2 with no pattern or any higher code is a
    # real rg failure with stderr attached - never a discarded diagnostic or a misfiled
    # dependency_unavailable.
    switch ($rgResult.ExitCode) {
        0 { }
        1 { return }
        2 {
            if ($hasPattern) {
                throw (New-VaultToolError -Code 'invalid_pattern' -Message "ripgrep rejected the glob pattern: $($rgResult.Stderr.Trim())" -Data @{ pattern = $Pattern; stderr = $rgResult.Stderr.Trim() })
            }
            throw (New-VaultToolError -Code 'ripgrep_failed' -Message "ripgrep exited with code 2 enumerating '$($resolved.Root)': $($rgResult.Stderr.Trim())" -Data @{ exitCode = 2; stderr = $rgResult.Stderr.Trim() })
        }
        default {
            throw (New-VaultToolError -Code 'ripgrep_failed' -Message "ripgrep exited with code $($rgResult.ExitCode) enumerating '$($resolved.Root)': $($rgResult.Stderr.Trim())" -Data @{ exitCode = $rgResult.ExitCode; stderr = $rgResult.Stderr.Trim() })
        }
    }

    $rootPrefix = $resolved.Root + [System.IO.Path]::DirectorySeparatorChar
    foreach ($line in $rgResult.Stdout) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        # rg prints paths relative to the "." search root (e.g. ".\Projects\Widget.md");
        # resolve each against the vault root, then re-derive the vault-relative form. The
        # containment assert drops anything that resolves outside the vault (junctions,
        # reparse points) as a second guard beyond the structural rooting.
        $full = [System.IO.Path]::GetFullPath($line, $resolved.Root)
        if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $full).Replace('\', '/')
        if ($flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $relative -ScopeRoots $flatScopeRoots)) { continue }
        $relative
    }
}
