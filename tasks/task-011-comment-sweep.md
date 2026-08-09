# task-011: Comment sweep over the repository, including the test projects' doc references

**Goal:** Every comment in the repository meets the standard in `conventions/Code-Comments.md`, and no `<see cref="..."/>` anywhere resolves to nothing.

## Context

Run this after [task-009](task-009-windows-only.md) and [task-010](task-010-conformance-audit.md), so it covers every code change those made. Sweeping before them means sweeping twice: a comment rewritten now and then deleted or contradicted by a later finding was wasted work, and a hedge those tasks remove would be polished on the way out.

`conventions/Comment-Sweep.md` has the procedure for a sweep over the whole repository against the standard. It is the authority on how the pass runs.

**The test projects' dangling doc references fold into the same pass.** The three test projects do not generate documentation, so a broken `<see cref="..."/>` there is invisible and nothing reports it. Enable `GenerateDocumentationFile` on each, build, fix what it reports, then revert the project files. That half is mechanical.

`CS1573` already holds parameter completeness on the three `src/` projects at build time, so a documented member missing a parameter is not what this pass is looking for. What it is looking for is the standard's substance: a summary that opens with a verb phrase and says plainly what the thing does, and a comment that stands on its own — leaning on no compressed idiom and no fact stated elsewhere, so a reader who has opened nothing but the file in front of them understands it on first read.

**A comment referencing a document under `docs/` is a defect this pass fixes**, not a pointer to preserve. `docs/` is deleted at cutover ([task-012](task-012-cut-over-to-vault-server.md)) and every such reference becomes dangling prose. A fact worth keeping is stated in the comment itself, including what it was measured or agreed against and what changing it breaks.

## Acceptance criteria

- [ ] The whole repository has been swept per `conventions/Comment-Sweep.md`.
- [ ] No comment under `src/`, `tests/` or `conventions/` references a file under `docs/`, by name or by section.
- [ ] `GenerateDocumentationFile` was enabled on the three test projects, the build's reported doc-reference errors were fixed, and the project files were reverted afterwards.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes
