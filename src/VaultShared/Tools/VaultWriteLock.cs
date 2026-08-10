using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>
/// Acquires the vault's cross-process write mutex around a whole invocation of a tool that declares
/// it mutates.
/// </summary>
/// <remarks>
/// Nothing else acquires this lock - not the tools, not the access layer's write primitives - so a
/// write primitive called outside a tool runs with no lock held. The lock name comes from
/// <see cref="Vault.LockName"/>, which carries what the derivation contracts with and what changing
/// it breaks. The hold is synchronous and stays on one thread, because a <see cref="Mutex"/> is thread-affine
/// and the hold must not cross an await.
/// </remarks>
public static class VaultWriteLock
{
    /// <summary>
    /// How long an acquire waits before refusing. This is a constant rather than a configured
    /// setting because it is a liveness diagnostic, and a tunable value gets raised until it stops
    /// reporting the problem it exists to report. A writer that is hung rather than dead trips this
    /// timeout, not the inherited-lock signal.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Acquires the write lock for a vault, using the standard <see cref="Timeout"/>.</summary>
    /// <param name="vault">The vault whose lock name is used.</param>
    /// <param name="onInherited">Receives the message when the lock is inherited from an owner that
    /// died without releasing it, or null to ignore it.</param>
    /// <returns>A hold that releases the lock when disposed.</returns>
    /// <exception cref="ToolRefusal">The lock was not acquired before the timeout elapsed.</exception>
    public static IDisposable Acquire(Vault vault, Action<string>? onInherited = null) =>
        Acquire(vault.LockName, Timeout, onInherited, vault.Name);

    /// <summary>
    /// Acquires the named write lock.
    /// </summary>
    /// <param name="lockName">The system-wide mutex name.</param>
    /// <param name="timeout">
    /// How long to wait. Always <see cref="Timeout"/> in production; the parameter exists so the
    /// timeout refusal can be tested in less than half a minute, and no configuration reaches it.
    /// </param>
    /// <param name="onInherited">
    /// Receives the inherited-lock message. A delegate rather than a logging dependency, because this
    /// assembly carries no logging abstraction and one event does not justify adding one.
    /// </param>
    /// <param name="vaultName">
    /// The vault the lock belongs to, named in the inherited-lock message, or null where the caller
    /// has no vault to name.
    /// </param>
    /// <returns>A hold that releases the lock when disposed.</returns>
    /// <exception cref="ToolRefusal">The lock was not acquired before the timeout elapsed.</exception>
    /// <remarks>
    /// An acquire that inherits the lock from an owner that died without releasing it proceeds. That
    /// signal is a fact about the dead process, not about this call. There is no journal to replay
    /// and no state to repair, so refusing would leave the vault permanently unwritable. The message
    /// goes to <paramref name="onInherited"/> and the invocation continues.
    ///
    /// The message names the vault and carries the mutex name beside it. The vault is what a reader
    /// can act on, and the mutex name is what correlates this event with the same lock reported by
    /// another process.
    /// </remarks>
    public static IDisposable Acquire(
        string lockName, TimeSpan timeout, Action<string>? onInherited = null, string? vaultName = null)
    {
        var mutex = new Mutex(initiallyOwned: false, lockName);
        try
        {
            if (!mutex.WaitOne(timeout))
                throw new ToolRefusal(
                    ToolCodes.LockTimeout,
                    $"The vault write lock was not acquired within {timeout.TotalSeconds:0.###} seconds.",
                    new { timeout = timeout.TotalSeconds });
        }
        catch (AbandonedMutexException)
        {
            // The wait succeeded and this thread owns the mutex; the exception is the recovery
            // signal, not a failure.
            var names = vaultName is null ? $"\"{lockName}\"" : $"for vault \"{vaultName}\" (mutex \"{lockName}\")";
            onInherited?.Invoke($"Inherited the vault write lock {names} from an owner that died without releasing it. Proceeding.");
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new Hold(mutex);
    }

    private sealed class Hold(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
