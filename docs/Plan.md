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
- `dotnet test` - 1576 passing: 1212 `VaultShared`, 305 `VaultMcpServer`, 59 `VaultCli`

## Done

- [x] **Prework** - spec bundle carried in, vault unreachability verified, retained code swept of stale pointers
- [x] **Design** - four documents under `design/`, reviewed and signed off
- [x] **Build** - core access layer, frontmatter surface, tool infrastructure, all eight tools, both front-ends, and the suite
- [x] **Comment passes** - chronology removed, then rewritten into the MSDN register with `CS1573` holding parameter completeness
- [x] **Obsidian re-measurement** - link resolution measured again and the layer corrected against it
- [x] **Harvest and reconcile** - 944 behavioral cases taken blind from the preserved old repo and checked against this build: 679 matched, 20 findings
- [x] **Triage** - every finding decided with the operator
- [x] **Fix the findings** - all 20 closed, each with the tests that would have caught it
- [x] **Work the coverage list** - fifty candidate gaps triaged against the code and the suite's assertions, twenty-two written, twenty-eight dropped

## What is left

Two steps. The second is the risky one.

### 1. Fix dangling doc references in the test projects

The three test projects do not generate documentation, so a broken `<see cref="..."/>` there is invisible.

Enable `GenerateDocumentationFile` on each, build, fix what it reports, then revert the project files. `conventions/Comment-Sweep.md` has the procedure. Mechanical.

### 2. Cut over to vault-server

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

**The harvest, its reconciliation, and the coverage list are deleted.** Each was the work list for a job that is now finished. Git history keeps them if anyone ever needs to look, and nothing outside it resolves the old case identifiers they carried.

**This does not extend to the specs or the design documents, which stay live until cutover.** They are the specification and the shape chosen to meet it, and no old implementation exists to check them against - which is why a change to behavior still lands its spec amendment and rewrites the affected design text in the same commit, exactly as before. A stale spec is undetectable from inside this repository and leaves the archived record describing something that was never decided. Neither remaining step changes behavior, so neither should need one. The coverage sweep turned up no defect: every entry it worked was a behavior already correct and merely unasserted, and no test written for it required a change under `src/`.

**`Questions.md` is empty and unused in this phase.** A gap the specs cannot answer is raised with the operator in session.
