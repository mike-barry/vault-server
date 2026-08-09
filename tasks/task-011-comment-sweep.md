# task-011: Comment sweep over the repository, including the test projects' doc references

**Goal:** Every comment in the repository meets the standard in `docs/conventions/Code-Comments.md`, and no `<see cref="..."/>` anywhere resolves to nothing.

## Context

Run this after [task-009](task-009-windows-only.md) and [task-010](task-010-conformance-audit.md), so it covers every code change those made. Sweeping before them means sweeping twice: a comment rewritten now and then deleted or contradicted by a later finding was wasted work, and a hedge those tasks remove would be polished on the way out.

`docs/misc/Comment-Sweep.md` has the procedure for a sweep over the whole repository against the standard. It is the authority on how the pass runs.

**The test projects' dangling doc references fold into the same pass.** The three test projects do not generate documentation, so a broken `<see cref="..."/>` there is invisible and nothing reports it. Enable `GenerateDocumentationFile` on each, build, fix what it reports, then revert the project files. That half is mechanical.

`CS1573` already holds parameter completeness on the three `src/` projects at build time, so a documented member missing a parameter is not what this pass is looking for. What it is looking for is the standard's substance: a summary that opens with a verb phrase and says plainly what the thing does, and a comment that stands on its own — leaning on no compressed idiom and no fact stated elsewhere, so a reader who has opened nothing but the file in front of them understands it on first read.

**A comment referencing a document under `docs/` is a defect this pass fixes**, not a pointer to preserve. The specification and design do not travel at cutover ([task-012](task-012-cut-over-to-vault-server.md)), so in `vault-server` every such reference is dangling prose naming a file nobody there can open; they are being retired here as well, so it is dangling in this repository too. `docs/conventions/Documentation-Strategy.md` carries the rule as a convention rather than a one-off: a comment references no document, and a fact worth keeping is stated in the comment itself, including what it was measured or agreed against and what changing it breaks.

## Acceptance criteria

- [ ] The whole repository has been swept per `docs/misc/Comment-Sweep.md`.
- [ ] No comment under `src/` or `tests/` references a file under `docs/`, by name or by section.
- [ ] `GenerateDocumentationFile` was enabled on the three test projects, the build's reported doc-reference errors were fixed, and the project files were reverted afterwards.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

**The documentation strategy changed while this task was open (2026-08-09).** Code comments are now the primary description of the system. `docs/conventions/Documentation-Strategy.md` states where a fact goes when a comment cannot hold it, `docs/reference/` was created for facts about subjects outside this repository, and `Spec-Obsidian-Link-Behavior.md` moved there as `Ref-Obsidian-Link-Behavior.md`. `docs/spec/` and `docs/design/` are being retired. None of this changes what the sweep does, and it settles the ambiguity behind the second acceptance criterion: no comment cites a document, whatever becomes of the documents.

**Recommended when the strategy change was made, and not scoped here:** rescue the `What This Closes` entries from the design documents before they are archived. Those sections record the decisions the specifications left open, and an entry with a code site is already constraint-shaped, which is the form a comment is meant to take. Archiving without a pass over them drops reasoning a later reader would have to reconstruct from the code alone, and this sweep is the natural place to notice it, since it is already reading every comment. Whether that belongs to this task, to one of its own, or nowhere is the operator's call, and nothing in this task depends on the answer.