# publish.ps1
# Publishes the release build of vault.exe into bin\vault, a fixed location outside the
# project tree that another repository can put on PATH and call. The folder is gitignored.
#
# The published CLI reads the repository's live configuration at the root - op-classes.json,
# vaults.json and grants.json - rather than a copy of it, so an edit to one of those three
# takes effect on the next invocation. A change to the CLI's own code does not, because
# bin\vault holds a snapshot and every caller keeps getting the build staged here until this
# script runs again.
#
# Usage: .\publish.ps1
# Exit code: 0 published, 1 otherwise.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'src\VaultCli\VaultCli.csproj'
$deployedSettings = Join-Path $repoRoot 'src\VaultCli\vault.settings.deployed.json'
$outputDir = Join-Path $repoRoot 'bin\vault'

# dotnet publish overwrites what it produces and removes nothing else, so a file the project
# has since stopped producing would survive here from an earlier run. Clearing first makes
# this folder the output of one build rather than the union of every build.
if (Test-Path $outputDir) {
    Remove-Item -Path $outputDir -Recurse -Force
}

dotnet publish $project -c Release -o $outputDir
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Publish failed.' -ForegroundColor Red
    exit 1
}

# The settings file the build copies names the three configuration files at their distance from
# src\VaultCli\bin\<configuration>\<framework>, which is not where they are from here. The
# deployed file names the same three at their distance from bin\vault.
Copy-Item -Path $deployedSettings -Destination (Join-Path $outputDir 'vault.settings.json') -Force

Write-Host ''
Write-Host "Published to $outputDir" -ForegroundColor Green
Write-Host "Put that folder on PATH to call vault.exe from another repository."
