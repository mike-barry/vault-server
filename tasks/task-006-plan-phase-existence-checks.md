# task-006: Establish existence in the plan phase for `create` and `delete`

**Goal:** `create` refuses a missing parent before it rewrites anything, and a `delete` preview of a folder that is not there refuses instead of predicting a removal the applied call cannot perform.

## Context

Two findings from the full code review (plan step 1), both surviving an independent refutation pass. They are one defect wearing two hats: a condition decidable in the plan phase is instead discovered later, in a tool that has a sibling doing it correctly.

**`create` — `src/VaultShared/Tools/CreateTool.cs:160`.** `CreateNote` runs the vault-wide wikilink pinning pass before anything establishes that the target's parent folder exists. `parent_missing` is classified from the exception `Store` raises at the end (`src/VaultShared/Access/VaultFiles.cs:572`). So `create {path: "Nowhere/Notes.md"}` — a typo in the folder name, into a vault that already holds a `Notes.md` — runs the pass, rewrites bystander notes across the vault to pin their bare links, and then refuses. The caller is told the creation did not happen and is told nothing about notes it never named being modified.

The pins left behind are individually correct: a pin freezes a resolution that already applied, so the vault is not corrupted. What is wrong is the phasing. `docs/design/Design-Tool-Vocabulary.md:218-230` puts existence in phase 4 and states that nothing is written before phase 5, and `docs/spec/Spec-Tool-Capabilities.md:Multi-Target Mutation` requires a multi-target mutation to plan, validate the whole set, then apply, "so a refusal is impossible to reach after the first write". A creation that pins is a multi-target mutation by that section's own words.

`MoveTool` shows the intended convention: `src/VaultShared/Tools/MoveTool.cs:128-152` calls `VaultFiles.PreflightMoveFile` / `PreflightMoveFolder` before its pass, and its comment says why — otherwise an applied call would discover the refusal with the whole vault's links already rewritten for a move that never lands. `CreateTool` has no equivalent.

**`delete` — `src/VaultShared/Tools/DeleteTool.cs:169`.** `Plan`'s note branch refuses `path_not_found` when the census does not carry the target (line 161). The folder branch prefix-filters the census and seeds `folders` with the target unconditionally, and nothing in the tool establishes that the folder is on disk. Path admission is `ToolPaths.WriteFolder` → `VaultAccess.ForWrite`, which is grammar, dot-segment and scope only and never touches the filesystem.

So `delete {path: "Projects/Widgets", kind: "folder"}` against a vault holding `Projects/Widget` previews as a clean, empty, removable folder — the same shape as the preview of a real one. The caller re-sends with `apply: true` and `Remove` raises `path_not_found` (line 289), a refusal the preview did not make. `docs/spec/Spec-Tool-Capabilities.md:The Preview Contract` requires a plan to run the same validation and refuse everything a real call would. A folder-kind call naming an extensionless *file* previews the same way, and its applied call fails as `write_failed`.

The layer already offers the probe: `VaultFiles.PreflightMoveFolder` (`src/VaultShared/Access/VaultFiles.cs:419-422`) does exactly this `Directory.Exists` check for the move preview, its comment stating that a folder operand has nothing else to establish existence with.

## Acceptance criteria

- [ ] `CreateNote` establishes occupancy and parent existence on the target before the pinning pass runs, raising `already_exists` / `parent_missing` there. `VaultFiles.Create` stays the authority on occupancy, so the atomic check-and-write guarantee is not weakened — the preflight narrows the window, it does not replace the write's own refusal.
- [ ] Pinning still runs before the note is written. `docs/spec/Spec-Tool-Capabilities.md:Wikilink Maintenance` requires that ordering and gives the reason; only the decidable refusals move ahead of it.
- [ ] `DeleteTool.Plan` establishes that a folder operand exists, refusing `path_not_found` naming the caller's spelling, and the same refusal covers a folder-kind call on an extensionless file.
- [ ] A test drives `create` into a missing parent with a duplicated basename in the vault, and asserts no bystander note was modified.
- [ ] A test drives a `delete` preview of a folder that does not exist and asserts it refuses, as `AMissingNoteIsAFailureEvenInThePreview` does for the note case.
- [ ] `tests/VaultShared.Tests/Tools/CreateToolTests.cs:193` (`PinsAreAppliedBeforeTheNoteIsWritten`) is updated: it currently asserts the pins land on a creation that then fails, which is the behaviour being changed. It should assert that pins land before the write on a creation that can proceed.
- [ ] Any text in `docs/design/Design-Tool-Vocabulary.md` stating either tool's failure ordering is rewritten in the same commit, or confirmed to already describe the new behaviour. Failure ordering is contract per `docs/spec/Spec-Tool-Capabilities.md:The Error Vocabulary`.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

The `create` finding was narrowed by its refutation pass. Two things it ruled out, so they are not in scope: relabelling the refusal `partial_mutation` (the pins are correct, so there is no partial state to report), and moving the pinning pass after the write (the spec forbids it). `write_failed` arriving from the store after the pass remains a legitimate outcome — a write can fail for reasons no plan phase can decide.
