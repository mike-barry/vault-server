# Public/Enumerate.ps1 - vault enumeration and content search.
# Get-VaultNotePath is the note-only (.md) enumeration read/write use. Get-VaultFilePath (the
# ripgrep file census for move/glob) and Search-VaultContent (the ripgrep content search for
# grep) are PowerShell-only, with no C# counterpart: both build their result-shaping on top of
# the shared Private/RipgrepHelpers primitives (Resolve-VaultRgPath / Invoke-VaultRipgrep).
# See ../VaultAccess.psm1 for the full mirror policy.

function Get-VaultNotePath {
    <#
    .SYNOPSIS
    Enumerates all notes in a vault as vault-relative, forward-slash paths, skipping
    dot-directories (.git, .obsidian, ...) and - when the vault has a non-empty scoped
    grant - any note outside those folder-prefix roots (Test-VaultPathInScope). Out-of-scope
    notes are silently dropped from the result, not surfaced as an error - a consumer built
    on this enumeration sees a naturally scope-filtered result set.

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

function Search-VaultContent {
    <#
    .SYNOPSIS
    grep's content-search surface: runs ripgrep for a regex $Pattern over the vault and emits
    Tier-1 plain-text output lines, one per match/file, with every path normalized to
    vault-relative forward slashes. The content analogue of Get-VaultFilePath: it shares the
    Private/RipgrepHelpers primitives (Resolve-VaultRgPath binary resolution, Invoke-VaultRipgrep
    invocation) but shapes its own output, so grep and glob resolve the binary and shell out the
    same way while returning different result shapes.

    rg runs rooted at the vault directory searching "." (or the vault-relative $Path when given),
    so pattern semantics are native-Grep regex and output paths are vault-relative. Each emitted
    path is re-resolved against the vault root and asserted to stay under it (the junction/
    reparse-point guard beyond the structural rooting), then run through the scope post-filter -
    out-of-scope lines are silently dropped, never surfaced as an error, exactly as
    Get-VaultFilePath drops out-of-scope files. Visibility follows ripgrep's accepted v1 defaults:
    .gitignore respected, dot-directories invisible, symlinks never followed (-L is never passed).

    Error posture is coarse and exit-code-first (Plan-Tool-Grep: Errors): exit 0 emits the
    (post-filtered) stdout; exit 1 is an empty result, not an error; exit 2 whose stderr carries
    rg's regex-parse signature is invalid_pattern (a malformed regex, rejected before any search),
    and any other exit 2-or-higher is ripgrep_failed carrying rg's stderr - which INCLUDES a
    per-file read error, so an unreadable file aborts the whole scan rather than silently
    narrowing the result.
    .PARAMETER Pattern
    The content regex, ripgrep syntax (native Grep semantics). Passed via `-e` so a pattern
    beginning with "-" is never misread as a flag.
    .PARAMETER Vault
    The vault to search. Defaults to the configured default vault.
    .PARAMETER ScopeRoots
    The grant's per-vault scope-roots map; defaults to the host-injected ambient set, absent
    (standalone) degrades unscoped, matching every other chokepoint.
    .PARAMETER Path
    Optional vault-relative folder to narrow the walk (passed as rg's search target instead of
    "."). Must already be validated as non-rooted/non-escaping by the caller; empty means the
    whole vault.
    .PARAMETER Glob
    Optional file-filter glob passed as `-g <glob> --glob-case-insensitive`, case-insensitive
    like the glob tool's matching.
    .PARAMETER Mode
    Output mode: `content` (path:line:text per matching line, `-n --no-heading`),
    `files_with_matches` (one path per file with a match, `-l`), or `count` (path:count per file,
    `-c`).
    .PARAMETER IgnoreCase
    When set, case-insensitive matching (rg `-i`); default is case-sensitive, matching native Grep.
    .PARAMETER RgPath
    Optional explicit ripgrep executable path (Resolve-VaultRgPath), highest precedence; throws
    dependency_unavailable naming the path tried when it does not resolve.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Pattern,
        [string] $Vault,
        [AllowNull()] $ScopeRoots = (Get-VaultAccessAmbientScopeRoots),
        [string] $Path,
        [string] $Glob,
        [ValidateSet('content', 'files_with_matches', 'count')] [string] $Mode = 'content',
        [switch] $IgnoreCase,
        [string] $RgPath
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $flatScopeRoots = Resolve-VaultScopeRoots -Vault $resolved.Name -ScopeRoots $ScopeRoots
    Assert-VaultOperationAllowed -Vault $resolved.Name -RelativePath '.' -ScopeRoots $flatScopeRoots

    $rg = Resolve-VaultRgPath -RgPath $RgPath

    # rg is rooted at the vault directory (Invoke-VaultRipgrep -WorkingDirectory) and searches
    # "." (whole vault) or the vault-relative $Path, so a match line's path is vault-relative and
    # results are structurally confined to the subtree. --color never keeps ANSI escapes out of
    # the parsed output; the pattern goes through -e so a leading "-" is never a flag; -- ends
    # options so the search target is always a path.
    $searchTarget = if ([string]::IsNullOrWhiteSpace($Path)) { '.' } else { $Path }
    $rgArgs = switch ($Mode) {
        'content' { @('-n', '--no-heading') }
        'files_with_matches' { , @('-l') }
        'count' { , @('-c') }
    }
    $rgArgs += @('--color', 'never')
    if ($IgnoreCase) { $rgArgs += '-i' }
    if (-not [string]::IsNullOrWhiteSpace($Glob)) { $rgArgs += @('--glob-case-insensitive', '-g', $Glob) }
    $rgArgs += @('-e', $Pattern, '--', $searchTarget)

    $rgResult = Invoke-VaultRipgrep -RgPath $rg -Arguments $rgArgs -WorkingDirectory $resolved.Root

    # Coarse exit-code classification (Plan-Tool-Grep: Errors). 0 = matches follow; 1 = empty
    # result (not an error); 2 with rg's regex-parse stderr signature = a malformed regex
    # (invalid_pattern); any other 2-or-higher = a real rg failure with stderr attached, per-file
    # read errors included - the whole scan aborts rather than silently narrowing.
    switch ($rgResult.ExitCode) {
        0 { }
        1 { return }
        2 {
            if ($rgResult.Stderr -match 'regex parse error') {
                throw (New-VaultToolError -Code 'invalid_pattern' -Message "ripgrep rejected the regex pattern: $($rgResult.Stderr.Trim())" -Data @{ pattern = $Pattern; stderr = $rgResult.Stderr.Trim() })
            }
            throw (New-VaultToolError -Code 'ripgrep_failed' -Message "ripgrep exited with code 2 searching '$($resolved.Root)': $($rgResult.Stderr.Trim())" -Data @{ exitCode = 2; stderr = $rgResult.Stderr.Trim() })
        }
        default {
            throw (New-VaultToolError -Code 'ripgrep_failed' -Message "ripgrep exited with code $($rgResult.ExitCode) searching '$($resolved.Root)': $($rgResult.Stderr.Trim())" -Data @{ exitCode = $rgResult.ExitCode; stderr = $rgResult.Stderr.Trim() })
        }
    }

    # Normalize each output line's path prefix the same way Get-VaultFilePath does: rg prints
    # paths relative to the search root (".\Projects\Widget.md", or "Projects\Widget.md" under a
    # $Path target), so re-resolve against the vault root, drop anything resolving outside it
    # (junction/reparse guard), re-derive the vault-relative forward-slash form, and apply the
    # scope post-filter. content/count lines are "path:...": the path is the field before the
    # first colon (a vault-relative path never contains one), and the ":line:text" / ":count"
    # suffix is preserved verbatim. files_with_matches lines are the bare path.
    $rootPrefix = $resolved.Root + [System.IO.Path]::DirectorySeparatorChar
    foreach ($line in $rgResult.Stdout) {
        if ([string]::IsNullOrEmpty($line)) { continue }
        if ($Mode -eq 'files_with_matches') {
            $rawPath = $line
            $suffix = ''
        }
        else {
            $colon = $line.IndexOf(':')
            if ($colon -lt 0) { continue }
            $rawPath = $line.Substring(0, $colon)
            $suffix = $line.Substring($colon)
        }
        $full = [System.IO.Path]::GetFullPath($rawPath, $resolved.Root)
        if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = [System.IO.Path]::GetRelativePath($resolved.Root, $full).Replace('\', '/')
        if ($flatScopeRoots.Count -gt 0 -and -not (Test-VaultPathInScope -Path $relative -ScopeRoots $flatScopeRoots)) { continue }
        $relative + $suffix
    }
}
