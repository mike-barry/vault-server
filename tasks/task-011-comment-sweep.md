# task-011: Comment sweep over the repository, including the test projects' doc references

**Goal:** Every comment in the repository meets the standard in `docs/conventions/Code-Comments.md`, and no `<see cref="..."/>` anywhere resolves to nothing.

## Context

Run this after [task-009](task-009-windows-only.md) and [task-010](task-010-conformance-audit.md), so it covers every code change those made. Sweeping before them means sweeping twice: a comment rewritten now and then deleted or contradicted by a later finding was wasted work, and a hedge those tasks remove would be polished on the way out.

`docs/guides/Comment-Sweep.md` has the procedure for a sweep over the whole repository against the standard. It is the authority on how the pass runs.

**The test projects' dangling doc references fold into the same pass.** The three test projects do not generate documentation, so a broken `<see cref="..."/>` there is invisible and nothing reports it. Enable `GenerateDocumentationFile` on each, build, fix what it reports, then revert the project files. That half is mechanical.

`CS1573` already holds parameter completeness on the three `src/` projects at build time, so a documented member missing a parameter is not what this pass is looking for. What it is looking for is the standard's substance: a summary that opens with a verb phrase and says plainly what the thing does, and a comment that stands on its own — leaning on no compressed idiom and no fact stated elsewhere, so a reader who has opened nothing but the file in front of them understands it on first read.

**A comment referencing a document under `docs/` is a defect this pass fixes**, not a pointer to preserve. The specification and design travel at cutover ([task-012](task-012-cut-over-to-vault-server.md)) as frozen historical records, which makes citing them worse rather than better. A reference to a file nobody can open fails loudly and the reader falls back on the code; a reference to a frozen file resolves, and tells the reader something the code has since changed with nothing marking it stale. `docs/conventions/Documentation-Strategy.md` carries the rule as a convention rather than a one-off. A comment cites no document except one under `docs/reference/`, and a fact worth keeping is stated in the comment itself, including what it was measured or agreed against and what changing it breaks.

## Acceptance criteria

- [ ] The whole repository has been swept per `docs/guides/Comment-Sweep.md`.
- [ ] No comment under `src/` or `tests/` cites a document other than one under `docs/reference/`, by name or by section, and every permitted citation is a bare `Ref-<Subject>.md` filename supplementing a comment that already stands on its own.
- [ ] `GenerateDocumentationFile` was enabled on the three test projects, the build's reported doc-reference errors were fixed, and the project files were reverted afterwards.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

**The documentation strategy changed while this task was open (2026-08-09).** Code comments are now the primary description of the system. `docs/conventions/Documentation-Strategy.md` states where a fact goes when a comment cannot hold it, `docs/reference/` was created for facts about subjects outside this repository, and `Spec-Obsidian-Link-Behavior.md` moved there as `Ref-Obsidian-Link-Behavior.md`. `docs/spec/` and `docs/design/` are being retired as authorities, though they travel at cutover as frozen records.

**A comment may cite a `docs/reference/` document, and only that.** The property that admits a document to `docs/reference/` - no change here can falsify it - is the same property that makes citing it safe. The citation supplements the comment and never replaces it. The strategy also carries the three conditions a fact must meet to be promoted out of a comment into a reference document, and the sweep proposes promotions rather than filing them.

**`WikilinkRanking` is promoted, and this is Pass 1 work.** Its class remark restates Obsidian's resolution rule in full, on the stated grounds that it is the only copy someone who cannot re-measure could implement from. `Ref-Obsidian-Link-Behavior.md` now states the same rule more fully, plus the settings and measurement dates a comment may not carry, so that premise has lapsed and the remark is a second, shorter copy. It keeps what constrains this code - that the rule is observed rather than chosen, that existing content depends on it, that changing it changes which note existing links open, that it is confined to `Compare`, and that re-measurement must follow a link rather than read the autocomplete popup - plus a citation. The three-paragraph statement of the rule goes; `Compare` and `Spelling` already document their own halves beside the code.

**`Code-Comments.md`'s roster of four protected comments was deleted, not amended.** Enumerating sites protects only what existed when the list was written and implies everything absent from it is fair game. The replacement is a property the sweep can recognize anywhere: a comment recording a measured or agreed fact is not shortened by anyone who cannot repeat the measurement, and it identifies itself by saying what was measured, what against, and what changing it breaks. The roster's `Vault.LockName` entry also carried a false claim - that a separate process reimplements the derivation, where the comment itself says only that `VaultWatcher` is the second holder of the lock - and deleting it removed that too.

**Rescuing the design documents' `What This Closes` entries is in scope, and runs in Pass 1.** Those sections record the decisions the specifications left open, and an entry with a code site is already constraint-shaped, which is the form a comment is meant to take. Freezing them without a pass over them leaves reasoning a later reader cannot reach, since a frozen document may not be cited. Pass 1 is the only pass whose workers may read those documents, so deferring the rescue means reading them a second time. `docs/guides/Comment-Sweep.md` carries this as part of the procedure.

**Pass 2 gains a second report category: a comment describing behavior whose site is outside the worker's area.** The documentation strategy requires one site to own a cross-file concern and the participants to restate nothing, and a worker partitioned by directory cannot tell an owner from a restater. Ownership is decided by an arbiter pass after Pass 2, over the collected reports. Refusal rendering, scope enforcement, and the lock derivation are the concerns most likely to surface.