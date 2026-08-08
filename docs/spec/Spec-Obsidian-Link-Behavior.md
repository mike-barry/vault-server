# Obsidian Link Behavior

How Obsidian resolves and maintains `[[wikilinks]]` when multiple files share a basename, established empirically (2026-07-23, re-measured 2026-08-04 and 2026-08-08, Obsidian with *New link format = shortest path when possible* and *Automatically update internal links = on*). This model is the behavioral reference the `move` tool mirrors.

## Resolution Rule

A target is first read as an **address**, then resolved in two tiers. Both steps are **deterministic**.

**Addressing.** A target beginning with `/` is measured from the vault root; one beginning with `./` or `../` is measured from the linking file's own folder, each `..` climbing one level and a climb past the root simply stopping there (`../../../../Delta` written in `S/T/` addresses `Delta`). The address is what the tiers below then resolve, loose matching included: `../Delta` written in `S/T/` addresses `S/Delta`, and where no file sits there it still falls through to the second tier. Only the single leading empty component is an anchor - any other empty component, including a trailing one, makes the target name nothing, so `//Delta` and `./Delta/` resolve to nothing at all, as do `.` and `..` alone. A target whose first component is anything else is already measured from the vault root and is read exactly as written: an interior `..` does not fold (`S/../Delta` names nothing), a backslash is an ordinary character (`.\Delta` names nothing), and a leading `..` that is part of a name is not an anchor (`..Delta` names nothing).

**Exact path wins outright.** The target text is first tried as a path from the vault root. A file sitting at that path takes the link and no other candidate is considered. Targets are vault-relative, never relative to the linking file: `[[Q/Kappa]]` written in `W/Note.md` opens `Q/Kappa.md` and not `W/Q/Kappa.md`.

**Otherwise, by proximity.** Where the target matches no path, the candidates are every file the target *names*: its final component must match the file's basename whole, and the text before it must be a character suffix of the file's folder path. The suffix is measured in characters rather than components, so `sign/Spec-Tool` names `.../Redesign/Spec-Tool` - only the leading folder name can be cut that way, and the final component is never partial (`Tool` alone names nothing). A bare target names every file with that basename.

Those candidates fall into two groups: those inside the linking file's own folder subtree and those outside it. Climbing out of that subtree costs more than descending into it at any depth, so a candidate three folders below the linking file beats one a single folder above it. Within either group the shortest relative path wins, measured in characters, each `..` costing only the three it is written with - so a candidate two folders down under short names beats one a single folder down under a long one. Only then are ties broken by ordinal comparison of the candidates' **vault paths**.

A path-qualified link is therefore not safe from re-aiming: it reaches its file by a rule wider than that file's own path, so a file created at the path the target spells takes the link from it. Only a target spelling a file's whole path is immune, which is what a pin writes.

The two tiers together are what make a **vault-root file special**: a bare `[[Name]]` is the exact vault path of a root-level `Name.md`, so a root file holds every bare link to its basename unconditionally and cannot be displaced by a same-named file created anywhere below it. The same fact runs the other way - creating a file at the vault root captures every bare link to that basename across the whole vault at once, which is the widest re-aiming any single change can cause.

An embed (`![[...]]`) is addressed and resolved exactly as a link is, and renders as not-yet-created where the target names nothing.

Anchored targets are a form a vault holds by configuration rather than only by hand. Under *New link format = path from current file* the editor writes them itself: from `Nv/Aa/Bb/`, a link to `Nv/Zz/Nova.md` is inserted as `[[../../Zz/Nova|Nova]]` and one to the root `Nova.md` as `[[../../../Nova|Nova]]`. Under *path from vault folder* the same two are `[[Nv/Zz/Nova|Nova]]` and `[[Nova]]`. An inserted link carries a display alias wherever the emitted target is not already the basename.

The autocomplete suggestion popup for a duplicated basename orders its candidates by a rule of its own, not the ranking above - its first entry is not necessarily the note the link opens once accepted, so it cannot be read as a preview of resolution.

## Maintenance Rules

- **Emit on rename/move:** links to the renamed/moved file are re-emitted per the link-format setting - bare when the basename is unique, full vault path when it is not. Deliberately path-qualified links to a unique-basename file are collapsed to bare on move (no form preservation). An anchored link is flattened the same way: `./Delta` is re-emitted as `Delta` or `S/T/Delta`. An emitted link is never anchored, which is a property worth keeping - an anchored target is measured from the linking file's folder, so it would silently change meaning the next time the *linking* file moved.
- **Protection:** an in-app action that creates or shifts ambiguity for a basename (creating a duplicate, renaming into a collision, moving a same-basename file closer) rewrites affected bare links to **full vault path** first, so no link's resolution ever silently changes. Protection extends to bystanders - links to the file the action didn't touch. It is offered as a prompt naming the count of affected links, with *Always update*, *Just once*, and *Do not update*; declining leaves the links bare and lets them re-aim. A pin is always safe to write, because the full vault path lands in the exact-path tier and cannot itself be captured.
- **Aliases on repair:** the display alias is refreshed when it was the renamed file's own basename and preserved otherwise, so `[[Nv/Zz/Nova-Far|Nova-Far]]` becomes `[[Nova-Rename|Nova-Rename]]` while `[[Nv/Zz/Nova-Far|My Own Words]]` becomes `[[Nova-Rename|My Own Words]]`. A heading anchor is untouched in either case. This matters because the editor writes an alias equal to the basename whenever it emits a target that is not already bare, so a vault holds many links whose alias would otherwise go stale the first time the file is renamed.
- **One-way ratchet:** removing ambiguity (renaming a duplicate away, deleting one) never simplifies remaining qualified links back to bare.
- **No protection on delete:** deleting one duplicate leaves bare links repointing to the survivor, with nothing rewritten. The standard delete confirmation does name how many files point at what is being deleted, but it carries a *do not show again* option and stays off once taken, so a vault where it has been dismissed reports nothing at all. The count is not a mechanism anything can rely on.
- **No protection for external changes:** files created, moved, or deleted outside the app (file explorer, git sync, external tools) trigger no link maintenance whatsoever, whether Obsidian is open or closed. External tooling must self-protect; ambiguity arriving via sync lands silently.

## Coexistence

An external tool rewriting links while Obsidian is open is safe: Obsidian re-indexes the result cleanly and performs no competing edits.
