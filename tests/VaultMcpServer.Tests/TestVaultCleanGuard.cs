using System.Diagnostics;

namespace VaultMcpServer.Tests;

/// <summary>
/// Detection half of the test-isolation guard pair covering the "two env vars" gotcha
/// (prevention half lives in <see cref="ProgramHostFixtureBase.AssertMutationSandboxed"/>). Every fixture that copies
/// the committed <c>test-vault/</c> into a temp directory calls
/// <see cref="AssertCommittedFixtureUntouched"/> on dispose, after its own temp-dir cleanup, to
/// catch any escape prevention didn't anticipate - content changes, renames, and additions in
/// one check. Fails loudly (throws, with the porcelain output inline) rather than silently
/// leaving a dirty working tree for the next `git status` to discover cold.
///
/// Scoped to `test-vault/` only, so it is safe to run unconditionally even while sibling work
/// elsewhere in the repo is in flight: nothing but these tests ever writes under
/// `test-vault/`, so porcelain output there can only mean a real escape, never noise from
/// concurrent edits to other files.
/// </summary>
internal static class TestVaultCleanGuard
{
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
