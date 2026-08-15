# task-015: Decide whether the runspace pool's `UseNewThread` still has a reason

**Goal:** Establish whether `PSThreadOptions.UseNewThread` on the script runspace pool still does anything this server depends on, then either keep it with a comment saying why or remove it.

## Context

`ScriptToolRunner.cs:56` sets `_pool.ThreadOptions = PSThreadOptions.UseNewThread` when opening the shared runspace pool. The line carries no comment, while the lines around it (the `InitialSessionState` overload choice, the `Bypass` execution policy) each explain themselves.

The setting has a recorded rationale, but it comes from outside this repo and predates the C# rewrite. A corpus doc in the vault (`Tool-Dispatch.md`, retired during the 2026-08 corpus triage) described the line as load-bearing for write-lock crash recovery, on this reasoning: Windows marks a named mutex abandoned only when the thread that owns it dies, so a pooled runspace reusing a host thread could outlive a crashed tool script while still holding the vault write lock. A fresh thread per runspace meant a dead script surfaced as an abandoned mutex rather than a lock nobody would release. That doc is being deleted, so unless the reasoning is recorded here it stops existing anywhere.

What has changed since that rationale was written:

- The vault write lock is now `src/VaultShared/Tools/VaultWriteLock.cs`, acquired by the tool layer around a whole invocation of a tool declaring `Mutates`, held synchronously on one thread, with its own `AbandonedMutexException` path. Its remarks state that nothing else acquires it.
- No PowerShell-side lock helper remains in the repo.
- `tools/` contains only `.gitkeep`, so the pool currently loads no scripts.

What has not changed: `ScriptToolRunner`, `ScriptToolDispatcher`, `ScriptToolLoader`, `ScriptToolRegistry`, `ScriptToolsWatcher` and `ScriptToolArgumentEncoder` are all still present, still wired from `Program.cs`, and still covered by `tests/VaultMcpServer.Tests/ScriptToolRunnerTests.cs`.

So the specific mutex argument no longer describes anything the running system does. Whether the setting has some other justification — thread-affine or `[ThreadStatic]` state leaking between pooled invocations, apartment state, host-thread reuse — has not been looked at, and is what this task is for. Either answer is a fine outcome; the point is that the line stops being unexplained.

## Acceptance criteria

- [ ] Establish what, if anything, `UseNewThread` changes for the pool as this server actually uses it, as against the default thread option
- [ ] Decide to keep or remove it
- [ ] If kept: the line carries a comment giving the reason it is kept, per `docs/conventions/Code-Comments.md`
- [ ] If removed: the commit message records what the setting was for and why it no longer applies, per `task-014` if that has landed by then
- [ ] The decision states what it assumes about `tools/` staying empty, since a script tool arriving later could change the answer

## Notes
