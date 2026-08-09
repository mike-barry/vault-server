# Plan

The working plan for this build, held by the agent and the operator together.

Read `Brief-Vault-Access-Build.md` first if this session has not. Read `../CLAUDE.md` for the constraints that bind every step.

## How to use it

- Read this at the start of a session and say which step is in play.
- Work one step at a time, in order, unless the operator redirects.
- Check a step off when its work is done and verified, not when it is written.
- Propose changes to this file rather than making them silently.
- This plan does not overrule the specs. A contradiction with them is an escalation, not a plan edit.

## Where the build stands

The software is built and working. The access layer, the tool layer, the MCP server, the CLI, and their tests are all committed on `master`.

Current state, which any step should re-verify rather than trust:

- `dotnet build --no-incremental` - 0 warnings, 0 errors
- `dotnet test` - 1538 passing: 1174 `VaultShared`, 305 `VaultMcpServer`, 59 `VaultCli`

## Done

- [x] **Prework** - spec bundle carried in, vault unreachability verified, retained code swept of stale pointers
- [x] **Design** - four documents under `design/`, reviewed and signed off
- [x] **Build** - core access layer, frontmatter surface, tool infrastructure, all eight tools, both front-ends, and the suite
- [x] **Comment passes** - chronology removed, then rewritten into the MSDN register with `CS1573` holding parameter completeness
- [x] **Obsidian re-measurement** - link resolution measured again and the layer corrected against it
- [x] **Harvest and reconcile** - 944 behavioral cases taken blind from the preserved old repo and checked against this build: 679 matched, 20 findings
- [x] **Triage** - every finding decided with the operator
- [x] **Fix the findings** - all 20 closed, each with the tests that would have caught it

## What is left

Three steps. Only the last is risky.

### 1. Work the coverage list

Triage `Test-Coverage-Gaps.md` and add tests only where they earn their place. Expect to reject most of it; a suite of 1538 tests already covers what the specs justify, and this list is a second axis over the same code rather than a set of known defects.

Start with the three the reconciliation promoted. Each is deliberately unscoped, which is exactly what a later reader "tidies up" by adding a scope check:

- `delete`'s backlink census scans every note in the vault, not just the caller's scope. Drive `delete` under a narrow grant and assert it still reports a backlink from outside that scope.
- `move`'s link repair does the same. Drive `move` under a narrow grant and assert a note outside the scope still gets repaired.
- Search arguments reach ripgrep as data, never through a shell. Assert that a pattern beginning with `-` is searched for rather than read as a flag.

Three rules keep this from bloating the suite:

- **If you cannot say what breaks silently when the property regresses, skip it.** Some entries hold only because no argument exists to violate them, and there is nothing there to guard.
- **Covered from another angle is covered.** An entry earns a test only where the property is genuinely unasserted.
- **Widen a near-miss rather than adding a second test beside it.** Two tests asserting one property both have to be understood and both have to be kept true, and the second buys nothing.

Watch for the shape where the suite itself hides a property: `SearchToolTests` sorts every result before asserting, so a change that started sorting would satisfy the whole suite. That kind of gap is found by reading assertions, not by adding tests.

**Two branches cannot be tested, and this is why.** Do not spend the session rediscovering it:

- `move`'s `partial_mutation` branch needs two notes linking to the moved note with the second write blocked. Note enumeration is unordered, so a fixture cannot control which fails first and any such test passes or fails by luck. `WikilinkRepairTests.AWriteThatFailsNamesWhatLandedAndWhatDidNot` covers the data the tool relays; the tool's branch on it is what stays uncovered.
- The folder-note rename's partial path needs a folder move to succeed while the note rename inside it fails. The open-handle mechanism cannot arrange that, since the directory rename would have to carry an open handle across.

Delete `Test-Coverage-Gaps.md` when the step is done.

### 2. Fix dangling doc references in the test projects

The three test projects do not generate documentation, so a broken `<see cref="..."/>` there is invisible.

Enable `GenerateDocumentationFile` on each, build, fix what it reports, then revert the project files. `conventions/Comment-Sweep.md` has the procedure. Mechanical.

### 3. Cut over to vault-server

This build's commits are layered onto the canonical `vault-server` repo rather than replacing its files, so that repo keeps its identity, its remote, and its history.

**Rehearse all of it on a branch in a clone before doing any of it for real.**

Three moves:

1. **Baseline commit** in `vault-server`: delete what this build deleted, align every remaining path to this repo's root commit, and leave untouched any path this repo does not track. `vaults.json` and `.mcp.json` hold the real vault's configuration and no commit here mentions them, so nothing needs restoring afterwards.
2. **Replay** every non-root commit onto that baseline: `git rebase --onto <baseline> <root> master --committer-date-is-author-date`. Start from the root's children - the root's diff is against the empty tree and cannot apply to a non-empty baseline. Author dates are genuine and preserved; matching committer dates to them keeps the graph readable, at the cost that anything keyed on committer date places this work in the past.
3. **Cutover commit**: delete `docs/` entirely, and write the merged `CLAUDE.md` - the canonical repo's as the base, with the comment standard and the `conventions/` pointer grafted in. Most of this repo's `CLAUDE.md` is machinery for `docs/` and loses its referent. `conventions/` carries across as code: it governs the code rather than recording the build.

**The acceptance test is exact.** The resulting tree equals this repo's tree, minus `docs/`, with `CLAUDE.md` replaced, plus the configuration that survived from the canonical repo. Anything else in a diff against this repo means a commit resolved wrongly.

Keep this repo afterwards. It is the only intact record of the build and the thing to diff against if the replay is later found to be wrong.

## Open items

**`BearerGrantMiddleware` logs every call's full arguments.** `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:131` writes `args={Args}` to an unrotated log on every request. With `edit` and `create` shipped, that means note content and frontmatter operations land in `logs/`. This is an operator decision that has not been made, and it is not blocked by anything above.

**The harvest and its reconciliation documents are deleted.** They were the work list for a job that is finished, and `Test-Coverage-Gaps.md` holds the only part still owed work. Git history keeps them if anyone ever needs to look; the old case identifiers in that document resolve nowhere else.

**This does not extend to the specs or the design documents, which stay live until cutover.** They are the specification and the shape chosen to meet it, and no old implementation exists to check them against - which is why a change to behavior still lands its spec amendment and rewrites the affected design text in the same commit, exactly as before. A stale spec is undetectable from inside this repository and leaves the archived record describing something that was never decided. Steps 1 and 2 change no behavior and should need neither; a defect the coverage sweep turns up is an ordinary change and carries both.

**`Questions.md` is empty and unused in this phase.** A gap the specs cannot answer is raised with the operator in session.
