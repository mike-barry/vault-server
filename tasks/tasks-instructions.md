# Task Management Instructions

How to read and maintain `tasks/tasks.md`. Load this file before making any change to the task list or to a `task-*.md` file.

## Who adds tasks

**The user adds tasks. An agent never does so on its own initiative.**

This applies to every section, including Sidequests, and to both forms an item can take — a bare checklist line and a `task-*.md` file. Adding a line to `tasks.md` counts as adding a task; so does creating a task file.

An agent may **recommend** a task. Do that by describing it in the conversation — what it is, why it is worth doing, and which section it belongs in — and then wait. Write nothing to `tasks/` until the user says to.

The rule holds even when adding the task seems obviously right: work uncovers a bug, a `TODO` turns up in the source, a task splits into three. Recommend it, and let the user decide. A task list the user did not choose is no longer a reliable statement of what they intend to do.

Once the user approves, follow the lifecycle and ID rules below as normal.

Editing an **existing** task is different and needs no permission: updating Notes, checking off acceptance criteria, or recording findings as you work is expected. So is marking an item in progress in the index. Moving an item to a different section is the user's call, because the section is the item's status.

## Structure

- `tasks/tasks.md` — the index. Holds every open item, the section it sits under, and its progress marker. **The index is the single source of truth for status and priority.**
- `tasks/task-###-slug.md` — detail for one task. Holds the what/why/how. **Never put a status field in a task file** — status lives only in the index.
- `tasks/archive/` — completed task files, moved here on completion.

## Sections

An item's **status is the section it sits under**. There is no status field anywhere: moving the line is how status changes, which is why moving it is the user's call. Priority is the order of lines within a section, highest first.

| Section | Meaning | May an agent start it unprompted? |
| --- | --- | --- |
| **Active** | Committed and specified. Being worked now or next. | Yes — this is the answer to "what's next?" |
| **Backlog** | Agreed it should happen, not scheduled. | No. Only when Active is empty or the user names it, and confirm before starting. |
| **Sidequests** | Ideas with no commitment. May never happen. | **Never.** Do not start one, and do not propose one as next work. Only act if the user points at it directly. |

Keep Active to roughly 5–7 items. If it grows past that, move items down to Backlog rather than letting Active become a second backlog.

Sidequests is an idea parking lot, not a low-priority backlog. It is expected to accumulate items that are never done. Do not prune it or nag about its size.

## Index lines

Every item is one checklist line under a section heading. An item with a task file links to it and leads with its ID; an inline item is bare text.

```markdown
## Active

- [/] [task-003](task-003-a-filed-task.md) — One line saying what the task is
- [ ] [task-001](task-001-another-filed-task.md) — One line saying what the task is

## Backlog

- [ ] An inline item, captured as a single line
- [ ] [task-008](task-008-a-filed-task.md) — One line saying what the task is
```

The description after the em dash is a single line, whatever the section. For a filed task it summarises the file rather than repeating its Goal.

Two markers, and only two:

- `[ ]` — not started.
- `[/]` — in progress. An agent sets this when it starts work on the item, without asking.

**An agent never writes `[x]`.** The user may. An `[x]` found in the index means the user considers that item finished, so raise it: recommend archiving it if it has a task file, or deleting the line if it is inline, and wait for an answer.

## Naming a task

A task is named by its bare filename — `task-011-comment-sweep.md` — never by a path.

`tasks.md` is the one file that links. Its checklist lines carry a markdown link whose target is that bare filename, which is what makes the index navigable. Those links never break, because archiving an item deletes its line in the same edit that moves its file.

Everywhere else — in a task file, archived or not, and in prose anywhere — the filename is written in backticks and is not a link. A link written outside the index breaks the moment the task it names is archived, and it breaks silently, since nothing here checks that a link resolves. Writing `archive/` or `../` into the target is worse still. It is a claim about where the file sits today, and archiving is exactly the thing that falsifies it.

## Lifecycle

An item may be captured as a bare checklist line in any section, or start life with a task file. The one hard rule:

> **An item must have a task file before it enters Active.**

So the two valid paths are:

1. Capture inline in Backlog or Sidequests → promote to a file when it moves to Active.
2. Create the file immediately, at whatever section it belongs in.

Inline items are a single line with no sub-bullets. The moment an item needs more than one line, it needs a file.

Items move freely between sections. Moving an item edits its line in the index only — the task file does not move and its ID does not change.

## IDs

- IDs are assigned when a task file is created, using the next unused number.
- Zero-padded to three digits, followed by a short slug: `task-003-an-example-task.md`.
- **IDs are never reused, renumbered, or changed** — including on completion, or when an item moves between sections. Consult `NEXT_TASK_ID:` in `tasks.md` for the next available ID.
- When creating a new task, increment the value of `NEXT_TASK_ID:` in `tasks.md` for the next task.
- Inline items have no ID. "Has an ID" and "has a file" mean the same thing.

## Completing a task

**An agent never archives a task on its own initiative.** Believing the work is done is not the same as the user agreeing it is done, and archiving is awkward to notice after the fact — the item disappears from the index, so the user has nothing left to review in the place they normally look.

When an agent judges a task complete:

1. Verify the acceptance criteria in the task file actually pass. Check them off.
2. **Tell the user the task looks complete and ask whether to archive it.** Say what was done, and name anything that was descoped, left failing, or handled differently than the file describes.
3. Wait. Leave the task in Active, and leave its line marked `[/]`, until the user answers.

Only once the user says to archive:

4. Move the file to `tasks/archive/` (keep the same filename).
5. Delete its line from `tasks.md`.

Completed work is not listed in the index — the git history and the archive folder are the record.

The same rule applies in reverse: do not un-archive a task, or resurrect its line in the index, without being asked.

### Editing an archived task file

Prefer not to. An archived file records what was known when the work finished, and later sessions read it as history. If something in it is now actively misleading — it describes behavior that has since been filed as a bug, or points at a file that has moved — ask the user before editing, and prefer adding a forward pointer over rewriting the original wording.

## Working a task

- Update the **Notes** section of the task file as you go: dead ends, decisions, gotchas, and file:line pointers. This is the main reason task files exist — the next session should not have to rediscover what you learned.
- Check off acceptance criteria in the task file as they are met.
- If work reveals a new, unrelated item, do not expand the current task's scope and do not add it to the list yourself. Recommend it to the user, naming the section you think it belongs in.
- If a task turns out to be substantially bigger than its file describes, say so and recommend a split. Do not create the new task files until the user agrees.
- If a task evolves past what its filename describes, suggest to the user a rename of the file.

## Task file template

```markdown
# task-###: Title

**Goal:** One sentence describing what "done" looks like.

## Context

Why this matters, plus relevant `file.cs:line` pointers.

## Acceptance criteria

- [ ] Concrete, checkable outcomes

## Notes

Findings as work proceeds — dead ends, decisions, gotchas.
```
