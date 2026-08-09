# task-012: Cut over to the canonical vault-server repository

**Goal:** This build's history is layered onto the canonical `vault-server` repository, which keeps its identity, remote and history, and whose resulting tree is exactly this repository's tree minus the parts of `docs/` that do not travel.

## Context

The risky one. Do it last, after [task-009](task-009-windows-only.md), [task-010](task-010-conformance-audit.md) and [task-011](task-011-comment-sweep.md), because every one of them changes the tree this task's acceptance test compares against.

The commits are **layered onto** the canonical repo rather than replacing its files, so that repo keeps its identity, its remote, and its history.

**Rehearse all of it on a branch in a clone before doing any of it for real.**

### Three moves

1. **Baseline commit** in `vault-server`: delete what this build deleted — the PowerShell access module and the PowerShell tool scripts, along with their test suites — align every remaining path to this repo's root commit, and leave untouched any path this repo does not track. `vaults.json` and `.mcp.json` hold the real vault's configuration and no commit here mentions them, so nothing needs restoring afterwards.
2. **Replay** every non-root commit onto that baseline: `git rebase --onto <baseline> <root> master --committer-date-is-author-date`. Start from the root's children — the root's diff is against the empty tree and cannot apply to a non-empty baseline. Author dates are genuine and preserved; matching committer dates to them keeps the graph readable, at the cost that anything keyed on committer date places this work in the past.
3. **Cutover commit**: drop the parts of `docs/` that do not travel, and reconcile `CLAUDE.md` against the canonical repo's.

**The specification and design documents do not travel.** What settles that is that they are not to be maintained: nothing should have to hold them in sync with an implementation that goes on changing, so they cannot follow it.

**Where they end up instead is not decided.** The working assumption is that they stay in this repository as the originating record, kept for provenance rather than reference. Whether that is what happens, whether the archive is marked and how, and whether they are kept at all are open — see Notes. This task is written on the working assumption, and its cutover commit and its criteria change if the decision goes another way. Settle it before doing the work, not while.

`docs/conventions/` is the exception and carries across as code: it governs the code, and the comment, test and cross-platform standards travel with it.

### What `CLAUDE.md` still needs

Most of this repo's `CLAUDE.md` already describes the code rather than the build, so the merge is small. Two things remain:

- Its **Specifications** section names `docs/spec/` and `docs/design/`, neither of which travels. It goes from the canonical repo's copy, and with it the rule that a behavior change amends the spec in the same commit: in `vault-server` there is no spec present to amend and the code is the only description of itself. Its **Reference documents** section stays, since `docs/conventions/` does travel.
- The canonical repo's own `CLAUDE.md` is the base for anything it says that this repo's does not. Reconcile the two rather than overwriting.

## Acceptance criteria

- [ ] The whole sequence was rehearsed on a branch in a clone before any of it was done for real.
- [ ] `vault-server` has a baseline commit, every non-root commit of this repo replayed onto it, and a cutover commit.
- [ ] The resulting tree equals this repo's tree, minus the parts of `docs/` that do not travel, with `CLAUDE.md` reconciled, plus the configuration that survived from the canonical repo. **Anything else in a diff against this repo means a commit resolved wrongly.**
- [ ] `CLAUDE.md` in `vault-server` names nothing under `docs/` that did not travel there.
- [ ] `docs/conventions/` is present in `vault-server` unchanged.
- [ ] `docs/spec/` and `docs/design/` are absent from `vault-server`, and whatever the decision below settles for them here has been carried out. Under the working assumption that is: present and unchanged in this repository after the cutover.
- [ ] `dotnet build --no-incremental` clean and `dotnet test` green in `vault-server` after the cutover commit.

## Notes

**Keep this repository afterwards.** It is the only intact record of the build and the thing to diff against if the replay is later found to be wrong. That holds however the question below is settled.

**Open, and to be settled before this task is worked. Each answer changes the task:**

- **Whether the specification and design are kept at all.** They hold the reasoning behind the build - including facts established by measurement that cannot be re-derived - but only until [task-010](task-010-conformance-audit.md) migrates what is load-bearing to the code sites that depend on it. What remains after that is provenance, not reference: correct as of one commit and drifting from then on. The case for keeping it is that deleting is irreversible and storing it costs nothing; the case against is that an unmaintained document in the present tense reads as authority to whoever finds it next.
- **If kept, how the archive is marked, and where the marking goes.** The documents state requirements in the present tense and read as live. A note on the repository as a whole and a header on every document are different bets about who gets confused and when. Whether this repository's own `CLAUDE.md` says anything about their status is part of the same question.
- **When they stop being authoritative.** They must stay live through [task-009](task-009-windows-only.md), task-010 and [task-011](task-011-comment-sweep.md), each of which may amend one. Cutover is the obvious moment for that to end, but nothing has fixed it.
- **Whether `docs/misc/Comment-Sweep.md` travels.** It is procedure for a pass rather than a standard, so it sits in neither category this task decides.
