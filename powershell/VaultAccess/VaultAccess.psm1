# VaultAccess.psm1 - the vault-access chokepoint for PowerShell script tools.
#
# ALL vault I/O in tool scripts flows through this module - path resolution, bare-wikilink
# resolution, reads, writes, enumeration, the grant check, and write-locking. Tool scripts
# never run raw Get-Content/Set-Content against vault paths. A tool needing an operation the
# module does not expose (e.g. `move`'s Move-Item) still resolves and scope-checks its paths
# through here first.
#
# This module is the SOLE full-operation implementation. The C# side
# (src/VaultMcpServer/Vault/VaultAccess.cs) is deliberately NOT a full mirror: it carries only
# what the host itself calls - config load and the static scope primitives, used by
# GrantFileLoader, MintTokenTool, and ElevationRequestTool - plus the instance path resolvers,
# which have no production caller and are held, pinned by the shared cases, against the first
# native vault tool. The instance operations (read/write/enumerate/search) live only here. Two
# semantics remain mirrored across the two sides - path validity and scope containment - and a
# change to those lands as a shared tests/cases/*.json case run by both suites, never as
# hand-copied code. Everything else here (note I/O, enumeration, bare-wikilink resolution,
# write-locking, tool-result/error shaping, name convention, the ripgrep file census) is
# PowerShell-only with no C# counterpart to keep in step; re-mirror an operation by porting
# from the then-current PowerShell implementation only when a host caller actually needs it.
# See Vault-MCP-Server/Corpus/Server-Structure.md and Testing.md.
#
# Vault conventions (wikilink forms, filename rules, layout) are encoded here (and, for the
# mirrored path/scope core, in the C# class); they implement the vault's
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
