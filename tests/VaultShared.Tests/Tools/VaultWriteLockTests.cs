using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the vault write lock's two less obvious behaviours: acquiring a mutex abandoned by a dead
/// owner succeeds, and a wait that elapses refuses. A writer that crashed must not leave the vault
/// permanently locked, while one that is merely hung is still running and has to be reported.
/// </summary>
public sealed class VaultWriteLockTests
{
    private static string UniqueName() => $"VaultWriteLockTests-{Guid.NewGuid():N}";

    [Fact]
    public void Acquire_ThenDispose_LeavesTheLockFree()
    {
        var name = UniqueName();

        using (VaultWriteLock.Acquire(name, VaultWriteLock.Timeout))
        {
            Assert.False(FreeElsewhere(name));
        }

        Assert.True(FreeElsewhere(name));
    }

    /// <summary>A holder that is still alive never releases the mutex, so the wait elapses and the
    /// call refuses. The payload carries the timeout, the one value a caller can act on.</summary>
    [Fact]
    public void Acquire_WhileAnotherHolderIsAlive_TimesOut()
    {
        var name = UniqueName();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();

        try
        {
            var refusal = Assert.Throws<ToolRefusal>(() => VaultWriteLock.Acquire(name, TimeSpan.FromMilliseconds(50)));
            Assert.Equal(ToolCodes.LockTimeout, refusal.Code);
            Assert.Contains("\"timeout\"", ToolRefusalEnvelope.Json(refusal));
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    /// <summary>
    /// A thread that takes the mutex and exits without releasing it leaves it abandoned, which is
    /// what a writer process dying mid-write leaves behind. The next acquire takes the abandoned
    /// mutex, reports that it did, and proceeds. Refusing instead would leave the vault permanently
    /// locked, and there is no journal to replay or state to repair.
    /// </summary>
    [Fact]
    public void Acquire_AfterAnOwnerDiedWithoutReleasing_ProceedsAndReports()
    {
        var name = UniqueName();
        using var abandoned = new ManualResetEventSlim();

        var deadOwner = new Thread(() =>
        {
            var mutex = new Mutex(false, name);
            mutex.WaitOne();
            abandoned.Set();
            // The thread ends while still holding the mutex, abandoning it.
        });
        deadOwner.Start();
        abandoned.Wait();
        deadOwner.Join();

        var reported = new List<string>();
        using (VaultWriteLock.Acquire(name, VaultWriteLock.Timeout, reported.Add))
        {
            Assert.False(FreeElsewhere(name));
        }

        var message = Assert.Single(reported);
        Assert.Contains(name, message);
        Assert.True(FreeElsewhere(name));
    }

    /// <summary>
    /// The inherited-lock message names the vault and keeps the mutex name beside it. A reader acts
    /// on the vault; the mutex name is what correlates the event with another process reporting the
    /// same lock.
    /// </summary>
    [Fact]
    public void Acquire_ForAVault_NamesTheVaultAndTheMutexInTheInheritedMessage()
    {
        var name = UniqueName();
        using var abandoned = new ManualResetEventSlim();

        var deadOwner = new Thread(() =>
        {
            var mutex = new Mutex(false, name);
            mutex.WaitOne();
            abandoned.Set();
        });
        deadOwner.Start();
        abandoned.Wait();
        deadOwner.Join();

        var reported = new List<string>();
        using (VaultWriteLock.Acquire(name, VaultWriteLock.Timeout, reported.Add, "primary"))
        {
        }

        var message = Assert.Single(reported);
        Assert.Contains("primary", message);
        Assert.Contains(name, message);
    }

    /// <summary>Reports whether a named mutex can be acquired right now, asked from a thread that is
    /// not the holder. It has to be another thread, because a <see cref="Mutex"/> is re-entrant for
    /// the thread that owns it.</summary>
    /// <param name="name">The name of the mutex to try.</param>
    /// <returns>True if the mutex was acquired; otherwise false.</returns>
    private static bool FreeElsewhere(string name)
    {
        bool acquired = false;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (acquired)
                mutex.ReleaseMutex();
        });
        thread.Start();
        thread.Join();
        return acquired;
    }
}
