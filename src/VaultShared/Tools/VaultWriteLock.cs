using VaultShared.Access;

namespace VaultShared.Tools;

/// <summary>
/// The vault's cross-process write mutex, acquired around a whole invocation of a tool that declares
/// it mutates. <b>Nothing else in the build acquires it</b> - not the tools, not the layer's write
/// primitives - so a write primitive called outside a tool runs with no lock held.
///
/// The name comes from <see cref="Vault.LockName"/>, a contract <c>VaultWatcher</c> shares as code.
/// The hold is synchronous and stays on one thread: a <see cref="Mutex"/> is thread-affine and the
/// hold must not cross a suspension point.
/// </summary>
public static class VaultWriteLock
{
    /// <summary>
    /// How long an acquire waits before refusing. A constant rather than a configured knob: it is a
    /// liveness diagnostic, and a tunable one gets tuned upward until it stops reporting the problem
    /// it exists to report. A <em>hung</em> writer is alive and trips this rather than the recovery
    /// signal below.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc cref="Acquire(string, TimeSpan, Action{string}?)"/>
    public static IDisposable Acquire(Vault vault, Action<string>? onInherited = null) =>
        Acquire(vault.LockName, Timeout, onInherited);

    /// <summary>
    /// Takes the lock, or raises <see cref="ToolCodes.LockTimeout"/>. Dispose releases it.
    ///
    /// <b>An acquire that inherits the lock from a dead owner proceeds.</b> The signal is that the
    /// previous holder died without releasing, which is a fact about that process and not about this
    /// call - there is no journal to replay and no state to repair, so refusing would wedge the vault
    /// exactly as the guarantee forbids. It is reported through <paramref name="onInherited"/> and
    /// the invocation continues.
    /// </summary>
    /// <param name="timeout">
    /// Always <see cref="Timeout"/> in the build. The parameter exists so the liveness refusal is
    /// testable in less than half a minute; no configuration reaches it.
    /// </param>
    /// <param name="onInherited">
    /// Where the inherited-lock signal goes. A delegate rather than a logging dependency: this
    /// assembly carries no logging abstraction, and one event does not justify adding one.
    /// </param>
    public static IDisposable Acquire(string lockName, TimeSpan timeout, Action<string>? onInherited = null)
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
            onInherited?.Invoke($"Inherited the vault write lock \"{lockName}\" from an owner that died without releasing it. Proceeding.");
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
