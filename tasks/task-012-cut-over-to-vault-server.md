# task-012: Cut over to the canonical vault-server repository

**Goal:** This build's history is layered onto the canonical `vault-server` repository, which keeps its identity, remote and history, and whose resulting tree is exactly this repository's tree plus the configuration that survives from the canonical repo.

## Context

The risky one. Do it last, after `task-009-windows-only.md`, `task-010-conformance-audit.md` and `task-011-comment-sweep.md`, because every one of them changes the tree this task's acceptance test compares against. All three are archived.

The commits are **layered onto** the canonical repo rather than replacing its files, so that repo keeps its identity, its remote, and its history.

**Rehearse all of it on a branch in a clone before doing any of it for real.**

### Three moves

1. **Baseline commit** in `vault-server`: delete what this build deleted — the PowerShell access module under `powershell/` and the PowerShell tool scripts under `tools/`, along with their test suites — align every remaining path to this repo's root commit `7485317`, and leave untouched any path this repo does not track. `.mcp.json` is the only such path, and no commit here mentions it.
2. **Replay** every non-root commit onto that baseline: `git rebase --onto <baseline> 7485317 master --committer-date-is-author-date`. Start from the root's children, because the root's diff is against the empty tree and cannot apply to a non-empty baseline. Author dates are genuine and preserved; matching committer dates to them keeps the graph readable, at the cost that anything keyed on committer date places this work in the past.
3. **Cutover commit**: replace `CLAUDE.md` and hand-patch `vaults.json`, both described below.

### All of `docs/` travels

Every tracked document travels, in four categories:

- **`docs/conventions/`** carries across as code. It governs the code, and the comment, test and cross-platform standards travel with it.
- **`docs/reference/`** must travel. Comments cite `Ref-Obsidian-Link-Behavior.md` at every site that records link behavior, and `docs/reference/` is the only kind of document a comment may cite, so leaving it behind dangles every one of those citations.
- **`docs/guides/Comment-Sweep.md`** travels. `CLAUDE.md` describes `docs/guides/` as a live category and travels whether or not the guide does, so leaving the guide behind lands that paragraph in a repo with no guides directory. The sweep is a pass that gets run again on `vault-server` rather than a one-off of this build.
- **`docs/archive/redesign/`** travels. The specification and design sets are no longer maintained, because nothing should have to hold them in sync with an implementation that goes on changing. They are kept for provenance rather than reference, which is what the directory name and the per-document disclaimer say.

The archive move already landed here, in commit `59bf027`, so it replays like any other commit and the cutover conjures nothing. That commit flattened both sets into one directory, renamed the design set's `README.md` to `Design-Documents-Convention.md` so a bare `README.md` could not be mistaken for the archive directory's own front matter, fixed the one relative path that flattening broke, and gave every document a disclaimer naming the date it was archived and stating it is not to be modified.

### `CLAUDE.md` is replaced, not reconciled

This repo's copy replaces the canonical one wholesale. There is nothing to merge. `vault-server`'s `CLAUDE.md` is only a pointer to an external corpus under `C:\Repos\vault-primary\Projects\Vault-MCP-Server\Corpus\`, declared authoritative and mandatory reading before any structural change, and after the baseline commit all of it — `Server-Structure.md`, `Testing.md`, `Dev-Setup.md` — describes a repo that no longer exists.

Whether that corpus is updated or retired is a separate question, in a repository this task does not touch.

### `vaults.json` is hand-patched last

Both repos track it, and it is the one file where the replay and the real configuration collide. This repo's copy is scoped to the test vault, and two non-root commits patch it — `e011685`, which moves the read floor onto a per-vault path policy, and `fafe9b7`.

So the baseline aligns it to this repo's root commit like any other tracked path, and the replay applies cleanly. After the replay it is patched by hand to carry the real vault's configuration, which is recoverable from `vault-server`'s history, expressed in the schema this build arrived at. Its pre-cutover content is not restored verbatim, because what has to survive is the information rather than the form it was written in.

## Acceptance criteria

- [ ] The whole sequence was rehearsed on a branch in a clone before any of it was done for real.
- [ ] `vault-server` has a baseline commit, every non-root commit of this repo replayed onto it, and a cutover commit.
- [ ] The resulting tree equals this repo's tree, with `CLAUDE.md` and `vaults.json` as described above, plus the configuration that survived from the canonical repo. **Anything else in a diff against this repo means a commit resolved wrongly.**
- [ ] `docs/` in `vault-server` is identical to `docs/` here, with conventions, reference, guides and `archive/redesign/` all present and unchanged.
- [ ] `CLAUDE.md` in `vault-server` is this repo's copy, and names nothing that does not exist there.
- [ ] `powershell/` is absent from `vault-server`, and `tools/` holds only `.gitkeep`.
- [ ] `vaults.json` in `vault-server` names the real vault and validates against the path policy and frontmatter registry this build implements. The server starts against it.
- [ ] `dotnet build --no-incremental` clean and `dotnet test` green in `vault-server` after the cutover commit.

## Notes

**Keep this repository afterwards.** It is the only intact record of the build and the thing to diff against if the replay is later found to be wrong.

**Check during rehearsal:**

- **The `.claude/settings.json` grant.** The canonical `CLAUDE.md` documents a `permissions.additionalDirectories` grant giving a session rooted in `vault-server` read access to `C:\Repos\vault-primary`. Replacing `CLAUDE.md` leaves that grant in place but undocumented. Decide whether it goes with the corpus reference or stays.
- **Where the conflicts cluster.** 178 commits replay, and one of them scoped this repo to the test vault. Expect configuration to conflict rather than `src/`.
