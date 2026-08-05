# Harvest: The `move` tool and link repair on relocation

Sources: `C:\Repos\vault-server\tools\Tests\move.Tests.ps1` (1168 lines); `C:\Repos\vault-server\test-vault\**` (6 notes, read to describe fixture arrangements).
Read the implementation for: `tools/move.ps1` (lines 55-95 only) — to confirm the parameter surface (`source`, `dest`, `kind`, `vault`, `apply`), the declared `OpClass: content:write`, and the exact invalid-basename character set behind the two "invalid character" tests. Also grepped the whole repo to confirm no cross-vault parameter exists.

## Summary

`move` renames and/or relocates a single file or folder inside one vault, and repairs every wikilink and
embed in the vault that the relocation would have broken. It runs under the per-vault write lock as
plan -> validate -> apply, and `apply:false` returns the complete report after validate having mutated
nothing.

Four things a reconciler should know before reading the cases:

1. **The fixture.** `test-vault/` is six notes: `Test-Vault.md`, `Inbox/Note-Loose-Thought.md`,
   `Resources/Gizmo-Reference.md`, `Projects/Widget/Widget.md` (a folder note),
   `Projects/Widget/Tasks.md`, `Projects/Widget/Inbox/Plan-Widget-Assembly.md`. `Gizmo-Reference` is
   referenced **six times across three files** — bare, `#heading`, `#heading|alias`, and
   path-qualified — and `[[Widget]]` links exist as bystanders. The recurring `referencesRewritten
   | Should -Be 6` / `filesRewritten.Count | Should -Be 3` assertion is entirely a property of that
   arrangement (MOV-001, MOV-F01). Every test copies `test-vault` to a fresh temp `scratch` vault
   with a generated `vaults.json`; duplicate-basename fixtures (`A/Dupe.md`, `B/Dupe.md`, `Other/Widget.md`)
   are injected into the scratch copy per-Context, never committed.
2. **Link resolution is shortest-relative-path-wins, ties broken ordinally.** Nearly all of the
   protect-and-proceed cases (MOV-050..MOV-055) turn on comparing the *character length of the
   relative path* from the linking file to each candidate. The tests state those lengths inline;
   they are reproduced in each case.
3. **Protect-and-proceed replaced an older refuse model.** The suite explicitly asserts the absence
   of retired behavior: no ambiguity gate, no `-Force`, no `referencesSkippedAmbiguous`, no
   `basenameCollisionCreated`, no `folderMoveDeferred`, no `stub` field, no `.md` inference, no
   integer `filesSkippedUnreadable`. A reconciler should treat these negative assertions as harvested
   behavior in their own right.
4. **Cross-vault refusal has no test and no mechanism.** `move.ps1` takes one `-vault`; there is no
   source-vault/dest-vault pair, so a cross-vault move is structurally unexpressible rather than
   refused. See MOV-100.

The thinnest-looking areas: there is no test for a link that was *already broken* (pointing at a
nonexistent note) before the move, and none for a link that was *already ambiguous* in a way the move
does not touch — the closest is MOV-052 (a bystander whose winner the move leaves unaffected is left
alone). Frontmatter links are exercised only incidentally (`Widget.md`'s nav frontmatter carries
`"[[Widget/Tasks]]"`), never asserted as a distinct surface.

## Cases

### MOV-001 — A file rename returns a well-formed JSON object naming old and new path
- **Behavior:** A successful move returns a single JSON object carrying `oldPath` (the vault-relative source), `newPath` (the vault-relative destination), and `kind`.
- **Trigger:** `move Resources/Gizmo-Reference.md -> Renamed-Gizmo.md`, yielding `newPath` `Resources/Renamed-Gizmo.md` and `kind` `file`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:68` — `returns a well-formed Tier-2 JSON object and applies a bare-name rename`

### MOV-002 — The move actually lands on disk: source gone, destination present
- **Behavior:** After a successful move the source path no longer exists and the destination path does.
- **Trigger:** Any applied move.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:68` — `returns a well-formed Tier-2 JSON object and applies a bare-name rename`

### MOV-003 — The return reports how many references were rewritten and in which files
- **Behavior:** The return carries `referencesRewritten` (a count of rewritten link occurrences) and `filesRewritten` (the list of files whose content changed).
- **Trigger:** Renaming `Resources/Gizmo-Reference.md` rewrites 6 occurrences across 3 files: `Projects/Widget/Tasks.md` (bare + `#heading`), `Projects/Widget/Inbox/Plan-Widget-Assembly.md` (bare, `#heading|alias`, path-qualified), `Projects/Widget/Widget.md` (bare).
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:68` — `returns a well-formed Tier-2 JSON object and applies a bare-name rename`
- **Note:** The count 6 and file count 3 are properties of the fixture, not of the rule. See MOV-F01.

### MOV-004 — No `stub` field is emitted
- **Behavior:** The return never carries a `stub` property; `move` leaves no forwarding stub at the old location.
- **Trigger:** Any file move.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:81` — `returns a well-formed Tier-2 JSON object and applies a bare-name rename`

### MOV-005 — An explicit `kind` matching the resolved kind is accepted
- **Behavior:** Supplying `kind` as an assertion about what the source is succeeds when it agrees with what the source actually is.
- **Trigger:** `-Kind file` on a file source.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:88` — `accepts the optional Kind parameter without erroring`

### MOV-006 — No `Force` parameter exists
- **Behavior:** There is no override parameter for the (retired) ambiguity gate; passing one fails at argument binding rather than being silently ignored.
- **Trigger:** `-Force` on any move.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:93` — `rejects the retired Force parameter as a binding error (no ambiguity gate to override)`
- **Note:** Marked artifact because "PowerShell parameter binding error" is implementation-shaped, but the underlying semantic — an unknown argument is rejected, not ignored — is portable.

### MOV-007 — `referencesQualifiedProtectively` is always present, null when nothing was pinned
- **Behavior:** The field naming protectively-pinned bystander links is present on every return; it is null/empty when no pinning occurred.
- **Trigger:** A move with no threatened bystanders.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:100` — `reports referencesQualifiedProtectively as null when no pinning occurred`

### MOV-008 — The retired ambiguity fields are gone
- **Behavior:** The return carries neither `referencesSkippedAmbiguous` nor `basenameCollisionCreated`.
- **Trigger:** Any move.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:107` — `reports referencesQualifiedProtectively as null when no pinning occurred`

### MOV-009 — A directory source resolves to `kind: folder`
- **Behavior:** The tool determines the kind from the source itself; a directory yields `folder`.
- **Trigger:** `move Projects/Widget -> Projects/Renamed-Widget`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:111` — `resolves kind as folder for a directory source`

### MOV-010 — A whitespace-only dest is `invalid_argument`
- **Behavior:** An empty or whitespace-only destination is refused.
- **Trigger:** `Dest = '   '`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:121` — `rejects an empty dest as invalid_argument`

### MOV-011 — `kind` accepts only `file` and `folder`; anything else is `invalid_argument`
- **Behavior:** An unrecognized `kind` value is refused with the structured `invalid_argument` code, not a binding error — the vocabulary is validated in the tool, not by the parameter type.
- **Trigger:** Three distinct rejected values, each its own `It`: `project`, `area`, `note`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:126,131,136` — `rejects kind "project"...`, `rejects kind "area"...`, `rejects an unrecognized kind value...`
- **Note:** `project` and `area` are called out separately because they were plausibly a former vocabulary; `note` because it is the obvious synonym for `file`.

### MOV-012 — A dest literally equal to the source is `invalid_argument`
- **Behavior:** A move to where the thing already is, spelled identically, is refused.
- **Trigger:** `Source = Dest = 'Resources/Gizmo-Reference.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:141` — `rejects dest equal to source (literal match) as invalid_argument`
- **Note:** "Literal match" — a case-differing dest is a legal rename (MOV-023).

### MOV-013 — A folder cannot be moved inside itself
- **Behavior:** A destination nested under the source folder is refused as `invalid_argument`.
- **Trigger:** `Source = 'Projects/Widget'`, `Dest = 'Projects/Widget/Nested'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:146` — `rejects a dest inside the source folder for a folder move as invalid_argument`

### MOV-014 — A dest basename containing a link-breaking character is refused
- **Behavior:** The destination basename may not contain characters that would break a wikilink to it.
- **Trigger:** `Dest = 'Bad[Name]'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:151` — `rejects a dest basename with a link-breaking character as invalid_argument`
- **Note:** Implementation (`move.ps1:86`) gives the full forbidden set as `[ ] # ^ |` plus the Windows-invalid set `< > : " / \ ? *` and control characters, plus a trailing dot or space. Checked against the **basename only**, after dest construction — the full path legitimately contains `/`.

### MOV-015 — A dest basename containing an OS-invalid character is refused
- **Behavior:** The destination basename may not contain a character the filesystem forbids.
- **Trigger:** `Dest = 'Bad:Name'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:156` — `rejects a dest basename with an OS-invalid character as invalid_argument`

### MOV-016 — A naming-convention violation is a warning, not a failure
- **Behavior:** A destination name that breaks the vault's naming convention still moves, and the return carries a non-empty `namingWarnings`.
- **Trigger:** `Dest = 'lowercase_name.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:161` — `reports a naming-convention violation as a warning, not a failure`

### MOV-017 — A conformant dest raises no naming warning
- **Behavior:** `namingWarnings` is empty for a Title-Case-hyphenated destination.
- **Trigger:** `Dest = 'Renamed-Gizmo.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:167` — `raises no naming warning for a convention-conformant dest`

### MOV-018 — An unconfigured vault fails `unknown_vault`, naming the vault
- **Behavior:** A `vault` argument naming a vault absent from configuration fails with `unknown_vault` and `data.vault` echoing the requested name.
- **Trigger:** `Vault = 'nope'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:178` — `refuses an unconfigured vault with unknown_vault`
- **Note:** The Context comment states both vault failures must carry a *structured code* rather than the module's plain-text error, and must fire **ahead of the write lock**, whose mutex name is derived from the resolved vault root.

### MOV-019 — A configured vault whose root does not exist fails `vault_unavailable`
- **Behavior:** A vault present in configuration but whose root directory is missing fails with `vault_unavailable` and `data.reason` matching `does not exist`.
- **Trigger:** A `vaults.json` entry `gone` pointing at a nonexistent path; `Vault = 'gone'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:184` — `refuses a configured vault whose root is missing with vault_unavailable`

### MOV-020 — A missing source fails `not_found`, echoing the source
- **Behavior:** A source that resolves to nothing fails `not_found` with `data.source` carrying the requested path.
- **Trigger:** `Source = 'Resources/Does-Not-Exist.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:197` — `fails not_found for a missing source`

### MOV-021 — A bare-name dest is a rename in place, keeping the source's parent
- **Behavior:** A destination containing no `/` renames within the source's current folder.
- **Trigger:** `Source = 'Projects/Widget/Inbox/Plan-Widget-Assembly.md'`, `Dest = 'Renamed-Plan.md'` -> `Projects/Widget/Inbox/Renamed-Plan.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:203` — `renames in place for a bare-name dest, keeping the current parent`

### MOV-022 — A dest containing `/` is a full vault-relative destination
- **Behavior:** A destination with any `/` is interpreted from the vault root, not relative to the source's parent.
- **Trigger:** `Dest = 'Archive/Old-Gizmo.md'` -> `newPath` `Archive/Old-Gizmo.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:209` — `treats a dest containing "/" as a full vault-relative destination`

### MOV-023 — A leading `/` on the dest normalizes to the vault root
- **Behavior:** `/Root-Gizmo.md` is the vault root, and the returned `newPath` is the normalized form without the leading slash.
- **Trigger:** `Dest = '/Root-Gizmo.md'` -> `newPath` `Root-Gizmo.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:215` — `normalizes a leading "/" to the vault root`

### MOV-024 — An explicit `.md` dest is taken verbatim, with no doubled extension
- **Behavior:** A destination already carrying `.md` does not gain a second one.
- **Trigger:** `Dest = 'Renamed-Gizmo.md'` -> `Resources/Renamed-Gizmo.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:221` — `takes an explicit .md dest verbatim (no double extension)`

### MOV-025 — An occupied destination fails `already_exists`, echoing the dest
- **Behavior:** Moving onto an existing file fails `already_exists` with `data.dest` naming the occupied path. No overwrite, no override.
- **Trigger:** `Dest = 'Projects/Widget/Tasks.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:227` — `fails already_exists when the destination is occupied`

### MOV-026 — A case-only rename is allowed and is not `already_exists`
- **Behavior:** On a case-insensitive filesystem, renaming a file to a case variant of its own name is permitted rather than colliding with itself, and reports `pureMove: false`.
- **Trigger:** `Resources/Gizmo-Reference.md -> gizmo-reference.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:233` — `allows a case-only rename without already_exists, and reports pureMove: false`

### MOV-027 — `pureMove: true` when the basename is unchanged
- **Behavior:** A relocation that keeps the basename reports `pureMove: true`; any basename change (including case) reports false.
- **Trigger:** `Resources/Gizmo-Reference.md -> Archive/Gizmo-Reference.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:241` — `reports pureMove: true when the basename is unchanged`
- **Note:** `pureMove` governs which links need repair — see MOV-046.

### MOV-028 — An asserted `kind` that contradicts the source fails `kind_mismatch`
- **Behavior:** When `kind` disagrees with what the source actually is, the move fails `kind_mismatch` with `data.resolvedKind` and `data.assertedKind`. Asserted in both directions.
- **Trigger:** `-Kind folder` on `Resources/Gizmo-Reference.md` (resolvedKind `file`, assertedKind `folder`); `-Kind file` on `Projects/Widget` (resolvedKind `folder`).
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:247,254` — `fails kind_mismatch when Kind asserts folder for a file source`, `...asserts file for a folder source`

### MOV-029 — The dest must carry the resolved source's extension
- **Behavior:** `move` neither guesses an extension nor converts one. A destination whose extension differs from the source's is refused `invalid_argument`, and the error names the value that would work so the caller retries in one call: `data.suggestedDest`, `data.sourceExtension`, `data.destExtension`, and the suggestion echoed in `message`.
- **Trigger:** `Resources/Gizmo-Reference.md -> Renamed-Gizmo` (no extension): `suggestedDest` `Renamed-Gizmo.md`, `sourceExtension` `.md`, `destExtension` empty.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:266` — `refuses an extensionless dest for a .md source, naming the correct form`
- **Note:** This replaced an earlier `.md`-inference behavior, which the suite explicitly calls retired. Refused during validate — the source is still in place afterward.

### MOV-030 — The suggested dest keeps the caller's spelling shape (bare vs full path)
- **Behavior:** When the caller gave the dest as a full path, `suggestedDest` is a full path; when bare, it is bare.
- **Trigger:** `Dest = 'Archive/Old-Gizmo'` -> `suggestedDest` `Archive/Old-Gizmo.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:278` — `names the correct form as a full path when dest was given as a full path`

### MOV-031 — Both-absent satisfies extension preservation
- **Behavior:** An extensionless source may move to an extensionless dest; `move` is not note-only and must handle files with no extension.
- **Trigger:** Fixture: create `LICENSE` at the vault root; `move LICENSE -> COPYING`. Succeeds, `LICENSE` gone, `COPYING` present.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:284` — `allows an extensionless dest for an extensionless source (both-absent)`

### MOV-032 — Adding an extension to an extensionless source is refused, suggesting the bare form
- **Behavior:** `LICENSE -> COPYING.md` is refused; `suggestedDest` is `COPYING` with no trailing dot — the suggestion strips the period along with the extension.
- **Trigger:** Fixture: `LICENSE` at the vault root.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:295` — `refuses adding an extension to an extensionless source, naming the bare form`

### MOV-033 — A folder source has no extension to preserve, so an extensionless dest is fine
- **Behavior:** The extension rule does not apply to folders.
- **Trigger:** `Projects/Widget -> Projects/Widget-Renamed`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:304` — `allows an extensionless dest for a folder source (a folder has no extension to preserve)`

### MOV-034 — The extension rule is general, not note-specific: `diagram.png -> photo` is refused
- **Behavior:** Dropping the extension from a non-note file is refused with `sourceExtension` `.png` and `suggestedDest` `photo.png`; nothing is created and nothing moves.
- **Trigger:** Fixture: create `Resources/diagram.png`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:311` — `refuses diagram.png -> photo, the case a note-only rule would have missed`
- **Note:** The test comment records that this silently succeeded under the previous contract, producing an extensionless file.

### MOV-035 — `diagram.png -> photo.png` is allowed
- **Behavior:** A non-note file renames freely when the extension is preserved.
- **Trigger:** Fixture: `Resources/diagram.png` -> `photo.png` = `Resources/photo.png`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:323` — `allows diagram.png -> photo.png (the extension is preserved)`

### MOV-036 — A deliberate conversion is refused with no override available
- **Behavior:** `notes.txt -> notes.md` fails `invalid_argument` with `sourceExtension` `.txt`, `destExtension` `.md`, and a message stating there is no override.
- **Trigger:** Fixture: create `Inbox/notes.txt`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:331` — `refuses a deliberate conversion (notes.txt -> notes.md) with no override available`

### MOV-037 — No override parameter exists for the extension rule
- **Behavior:** There is no `-Force`, `-AllowExtensionChange`, or equivalent; every such spelling fails at argument binding.
- **Trigger:** `-AllowExtensionChange` on any move.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:341` — `exposes no override parameter for the extension rule`

### MOV-038 — An extension case difference is a spelling, not a conversion
- **Behavior:** `.md -> .MD` passes the extension rule (same file type on a case-insensitive filesystem) and the caller's chosen spelling is honored verbatim in `newPath`.
- **Trigger:** `Resources/Gizmo-Reference.md -> Renamed-Gizmo.MD` -> `Resources/Renamed-Gizmo.MD`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:348` — `treats an extension case difference as a spelling, not a conversion`

### MOV-039 — A move succeeds when both source and dest are inside the grant's scope roots
- **Behavior:** Scope membership is checked against both paths; both inside passes.
- **Trigger:** Scope roots `@('Resources')`; move within `Resources/`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:358` — `passes when both source and dest are in scope`

### MOV-040 — An out-of-scope source fails `out_of_scope`, naming the source in `data.paths`
- **Behavior:** A source outside the grant's scope roots is refused and the offending path is listed.
- **Trigger:** Scope roots `@('Projects')`; source `Resources/Gizmo-Reference.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:364` — `fails out_of_scope naming the source when the source falls outside scope roots`

### MOV-041 — An out-of-scope dest fails `out_of_scope`, naming the dest in `data.paths`
- **Behavior:** A destination outside the grant's scope roots is refused even when the source is inside — a move may not carry a file out of scope.
- **Trigger:** Scope roots `@('Resources')`; dest `Projects/Escaped-Gizmo.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:371` — `fails out_of_scope naming the dest when the dest falls outside scope roots`

### MOV-042 — An empty scope-roots set for the vault means unscoped, not deny-all
- **Behavior:** A grant carrying an empty scope-roots list for the vault permits the whole vault.
- **Trigger:** Scope roots `@()`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:378` — `passes unscoped when the grant has an empty scope-roots set for the vault`

### MOV-043 — Link repair is not scope-limited
- **Behavior:** Scope constrains where the *move* may act, not where the *rewrite* may reach. Links living outside the grant's scope roots are still repaired.
- **Trigger:** Scope roots `@('Resources')`; source and dest both in `Resources/`; all 6 references are still rewritten, including those in `Projects/` and `Inbox/`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:384` — `rewrites references outside the grant scope (the rewrite is not scope-limited)`
- **Note:** One of the higher-value cases: the rule is that a scoped grant cannot leave a vault in a broken-link state by half-repairing it.

### MOV-044 — Rewriting a link replaces only its target, preserving `#heading` and `|alias`
- **Behavior:** A link's heading anchor and display alias survive the rewrite untouched; only the target portion changes. `[[Gizmo-Reference#Torque Specs|the torque table]]` becomes `[[Renamed-Gizmo#Torque Specs|the torque table]]`.
- **Trigger:** Renaming `Resources/Gizmo-Reference.md`; inspect `Projects/Widget/Inbox/Plan-Widget-Assembly.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:396` — `preserves the embed marker, #heading, and |alias, rewriting only the target`
- **Note:** The alias is preserved verbatim even when it does not resemble the old name — it is display text and is never re-derived from the new basename.

### MOV-045 — A bystander link to an unrelated note is never touched
- **Behavior:** Links whose target is not the moved file are left exactly as written. `[[Widget]]` survives a `Gizmo-Reference` rename.
- **Trigger:** Any file move; the fixture's `[[Widget]]` links.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:403` — `preserves the embed marker, #heading, and |alias, rewriting only the target`

### MOV-046 — The embed marker `!` is preserved on an embed of the moved file
- **Behavior:** `![[...]]` embeds of the moved file are repaired like wikilinks, with the `!` marker intact.
- **Trigger:** Fixture: write `Inbox/Ref-Embed.md` containing `Bare ![[Gizmo-Reference]] and qualified ![[Resources/Gizmo-Reference]].` then rename `Resources/Gizmo-Reference.md -> Renamed-Gizmo.md`. Both embeds become `![[Renamed-Gizmo]]` — two occurrences — and no `Gizmo-Reference` text survives.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:406` — `preserves the embed marker on an ![[...]] embed of the moved file`

### MOV-047 — A path-qualified link canonicalizes to the bare new name when that name is unique
- **Behavior:** A repaired link is spelled bare whenever the new basename is unique vault-wide, even if the link was originally path-qualified. `[[Resources/Gizmo-Reference]]` becomes `[[Renamed-Gizmo]]`, not `[[Resources/Renamed-Gizmo]]`.
- **Trigger:** File rename with a vault-unique new basename.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:419` — `canonicalizes a path-qualified link to the bare new name when it is unique`
- **Note:** This is the spelling rule for a repaired link, and it is *lossy* with respect to the caller's original spelling. It pairs with MOV-051, where a non-unique new basename forces the full-path spelling.

### MOV-048 — A pure relocation repairs path-qualified links and leaves bare links alone
- **Behavior:** When the basename is unchanged, bare links remain textually valid and are not rewritten; only path-qualified links, which now name a wrong path, are repaired.
- **Trigger:** `Resources/Gizmo-Reference.md -> Archive/Gizmo-Reference.md`. `referencesRewritten` is 1 (the single `[[Resources/Gizmo-Reference]]`); every bare `[[Gizmo-Reference]]` is untouched.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:426` — `pureMove repairs path-qualified links but leaves bare links unchanged`
- **Note:** The minimality rule: a link that still resolves correctly is not rewritten just because it could be.

### MOV-049 — A case-only rename changes the name on disk and rewrites bare links to the new case
- **Behavior:** A case-only rename is applied on the filesystem as a genuine case change (not a no-op), and all 6 references are rewritten to the new casing.
- **Trigger:** `Resources/Gizmo-Reference.md -> gizmo-reference.md`. The test asserts case-sensitively (`-ccontains`) that `gizmo-reference.md` exists on disk and `Gizmo-Reference.md` does not.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:439` — `applies a case-only rename on disk and rewrites bare links to the new case`
- **Note:** Bare links *are* rewritten here even though they would still resolve case-insensitively — so the rule in MOV-048 is keyed on `pureMove` (basename identity including case), not on whether the link would still resolve.

### MOV-050 — A file enumerated but unreadable is skipped and named in `filesSkippedUnreadable`
- **Behavior:** A file that satisfies the note enumeration but cannot be read is skipped rather than failing the move, and the envelope names it by path. The rest of the rewrite proceeds normally.
- **Trigger:** Fixture: create a dangling symlink `Inbox/Dangling.md` pointing at a nonexistent target, then rename `Gizmo-Reference`. `filesSkippedUnreadable` contains `Inbox/Dangling.md`; `referencesRewritten` is still 6.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:452` — `skips a note that vanished between enumeration and read (unreadable-skip)`
- **Note:** Windows-only, and skipped (not failed) when symlink creation needs Developer Mode or elevation. The report is *paths, not a count* — see MOV-085.

### MOV-051 — Renaming a non-unique file proceeds, and links that resolved to it follow it
- **Behavior:** There is no ambiguity gate. Moving a file whose basename is shared elsewhere succeeds, and every link that resolved to *that* file before the move is rewritten to keep resolving to it after.
- **Trigger:** Fixture arrangement: `A/Dupe.md` and `B/Dupe.md` both exist; `Inbox/Refs.md` contains `See [[Dupe]] and [[A/Dupe]].`. From `Inbox/`, bare `[[Dupe]]` resolves to `A/Dupe` (equal-length tie with `B/Dupe`, `A` wins ordinally) and `[[A/Dupe]]` is definitive — both point at the moved file. `move A/Dupe.md -> A/Renamed-Dupe.md` rewrites both to `[[Renamed-Dupe]]` (2 occurrences, bare because the new basename is vault-unique), `referencesRewritten` 2, nothing pinned.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:482` — `renames a non-unique file and proceeds without failing (no gate), following links that resolved to it`
- **Note:** The old model refused this outright. Ordinal tie-breaking on equal-length relative paths is load-bearing here.

### MOV-052 — Renaming into an occupied basename emits full-path links for the moved file
- **Behavior:** When the *new* basename is already taken by another file, the moved file's own inbound links are rewritten to the full vault path rather than the bare name.
- **Trigger:** Fixture: `A/Dupe.md`, `B/Dupe.md` exist. `move Resources/Gizmo-Reference.md -> Resources/Dupe.md`. `Projects/Widget/Tasks.md` now carries `[[Resources/Dupe]]`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:501` — `T5: renaming into an occupied basename emits the moved file's links full-path and pins threatened bystanders`

### MOV-053 — A bystander link the move would silently re-aim is pinned to its pre-move target
- **Behavior:** If a move would change which file an *unrelated* bare link resolves to, that link is qualified to its original target rather than allowed to drift. It is never re-aimed at the moved file. The pinning is reported in `referencesQualifiedProtectively` with a `count` and a `files` list.
- **Trigger:** Same T5 arrangement plus `Resources/Note.md` containing `Link [[Dupe]] here.`. Pre-move that bare link resolves to `A/Dupe` (`../A/Dupe.md`, 12 chars); post-move the moved file sits at `Resources/Dupe.md` (`Dupe.md`, 7) and would win. So the link is rewritten to `[[A/Dupe]]`; `referencesQualifiedProtectively.count` is 1 and `.files` contains `Resources/Note.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:501` — `T5: renaming into an occupied basename emits the moved file's links full-path and pins threatened bystanders`
- **Note:** The core of protect-and-proceed and the single most intricate rule in the area. Note the asymmetry with MOV-051: a link that *pointed at* the moved file follows it; a link that *pointed elsewhere* is pinned in place.

### MOV-054 — A bystander whose winner the move leaves unaffected is not pinned
- **Behavior:** Pinning fires only when the move actually flips the resolution. Adding a farther candidate that does not win leaves the link untouched and `referencesQualifiedProtectively` empty.
- **Trigger:** Same T5 move (`Resources/Gizmo-Reference.md -> Resources/Dupe.md`) but the bystander is `Inbox/Refs.md` containing `See [[Dupe]].`. From `Inbox/`, `A/Dupe` is `../A/Dupe.md` (12, ties with `../B/Dupe.md`, `A` wins) and the new candidate is `../Resources/Dupe.md` (20) — it does not win. The link stays `[[Dupe]]`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:518` — `does not pin a bystander whose winner the move leaves unaffected`
- **Note:** The minimality counterpart to MOV-053. A reconciler should treat MOV-053/MOV-054 as a matched pair.

### MOV-055 — Relocating a same-basename file *closer* also flips a bystander's winner and pins it
- **Behavior:** Pinning is triggered by relocation as well as rename — a `pureMove` that lands the moved file nearer a linking file can steal a bare link, and the link is pinned to its original target.
- **Trigger:** Fixture: `A/Dupe.md` and `B/Dupe.md` exist; create `A/B/` and `Far/`, write `Far/Dupe.md` and `A/B/Link.md` containing `Points [[Dupe]].`. From `A/B/`, `[[Dupe]]` resolves to `A/Dupe.md` (`../Dupe.md`, 10) over `Far/Dupe.md`. `move Far/Dupe.md -> A/B/Dupe.md` makes the moved file the winner (`Dupe.md`, 7), so the link is pinned to `[[A/Dupe]]`. `pureMove` true, `referencesQualifiedProtectively.count` 1, `.files` contains `A/B/Link.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:529` — `T8: relocating a same-basename file closer flips a bystander's winner, pinning it to its original target`

### MOV-056 — A pure relocation *away* makes a link that resolved to the moved file follow it full-path
- **Behavior:** When a `pureMove` relocates the file so a bare link would now resolve to a *different* file with the same basename, the link follows the moved file — spelled as the full new path because the basename is non-unique. It is followed, not pinned.
- **Trigger:** Fixture: `A/Dupe.md`, `B/Dupe.md` exist; create `A/B/` and `Far/Deep/`, write `A/B/Dupe.md` and `A/B/Link.md` containing `Points [[Dupe]].` — the link resolves to `A/B/Dupe.md` pre-move. `move A/B/Dupe.md -> Far/Deep/Dupe.md` flips the natural winner to `A/Dupe.md`, so the link is rewritten to `[[Far/Deep/Dupe]]`. `pureMove` true, `referencesQualifiedProtectively` empty.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:545` — `pureMove with a duplicate basename follows the moved file when the link resolved to it`
- **Note:** Together with MOV-048, this bounds the "bare links are untouched on a pureMove" rule: they are untouched only while they still resolve to the moved file.

### MOV-057 — File-renaming a folder note away from its folder name warns `folderNoteOrphaned`
- **Behavior:** The `Foo/Foo.md` folder-note invariant is reported when a file move breaks it. The move still proceeds.
- **Trigger:** `move Projects/Widget/Widget.md -> Renamed-Widget.md` (stays inside `Projects/Widget/`). `kind` `file`, `folderNoteOrphaned` true, `Projects/Widget/Renamed-Widget.md` exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:563` — `warns folderNoteOrphaned when a folder note is file-renamed away from its folder name`

### MOV-058 — File-moving a folder note out of its folder also warns `folderNoteOrphaned`
- **Behavior:** The invariant is reported for the moved-away variant too — basename unchanged, folder left. A pure relocation still breaks `Foo/Foo.md`.
- **Trigger:** `move Projects/Widget/Widget.md -> Inbox/Widget.md`. `kind` `file`, `pureMove` true, `folderNoteOrphaned` true; `Inbox/Widget.md` exists and `Projects/Widget/Widget.md` does not.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:571` — `warns folderNoteOrphaned when a folder note is file-moved to another folder, basename unchanged`

### MOV-059 — A folder rename carries the folder note with it, renaming `Foo/Foo.md` to `Bar/Bar.md`
- **Behavior:** A folder move renames the folder note to match the new folder name, and reports it as `folderNoteRenamed` with `oldPath` and `newPath`.
- **Trigger:** `move Projects/Widget -> Projects/Renamed-Widget`. `folderNoteRenamed.oldPath` `Projects/Widget/Widget.md`, `.newPath` `Projects/Renamed-Widget/Renamed-Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:586` — `renames the folder, fires the folder-note invariant, and reports the full shape`

### MOV-060 — A folder move reports every contained file at its post-move path
- **Behavior:** `containedFilesMoved` lists the contained files, recursively, spelled at their new locations.
- **Trigger:** Same folder rename. Contains `Projects/Renamed-Widget/Renamed-Widget.md`, `.../Tasks.md`, `.../Inbox/Plan-Widget-Assembly.md`; all present on disk and the old directory gone.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:600` — `renames the folder, fires the folder-note invariant, and reports the full shape`

### MOV-061 — No `folderMoveDeferred` remnant field
- **Behavior:** A folder move is executed, not deferred; the return carries no `folderMoveDeferred` property.
- **Trigger:** Any folder move.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:595` — `renames the folder, fires the folder-note invariant, and reports the full shape`

### MOV-062 — A bare link to the folder note is rewritten vault-wide on a folder rename
- **Behavior:** Because the folder note's basename tracks the folder's, `[[Widget]]` becomes `[[Renamed-Widget]]` everywhere it resolved to the folder note.
- **Trigger:** `move Projects/Widget -> Projects/Renamed-Widget`; `Resources/Gizmo-Reference.md` afterwards matches `[[Renamed-Widget]]` and no longer `[[Widget]]`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:611` — `rewrites the folder note bare link across the vault`

### MOV-063 — A path-qualified link to a contained file is repaired by prefix substitution, keeping its depth
- **Behavior:** The old folder path prefix is swapped for the new one; the contained tail is preserved; the link keeps its original number of leading components.
- **Trigger:** Fixture: write `Inbox/Ref-Contained.md` with `See [[Widget/Tasks]] and [[Projects/Widget/Tasks]].`; rename `Projects/Widget -> Projects/Renamed-Widget`. Results: `[[Renamed-Widget/Tasks]]` and `[[Projects/Renamed-Widget/Tasks]]`; neither old form survives.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:618` — `repairs a path-qualified link to a contained file by prefix substitution`

### MOV-064 — A qualified link to the folder note itself changes *both* components
- **Behavior:** For the folder note, the folder component and the leaf both change, since the note's basename tracks the folder's.
- **Trigger:** Fixture: write `Inbox/Ref-Note.md` with `Project [[Projects/Widget/Widget]] and [[Widget/Widget]].`; rename to `Projects/Renamed-Widget`. Results: `[[Projects/Renamed-Widget/Renamed-Widget]]` and `[[Renamed-Widget/Renamed-Widget]]`; no `Widget/Widget` survives.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:631` — `changes both components of a qualified link to the folder note itself`

### MOV-065 — A bare link to a contained file is left untouched on a folder move
- **Behavior:** Contained basenames do not change on a folder move, so bare links to contained files still resolve and are not rewritten.
- **Trigger:** Fixture: write `Inbox/Ref-Bare-Contained.md` with `See [[Tasks]] and [[Plan-Widget-Assembly]].`; rename the folder. Both survive verbatim.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:641` — `leaves a bare link to a contained file untouched (contained basenames do not change)`

### MOV-066 — A contained note's own outbound link is repaired and written at the note's new location
- **Behavior:** Files inside the moved folder are themselves rewrite targets, and the rewrite lands in the new location. `filesRewritten` names them at their post-move paths.
- **Trigger:** Fixture: write `Projects/Widget/Sibling.md` with `Points at [[Projects/Widget/Tasks]].`; rename the folder. `filesRewritten` contains `Projects/Renamed-Widget/Sibling.md`; the old path is gone and the new file carries `[[Projects/Renamed-Widget/Tasks]]`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:649` — `remaps a contained note's own sibling qualified link and writes it to the new location`

### MOV-067 — A pure folder relocation reports `folderNoteRenamed` null but still runs prefix repair
- **Behavior:** When the folder basename is unchanged, no folder note is renamed, but full-path qualified links to contained files still break and are repaired. Shorter suffixes that still resolve are kept.
- **Trigger:** Fixture: write `Inbox/Ref-Reloc.md` with `Full [[Projects/Widget/Tasks]] and short [[Widget/Tasks]].`; `move Projects/Widget -> Archive/Widget`. `pureMove` true, `folderNoteRenamed` empty; result carries `[[Archive/Widget/Tasks]]` and the unchanged `[[Widget/Tasks]]`; `Archive/Widget/Widget.md` exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:661` — `pure relocation keeps the basename: folderNoteRenamed null, prefix repair still runs`

### MOV-068 — A missing folder note is a warn-and-proceed, not a failure
- **Behavior:** A folder with no `Foo/Foo.md` still moves; `folderNoteRenamed` is null and contained files are carried and reported.
- **Trigger:** Delete `Projects/Widget/Widget.md`, then rename the folder. `Projects/Renamed-Widget/Tasks.md` exists and is listed in `containedFilesMoved`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:676` — `warns and proceeds when the folder note is absent (folderNoteRenamed null)`

### MOV-069 — A pre-existing destination folder is `already_exists` — a merge is not a move
- **Behavior:** Moving a folder onto an existing directory is refused rather than merged, and nothing is mutated.
- **Trigger:** Create `Projects/Renamed-Widget` as an empty directory, then `move Projects/Widget -> Projects/Renamed-Widget`. Error `already_exists`; `Projects/Widget/Widget.md` still present.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:686` — `refuses a pre-existing destination folder as already_exists (a merge, not a move)`

### MOV-070 — A depth-changing folder move swaps in the *same-length* suffix of the new path, per link
- **Behavior:** A qualified link's leading folder suffix is replaced with the suffix of the new folder path having the same number of components — not with the whole new path. Each link keeps its own original depth.
- **Trigger:** Fixture: write `Inbox/Ref-Depth.md` with `Short [[Widget/Tasks]] and full [[Projects/Widget/Tasks]].`; `move Projects/Widget -> Archive/2026/Widget` (2 components -> 3). The 1-component form takes the new path's last 1 component (`Widget`, unchanged) and stays `[[Widget/Tasks]]`; the 2-component form takes the last 2 (`2026/Widget`) and becomes `[[2026/Widget/Tasks]]`. `[[Projects/Widget/Tasks]]` must not survive. `pureMove` true.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:694` — `emits the same-length new-path suffix when the folder move changes component depth`
- **Note:** The most counterintuitive rule in the folder-move set: the rewrite is depth-preserving per link rather than canonicalizing to a full path. A reconstruction is very likely to get this wrong by emitting `[[Archive/2026/Widget/Tasks]]` for both.

### MOV-071 — Destination parent folders are created as needed
- **Behavior:** A destination whose parent directories do not exist has them autocreated.
- **Trigger:** `move Projects/Widget -> Archive/2026/Widget` where `Archive/2026` does not exist; `Archive/2026/Widget/Widget.md` exists afterward.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:709` — `emits the same-length new-path suffix when the folder move changes component depth`
- **Note:** Also implied by the file-move cases targeting `Archive/` (MOV-022, MOV-027), which the fixture does not contain.

### MOV-072 — An embed of a contained non-`.md` file is repointed on a folder move
- **Behavior:** The rewrite grammar covers embeds of any contained file, not only note-to-note links; the same prefix substitution applies, and the file itself is carried into the moved folder.
- **Trigger:** Fixture: write `Projects/Widget/diagram.png`; write `Inbox/Ref-Embed-Png.md` with `See ![[Widget/diagram.png]] and ![[Projects/Widget/diagram.png]].`; rename the folder. Results: `![[Renamed-Widget/diagram.png]]` and `![[Projects/Renamed-Widget/diagram.png]]`; neither old form survives; `Projects/Renamed-Widget/diagram.png` exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:713` — `repoints an embed of a contained non-.md file (![[Widget/diagram.png]]) on a folder move`
- **Note:** The test comment records that the contained-file census is not `.md`-only, which is what makes the png enumerable and its embed key matchable.

### MOV-073 — A case-only folder rename works, bouncing the folder note through the case change
- **Behavior:** A folder can be renamed to a case variant of itself, and the folder note follows the case change.
- **Trigger:** `move Projects/Widget -> widget` (bare dest, so in-place). `newPath` `Projects/widget`, `pureMove` false, `folderNoteRenamed.newPath` `Projects/widget/widget.md`. Case-sensitively, `widget` exists on disk and `Widget` does not.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:730` — `applies a case-only folder rename, bouncing the folder note through the case change`

### MOV-074 — A folder move proceeds when the folder-note basename is non-unique; definitive qualified links follow
- **Behavior:** A non-unique folder-note basename does not gate the folder move. Path-qualified links, being definitive, follow the folder note to its renamed two-component path.
- **Trigger:** Fixture: write `Other/Widget.md` (making `Widget` non-unique); write `Inbox/Ref-Q.md` with `Bare [[Widget]] and qualified [[Projects/Widget/Widget]].`; rename `Projects/Widget -> Projects/Renamed-Widget`. Result carries `[[Projects/Renamed-Widget/Renamed-Widget]]` and the move lands.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:750` — `proceeds when the folder-note basename is non-unique, rewriting definitive qualified links`
- **Note:** The old model refused this as `ambiguous_references`.

### MOV-075 — A bare folder-note link that resolved to the folder note follows it across the rename
- **Behavior:** A bare link whose resolution was the folder note is rewritten rather than left dangling; because the basename is non-unique post-move, the emit is the full path.
- **Trigger:** Fixture: `Other/Widget.md` exists; write `Projects/Widget/Inbox/Ref-Inside.md` with `Parent [[Widget]].` — from inside the folder the folder note is the clear winner. Rename the folder; the file (now at `Projects/Renamed-Widget/Inbox/Ref-Inside.md`) matches `Renamed-Widget` and no longer `[[Widget]]`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:763` — `follows a bare folder-note link that resolved to the folder note across the rename`
- **Note:** The test's own comment works through and *corrects itself* on the resolution from `Resources/Gizmo-Reference.md` — `../Other/Widget` (15) beats `../Projects/Widget/Widget` (30), so that file's `[[Widget]]` resolves to `Other`, which is why the linking file was placed inside the folder instead. Useful evidence that the resolution rule is genuinely shortest-relative-path.

### MOV-076 — A folder move whose new folder-note basename already exists elsewhere proceeds, emitting full-path
- **Behavior:** A basename collision created by the folder-note rename does not gate the move; the folder note's inbound links emit the full vault path.
- **Trigger:** `move Projects/Widget -> Projects/Gizmo-Reference` while `Resources/Gizmo-Reference.md` exists. `folderNoteRenamed.newPath` `Projects/Gizmo-Reference/Gizmo-Reference.md`, and that file exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:779` — `proceeds when the new folder-note basename already exists elsewhere, emitting full-path`
- **Note:** The old model refused this as `basename_collision`.

### MOV-077 — A read-only rewrite target fails `readonly_targets` pre-flight, mutating nothing
- **Behavior:** Before applying anything, every file the rewrite would touch is checked for writability. Any read-only target fails the whole move with `readonly_targets` and `data.readOnlyFiles` naming them; the source is not moved.
- **Trigger:** Set `Projects/Widget/Tasks.md` read-only, then rename `Resources/Gizmo-Reference.md`. `data.readOnlyFiles` contains `Projects/Widget/Tasks.md`; the source still exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:792` — `fails readonly_targets when a rewrite target is read-only, mutating nothing`
- **Note:** This pre-flight is what makes the move *effectively* atomic for the common failure; MOV-078 documents the seam it cannot cover.

### MOV-078 — Move and link repair are NOT atomic: a mid-apply write failure yields `partial_state`
- **Behavior:** If a rewrite write fails after the source has already moved, the tool reports `partial_state` with `data.moveApplied: true` and `data.filesRewritten` listing the rewrites that did land — never including the file that failed. There is no rollback.
- **Trigger:** Hold an open `FileShare.Read` handle on `Projects/Widget/Tasks.md` (readable during the scan so validate passes, unwritable at apply), then rename `Resources/Gizmo-Reference.md`. `Resources/Renamed-Gizmo.md` exists; `filesRewritten` is present and excludes `Projects/Widget/Tasks.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:809` — `reports partial_state naming the moved item and already-rewritten files when a rewrite write fails mid-apply`
- **Note:** Directly answers "is repair atomic with the move" — no. The design is fail-fast in validate plus an honest torn-state report.

### MOV-079 — The file `partial_state` message carries an executable recovery recipe
- **Behavior:** The message names concrete recovery commands: `git clean -f -- '<newPath>'` first (the destination is an untracked copy that `git checkout` can neither restore nor delete), then `git checkout -- '<oldPath>'`, explicitly excluding the destination from the checkout list, and using the word `untracked`.
- **Trigger:** The file-move `partial_state` above.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:831` — `reports partial_state naming the moved item and already-rewritten files when a rewrite write fails mid-apply`
- **Note:** Marked artifact because the recipe is git-specific and message-shaped, but the underlying semantic — a torn state must be recoverable from the error alone, with the destination cleaned before the source is restored so the note never exists at two paths — is portable and worth keeping.

### MOV-080 — The folder `partial_state` has its own recovery: clean the destination tree, restore the source folder
- **Behavior:** A torn folder move reports `partial_state` with `moveApplied: true` and `folderNoteRenamed: true`, and a recovery recipe using `git clean -fd -- '<newFolder>'` (the whole destination tree is untracked, so `-fd` not `-f`) plus `git checkout -- '<oldFolder>'`, listing only the rewrites `outside` the destination — restoring the source folder brings its `contained rewrites` back with it.
- **Trigger:** Hold an open read handle on `Resources/Gizmo-Reference.md` — a rewrite target *outside* the moved folder, since the folder-note rename makes it one; a handle on a contained file would block the directory move itself and never reach the apply-phase rewrite. Then `move Projects/Widget -> Projects/Renamed-Widget`. `Projects/Renamed-Widget/Renamed-Widget.md` exists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:842` — `gives the folder-move partial_state its own recovery: clean the destination TREE, restore the source folder`

### MOV-081 — A write lock held by another process fails `lock_timeout` before any mutation
- **Behavior:** The move runs under a per-vault write lock. When another process holds it, the move fails `lock_timeout` and the source is untouched.
- **Trigger:** A background job acquires the `scratch` vault write lock and signals a handshake file; the acquire timeout is squeezed to 1s via `VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS`; the foreground move then times out.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:870` — `fails lock_timeout when the write lock is held by another process`
- **Note:** Windows-only. The lock is reentrant for the calling thread, so contention requires a genuinely separate process. The timeout being env-overridable is an artifact detail.

### MOV-082 — `capabilitiesUsed` is present on every write-class return, `[]` when nothing was asserted
- **Behavior:** The write-class envelope always carries `capabilitiesUsed`, serialized as an empty array when the tool asserted no capability over its floor. `move` can never populate it, and it is emitted anyway — the audit echo is uniform.
- **Trigger:** Any move.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:928` — `emits capabilitiesUsed as an empty array (move asserts no key over its floor)`

### MOV-083 — `filesSkippedUnreadable` is an array of paths, never an integer count
- **Behavior:** The envelope reports skipped-unreadable files as paths; an empty run emits `[]`, never `0`.
- **Trigger:** Any move. The JSON is asserted textually as `"filesSkippedUnreadable": []`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:936` — `emits filesSkippedUnreadable as an array of paths, not an integer count`
- **Note:** The integer form is explicitly retired.

### MOV-084 — `lockWarning` is absent, not null, when the lock was not abandoned
- **Behavior:** The property is omitted from the return entirely unless an abandoned mutex was inherited — an always-present null would read as checked-and-clear.
- **Trigger:** Any normal move.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:945` — `omits lockWarning entirely when the lock was not abandoned`

### MOV-085 — The same envelope appears on a folder move and on an `apply:false` plan
- **Behavior:** The write-class envelope is uniform across every return shape the tool produces.
- **Trigger:** A folder move; and a file move with `-Apply:$false`. Both carry `capabilitiesUsed: []`, `filesSkippedUnreadable: []`, and no `lockWarning`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:952,960` — `carries the same envelope on a folder move`, `carries the same envelope on an apply:false plan`

### MOV-086 — `apply` defaults to true on `move`
- **Behavior:** With no `apply` argument, the move mutates and reports `applied: true` — mutation is the point of the call.
- **Trigger:** A plain file move.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:974` — `defaults applied: true and mutates, with no apply argument supplied`

### MOV-087 — `apply:false` returns `applied: false` and mutates nothing at all
- **Behavior:** A preview runs plan and validate and stops. The source remains, the destination is never created, and no rewrite target's bytes change (asserted byte-exact on two files).
- **Trigger:** `-Apply:$false` on a file move.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:981` — `apply:false returns applied: false and mutates nothing`

### MOV-088 — `apply:false` returns the COMPLETE report, not a stub
- **Behavior:** The preview carries the full result shape: `oldPath`, `newPath`, `kind`, `pureMove`, the would-rewrite `referencesRewritten` and `filesRewritten`, and the fields `referencesQualifiedProtectively`, `namingWarnings`, `folderNoteRenamed`, `folderNoteOrphaned`, `containedFilesMoved`.
- **Trigger:** `-Apply:$false` on the `Gizmo-Reference` rename: `referencesRewritten` 6, `filesRewritten` 3 including `Projects/Widget/Tasks.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:997` — `apply:false returns the COMPLETE report, not a stub`

### MOV-089 — A preview is a faithful prediction: plan and result agree field for field
- **Behavior:** The only difference between a plan and the result of applying the same move is `applied`. `oldPath`, `newPath`, `kind`, `pureMove`, `referencesRewritten` are equal and `filesRewritten` is identical in content and order.
- **Trigger:** Run the same move with `-Apply:$false` and then applied (on fresh copies), and compare.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:1014` — `apply:false is a faithful prediction: the plan and the result agree field for field`
- **Note:** The comment makes the intent explicit: `filesRewritten` names the same POST-move paths in both, so a caller never has to translate between two path vocabularies.

### MOV-090 — A self-referential rewrite is reported at its post-move path, in the plan too
- **Behavior:** When the moved note links to itself, the rewrite lands at the destination, and the preview names it there, not at the source path.
- **Trigger:** Append `Self [[Resources/Gizmo-Reference]].` to `Resources/Gizmo-Reference.md`, then plan `-> Renamed-Gizmo.md`. `filesRewritten` contains `Resources/Renamed-Gizmo.md` and not `Resources/Gizmo-Reference.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:1029` — `apply:false predicts a self-referential rewrite at its post-move path`
- **Note:** A self-link is repaired, not exempted.

### MOV-091 — `apply:false` on a folder move reports the full folder report and moves nothing
- **Behavior:** A folder preview reports `folderNoteRenamed` (old and new paths), `containedFilesMoved`, and a non-empty `filesRewritten`, while the old folder note remains, the destination is never created, and `[[Widget]]` links are unchanged.
- **Trigger:** `move Projects/Widget -> Projects/Renamed-Widget -Apply:$false`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:1038` — `apply:false on a folder move reports the full folder report and moves nothing`

### MOV-092 — A preview is not a bypass: every validate-phase refusal still fires
- **Behavior:** `apply:false` refuses exactly where `apply:true` would. Asserted for `already_exists` (occupied dest), `invalid_argument` (extension change), and `readonly_targets` (read-only rewrite target).
- **Trigger:** Three `It` blocks, each running the corresponding failing move with `Apply = $false`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:1055,1062,1067` — `apply:false still refuses in validate (already_exists), mutating nothing`, `apply:false still refuses an extension change`, `apply:false still runs the read-only pre-flight`
- **Note:** The `readonly_targets` one is the sharpest: the pre-flight is part of validate, so a preview can report an authorization/writability problem before the caller commits.

### MOV-093 — `move` declares the `content:write` op class
- **Behavior:** The tool declares `content:write` as its op-class floor, and the dispatcher gates a caller's grant against it.
- **Trigger:** The script's metadata declares `OpClass: content:write`; the test asserts the line by regex.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:1081` — `declares the content:write floor in its .NOTES comment-help (the loader-parsed op class)`
- **Note:** The gate itself is proven generically on the C# side (a read-only grant calling a write tool is a tool error); `move` declares no move-specific dispatcher test. The *semantic* worth carrying is that `move` requires write authority — only the comment-help spelling is an artifact.

### MOV-094 — `move` is invocable standalone with no host context and returns well-formed JSON
- **Behavior:** The tool runs outside the server — a separate process, no injected globals, no ambient scope — and produces the same result.
- **Trigger:** A fresh `pwsh -NoProfile -NonInteractive -Command` process with only the vault config env var set, running the `Gizmo-Reference` rename. Exit code 0, `oldPath`/`newPath` correct, `referencesRewritten` 6, destination on disk.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:1120` — `runs via pwsh -File with no runspace context or ambient scope and returns well-formed JSON`

### MOV-095 — With no injected scope roots, `move` degrades open (no scope enforcement)
- **Behavior:** Absence of a scope-roots grant is not deny-all; a standalone invocation moves freely.
- **Trigger:** Standalone process, `move Projects/Widget/Tasks.md -> Renamed-Tasks.md`. Exit 0, well-formed JSON.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:1138` — `still degrades open (no scope enforcement) with no injected VaultMcpScopeRoots`
- **Note:** Pairs with MOV-042: an empty scope-roots set and an absent one both mean unscoped.

### MOV-096 — The literal `pwsh -File move.ps1` spelling works, with config passed via inherited environment
- **Behavior:** The tool is invocable by the exact authoring-standard spelling, taking `-Source` and `-Dest` as file-mode arguments.
- **Trigger:** `pwsh -NoProfile -NonInteractive -File move.ps1 -Source ... -Dest ...` with `VAULT_MCP_VAULTS_JSON` set in the parent environment.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:1145` — `runs via a literal "pwsh -File move.ps1" invocation (the Tool Authoring Standard spelling)`

### MOV-097 — Errors are the shared structured `{code, message, data?}` JSON shape
- **Behavior:** Every failure surfaces as a JSON object carrying a machine-readable `code`, a human `message`, and an optional `data` bag of specifics. All error cases in this area are asserted through that shape.
- **Trigger:** Every failing move; the suite's `Invoke-MoveError` helper parses the thrown message as JSON.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:30` — `Invoke-MoveError` (top-level `BeforeAll` helper)
- **Note:** The complete error-code inventory for `move`: `invalid_argument`, `unknown_vault`, `vault_unavailable`, `not_found`, `already_exists`, `kind_mismatch`, `out_of_scope`, `readonly_targets`, `partial_state`, `lock_timeout`.

### MOV-098 — Every move runs inside the per-vault write lock, acquired after vault resolution
- **Behavior:** The tool executes plan -> validate -> apply under the vault's write lock; `apply:false` returns after validate. Vault-resolution failures fire *before* the lock is taken, because the lock's identity derives from the resolved vault root.
- **Trigger:** Implied by MOV-018/MOV-019 (refuse ahead of the lock) and MOV-081 (contended lock).
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:174` (Context comment), `:869` (lock_timeout Context) — implementation `tools/move.ps1:58-62` read to confirm the phase names.

### MOV-099 — Nothing is left at the old location and no redirect is written
- **Behavior:** There is no forwarding note, no `.md` stub, no alias file. The only trace of the move is the repaired links.
- **Trigger:** Every applied move asserts the source path is gone.
- **Kind:** `semantic`
- **Source:** `tools/Tests/move.Tests.ps1:84` — `returns a well-formed Tier-2 JSON object and applies a bare-name rename`
- **Note:** Redundant with MOV-002/MOV-004 but stated separately because "no redirect" is a design choice a reimplementation could plausibly reverse.

### MOV-100 — Cross-vault moves are unexpressible, not refused
- **Behavior:** `move` takes a single `vault` argument; source and destination are both vault-relative within it. There is no source-vault/dest-vault pair and consequently no cross-vault refusal error.
- **Trigger:** N/A — asserted by absence.
- **Kind:** `artifact`
- **Source:** No test. Confirmed from the parameter block at `tools/move.ps1:67-73` and a repo-wide grep for any cross-vault identifier, which returned nothing.
- **Note:** Harvested deliberately as a gap: a reconciler looking for "cross-vault refusal" in the old suite will not find it, and should decide whether the new design's single-vault parameter surface makes the refusal similarly unnecessary or whether it needs an explicit error.

### MOV-F01 — The test-vault fixture: six notes with a deliberately over-linked rename target
- **Behavior:** `test-vault/` contains `Test-Vault.md`, `Inbox/Note-Loose-Thought.md`, `Resources/Gizmo-Reference.md`, `Projects/Widget/Widget.md`, `Projects/Widget/Tasks.md`, `Projects/Widget/Inbox/Plan-Widget-Assembly.md`. `Gizmo-Reference` is referenced six times across three files, in four spellings: bare `[[Gizmo-Reference]]` (x3), `[[Gizmo-Reference#Torque Specs]]`, `[[Gizmo-Reference#Torque Specs|the torque table]]`, and `[[Resources/Gizmo-Reference]]`. `Widget.md` is a folder note whose frontmatter carries `link: "[[Widget/Tasks]]"` and `link: "[[Plan-Widget-Assembly]]"`.
- **Trigger:** Every test.
- **Kind:** `fixture`
- **Source:** `C:\Repos\vault-server\test-vault\**` — read directly.
- **Note:** The frontmatter link in `Widget.md` is the only frontmatter-embedded wikilink in the fixture, and it is a *qualified contained-file* link — so MOV-063's prefix substitution must reach inside YAML frontmatter, though no test asserts frontmatter as a distinct surface.

### MOV-F02 — Each test runs against a fresh temp copy of the vault with a generated config
- **Behavior:** Every test copies `test-vault` to a new temp directory as the vault `scratch`, writes a `vaults.json` naming `scratch` as `default_vault`, and points `VAULT_MCP_VAULTS_JSON` at it. Globals `VaultMcpScopeRoots` and `VaultMcpOpClasses` are removed after each test.
- **Trigger:** `BeforeEach` / `AfterEach`.
- **Kind:** `fixture`
- **Source:** `tools/Tests/move.Tests.ps1:51` — Describe-level `BeforeEach`
- **Note:** Because the move mutates, per-test isolation is mandatory; the standalone Describe repeats the same setup per test for the same reason.

### MOV-F03 — Duplicate-basename fixtures are injected into the scratch copy, never committed
- **Behavior:** `A/Dupe.md` and `B/Dupe.md` (protection Context) and `Other/Widget.md` (folder-note protection Context) are created per-Context in the scratch copy only. The committed `test-vault` contains no duplicate basenames.
- **Trigger:** Context-level `BeforeEach` at `:476` and `:743`.
- **Kind:** `fixture`
- **Source:** `tools/Tests/move.Tests.ps1:472,742`
- **Note:** Any reconstruction of MOV-051 through MOV-056 must recreate these; the resolution-length arithmetic in those cases depends on the exact folder depths (`A/`, `B/`, `A/B/`, `Far/`, `Far/Deep/`, `Resources/`, `Inbox/`).

### MOV-F04 — The Tests directory placement is itself a constraint
- **Behavior:** The suite lives in `tools/Tests/` rather than beside `move.ps1`, because the tool loader scans `tools/*.ps1` top-directory-only and would load a `*.Tests.ps1` file sitting in `tools/` as a broken tool.
- **Trigger:** N/A.
- **Kind:** `artifact`
- **Source:** `tools/Tests/move.Tests.ps1:6` — file header comment
