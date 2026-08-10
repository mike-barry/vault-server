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

### `CLAUDE.md` needs no step of its own

This repo's copy replaces the canonical one wholesale, and there is nothing to merge. `vault-server`'s `CLAUDE.md` is only a pointer to an external corpus under `C:\Repos\vault-primary\Projects\Vault-MCP-Server\Corpus\`, declared authoritative and mandatory reading before any structural change, and after the baseline commit all of it — `Server-Structure.md`, `Testing.md`, `Dev-Setup.md` — describes a repo that no longer exists.

The rehearsal showed the replacement needs no action at cutover. The baseline sets `CLAUDE.md` to the root commit's version like every other tracked path, and the replay then applies every later edit to it, so it arrives as this repo's copy on its own.

Whether that corpus is updated or retired is a separate question, in a repository this task does not touch.

### `vaults.json` is hand-patched last

Both repos track it, and it is the one file where the replay and the real configuration collide. This repo's copy is scoped to the test vault, and two non-root commits patch it — `e011685`, which moves the read floor onto a per-vault path policy, and `fafe9b7`.

So the baseline aligns it to this repo's root commit like any other tracked path, and the replay applies cleanly. After the replay it is patched by hand to carry the real vault's configuration, which is recoverable from `vault-server`'s history, expressed in the schema this build arrived at. Its pre-cutover content is not restored verbatim, because what has to survive is the information rather than the form it was written in.

The patched file names both vaults. `primary` points at `C:/Repos/vault-primary` and names `frontmatter-registry.json`, and `test` points at the repository's own `test-vault` and names `test-vault-frontmatter-registry.json`. `primary` is the default vault, as it was before the cutover.

## Acceptance criteria

- [x] The whole sequence was rehearsed on a branch in a clone before any of it was done for real.
- [x] `vault-server` has a baseline commit, every non-root commit of this repo replayed onto it, and a cutover commit.
- [x] The resulting tree equals this repo's tree, with `CLAUDE.md` and `vaults.json` as described above, plus the configuration that survived from the canonical repo. **Anything else in a diff against this repo means a commit resolved wrongly.**
- [x] `docs/` in `vault-server` is identical to `docs/` here, with conventions, reference, guides and `archive/redesign/` all present and unchanged.
- [x] `CLAUDE.md` in `vault-server` is this repo's copy, and names nothing that does not exist there.
- [x] `powershell/` is absent from `vault-server`, and `tools/` holds only `.gitkeep`.
- [x] `vaults.json` in `vault-server` names the real vault and validates against the path policy and frontmatter registry this build implements. The server starts against it.
- [x] `dotnet build --no-incremental` clean and `dotnet test` green in `vault-server` after the cutover commit.

## Notes

**Keep this repository afterwards.** It is the only intact record of the build and the thing to diff against if the replay is later found to be wrong.

### What the rehearsal found

Rehearsed in a clone of `vault-server` on a branch, with all three moves carried out.

**The baseline is much smaller than expected.** Canonical `HEAD` and this repo's root commit differ by 11 added paths and 54 deleted ones, and only two shared paths differ in content — `CLAUDE.md` and `.claude/settings.json`. Everything under `src/`, `tests/` and `test-vault/` already aligns by name and by content. Of the 54 deletions, 53 are the PowerShell module and tool scripts and the 54th is `.mcp.json`, which must be kept rather than deleted.

**The replay is conflict-free.** All 182 non-root commits applied with no conflict, which follows from the baseline making the tree exactly the root tree. `rerere` was enabled to carry resolutions into the real run and cached nothing, because there was nothing to cache. The commit count is 182 rather than the 178 this file first recorded.

**The tree matched before the cutover commit.** The replayed tree equalled this repo's tree exactly, with `.mcp.json` as the only addition.

**`vaults.json` is the only hand-patch, and it needs one decision.** Two things came out of testing it.

- **The primary vault must name a frontmatter registry.** A vault naming none records a `Degraded` problem, and `CommittedDeclarationTests` asserts that the shipped configuration records none, so an entry carrying only `path` fails `dotnet test`. This is settled. The repository now ships two declarations — `frontmatter-registry.json` describes the real vault and `test-vault-frontmatter-registry.json` is the fixture vocabulary under a name that says so — and the cutover's `vaults.json` names one from each vault.
- **The read-floor warning during rehearsal is an artifact of the clone's location.** `vaults.json` names `C:/Repos/vault-server/test-vault` while the clone sits elsewhere, so the always-readable folder resolves to nothing. The folder ships in the replayed tree, so it resolves at the real location. This is the absolute-path fragility `task-003-vaults-json-absolute-paths.md` describes.

**The `.claude/settings.json` grant answers itself.** The root commit sets `additionalDirectories` to empty and nothing later re-adds it, so the replay drops the `C:\Repos\vault-primary` grant on its own. Restoring it, if it is wanted, is a deliberate act rather than an oversight to avoid.

### The real run

Done on `vault-redesign-cutover` in `vault-server`, branched from `master`. 185 commits replayed with no conflict, 187 on top of `master` counting the baseline and the cutover commit. `master` is untouched and still equal to `origin/master`, and nothing is pushed.

The tree differs from this repository's by `.mcp.json` and `vaults.json` and nothing else, which is the criterion. `docs/` is identical file-for-file, `CLAUDE.md` is identical and names no path absent there, `powershell/` is gone and `tools/` holds only its `.gitkeep`. The build is clean at 0 warnings and all 1571 tests pass.

**The configuration was verified running, not only loading.** `health_check` reports both vaults configured with `primary` as the default and no fatal problem, and the host starts with the always-readable root applied, the tools watcher attached, and the full ten-tool surface projecting over MCP. Read-only calls against both vaults return through the layer.

**One degraded problem survives, and it predates the cutover.** `grants.json` is untracked local configuration, and its `read-only` grant declares an empty `scopeRoots` list for each vault. This build requires `["*"]` as the only spelling of whole-vault access, so that grant is skipped entirely and only `admin` loads. The file was written for the server this build replaces. Fixing it is an edit to live authorization configuration and is the operator's.

**The host resolves its configuration relative to the content root, and the sanctioned launchers set that to `src/VaultMcpServer`.** `appsettings.json` names `../../vaults.json`, so the paths only resolve when the process starts in the project directory, which `run.ps1` and the VS Code launch configuration both do. Started any other way the host looks two levels above the repository, finds nothing, and comes up fatally degraded while still binding its port. This is unchanged by the cutover and is worth knowing before diagnosing a start that looks broken.
