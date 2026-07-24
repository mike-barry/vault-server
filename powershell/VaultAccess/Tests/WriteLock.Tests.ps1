# Tests for VaultAccess Public/WriteLock.ps1 (write-lock name derivation and mutex behavior,
# including cross-process contention/abandonment via Start-Job).
# Run with: Invoke-Pester powershell/VaultAccess/Tests

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
    $script:TempRoot = Initialize-VaultAccessTestFixture
}

AfterAll {
    Remove-VaultAccessTestFixture -TempRoot $script:TempRoot
}

Describe 'Vault write lock name' {
    # The name is the cross-process contract with VaultWatcher - these tests pin the
    # derivation rule (canonicalized root path, not vault name), so a change that would
    # silently stop the two processes contending for the same lock fails here first.

    It 'is deterministic for the same vault' {
        (Get-VaultWriteLockName -Vault test) | Should -BeExactly (Get-VaultWriteLockName -Vault test)
    }

    It 'differs between vaults with different roots' {
        (Get-VaultWriteLockName -Vault test) | Should -Not -Be (Get-VaultWriteLockName -Vault scratch)
    }

    It 'derives from the root path, not the vault name' {
        # Two vault names pointing at one directory must contend for the same lock.
        $aliased = Join-Path $script:TempRoot 'aliased-vaults.json'
        $root = (Resolve-VaultRoot -Vault test).Root
        @{ default_vault = 'test'; vaults = @{ test = $root; twin = $root } } | ConvertTo-Json | Set-Content -LiteralPath $aliased
        Initialize-VaultAccess -ConfigPath $aliased
        try {
            (Get-VaultWriteLockName -Vault test) | Should -BeExactly (Get-VaultWriteLockName -Vault twin)
        }
        finally {
            Initialize-VaultAccess -ConfigPath (Join-Path $script:TempRoot 'vaults.json')
        }
    }

    It 'is a legal mutex name - no backslash outside the reserved prefix, within length limit' {
        $name = Get-VaultWriteLockName -Vault test
        $name.Length | Should -BeLessThan 260
        $name.Substring($name.IndexOf('\') + 1) | Should -Not -Match '\\'
    }
}

Describe 'Vault write lock' {
    It 'runs the scriptblock and passes its output through' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'result' } | Should -Be 'result'
    }

    It 'releases the lock after the block completes' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'first' } | Out-Null
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'second' } | Should -Be 'second'
    }

    It 'releases the lock when the block throws' {
        # The whole point of the scriptblock-wrapper API: no tool can leak the lock on an
        # error path, because the tool never writes the finally.
        { Invoke-VaultWriteLock -Vault test -ScriptBlock { throw 'boom' } } | Should -Throw '*boom*'
        Invoke-VaultWriteLock -Vault test -ScriptBlock { 'after' } | Should -Be 'after'
    }

    It 'is reentrant on the same thread' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock {
            Invoke-VaultWriteLock -Vault test -ScriptBlock { 'nested' }
        } | Should -Be 'nested'
    }

    It 'does not serialize against a different vault' {
        Invoke-VaultWriteLock -Vault test -ScriptBlock {
            Invoke-VaultWriteLock -Vault scratch -TimeoutSeconds 5 -ScriptBlock { 'other-vault' }
        } | Should -Be 'other-vault'
    }

    It 'reports not-abandoned on a clean acquisition' {
        $abandoned = $false
        Invoke-VaultWriteLock -Vault test -Abandoned ([ref]$abandoned) -ScriptBlock { } | Out-Null
        $abandoned | Should -BeFalse
    }
}

Describe 'Vault write lock cross-process behavior' {
    # These take the lock from a real second process, since the failures being covered
    # (contention, owner death) cannot be reproduced in-process: the mutex is reentrant for
    # the calling thread, so a same-thread attempt would just succeed.

    BeforeAll {
        $script:HoldScript = {
            param($name, $signalPath)
            $m = [System.Threading.Mutex]::new($false, $name)
            $m.WaitOne() | Out-Null
            Set-Content -LiteralPath $signalPath -Value 'held'
            Start-Sleep -Seconds 120
        }

        # Each test gets its own vault root, hence its own mutex. "Abandoned" is a one-shot
        # signal the OS clears once a waiter consumes it, so tests sharing a mutex leak that
        # state into each other and pass or fail on ordering.
        function New-IsolatedVault {
            param($Label)
            $dir = Join-Path $script:TempRoot "lockvault-$Label"
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $cfg = Join-Path $script:TempRoot "vaults-$Label.json"
            @{ default_vault = 'iso'; vaults = @{ iso = $dir } } | ConvertTo-Json | Set-Content -LiteralPath $cfg
            Initialize-VaultAccess -ConfigPath $cfg
        }

        function Restore-DefaultVaultConfig {
            Initialize-VaultAccess -ConfigPath (Join-Path $script:TempRoot 'vaults.json')
        }

        function Start-LockHolder {
            param($Name, $SignalPath)
            Remove-Item -LiteralPath $SignalPath -Force -ErrorAction SilentlyContinue
            $job = Start-Job -ScriptBlock $script:HoldScript -ArgumentList $Name, $SignalPath
            $deadline = [datetime]::UtcNow.AddSeconds(60)
            while (-not (Test-Path -LiteralPath $SignalPath) -and [datetime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
            }
            if (-not (Test-Path -LiteralPath $SignalPath)) {
                Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
                throw 'Lock-holder job never acquired the mutex.'
            }
            $job
        }
    }

    It 'times out when another process holds the lock' {
        New-IsolatedVault -Label 'timeout'
        try {
            $name = Get-VaultWriteLockName -Vault iso
            $signal = Join-Path $script:TempRoot 'held-timeout.txt'
            $job = Start-LockHolder -Name $name -SignalPath $signal
            try {
                { Invoke-VaultWriteLock -Vault iso -TimeoutSeconds 2 -ScriptBlock { 'never' } } |
                    Should -Throw '*Timed out*waiting for the vault write lock*'
            }
            finally {
                Stop-Job $job -ErrorAction SilentlyContinue
                Remove-Job $job -Force -ErrorAction SilentlyContinue
            }
        }
        finally { Restore-DefaultVaultConfig }
    }

    It 'acquires and reports an abandoned lock when the holding process dies, and stays usable after' -Skip:(-not $IsWindows) {
        # Killing the holder leaves the mutex abandoned; the next waiter must ACQUIRE it (not
        # fail) and be told the previous owner died. This is the self-healing that replaces a
        # release timeout. Runs on its own mutex so the one-shot abandoned signal cannot have
        # been consumed by another test.
        New-IsolatedVault -Label 'abandon'
        $keepAlive = $null
        try {
            $name = Get-VaultWriteLockName -Vault iso
            $signal = Join-Path $script:TempRoot 'held-abandon.txt'
            $job = Start-LockHolder -Name $name -SignalPath $signal

            # Hold an independent handle to the same kernel mutex across the child's death.
            # Abandonment is a property of the mutex OBJECT, and Windows destroys that object
            # the instant its last handle closes: without this handle the object can vanish
            # between the killed child releasing it and the parent's WaitOne below, so the
            # parent would open a fresh, never-abandoned mutex and the recovery signal would be
            # lost. This makes the test verify the abandoned-owner semantics deterministically
            # instead of racing the child's process teardown.
            $keepAlive = [System.Threading.Mutex]::new($false, $name)

            Stop-Job $job -ErrorAction SilentlyContinue
            Remove-Job $job -Force -ErrorAction SilentlyContinue

            $abandoned = $false
            $result = Invoke-VaultWriteLock -Vault iso -TimeoutSeconds 30 -Abandoned ([ref]$abandoned) -ScriptBlock { 'recovered' } -WarningAction SilentlyContinue
            $result | Should -Be 'recovered'
            $abandoned | Should -BeTrue

            # The abandoned state is consumed, not sticky: the very next acquisition is clean.
            $stillAbandoned = $false
            Invoke-VaultWriteLock -Vault iso -TimeoutSeconds 30 -Abandoned ([ref]$stillAbandoned) -ScriptBlock { 'clean' } | Should -Be 'clean'
            $stillAbandoned | Should -BeFalse
        }
        finally {
            if ($keepAlive) { $keepAlive.Dispose() }
            Restore-DefaultVaultConfig
        }
    }
}
