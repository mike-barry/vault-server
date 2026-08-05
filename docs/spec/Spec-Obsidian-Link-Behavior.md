# Obsidian Link Behavior

How Obsidian resolves and maintains `[[wikilinks]]` when multiple files share a basename, established empirically (2026-07-23, re-measured 2026-08-04, Obsidian with *New link format = shortest path when possible* and *Automatically update internal links = on*). This model is the behavioral reference the `move` tool mirrors.

## Resolution Rule

A target resolves **deterministically**, in two tiers.

**Exact path wins outright.** The target text is first tried as a path from the vault root. A file sitting at that path takes the link and no other candidate is considered. Targets are vault-relative, never relative to the linking file: `[[Q/Kappa]]` written in `W/Note.md` opens `Q/Kappa.md` and not `W/Q/Kappa.md`.

**Otherwise, by proximity.** Where the target matches no path, the candidates are every file the target *names*: its final component must match the file's basename whole, and the text before it must be a character suffix of the file's folder path. The suffix is measured in characters rather than components, so `sign/Spec-Tool` names `.../Redesign/Spec-Tool` - only the leading folder name can be cut that way, and the final component is never partial (`Tool` alone names nothing). A bare target names every file with that basename.

Those candidates are ranked by the length of the path prefix they share with the linking file's folder - longest shared prefix wins. Descending into the linking file's own subtree is free and ascending out of it is not, so a candidate one level below the linking file beats one a level above. Ties are broken by ordinal comparison of the candidates' **vault paths**.

A path-qualified link is therefore not safe from re-aiming: it reaches its file by a rule wider than that file's own path, so a file created at the path the target spells takes the link from it. Only a target spelling a file's whole path is immune, which is what a pin writes.

The two tiers together are what make a **vault-root file special**: a bare `[[Name]]` is the exact vault path of a root-level `Name.md`, so a root file holds every bare link to its basename unconditionally and cannot be displaced by a same-named file created anywhere below it. The same fact runs the other way - creating a file at the vault root captures every bare link to that basename across the whole vault at once, which is the widest re-aiming any single change can cause.

The autocomplete suggestion popup for a duplicated basename ranks its candidates by the same rule.

## Maintenance Rules

- **Emit on rename/move:** links to the renamed/moved file are re-emitted per the link-format setting - bare when the basename is unique, full vault path when it is not. Deliberately path-qualified links to a unique-basename file are collapsed to bare on move (no form preservation).
- **Protection:** an in-app action that creates or shifts ambiguity for a basename (creating a duplicate, renaming into a collision, moving a same-basename file closer) rewrites affected bare links to **full vault path** first, so no link's resolution ever silently changes. Protection extends to bystanders - links to the file the action didn't touch. It is offered as a prompt naming the count of affected links, with *Always update*, *Just once*, and *Do not update*; declining leaves the links bare and lets them re-aim. A pin is always safe to write, because the full vault path lands in the exact-path tier and cannot itself be captured.
- **One-way ratchet:** removing ambiguity (renaming a duplicate away, deleting one) never simplifies remaining qualified links back to bare.
- **No protection on delete:** deleting one duplicate leaves bare links silently repointing to the survivor - no warning, no rewrite.
- **No protection for external changes:** files created, moved, or deleted outside the app (file explorer, git sync, external tools) trigger no link maintenance whatsoever, whether Obsidian is open or closed. External tooling must self-protect; ambiguity arriving via sync lands silently.

## Coexistence

An external tool rewriting links while Obsidian is open is safe: Obsidian re-indexes the result cleanly and performs no competing edits.
