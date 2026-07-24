# SharedCases.Tests.ps1 - the PowerShell half of the shared-case parity contract with the
# xUnit suite (tests/VaultMcpServer.Tests). The path and scope cases load from
# tests/cases/*.json, consumed identically on both sides so the mirrored chokepoint
# semantics are exercised the same way. Any change to mirrored semantics lands HERE as a
# shared case (add the case to the JSON file, or - for 'Vault config skip semantics' -
# to this file), and both implementations must then pass it.

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:TempRoot = Initialize-VaultAccessTestFixture
}

AfterAll {
    Remove-VaultAccessTestFixture -TempRoot $script:TempRoot
}

Describe 'Vault config skip semantics' {
    # A configured vault whose root directory does not exist is skipped per-entry rather
    # than failing the whole load - mirrors the C# side's VaultConfig.Load/SkippedVaults
    # (tests/VaultMcpServer.Tests/VaultConfigTests.cs, VaultConfigLoadTests). These tests
    # swap $script:VaultConfig via Initialize-VaultAccess -ConfigPath, so each restores the
    # suite's main config afterward for the Describe blocks that follow.
    #
    # This block belongs to the mirrored-semantics family, but is hand-written rather than
    # data-driven: it has no JSON case file (the skip behavior is expressed through
    # Initialize-VaultAccess configs rather than a single-call case row).

    BeforeAll {
        $script:SkipGoodRoot = Join-Path $script:TempRoot 'skip-good'
        New-Item -ItemType Directory -Path $script:SkipGoodRoot -Force | Out-Null
        $script:SkipMissingRoot = Join-Path $script:TempRoot 'skip-missing'
        $script:SkipMissingRootA = Join-Path $script:TempRoot 'skip-missing-a'
        $script:SkipMissingRootB = Join-Path $script:TempRoot 'skip-missing-b'
    }

    AfterEach {
        Initialize-VaultAccess -ConfigPath (Join-Path $script:TempRoot 'vaults.json')
    }

    It 'skips a configured vault whose root does not exist, without failing the load' {
        $configPath = Join-Path $script:TempRoot 'skip-one.json'
        @{ default_vault = 'good'; vaults = @{ good = $script:SkipGoodRoot; bad = $script:SkipMissingRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath

        { Initialize-VaultAccess -ConfigPath $configPath } | Should -Not -Throw

        $config = Get-VaultAccessConfig
        $config.Vaults.ContainsKey('good') | Should -BeTrue
        $config.Vaults.ContainsKey('bad') | Should -BeFalse
        $config.SkippedVaults.ContainsKey('bad') | Should -BeTrue
        $config.SkippedVaults['bad'] | Should -Match 'does not exist'
        (Resolve-VaultRoot -Vault good).Name | Should -Be 'good'
    }

    It 'fails resolving a skipped vault as "currently unavailable", distinct from unknown' {
        $configPath = Join-Path $script:TempRoot 'skip-two.json'
        @{ default_vault = 'good'; vaults = @{ good = $script:SkipGoodRoot; bad = $script:SkipMissingRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath
        Initialize-VaultAccess -ConfigPath $configPath

        { Resolve-VaultRoot -Vault bad } | Should -Throw '*currently unavailable*'
        { Resolve-VaultRoot -Vault bad } | Should -Throw '*does not exist*'

        $unavailableMessage = $null
        try { Resolve-VaultRoot -Vault bad } catch { $unavailableMessage = $_.Exception.Message }
        $unavailableMessage | Should -Match 'currently unavailable'
        $unavailableMessage | Should -Not -Match 'Unknown vault'

        $unknownMessage = $null
        try { Resolve-VaultRoot -Vault nope } catch { $unknownMessage = $_.Exception.Message }
        $unknownMessage | Should -Match 'Unknown vault'
        $unknownMessage | Should -Not -Match 'currently unavailable'
    }

    It 'loads successfully when default_vault''s root is missing, but resolving the default fails as unavailable' {
        $configPath = Join-Path $script:TempRoot 'skip-default.json'
        @{ default_vault = 'bad'; vaults = @{ good = $script:SkipGoodRoot; bad = $script:SkipMissingRoot } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath

        { Initialize-VaultAccess -ConfigPath $configPath } | Should -Not -Throw
        { Resolve-VaultRoot } | Should -Throw '*currently unavailable*'
    }

    It 'loads successfully even when every vault root is missing' {
        $configPath = Join-Path $script:TempRoot 'skip-all.json'
        @{ default_vault = 'a'; vaults = @{ a = $script:SkipMissingRootA; b = $script:SkipMissingRootB } } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath

        { Initialize-VaultAccess -ConfigPath $configPath } | Should -Not -Throw
        $config = Get-VaultAccessConfig
        $config.Vaults.Count | Should -Be 0
        $config.SkippedVaults.Count | Should -Be 2
        { Resolve-VaultRoot } | Should -Throw '*currently unavailable*'
    }
}

Describe 'Resolve-VaultPath' {
    It '<name>' -TestCases @(
        Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../../tests/cases/path-cases.json') -Raw |
            ConvertFrom-Json | ForEach-Object { @{ name = $_.name; path = $_.path; valid = $_.valid } }
    ) {
        if ($valid) {
            [System.IO.Path]::IsPathRooted((Resolve-VaultPath -Path $path)) | Should -BeTrue
        }
        else {
            { Resolve-VaultPath -Path $path } | Should -Throw
        }
    }
}

Describe 'Test-VaultPathInScope' {
    It '<name>' -TestCases @(
        Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../../tests/cases/scope-cases.json') -Raw |
            ConvertFrom-Json | ForEach-Object { @{ name = $_.name; vault = $_.vault; path = $_.path; scopeRoots = $_.scopeRoots; outcome = $_.outcome } }
    ) {
        # scopeRoots is the grant's per-vault map (a PSCustomObject from ConvertFrom-Json,
        # keyed by vault name); convert to a hashtable so Resolve-VaultScopeRoots - which
        # mirrors the C# side's dictionary-keyed lookup - can pick out $vault's own entry,
        # proving a root configured for a different vault never leaks into this one.
        $scopeRootsTable = @{}
        $scopeRoots.PSObject.Properties | ForEach-Object { $scopeRootsTable[$_.Name] = $_.Value }
        $flatScopeRoots = Resolve-VaultScopeRoots -Vault $vault -ScopeRoots $scopeRootsTable

        switch ($outcome) {
            'inScope' { Test-VaultPathInScope -Path $path -ScopeRoots $flatScopeRoots | Should -BeTrue }
            'outOfScope' { Test-VaultPathInScope -Path $path -ScopeRoots $flatScopeRoots | Should -BeFalse }
            'invalid' { { Test-VaultPathInScope -Path $path -ScopeRoots $flatScopeRoots } | Should -Throw }
            default { throw "unknown outcome: $outcome" }
        }
    }
}
