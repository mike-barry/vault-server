@{
    RootModule        = 'VaultAccess.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = '7f9c2a3e-4b1d-4e8a-9c6f-2d5b8e0a1c47'
    Author            = 'Mike Barry'
    Description       = 'Vault-access chokepoint for VaultMcpServer PowerShell tool scripts. All vault I/O flows through this module.'
    PowerShellVersion = '7.0'
    FunctionsToExport = @(
        'Initialize-VaultAccess'
        'Get-VaultAccessConfig'
        'Resolve-VaultRoot'
        'Resolve-VaultPath'
        'Get-VaultNotePath'
        'Resolve-WikiLink'
        'Read-VaultNote'
        'Write-VaultNote'
        'Move-VaultNote'
        'Test-VaultPathInScope'
        'Resolve-VaultScopeRoots'
        'Assert-VaultOperationAllowed'
        'Assert-VaultOpClass'
        'Get-VaultWriteLockName'
        'Invoke-VaultWriteLock'
        'Test-VaultNoteMissingError'
        'New-VaultToolError'
        'ConvertTo-VaultToolResult'
        'Test-VaultNameConvention'
        'Get-VaultFilePath'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
