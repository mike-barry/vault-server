# VaultAccess.psm1 - the vault-access chokepoint for PowerShell script tools.
#
# ALL vault I/O in tool scripts flows through this module - path resolution, wikilink
# resolution, reads, writes, moves, enumeration, the grant check, and write-locking. Tool
# scripts never run raw Get-Content/Set-Content against vault paths.
#
# This module is the SOLE full-operation implementation. The C# side
# (src/VaultMcpServer/Vault/VaultAccess.cs) is NOT a full mirror: it keeps only the surface
# with real host callers - config load, the scope primitives, and wikilink/path resolution.
# The instance operations (read/write/move) live only here; the C# cognates were removed as
# caller-less drift liability (2026-07-19). Only three semantics remain mirrored across the
# two sides - wikilink resolution, path validity, and scope containment - and a change to
# those lands as a shared tests/cases/*.json case run by both suites, never as hand-copied
# code. Everything else here (note I/O, enumeration, write-locking, tool-result/error
# shaping, name convention, the ripgrep file census) is PowerShell-only with no C#
# counterpart to keep in step. See Vault-MCP-Server/Corpus/Server-Structure.md and Testing.md.
#
# Vault conventions (wikilink forms, filename rules, layout) are encoded here (and, for the
# still-mirrored resolution/scope core, in the C# class); they implement the vault's
# Document-Conventions.md and Vault-Guide.md (Resources/Vault/Guides/ in the vault repo).
#
# ---------------------------------------------------------------------------------------------
# Structure: this .psm1 is a thin loader. The functions live in per-concern files that it
# dot-sources - Public/ (the exported chokepoint surface, listed in VaultAccess.psd1's
# FunctionsToExport, which is the sole export authority) and Private/ (internal helpers, not
# exported). Load order among function definitions is irrelevant: dot-sourcing only defines
# them, and cross-references resolve at call time. Only the module-scope state below runs at
# load, so it stays here in the loader.

# The module's own directory, so callers anchored to the module root (e.g. the default
# vaults.json path in Initialize-VaultAccess) resolve identically regardless of which
# Public/ or Private/ file a function lives in.
$script:VaultAccessModuleRoot = $PSScriptRoot

$script:VaultConfig = $null

foreach ($folder in @('Private', 'Public')) {
    $dir = Join-Path $PSScriptRoot $folder
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
    Get-ChildItem -LiteralPath $dir -Filter '*.ps1' -File | Sort-Object Name | ForEach-Object {
        . $_.FullName
    }
}
