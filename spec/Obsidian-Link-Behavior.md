# Obsidian Link Behavior

How Obsidian resolves and maintains `[[wikilinks]]` when multiple files share a basename, established empirically (2026-07-23, Obsidian with *New link format = shortest path when possible* and *Automatically update internal links = on*). This model is the behavioral reference the vault server's `move` tool mirrors.

## Resolution Rule

A bare link (`[[Name]]`) with multiple same-basename candidates resolves **deterministically**: the candidate with the shortest **relative path from the linking file** wins (pure character count - `..` components carry no extra weight, so two short hops beat one long one), with ties broken by ordinal comparison of the candidates' **vault-absolute paths** (not the relative paths: `..` sorts before letters relatively, yet the subtree copy wins a dead tie against a parent-folder copy). Resolution is relative, not vault-global - a deeper-nested candidate inside the linking file's own subtree beats a globally-shorter path elsewhere. All twelve micro-tests confirmed this model; a subtree-preference formulation also fits the tie-break data but the vault-path comparison is the simpler rule consistent with every observation.

The autocomplete suggestion popup for a duplicated basename ranks its candidates by the same rule.

## Maintenance Rules

- **Emit on rename/move:** links to the renamed/moved file are re-emitted per the link-format setting - bare when the basename is unique, full vault path when it is not. Deliberately path-qualified links to a unique-basename file are collapsed to bare on move (no form preservation).
- **Protection:** an in-app action that creates or shifts ambiguity for a basename (creating a duplicate, renaming into a collision, moving a same-basename file closer) rewrites affected bare links to **full vault path** first, so no link's resolution ever silently changes. Protection extends to bystanders - links to the file the action didn't touch.
- **One-way ratchet:** removing ambiguity (renaming a duplicate away, deleting one) never simplifies remaining qualified links back to bare.
- **No protection on delete:** deleting one duplicate leaves bare links silently repointing to the survivor - no warning, no rewrite.
- **No protection for external changes:** files created, moved, or deleted outside the app (file explorer, git sync, external tools) trigger no link maintenance whatsoever, whether Obsidian is open or closed. External tooling must self-protect; ambiguity arriving via sync lands silently.

## Coexistence

An external tool rewriting links while Obsidian is open is safe: Obsidian re-indexes the result cleanly and performs no competing edits (verified with the vault server's `move` tool).
