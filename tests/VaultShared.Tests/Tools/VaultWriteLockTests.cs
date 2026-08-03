using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The lock's two non-obvious behaviours: an acquire that inherits from a dead owner proceeds, and
/// a wait that elapses refuses rather than proceeding. A crashed writer must not wedge the vault; a
/// hung one is alive and must be reported.
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

    /// <summary>A hung writer is alive, so nothing signals recovery and the wait simply elapses. The
    /// payload carries the timeout, which is the one value a caller could act on.</summary>
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
    /// The crash-recovery path. A thread that takes the mutex and exits without releasing it leaves
    /// it abandoned, which is exactly what a writer process dying mid-write leaves behind. The next
    /// acquire inherits it, reports that it did, and proceeds - refusing would wedge the vault, and
    /// there is no journal to replay and no state to repair.
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
            // Falls off the end still holding it.
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

    /// <summary>Whether the named mutex is takeable right now, asked from a thread that is not the
    /// holder - a <see cref="Mutex"/> is re-entrant for its owner.</summary>
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
