# Tasks

Task index. See `tasks-instructions.md` for how to maintain this file — read it before changing anything here.

NEXT_TASK_ID: 016

## Active

## Backlog

- [ ] [task-004](task-004-logging-standard.md) — Standardize logging on two records, written as content-free JSON lines to rotated, retained files
- [ ] [task-005](task-005-tasks-instructions-as-skill.md) — Promote the task instructions into a skill
- [ ] [task-014](task-014-commit-standards.md) — Write the commit message standard, now that the message carries why a change was made
- [ ] [task-015](task-015-runspace-usenewthread.md) — Decide whether the runspace pool's `UseNewThread` still has a reason, and comment or remove it
- [ ] Recover the deleted powershell tool primitives for sync and lint to harvest for the new spec
- [ ] Evaluate whether delete should leave behind a [[Original-Note|Deleted-Note]] instead of leaving bad wikilinks; leaning yess
- [ ] Re-evaluate whether `UseAppHost=false` is still warranted for the MCP server; it was done due to Smart App Control blocking the newly built server but it still did it with the DLL; disabling SAC wasn't a one-way valve as the agent originally stated

## Sidequests

- [ ] [task-001](task-001-removal-census-over-files.md) — Index the removal census over every vault file, not only notes
- [ ] [task-002](task-002-cli-shell-completion.md) — Give the CLI shell completion, in PowerShell at least
- [ ] [task-003](task-003-vaults-json-absolute-paths.md) — Stop `vaults.json` hard-coding an absolute machine path
