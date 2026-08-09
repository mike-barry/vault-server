# task-005: Promote the task instructions into a skill

**Goal:** The rules for maintaining the task list load as a skill when an agent touches task work, instead of depending on an agent reading a sidecar file it was told about.

## Context

`tasks/tasks-instructions.md` currently reaches an agent by one route: the `## Tasks` section of `CLAUDE.md` says to read it before changing the index or a `task-*.md` file. That works only as long as the agent notices the pointer applies and spends a read on it, and it is the same cost every session whether or not any task work happens. The rules it carries are the kind that fail silently when skipped - an agent that never opened it will add a task on its own initiative, or archive one, and both are the things the file exists to prevent.

A skill inverts that. `.claude/skills/<name>/SKILL.md` carries a description the harness matches against what the agent is doing, and the body loads only when it matches. The repository has no `.claude/skills/` directory today; `.claude/` holds `settings.json` and `worktrees`.

The content itself needs no rewriting to move - it is already instructions addressed to an agent. What changes is how it is found, and what `CLAUDE.md` should say once it is found some other way.

## Acceptance criteria

- [ ] A skill exists whose body carries the rules currently in `tasks/tasks-instructions.md`
- [ ] Its description triggers on reading the task list, adding or working a task, and archiving one
- [ ] The rules are in one place - not duplicated between a skill and a sidecar file that can drift apart
- [ ] `CLAUDE.md`'s `## Tasks` section is updated to match however the rules are now reached

## Open questions

- Does `tasks/tasks-instructions.md` go away, or stay as the human-readable copy with the skill pointing at it? Two copies of the same rules is the drift risk the third criterion names; a skill that only points at a file reintroduces the read the change was meant to remove.
- `CLAUDE.md` currently restates two rules - operator-only adds, operator-only archives - so they hold whether or not the instructions were read. If the skill may not load, that hedge is still needed; if it always loads, the restatement is duplication.
- Skills under `.claude/` are not covered by the `conventions/` rule about what survives cutover. Does this skill travel to the canonical repository with the code, or archive with the working files?

## Notes

Findings as work proceeds - dead ends, decisions, gotchas.
