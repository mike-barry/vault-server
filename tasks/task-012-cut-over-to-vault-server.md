# task-012: Cut over to the canonical vault-server repository

**Goal:** This build's history is layered onto the canonical `vault-server` repository, which keeps its identity, remote and history, and whose resulting tree is exactly this repository's tree minus `docs/`.

## Context

The risky one. Do it last, after [task-009](task-009-windows-only.md), [task-010](task-010-conformance-audit.md) and [task-011](task-011-comment-sweep.md), because every one of them changes the tree this task's acceptance test compares against.

The commits are **layered onto** the canonical repo rather than replacing its files, so that repo keeps its identity, its remote, and its history.

**Rehearse all of it on a branch in a clone before doing any of it for real.**

### Three moves

1. **Baseline commit** in `vault-server`: delete what this build deleted — the PowerShell access module and the PowerShell tool scripts, along with their test suites — align every remaining path to this repo's root commit, and leave untouched any path this repo does not track. `vaults.json` and `.mcp.json` hold the real vault's configuration and no commit here mentions them, so nothing needs restoring afterwards.
2. **Replay** every non-root commit onto that baseline: `git rebase --onto <baseline> <root> master --committer-date-is-author-date`. Start from the root's children — the root's diff is against the empty tree and cannot apply to a non-empty baseline. Author dates are genuine and preserved; matching committer dates to them keeps the graph readable, at the cost that anything keyed on committer date places this work in the past.
3. **Cutover commit**: delete `docs/` entirely, and reconcile `CLAUDE.md` against the canonical repo's.

`conventions/` carries across as code rather than being archived with `docs/`: it governs the code, and the comment and test standards travel with it.

### What `CLAUDE.md` still needs

Most of this repo's `CLAUDE.md` already describes the code rather than the build, so the merge is small. Two things remain:

- Its **Specifications** section names `docs/spec/` and `docs/design/`, which this task deletes. It goes, and with it the rule that a behavior change amends the spec in the same commit — after cutover there is no spec to amend and the code is the only description of itself.
- The canonical repo's own `CLAUDE.md` is the base for anything it says that this repo's does not. Reconcile the two rather than overwriting.

## Acceptance criteria

- [ ] The whole sequence was rehearsed on a branch in a clone before any of it was done for real.
- [ ] `vault-server` has a baseline commit, every non-root commit of this repo replayed onto it, and a cutover commit.
- [ ] The resulting tree equals this repo's tree, minus `docs/`, with `CLAUDE.md` reconciled, plus the configuration that survived from the canonical repo. **Anything else in a diff against this repo means a commit resolved wrongly.**
- [ ] `CLAUDE.md` no longer names `docs/` or anything in it.
- [ ] `conventions/` is present in `vault-server` unchanged.
- [ ] `dotnet build --no-incremental` clean and `dotnet test` green in `vault-server` after the cutover commit.

## Notes

**Keep this repository afterwards.** It is the only intact record of the build, and the thing to diff against if the replay is later found to be wrong.
