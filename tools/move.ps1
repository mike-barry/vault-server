<#
.SYNOPSIS
Relocates or renames a file or folder within the vault as one atomic, write-locked
operation, rewriting every reference to the moved item so the link graph stays intact.
See Plan-Tool-Move (vault-primary) for the full spec. SCAFFOLD ONLY: this is the first
implementation slice - argument validation, source resolution/dest construction, scope
membership, the ambiguity gate, the reference-rewrite engine, and the folder-move path
are later slices (see the plan's Proposed Implementation Checklist). This slice proves
the dispatch shape - op-class floor declaration, the write lock, and the Tier-2 JSON
return - loads and runs correctly; the body below is a stub.
.PARAMETER Source
Vault-relative path of the file or folder to move.
.PARAMETER Dest
Dual semantics on the "/" discriminator: a bare name (no "/") renames in place, keeping
the current parent; a value containing "/" is the full vault-relative destination. A
leading "/" targets the vault root. ".md" is optional for notes.
.PARAMETER Kind
Intent assertion - "file" or "folder" - inferred from the source's shape when omitted;
fails fast (kind_mismatch) when supplied and wrong.
.PARAMETER Vault
The vault to operate in. Defaults to the configured default vault.
.PARAMETER Force
Overrides the reference-rewrite ambiguity gate: unforced, a move whose rewrite would
involve or create bare-name ambiguity fails in validate; forced, it proceeds with
skip-and-report and path-qualified emission. Not yet implemented in this slice.
.NOTES
OpClass: content:write

move asserts no additional op class ("key") beyond its content:write floor, even when a
reference rewrite lands inside nav.*/agent.* frontmatter - its rewrites preserve routing
intent rather than author it. See Plan-Tool-Move's Op Classes section. Runs under the
per-vault write lock as plan -> validate -> apply once the later slices land; this
scaffold only proves the lock, op-class floor, and Tier-2 JSON return load and dispatch
correctly.
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Dest,
    [ValidateSet('file', 'folder')] [string] $Kind,
    [string] $Vault,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

# ---------------------------------------------------------------------------
# STUB BODY. Argument validation (empty dest, dest == source, dest inside the source
# folder, link-breaking/OS-invalid dest characters, kind in {project, area}), source
# resolution, dest construction, scope membership, the ambiguity gate, and the
# reference-rewrite engine are later implementation slices - see Plan-Tool-Move's
# Proposed Implementation Checklist. This slice only proves the tool loads standalone,
# dispatches through the shared write lock (the pattern every write-class tool uses per
# the Tool Authoring Standards), and serializes a well-formed Tier-2 result through the
# shared ConvertTo-VaultToolResult helper - so later slices fill in real logic against an
# already-correct scaffold rather than also having to get the dispatch shape right.
# ---------------------------------------------------------------------------

$lockAbandoned = $false
$result = Invoke-VaultWriteLock -Vault $Vault -Abandoned ([ref]$lockAbandoned) -ScriptBlock {
    # Required: the script-scope preference does not reach cmdlets invoked from inside a
    # module-function scriptblock, and without it a failed mutation is non-terminating and
    # gets reported as success. Re-asserted here even though this slice's body performs no
    # mutation, so the later slices that add one do not need to remember to add this line.
    $ErrorActionPreference = 'Stop'

    [ordered]@{
        oldPath                    = $Source
        newPath                    = $Dest
        kind                       = $Kind
        pureMove                   = $null
        referencesRewritten        = 0
        filesRewritten             = @()
        referencesSkippedAmbiguous = $null
        basenameCollisionCreated   = $false
        folderNoteOrphaned         = $false
        namingWarnings             = @()
        filesSkippedUnreadable     = 0
        folderNoteRenamed          = $null
        containedFilesMoved        = @()
        lockWarning                = $null
        stub                       = $true
    }
}

if ($lockAbandoned) {
    $result.lockWarning = 'Write lock was abandoned by a process that died mid-operation - an earlier vault write may be incomplete. Check "git status" and "git diff".'
}

ConvertTo-VaultToolResult -Result $result
