# Reconciliation: The `move` tool and link repair on relocation

Harvest source: `10-Move-Harvest.md` (104 cases)
Implementation read: `src/VaultShared/Tools/MoveTool.cs`, `MoveArguments.cs`, `ToolPaths.cs`,
`MutationSupport.cs`, `VaultWriteLock.cs`, `NativeToolArguments.cs`,
`src/VaultShared/Links/WikilinkMaintenance.cs`, `WikilinkPlan.cs`, `WikilinkUniverse.cs`,
`src/VaultShared/Access/VaultFiles.cs` (move primitives)
Tests read: `tests/VaultShared.Tests/Tools/MoveToolTests.cs`,
`tests/VaultShared.Tests/Links/WikilinkRepairTests.cs`, `WikilinkPinningTests.cs`,
`WikilinkCensusTests.cs`, `tests/VaultShared.Tests/VaultWidePassTests.cs`
Design/spec consulted: `docs/spec/Spec-Obsidian-Link-Behavior.md`,
`docs/spec/Spec-Tool-Capabilities.md:50`, `docs/design/Design-Tool-Vocabulary.md` (The Preview Gate;
What This Closes), `docs/Build-Decisions.md:176-231`

## Summary

Cases: 104. MATCH 66 · IMPROVED 13 · N/A 18 · REGRESSION 4 · GAP 3 · UNRESOLVED 0

The link-repair half of this area carried over essentially intact and in several places is stronger
than what the harvest recorded. Every intricate rule survives: repair-versus-pin discrimination
(MOV-051/053/054/055/056), the minimality rule (MOV-048), canonical emission (MOV-047), the folder
prefix substitution and folder-note rename (MOV-059..MOV-067), and — checked directly, as asked —
link repair is **not** scope-limited: `MoveTool.cs:123` obtains the pass through
`vault.BypassingScope()`, and `VaultWidePassTests.cs:89` asserts a scoped grant writing outside its
own roots (MOV-043 MATCH). The two flagged divergences are both corrections rather than losses: the
alias refresh (commit `1e636f8`) is measured Obsidian behavior recorded at
`Spec-Obsidian-Link-Behavior.md:31` and asserted by `WikilinkRepairTests.cs:141`, and MOV-070's
depth-preserving suffix substitution is replaced by canonical emit, which is the same spec's
`Maintenance Rules` first bullet.

What did not carry over is the **validate phase**. The old tool decided every destination refusal
before mutating anything, and a preview ran that same validation. The new tool decides them inside
the filesystem primitive at apply time, after the vault-wide link repair has already been written,
and a preview never reaches them at all. Look at **MOV-092 first**: a preview of a move onto an
occupied destination, onto a missing parent, or to an ungrammatical name reports a clean plan, and
the applied call then rewrites the whole vault's links and refuses. The design document states the
opposite guarantee ("A plan differs from its result in that member and nothing else"), and nothing
records the change. The read-only pre-flight (MOV-077) is gone with it, and MOV-013 — a folder moved
inside itself — now reaches a torn state whose printed recovery step cannot work.

MOV-078/080 resolve the other way: the new implementation is also non-atomic, but the order is
inverted (repair, then move), so a failed repair leaves nothing moved and re-running is idempotent.
That is better than the old torn state, and it is recorded at `docs/Build-Decisions.md:176,220`.

## Findings

### REGRESSION — MOV-092 (with MOV-013, MOV-014, MOV-015, MOV-025, MOV-069, MOV-088, MOV-089) — a preview no longer runs the destination refusals, and an applied call runs them only after rewriting the vault

- **Old behavior:** `apply:false` refused exactly where `apply:true` would — asserted for
  `already_exists`, the extension rule and `readonly_targets` (MOV-092) — and every one of those
  refusals fired in validate, with the source still in place and nothing rewritten (MOV-025,
  MOV-069, MOV-014, MOV-015, MOV-029). The preview was a faithful prediction (MOV-088/089).
- **New behavior:** `MoveTool.Invoke` performs argument, kind, extension and scope checks, plans the
  repair, asserts authority, and then `if (arguments.Apply) Perform(...)` (`MoveTool.cs:148`).
  Destination occupancy, a missing destination parent and the destination's name grammar are all
  decided inside `VaultFiles.MoveFile`/`MoveFolder` (`VaultFiles.cs:243`, `:271`, `:308`, `:318-322`),
  which only the apply path calls. On apply they are reached *after*
  `WikilinkMaintenance.Apply(pass, plan)` has already written every repaired note
  (`MoveTool.cs:191-204`), so a non-empty plan turns them into `partial_mutation`
  (`MoveTool.cs:305-316`).
- **What is lost:** two things. (1) A preview of `move A.md -> B.md` where `B.md` exists, or where
  the destination parent is absent, or where the destination basename is outside the name grammar,
  returns a successful-looking plan; the caller authorizes it and the second call rewrites links
  across the whole vault and then refuses. The preview gate — the design's stated substitute for a
  content hash on `move` (`Design-Tool-Vocabulary.md`, Content Hashes) — does not cover the refusals
  a caller is most likely to hit. (2) A pure caller typo decidable from the argument text alone (an
  ungrammatical destination basename, MOV-014/MOV-015) churns the link graph before being refused.
  `docs/Build-Decisions.md:220` closes the *choice* to report `partial_mutation` when the relocation
  cannot happen, but it does not consider running the destination's own checks in validate first,
  where they cost one `File.Exists`-class probe and the folder form already performs one for the
  parent (`VaultFiles.cs:318`, and `docs/Build-Decisions.md:206` justifies exactly that probe).
- **Covered by a test?** No test asserts a preview against an occupied destination, a missing parent
  or a bad name. `MoveToolTests.cs:159` and `:178` exercise both only with `apply = true`, and
  `:300` asserts the name refusal with `apply = true` over a fixture whose plan happens to be empty.
  `VaultToolHarness.AssertPreviewParity` is only ever run on moves that succeed.
- **Recorded anywhere?** Nothing records it, and `Design-Tool-Vocabulary.md` (The Preview Gate) and
  `MoveTool.cs:68-71` both assert the contrary: "the preview runs the identical path - the same
  lock, the same validation, every refusal a real call would raise".

### REGRESSION — MOV-013 — a folder moved inside itself is no longer refused, and lands in a partial state its own recovery cannot resolve

- **Old behavior:** `Source = Projects/Widget`, `Dest = Projects/Widget/Nested` was refused
  `invalid_argument` in validate. Nothing was mutated.
- **New behavior:** there is no containment check anywhere on the move path — the only self-check is
  `destination.Path.HasSameSpellingAs(source.Path)` (`MoveTool.cs:116`). The destination's parent is
  the source folder, so it exists and `MoveFolder`'s parent probe passes (`VaultFiles.cs:318`); the
  repair is written first (`MoveTool.cs:191`), then `Directory.Move` fails and is classified
  `MoveOutcome.Failed` (or `DestinationOccupied`) at `VaultFiles.cs:333-340`, which becomes
  `partial_mutation` because the plan is non-empty.
- **What is lost:** an impossible-by-construction move now rewrites every link naming anything under
  the folder to a path that can never exist, and the recovery text the tool prints —
  "Re-run the identical move" (`MoveTool.cs:315`) — is guaranteed to fail again. The caller's only
  route out is a repository-level revert, which this build explicitly does not perform. The old
  refusal made this unreachable.
- **Covered by a test?** No test moves a folder into itself, at the tool level or the primitive level.
- **Recorded anywhere?** Nothing records this case.

### REGRESSION — MOV-077 — the read-only pre-flight over rewrite targets is gone, so an unwritable note leaves the link graph half-rewritten

- **Old behavior:** before applying anything, every file the rewrite would touch was checked for
  writability; any read-only target failed the whole move with `readonly_targets` and
  `data.readOnlyFiles`, mutating nothing. The check was part of validate, so a preview reported it
  too (MOV-092).
- **New behavior:** there is no writability check. `WikilinkMaintenance.Apply` writes note by note
  and stops at the first `WriteOutcome` that is not `Written` (`WikilinkMaintenance.cs:235-250`),
  and `MoveTool.cs:191-200` turns that into `partial_mutation` with the written/unwritten split.
- **What is lost:** the guarantee that a rewrite-target problem costs nothing. One read-only note —
  ordinary under sync tools, a checked-out working tree, or an open editor — now converts a routine
  rename into a vault whose link graph is partly rewritten to a destination nothing arrived at. The
  new order does keep the file unmoved and makes a re-run idempotent, which is a real mitigation,
  but the old behavior was strictly stronger: nothing was written at all, and the preview said so
  in advance.
- **Covered by a test?** No test makes a rewrite target unwritable through the tool.
  `WikilinkRepairTests.cs:482` covers the pass-level `Failed` outcome by deleting the note, which
  asserts the split but not the tool's pre-flight (there is none to assert).
- **Recorded anywhere?** `docs/Build-Decisions.md:220` records the decision to answer
  `partial_mutation` when the *relocation* cannot happen; it says nothing about dropping the
  writability pre-flight over rewrite targets, and no design section mentions it.

### REGRESSION — MOV-090 (with MOV-089) — rewrites inside relocated notes are reported at their pre-move paths

- **Old behavior:** `filesRewritten` named every rewritten file at its **post-move** path, in the
  plan and in the result alike, "so a caller never has to translate between two path vocabularies"
  (MOV-089); a self-referential rewrite in the moved note was reported at the destination (MOV-090).
- **New behavior:** `LinkRewrite.Path` is the path the plan found the note at, which for the moved
  note itself and for every note inside a moved folder is the pre-move path
  (`WikilinkMaintenance.cs:299,466`). `WikilinkRepairTests.cs:466` asserts exactly this
  (`plan.Rewrites.Single().Path` is `Old/Ref.md` for a `Old -> New` folder move). Only
  `MoveResult.Moved`, `Path` and `FolderNote` are post-move.
- **What is lost:** on a folder move — the case where most rewrites are inside the moved set — the
  paths in `RepairedLinks`/`PinnedLinks` do not exist once the call returns, so a caller cannot
  read, re-read or link to them without translating. `MoveResult`'s own summary claims the opposite
  ("Every path-valued member reports a post-move path", `MoveTool.cs:9-11`), so the two halves of
  the result are in different vocabularies while documenting one.
- **Covered by a test?** Yes, in the sense that `WikilinkRepairTests.cs:466` pins the pre-move
  spelling deliberately. No test compares it against `MoveResult.Moved`.
- **Recorded anywhere?** `docs/Build-Decisions.md:176` records that the *plan* names notes by the
  path they have now, which is the correct rule for applying it. Nothing records the decision to
  also *report* them that way, and the doc comment on `MoveResult` contradicts it.

### GAP — MOV-072 — links and embeds naming a non-note file inside a relocated folder are neither repaired nor reported

- **Old behavior:** the contained-file census was not `.md`-only, so `![[Widget/diagram.png]]` and
  `![[Projects/Widget/diagram.png]]` were repointed by the same prefix substitution when the folder
  moved, and the png itself was carried along.
- **New behavior:** the relocation pass resolves only through `WikilinkUniverse`, which indexes
  notes (`WikilinkUniverse.cs:42-64`), so `before.Resolve(...)` answers null for a target naming an
  attachment and no rewrite is planned (`WikilinkMaintenance.cs:304`). A folder move is a single
  `Directory.Move` (`VaultFiles.cs:326`), so the attachment *does* physically relocate with the
  folder — `docs/Build-Decisions.md:199` says so explicitly ("a folder also holds attachments") —
  and every embed naming it by a path breaks silently.
- **What is lost:** a folder move breaks inbound embeds of attachments it carries, with nothing in
  `RepairedLinks` and nothing in any warning to say so. The removal path does handle this case
  (`WikilinkMaintenance.cs:206-210` matches a non-resolving target against the removed set by
  address, precisely "how a link to a file that is not a note is reported"), so the asymmetry is
  visible inside one file.
- **Covered by a test?** No. No test in `WikilinkRepairTests.cs` or `MoveToolTests.cs` puts a
  non-note file inside a moved folder.
- **Recorded anywhere?** `Spec-Tool-Capabilities.md:50` closes the adjacent question — no tool
  creates, relocates or discovers an attachment — which is why moving a `.png` operand is correctly
  `N/A` (MOV-031/034/035). It does not cover an attachment relocated *incidentally* by a folder
  move, which is the only way one moves under this design.

### GAP — MOV-057, MOV-058 — nothing warns that a move orphaned a folder note

- **Old behavior:** file-renaming a folder note away from its folder name, or file-moving it out of
  its folder, still moved but reported `folderNoteOrphaned: true`.
- **New behavior:** `MoveResult` has no such member. The only mechanism that notices the shape
  change is the path policy's structural rule, which turns it into an *authority* requirement
  (`MoveTool.cs:392-404`, tested at `MoveToolTests.cs:333`) — and only in a vault whose policy
  declares `folder_note` at all.
- **What is lost:** in a vault with no `folder_note` policy entry, an agent can rename
  `Widget/Widget.md` to `Widget/Notes.md` and break the folder-note convention with no signal of any
  kind. The old tool's warn-and-proceed cost nothing and named the invariant it broke.
- **Covered by a test?** No test asserts any orphan report, because there is none.
- **Recorded anywhere?** Nothing records dropping the warning. `MoveTool.cs:64-66` and
  `docs/Build-Decisions.md:178` cover only the *folder* form's folder-note rename, which is the
  case where the invariant is preserved rather than broken.

## Present in code, unasserted by the suite

- Repair is not scope-limited (MOV-043): `MoveTool.cs:123` uses `BypassingScope()`, and
  `VaultWidePassTests.cs:89` asserts the primitive, but no test drives `move` under a narrow grant
  and checks that a link outside the scope roots was repaired.
- The write lock (MOV-081/MOV-098): `ToolExecution.cs:123` acquires it for a mutating tool and
  `VaultWriteLock.cs:63` raises `lock_timeout`; no `move`-level contention test exists.
- The folder-note rename's own partial state (`MoveTool.cs:239-248`) has no test.
- `caseOnly` on a folder move (`VaultFiles.cs:312`, MOV-073) is asserted only for the file form
  (`MoveToolTests.cs:207`).

## Dismissed

- **MOV-004** — no `stub` field; nothing in the new result or code emits a forwarding stub — same absence, by construction.
- **MOV-006** — `-Force` was a binding error; unknown JSON members are silently ignored by the shared binder (`NativeToolArguments.cs:44`, no `UnmappedMemberHandling.Disallow`) — a binder-wide property outside this area, and nothing is actually forced; flagged in the return message rather than raised here.
- **MOV-007** — `referencesQualifiedProtectively` null-when-empty becomes `PinnedLinks` where null means no pass ran and empty means nothing was pinned — a finer distinction, closed at `Design-Tool-Vocabulary.md` ("Nullable means the question was never asked").
- **MOV-008** — retired `referencesSkippedAmbiguous`/`basenameCollisionCreated` — the new result never had them.
- **MOV-016, MOV-017** — a naming-convention violation was a warning; the name grammar is now a refusal with a suggested spelling (`MutationSupport.InvalidName`) — stricter, not weaker.
- **MOV-027** — `pureMove` is not reported; `CaseOnly` is. The rule `pureMove` governed (a link that still resolves is not rewritten) is preserved internally by canonical emit — a reporting-shape difference.
- **MOV-031, MOV-032, MOV-034, MOV-035, MOV-036** — non-note operands (`LICENSE`, `diagram.png`, `notes.txt`) cannot be moved: the operand space is note-or-folder (`ToolPaths.KindOf`) — closed by `Spec-Tool-Capabilities.md:50`, "no tool creates, relocates, or discovers" non-markdown content.
- **MOV-037** — no extension-rule override; the new surface has none either, and the extension is a kind discriminator rather than a rule to override (`docs/Build-Decisions.md:215`).
- **MOV-044 (alias clause)** — the alias was carried verbatim on every rewrite; it is now refreshed where it was the moved note's own basename (commit `1e636f8`, `WikilinkMaintenance.Refreshed`) — a correction to re-measured editor behavior, `Spec-Obsidian-Link-Behavior.md:31`, asserted at `WikilinkRepairTests.cs:141,173,188`. The heading anchor and an author's own alias still survive untouched.
- **MOV-050** — an unreadable rewrite target was skipped and named; it now fails the whole pass (`WikilinkMaintenance.ReadAll`, `LinkPassOutcome.ReadFailed`) — fail-closed, and "skipping it would turn a failed operation into a silent success". A note that vanished between enumeration and read is still skipped.
- **MOV-061** — no `folderMoveDeferred` remnant; the folder move is one `Directory.Move`.
- **MOV-063, MOV-064, MOV-067, MOV-070** — depth-preserving suffix substitution per link is replaced by canonical emit (bare when the basename is unique after the move, full vault path when not) — this is the re-measured editor rule at `Spec-Obsidian-Link-Behavior.md:29`; the old form was minimal rather than canonical and the editor would have rewritten it. The repairs still land, at a different spelling.
- **MOV-071** — destination parents were autocreated; a missing parent is now `parent_missing` — a deliberate boundary, `Spec-Access-Layer-Capabilities.md:Writing` reserves directory creation to the tool that owns it (`docs/Build-Decisions.md:199`).
- **MOV-078** — still not atomic, but repair runs before the move, so a failed repair leaves nothing moved and a re-run is idempotent; `partial_mutation` carries `applied`, `not_applied` and ordered recovery steps — recorded at `docs/Build-Decisions.md:176,220`. (The pre-flight the old design paired with it is the separate MOV-077 finding above.)
- **MOV-079, MOV-080** — git recovery recipes in the message; this build performs no repository operations, and recovery is prose steps naming what landed — architecture, not loss.
- **MOV-083** — `filesSkippedUnreadable` as an array; there is no skip list because an unreadable note fails the pass (MOV-050).
- **MOV-084** — `lockWarning` omitted unless abandoned; the new lock reports an inherited mutex through `onInheritedLock` (`VaultWriteLock.cs:67`) rather than a result member.
- **MOV-086** — `apply` defaulted true; it now defaults false — the preview gate, closed at `Design-Tool-Vocabulary.md` ("`apply` is the gate, false by default").
- **MOV-094, MOV-096** — standalone `pwsh -File` invocation of a script tool: no counterpart; the tools are C# types behind one execute path.
- **MOV-095** — degraded open with no injected scope roots; the rewrite deliberately has no fail-open — the operator's canonical improvement.
- **MOV-100** — no cross-vault concept: `MoveArguments` carries one `Vault`, and `VaultFiles.MoveFile` *throws* on a cross-vault pair as a caller defect (`VaultFiles.cs:238`) — unexpressible in the same way, now enforced by construction one layer down.
- **MOV-F04** — the `tools/Tests/` placement constraint came from the PowerShell tool loader; there is no loader scanning a directory of scripts.
