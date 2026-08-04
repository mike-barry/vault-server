using System.Diagnostics;

namespace VaultShared.Tests;

/// <summary>
/// Verifies after a test run that the committed <c>test-vault/</c> fixture on disk was never
/// modified, by checking that <c>git status</c> reports it clean.
/// </summary>
/// <remarks>
/// This is the after-the-fact half of a pair of isolation checks; the before-the-fact half is
/// <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/>, which refuses to let a mutating
/// call run unless both config paths point into the fixture's temp directory. Every fixture that
/// copies <c>test-vault/</c> into a temp directory calls
/// <see cref="AssertCommittedFixtureUntouched"/> on dispose, after its own temp-dir cleanup. One
/// check covers content changes, renames, and additions.
///
/// The check is limited to <c>test-vault/</c>, so it is safe to run while unrelated work is in
/// flight elsewhere in the repository. Nothing but these tests writes under that directory, so
/// any output can only mean a test escaped its sandbox.
/// </remarks>
public static class TestVaultCleanGuard
{
    /// <summary>
    /// Throws if <c>git status --porcelain test-vault/</c> reports anything, or if it cannot be
    /// run at all.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Git could not be started, exited non-zero, or reported the fixture as modified.
    /// </exception>
    public static void AssertCommittedFixtureUntouched()
    {
        ProcessStartInfo psi = new("git", "status --porcelain test-vault/")
        {
            WorkingDirectory = TestVaultFixture.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Test-isolation guard could not start `git status --porcelain test-vault/` to verify the committed fixture is untouched.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Test-isolation guard: `git status --porcelain test-vault/` exited {process.ExitCode} instead of " +
                $"reporting cleanly - cannot confirm the committed fixture is untouched. stderr: {error}");
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                "Test-isolation guard failed: the committed test-vault/ fixture was modified during this test run - " +
                "a mutating test escaped its temp sandbox and wrote to the real, committed copy instead. " +
                "`git status --porcelain test-vault/` reported:\n" + output +
                "\nRestore it with `git checkout -- test-vault/` (and `git clean -fd test-vault/` if files were added) " +
                "before trusting any further test run.");
        }
    }
}
