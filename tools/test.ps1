<#
.SYNOPSIS
Throwaway probe tool for the layered op-class authorization spike - exercises the three
enforcement seams (dispatcher floor gate, in-script Assert-VaultOpClass elevation gate, and
standalone degrade-open) without touching any vault content. Not a real vault tool.
.PARAMETER Mode
Which seam to exercise. "read" just returns success once the dispatcher's floor gate
(content:read) has admitted the call - proving a grant lacking content:read never reaches this
script. "elevated" asserts content:write in-script before returning - proving a grant with
content:read but not content:write is denied here with insufficient_capability, while a
standalone `pwsh -File` run (no injected op-class context) degrades open and passes.
.NOTES
OpClass: content:read
#>
param(
    [ValidateSet('read', 'elevated')] [string] $Mode = 'read'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'powershell' 'VaultAccess' 'VaultAccess.psd1') -Force

if ($Mode -eq 'elevated') {
    # Elevation gate: this tool's declared floor is content:read (the dispatcher already gated the
    # call on it), but the elevated path requires the higher content:write class. A content:read
    # grant is denied here in-script; a standalone run with no injected context degrades open.
    Assert-VaultOpClass -OpClass 'content:write'
    Write-Output 'test probe: elevated mode OK (content:write asserted)'
    return
}

Write-Output 'test probe: read mode OK (content:read floor gate passed)'
