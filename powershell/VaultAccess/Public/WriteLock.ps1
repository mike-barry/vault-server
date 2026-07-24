# Public/WriteLock.ps1 - per-vault cross-process write serialization.
# PowerShell-only. The lock NAME is a cross-process contract shared with VaultWatcher (a
# separate process), not a C#/PS mirror of VaultAccess.cs. See ../VaultAccess.psm1 for the
# full mirror policy and Vault-MCP-Server/Corpus/Server-Structure.md for the lock design.

function Get-VaultWriteLockName {
    <#
    .SYNOPSIS
    Builds the per-vault write-mutex name: Global\VaultWrite-<32 hex chars of SHA-256 of the
    lowercased, canonicalized vault root path>.

    This name is the cross-process contract. VaultWatcher constructs the identical string to
    contend for the same lock, so any change here is a breaking change for both processes.

    Derived from the root path rather than the vaults.json vault name, so two configs naming
    the same directory contend for the same lock. Lowercased before hashing on every
    platform, so a case-only config difference cannot leave two processes unserialized.
    Hashed because a mutex name is capped at 260 characters and cannot contain a backslash
    outside the reserved prefix. Global\ scopes the mutex across sessions; Windows only.
    #>
    [CmdletBinding()]
    param(
        [string] $Vault
    )

    $resolved = Resolve-VaultRoot -Vault $Vault
    $normalized = $resolved.Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar).ToLowerInvariant()

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    }
    finally {
        $sha.Dispose()
    }
    $hash = [System.BitConverter]::ToString($bytes).Replace('-', '').Substring(0, 32)

    $prefix = if ($IsWindows) { 'Global\' } else { '' }
    "${prefix}VaultWrite-$hash"
}

function Invoke-VaultWriteLock {
    <#
    .SYNOPSIS
    Runs $ScriptBlock holding the per-vault write mutex, releasing it however the block exits.

    The scriptblock wrapper is the only supported way to take the write lock; there is no
    exported Enter/Exit pair. The try/finally lives here once, so a tool cannot hold the lock
    without releasing it.

    Wrap the whole logical operation, not individual chokepoint calls. A move issues N+1
    mutations (one Write-VaultNote per backlinked file, then the file move itself), so a
    lock taken inside those functions would acquire and release N+1 times without
    serializing the operation.

    When a process dies holding the mutex, the OS releases it and hands the next waiter an
    AbandonedMutexException - that waiter owns the lock, and a previous owner died mid-write.
    Reported via -Abandoned and a warning; the vault may be torn and git diff is the
    diagnosis. Recovery is therefore signalled rather than inferred from elapsed time.

    -TimeoutSeconds is a liveness guard only: it fails the call rather than hanging the MCP
    request when the lock cannot be acquired. It is not the recovery mechanism, and a hung
    holder never abandons.

    Reentrant for the same thread; each level releases its own hold.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [scriptblock] $ScriptBlock,
        [string] $Vault,
        [int] $TimeoutSeconds = $(if ($env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS) { [int]$env:VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS } else { 60 }),
        [AllowNull()] [ref] $Abandoned
    )

    $name = Get-VaultWriteLockName -Vault $Vault
    if ($Abandoned) { $Abandoned.Value = $false }

    $mutex = [System.Threading.Mutex]::new($false, $name)
    try {
        $acquired = $false
        try {
            $acquired = $mutex.WaitOne([timespan]::FromSeconds($TimeoutSeconds))
        }
        catch [System.Threading.AbandonedMutexException] {
            # Signals that the previous owner died without releasing; the lock is held now.
            $acquired = $true
            if ($Abandoned) { $Abandoned.Value = $true }
            Write-Warning "Vault write lock was abandoned by a process that died mid-operation (vault root lock '$name'). The vault may be in a partially-written state - check 'git status' and 'git diff'."
        }

        if (-not $acquired) {
            throw "Timed out after ${TimeoutSeconds}s waiting for the vault write lock. Another vault operation is holding it - if nothing else is running, a process may be wedged while holding the lock."
        }

        try {
            & $ScriptBlock
        }
        finally {
            $mutex.ReleaseMutex()
        }
    }
    finally {
        $mutex.Dispose()
    }
}
