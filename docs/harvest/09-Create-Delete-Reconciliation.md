# Reconciliation: The `create` and `delete` tools

Harvest source: `09-Create-Delete-Harvest.md` (128 cases)
Implementation read: `src/VaultShared/Tools/CreateTool.cs`, `DeleteTool.cs`, `CreateArguments.cs`,
`DeleteArguments.cs`, `ToolPaths.cs`, `MutationSupport.cs`, `src/VaultShared/Links/WikilinkMaintenance.cs`,
`src/VaultShared/Access/VaultAccess.cs`, `VaultFiles.cs`, `VaultSearch.cs`,
`src/VaultShared/Frontmatter/FrontmatterWrite.cs`, `FrontmatterTree.cs`, `src/VaultShared/Scope/ScopePath.cs`,
`src/VaultShared/Configuration/PathPolicyParser.cs`
Tests read: `tests/VaultShared.Tests/Tools/CreateToolTests.cs`, `DeleteToolTests.cs`,
`tests/VaultShared.Tests/Links/WikilinkCensusTests.cs`, `tests/VaultShared.Tests/Frontmatter/FrontmatterRefusalTests.cs`,
`FrontmatterAuthorityTests.cs`
Design/spec consulted: `docs/spec/Spec-Tool-Capabilities.md` (Authority, Destruction, Enumeration Domains,
What Must Not Come Back), `docs/design/Design-Tool-Vocabulary.md` (Path Policy At Removal And Relocation,
Reporting Out Of The Vault-Wide Pass, What This Closes), `docs/Build-Decisions.md` (The mutating trio),
`frontmatter-registry.json`, `vaults.json`

## Summary

Cases: 128. MATCH 78 · IMPROVED 15 · N/A 29 · REGRESSION 5 · GAP 1 · UNRESOLVED 0

This area carried over well, and every point the task flagged for direct checking came back clean.
The delete census really is vault-wide (`DeleteTool.cs:114` calls `vault.BypassingScope()`, and
`WikilinkMaintenance.Census` enumerates through the unscoped pass). The destruction plan really is a
raw file walk: `EnumerateFiles(pattern: null, includeDotEntries: true)` runs ripgrep with
`--hidden --no-ignore` fixed on every invocation (`VaultSearch.cs:25`), so dot-files and gitignored
files are in the plan, in the emptiness guard, and in the per-entry authority pass. The bare-link
bystander rule (CRD-096), untested in the old suite, is now directly asserted
(`WikilinkCensusTests.cs:50`). Delete reports and never refuses on inbound links. Both create
bypasses are closed by construction: the supplied block is parsed with YamlDotNet and re-emitted,
never spliced, so an unparseable line refuses (`FrontmatterWrite.cs:207`) and a quoted `"agent.x":`
is an ordinary key to the parser and classifies identically.

**The one thing to look at first is that `delete` no longer classifies a note by what its frontmatter
carries.** The old tool read every planned `.md` file and demanded `agentic:write` for any note
carrying an `agent.*`/`nav.*` key; the new one consults only the vault's `path_policy`
(`DeleteTool.cs:107-109`). With the shipped `vaults.json`, which declares no `mutation_requires`
entry at all, a `content:write` grant can today delete a note carrying `agent.mode` that the same
grant could not have created or edited. That is exactly the shed-the-`pinned:`-carrier bypass
CRD-102 and CRD-112 were written against, and nothing in the specs, the design documents, or
`Build-Decisions.md` records dropping it.

Three smaller losses follow: the read-only pre-flight (CRD-114) is gone, so a read-only file now
half-deletes a folder where it used to refuse with nothing removed; a folder create over an existing
folder now succeeds silently instead of refusing (CRD-018); and the orphaned-folder-note warning
(CRD-087) has no counterpart.

## Findings

### REGRESSION — CRD-102, CRD-103, CRD-112 — delete no longer classifies a note by the frontmatter it carries

- **Old behavior:** Delete read every `.md` file in the destruction plan and diffed its frontmatter
  against an empty result. Any note carrying an `agent.*` or `nav.*` key required `agentic:write`;
  one such note anywhere in a recursive folder plan denied the whole delete and named its path. The
  stated reason was that without it a `content:write` agent could shed a binding `pinned:` directive
  simply by deleting its carrier note. CRD-112 pins that this covered gitignored notes too.
- **New behavior:** The only authority the tool asserts beyond its `content:write` floor is what the
  vault's path policy declares for each planned entry —
  `plan.Entries.Select(entry => new PathRequirement(entry.Value, vault.Vault.Policy.MutationRequires(entry.Value)))`
  at `src/VaultShared/Tools/DeleteTool.cs:107-109`, through
  `MutationSupport.AssertArtifactAuthority` (`src/VaultShared/Tools/MutationSupport.cs:47`). No
  planned file is read, and no key is looked at. `Design-Tool-Vocabulary.md:196` states the mechanism
  ("Neither `delete` nor `move` routes through the frontmatter write path, so each consults the
  vault's `PathPolicy`") but records no decision about the key-level dimension going away for
  removal.
- **What is lost:** The protection is now membership of a declared path set rather than a property of
  the note. A `content:write` caller can delete any note carrying an `agent.*` key that no
  `path_policy` entry names — and the shipped `vaults.json` declares no `mutation_requires` entry at
  all, so today that is *every* note in `test-vault`, including one carrying the `agent.` namespace
  that `frontmatter-registry.json` prices at `agentic:write`. The asymmetry is sharp and
  demonstrable: `edit` classifies over the outcome diff, so removing an `agent.*` key through an edit
  requires `agentic:write` (`FrontmatterWrite.cs:601-628`); deleting the whole note that holds it
  does not. `Spec-Tool-Capabilities.md:195` lists "removing a note's frontmatter entirely" as a
  degenerate case that falls out of outcome classification, which reads as covering this and does
  not.
- **Covered by a test?** No test asserts it. `DeleteToolTests.TheWholePlanIsValidatedBeforeAnythingIsRemoved`
  looks like the coverage but injects a synthetic `path_policy` entry naming a literal path
  (`DeleteToolTests.cs:186`); it establishes the policy dimension and says nothing about note
  content. A test deleting a note carrying `agent.mode` under a `content:write` grant would pass
  today by removing it.
- **Recorded anywhere?** Nothing records it. `Design-Tool-Vocabulary.md#Path Policy At Removal And
  Relocation` and its `What This Closes` list are silent on key-level authority at removal, and
  `Build-Decisions.md#The mutating trio` does not raise it.

### REGRESSION — CRD-114 — no removability pre-flight, so a read-only file half-deletes a folder

- **Old behavior:** A pre-flight over every planned file refused `readonly_targets` with
  `data.readOnlyFiles` naming them, before the first removal, explicitly so a read-only file partway
  through a folder could not leave a half-deleted tree. Nothing was removed.
- **New behavior:** `Remove` (`src/VaultShared/Tools/DeleteTool.cs:265-297`) drives the plan entry by
  entry with no prior writability check. The first entry that fails raises `partial_mutation` naming
  what landed and what did not (`DeleteTool.cs:329`), and the already-removed entries stay removed.
  `DeleteToolTests.ARemovalThatStopsPartwayRaisesThePartialMutationVocabulary` asserts exactly this
  outcome for a held-open file.
- **What is lost:** A refusal that used to fire with zero mutation now fires after partial
  destruction. The spec's own guarantee that "the whole plan is validated before anything is removed"
  (`Spec-Tool-Capabilities.md:272`) covers the guards and the authority assertion but not
  removability, so the most common cause of a mid-run stop on Windows — a read-only attribute — is
  the one the validate phase does not look at. A caller deleting a ten-note folder with one read-only
  note now loses the other nine and gets recovery steps instead of a clean refusal.
- **Covered by a test?** No test asserts a pre-flight. The partial-mutation test asserts the weaker
  behavior.
- **Recorded anywhere?** Nothing records dropping the pre-flight.
  `Spec-Tool-Capabilities.md#Multi-Target Mutation` establishes the partial-failure vocabulary in
  general, which is the shape of the new behavior but not a decision to stop checking first.

### REGRESSION — CRD-018 — a folder create over an existing folder now succeeds instead of refusing

- **Old behavior:** `kind: folder` against an existing directory was refused `already_exists` with
  `data.path`.
- **New behavior:** `VaultFiles.CreateFolder` calls `Directory.CreateDirectory`, which is idempotent,
  and returns `WriteOutcome.Written` (`src/VaultShared/Access/VaultFiles.cs:139-160`). Its own remark
  says so: "A folder that is already there is not an error." `CreateTool.CreateFolder`
  (`CreateTool.cs:199-218`) therefore returns a success, and with `git_keep` set it then seeds
  `.gitkeep` into a folder it did not create.
- **What is lost:** A refusal that no longer fires, and the tool's own advertised contract is now
  wrong for one of its two kinds — the declaration text says "An occupied target is refused rather
  than overwritten" (`CreateTool.cs:85`) and the type remark says "An occupied target is refused
  rather than overwritten or merged" (`CreateTool.cs:44`). A caller cannot distinguish "I created
  this folder" from "it was already there", which is precisely the distinction the note form
  preserves through the atomic create.
- **Covered by a test?** No test asserts either behavior for an occupied folder path.
  `CreateToolTests.AnOccupiedTargetIsRefusedAndPinsNothingOnTheWay` covers the note form only.
- **Recorded anywhere?** The idempotence is reasoned at the primitive
  (`VaultFiles.cs:134-138`: the platform cannot report the difference without a preliminary check,
  which the primitives avoid). Nothing records the consequence for the tool's contract, and nothing
  weighs it against `Spec-Access-Layer-Capabilities.md:77`, which requires the creating form's
  refusal to come from the write.

### GAP — CRD-087 — the orphaned-folder-note warning has no counterpart

- **Old behavior:** Deleting `Foo/Foo.md` while `Foo/` survived with other content set
  `folderNoteOrphaned: true` on the result. Warn-and-proceed: the note was removed and the broken
  folder-note invariant was surfaced to the caller.
- **New behavior:** `DeleteResult` carries `Applied`, `Removed`, `Backlinks` and `AssertedAuthority`
  and nothing else (`src/VaultShared/Tools/DeleteTool.cs:23-27`). Nothing in `DeleteTool` looks at
  whether a removed note is its parent folder's folder note, even though the concept exists in the
  build as a `PathPolicy` structural rule and `MoveTool` maintains it.
- **What is lost:** A structural break the vault cares about now happens silently. The old tool left
  it "for lint"; `lint` is explicitly out of scope for this build
  (`Spec-Tool-Capabilities.md#Beyond The First Pass`), so nothing at all observes it now. The cost is
  a vault that accumulates folders whose folder note is gone with no record of when it happened.
- **Covered by a test?** No test asserts anything about folder notes on removal.
- **Recorded anywhere?** Nothing records it. `Design-Tool-Vocabulary.md` explains every member
  `DeleteResult` *has* and does not mention a folder-note member being considered and dropped;
  `MoveResult` keeps a `FolderNote` member, so the concept was not dropped layer-wide.

## Present in code, unasserted by the suite

- **CRD-095** — the census's unscoped reach is in `DeleteTool.cs:114` and asserted only at the
  `WikilinkMaintenance.Census` level, where every test passes `BypassingScope()` itself. No delete
  test narrows the grant's scope and checks a backlink from outside it survives in the report. A
  future change to `DeleteTool.cs:114` would break nothing.
- **CRD-047** — a quoted agentic key classifying identically follows from parsing with YamlDotNet
  rather than from a rule, and no test pins it. Cheap to add; it was a real bypass once.
- **CRD-082** — the emptiness guard is asserted for `.gitkeep` alone and for `.gitkeep` plus a note,
  but not for a folder holding only some *other* dot-file (a `.placeholder`). The code is right
  (`DeleteTool.cs:236-239` exempts the exact `.gitkeep` name and nothing else) and the case that
  motivated the old regression test is uncovered.
- **CRD-112** — the plan containing a gitignored file rests on `--no-ignore` being fixed in
  `VaultSearch.Always`; no delete test creates a `.gitignore` and checks the ignored file is in the
  plan.
- **CRD-098** — the vanished-versus-unreadable split in the census read
  (`WikilinkMaintenance.ReadAll`, `WikilinkMaintenance.cs:504-519`: a `ReadOutcome.NotFound` leaves a
  null and is skipped, a `ReadOutcome.Failed` fails the pass) is unasserted for the delete path.
- **CRD-085/086** — the `.git` guard matching a `.git` *file* as well as a directory follows from
  scanning every segment of every planned entry (`DeleteTool.cs:204-206`); the test uses a `.git`
  directory only.

## Dismissed

- **CRD-003** — `frontmatter` and `body` collapsed into one `content` argument — an empty note is
  empty text, so the deliberate-no-block request needs no separate spelling.
- **CRD-004** — no separate body to default — the same collapse; content is required and carries both.
- **CRD-005** — bare inner lines are no longer an accepted frontmatter spelling — content is a whole
  note, so a block is fenced or absent.
- **CRD-006** — backslashes still fold (`ScopePath.Normalize`), but a leading `/` is now refused as
  rooted rather than trimmed — refusing a spelling is safer than silently reinterpreting it.
- **CRD-007** — surrounding whitespace is no longer trimmed off the path; the segment fails the name
  grammar — same reason, and the grammar refusal names a spelling that would have worked.
- **CRD-008** — `capabilitiesUsed`/`filesSkippedUnreadable` replaced by `AssertedAuthority`, present
  when empty — envelope redesign; the always-present-when-empty property is kept.
- **CRD-009** — no `lockWarning` — there is no abandoned-mutex inheritance concept in the retained
  lock.
- **CRD-012** — a naming-convention miss is now a hard `invalid_name` refusal carrying one suggested
  spelling — `Spec-Access-Layer-Capabilities.md:Name Validation` requires refusal over warning.
- **CRD-014** — folder create is no longer mkdir-p; a missing parent refuses — recorded at
  `CreateTool.cs:256` and in `Build-Decisions.md` ("Folder creation establishes its parent with a
  check"); nothing may bring a folder into existence on the way past.
- **CRD-015** — no `created[]` list — a creation names one path.
- **CRD-019** — a directory occupying a note path refuses from the attempt, as `write_failed` rather
  than `already_exists` — the refusal still fires with nothing written.
- **CRD-022** — no partial tree is possible, since there is no mkdir-p walk to leave one.
- **CRD-024** — `project`/`area` kinds never existed; scaffolding is composed
  (`Spec-Tool-Capabilities.md#Naming And Composition`).
- **CRD-026** — `content` is required for a note, and null differs from empty — the unsupplied/empty
  distinction survives on the one argument; flagged in `Build-Decisions.md`.
- **CRD-028** — no `body` argument; the inert-parameter rule is carried by `git_keep` on a note and
  `content` on a folder, both refused with the argument named.
- **CRD-032** — a non-`.md` note path refuses with no suggestion — recorded at `ToolPaths.cs:144-149`:
  appending `.md` to `Notes.txt` would name a third thing the caller did not mean.
- **CRD-038** — content after the closing fence is now legitimately the body — one `content` argument
  makes the smuggling shape impossible rather than refused.
- **CRD-039** — a body cannot mint an unvalidated block, because every block in `content` is the
  supplied block and is parsed and validated — enforced by construction rather than by a guard.
- **CRD-040** — schema violations refuse with a redesigned payload (`unknown_key`, `invalid_value`,
  `shape_mismatch` with their own data) — same refusals, different vocabulary.
- **CRD-041** — no path-conditional schema rule exists; the registry is a flat key/namespace
  allowlist by design (`Spec-Frontmatter-Registry.md`) and a `Tasks.md`-shaped rule belongs to `lint`,
  out of scope. The path *is* still passed to the write path, for exclusion and policy.
- **CRD-053** — no degrade-open with no injected class set — the canonical dismissed divergence;
  authority comes from the grant, not from an ambient global.
- **CRD-060** — the floor is a typed `ToolDeclaration.OpClassFloor`, not a comment-help line.
- **CRD-061** — the parameter surface is a record and its schema is derived; the five names are now
  `path`/`kind`/`vault`/`content`/`git_keep`.
- **CRD-064** — the exclusion widened from `.claude/skills` to every dot segment
  (`FrontmatterWrite.RefusePathExclusion`, `VaultAccess.ForWrite`) — strictly wider.
- **CRD-066** — `.claude/guides` is consequently no longer creatable — a deliberate uniform operand
  perimeter (`Spec-Tool-Capabilities.md#The Operand Perimeter`), not an oversight.
- **CRD-067, CRD-068** — no `pwsh -File` standalone invocation; standalone-invocability is listed as
  incidental in `Spec-Tool-Capabilities.md#Incidental, Not Carried` and the property is structural in
  the front-end design.
- **CRD-069** — create now *does* pin wikilinks; the whole surface is new, per the task's framing note.
- **CRD-071** — `unrecoverableFiles`, `filesSkippedUnreadable` and `recoveryHint` are not members of
  `DeleteResult` — see CRD-109/110/113 for the recoverability half; the skip list has no consumer now
  that the census fails rather than reports a skip.
- **CRD-073** — an extensionless delete path refuses `invalid_path` carrying `suggested` rather than
  `not_found` — the two tools now spell the never-infer rule identically, which is more useful than
  the old split.
- **CRD-076** — delete's kind vocabulary is `note`/`folder`, the same enum create takes, so `note` is
  now legal rather than refused — one operand vocabulary across the surface; an unrecognized value is
  still the tool's own `invalid_argument`.
- **CRD-080** — `recursive: false` on a note is now accepted, because the record cannot distinguish
  supplied-false from absent — the inert-parameter refusal still fires for `recursive: true`, and the
  lost case corrects nothing a caller could act on.
- **CRD-088, CRD-089** — no `folderNoteDeleted`/`folderNoteOrphaned` members; see the CRD-087 finding
  for the orphaning half. `folderNoteDeleted` is redundant with `Removed`, which lists the note.
- **CRD-098** — the vanished/unreadable split survives inside the census read, but a vanished note is
  skipped silently rather than reported in a result member — the pass reports identities only
  (`Design-Tool-Vocabulary.md#Reporting Out Of The Vault-Wide Pass`).
- **CRD-108** — no degrade-open on delete either; same containment improvement as CRD-053.
- **CRD-109, CRD-110, CRD-113** — no git recoverability verdict, no `unrecoverableFiles`, no
  `recoveryHint` — explicitly rejected in `Spec-Tool-Capabilities.md#What Must Not Come Back`: a
  verdict computed at removal time is a safety claim the tool cannot back.
- **CRD-124, CRD-125** — no standalone `pwsh -File` invocation or stderr JSON shape; same reasoning as
  CRD-067.
- **CRD-126, CRD-127, CRD-128** — old Pester fixture mechanics (per-test scratch copy, the test-vault
  link graph, the separate-process contexts). The new suites use `ScratchVault` per test and
  `Synthetic`'s vocabulary; the separate-process contexts existed only to defeat ambient globals,
  which no longer exist.
