# Harvest: The `edit` tool

Sources:
- `C:\Repos\vault-server\tools\Tests\edit.Tests.ps1` (1387 lines) — every `It` block read.
- `C:\Repos\vault-server\tools\edit.ps1` (lines 1–220 read: comment-help, the `.SCHEMA
  frontmatter_ops` block, the `param()` block, and the facet-binding preamble).
- `C:\Repos\vault-server\CLAUDE.md` (points at an out-of-reach vault corpus; nothing harvestable).

Read the implementation for: the `.SCHEMA frontmatter_ops` side-car and the parameter help in
`tools/edit.ps1`. This was not disambiguation of an unclear test — the suite's "advertised
frontmatter_ops contract" Context *derives its entire test tree from that block at discovery time*,
so the block's content is literally the set of cases the suite dispatches. Harvesting the projection
rules meant reading the thing the tests iterate over. Also read the `param()` block and the
`$script:*Bound` preamble to state the unset-vs-empty-vs-null facet rule precisely (EDT-020).

## Summary

`edit` is the single mutating tool for an **existing** note. One call carries an ordered array of
frontmatter ops plus **at most one** body facet (whole-body replace, or one exactly-once
`old_string`/`new_string` patch), applied to an in-memory string under a per-vault write lock as
plan → validate → apply, written **once**. Everything follows from that shape: validation failure
means zero mutation and there is no partial-op disk state; there is no `apply`/preview parameter at
all (deliberate — `edit` is single-target with caller-supplied content, so it has nothing to
discover and a preview would be ceremony); and staleness is caller-carried (`expected_sha` in,
`resultingSha` out) rather than host-tracked, which is what lets the tool work standalone.

Things a reconciler should know before reading the cases:

- **Fixture.** Each test gets a fresh scratch copy of `test-vault/` and writes a byte-precise
  working note `Inbox/Edit-Case.md` (LF, `doc.created`/`doc.type` frontmatter, `# Edit-Case` +
  `alpha line` + `beta line`). Many byte-exact assertions only parse against that note.
  `Projects/Widget/Widget.md` (real `nav.contents` block) and `Projects/Widget/Tasks.md` (no
  frontmatter block; subject to a path-dependent validator rule) carry the rest.
- **Facet separation is the spine.** The body facet and the frontmatter facet are disjoint: a patch
  is confined to the body, and frontmatter is reachable *only* through the ops vocabulary. Several
  cases exist purely to nail that boundary from both sides.
- **`edit` does not own the frontmatter semantics** — it drives a substrate (`Invoke-VaultFrontmatter
  Write` and `FrontmatterOps.ps1`) that owns byte-preserving splices, the key registry, schema
  validation, the fence-introduction guard, and the agentic outcome diff. The suite's stated scope is
  "edit drives them correctly and shapes their outcomes into the tool contract." Cases here are still
  semantic where a caller can observe them.
- **Several older refusals were deliberately retired** and the tests rewritten to assert the
  replacement behavior (CRLF preservation → unconditional LF; the double-quote emit refusal →
  round-trip; the newline-in-value refusal → the value lands inside its own key). Comments say so
  explicitly. Note that `edit.ps1`'s own `.PARAMETER frontmatter_ops` prose still claims line
  break / control char / `"` are `malformed_op` — **the prose is stale relative to the tests**
  (EDT-069, EDT-072).
- **The projection seam.** `edit` projects the substrate's op vocabulary into a hand-authored JSON
  Schema side-car. The suite guards this *bidirectionally and derivationally* — no op name is
  hand-listed anywhere in the test file. EDT-081..EDT-092 are the projection rules themselves.
- Out-of-scope tools (`lint`, `sync`, `lint_suppressions`) are mentioned only in passing in the
  script's synopsis; no case in this area is about them.

## Cases

### EDT-001 — A frontmatter op returns one well-formed JSON object

- **Behavior:** A successful edit returns a single JSON object carrying `path` (echoed as supplied),
  `frontmatterChanged`, `bodyChanged`, `opsApplied[]`, and `resultingSha` (64 lowercase hex chars).
- **Trigger:** `set_field doc.type=Corpus` against a note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:273` — `returns a single well-formed JSON object for a frontmatter op`
- **Note:** The same case pins the byte-exact result: only the one value changed, block and body
  otherwise identical.

### EDT-002 — A frontmatter-only op reports bodyChanged false

- **Behavior:** `frontmatterChanged` and `bodyChanged` are independent booleans reflecting which
  facets actually moved, not which were addressed.
- **Trigger:** A `set_field` op with no body facet.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:273` — `returns a single well-formed JSON object for a frontmatter op`

### EDT-003 — opsApplied echoes op name, field, and a per-op changed flag

- **Behavior:** Each element of `opsApplied` carries at least `op`, the `field` it addressed, and
  `changed`.
- **Trigger:** Any frontmatter op.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:273` — `returns a single well-formed JSON object for a frontmatter op`

### EDT-004 — capabilitiesUsed and filesSkippedUnreadable are always present arrays

- **Behavior:** The write-class envelope always emits `capabilitiesUsed` and
  `filesSkippedUnreadable` as arrays, serialized as `[]` when empty rather than omitted or null.
- **Trigger:** Any successful edit.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:289` — `emits capabilitiesUsed and filesSkippedUnreadable as arrays, [] when empty`
- **Note:** Asserted against the raw JSON *text* (`"capabilitiesUsed":\s*\[\]`), so the empty-array
  serialization is the assertion, not just the parsed shape.

### EDT-005 — filesSkippedUnreadable is structurally always empty for edit

- **Behavior:** `edit` never reports a skipped file. It is single-target, so a missing or unreadable
  sole target is `not_found` / `read_failure`, never a skip.
- **Trigger:** Any edit.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:289` — `emits capabilitiesUsed and filesSkippedUnreadable as arrays, [] when empty`
- **Note:** The field exists solely for envelope uniformity across write-class tools.

### EDT-006 — lockWarning is omitted entirely when the lock was not abandoned

- **Behavior:** `lockWarning` is absent from the return — not present-and-null — unless an abandoned
  mutex was inherited. An always-present null would read as "checked and clear".
- **Trigger:** A normal edit with an uncontended lock.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:300` — `omits lockWarning entirely when the lock was not abandoned`
- **Note:** No case here exercises the *present* branch of `lockWarning`; the abandoned-mutex path is
  unproven by this suite.

### EDT-007 — capabilitiesUsed echoes agentic:write when the agentic layer moved

- **Behavior:** When the write changed an `agent.*` / `nav.*` key, `capabilitiesUsed` contains
  exactly `agentic:write`.
- **Trigger:** `add_hint` against a note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:306` — `echoes agentic:write in capabilitiesUsed when the agentic layer moved`

### EDT-008 — resultingFrontmatter is byte-identical to what read returns for the frontmatter part

- **Behavior:** The `resultingFrontmatter` field is the raw frontmatter block text (fences excluded),
  byte-identical to what a follow-up `read part: frontmatter` would return, so a caller can verify
  the outcome without a second call.
- **Trigger:** Any frontmatter op.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:311` — `returns resultingFrontmatter byte-identical to read part: frontmatter`
- **Note:** The stated motivation is that several ops are silently idempotent, so the caller needs the
  resulting state echoed. Also pins that a new `set_field` key is **appended** to the end of the
  block, and that the block text carries no trailing newline.

### EDT-009 — A whitespace-padded opening fence still opens a block

- **Behavior:** A fence line is recognized on its *trimmed* text, so `"--- "` opens a frontmatter
  block exactly as `"---"` does; read and edit see the same block extent.
- **Trigger:** A note whose first line is `"--- "`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:329` — `returns resultingFrontmatter matching read on a whitespace-padded opening fence`

### EDT-010 — Fence padding is not preserved through a frontmatter op

- **Behavior:** Once a frontmatter op re-emits the block, the fence is written bare (`---`); the
  original padding is gone. Recognizing padding is required; keeping it is not.
- **Trigger:** A frontmatter op on a note with a padded fence.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:329` — `returns resultingFrontmatter matching read on a whitespace-padded opening fence`

### EDT-011 — A whitespace-padded closing fence closes the block and is consumed whole

- **Behavior:** `"--- "` as a closing line closes the block, and the whole line is consumed — no
  `" \n"` fragment leaks into the body facet.
- **Trigger:** A note whose closing fence is `"--- "`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:347` — `returns resultingFrontmatter matching read on a whitespace-padded closing fence`
- **Note:** The body-facet cleanliness is asserted separately from the opening-fence case because it
  is a different code path.

### EDT-012 — A fence is a whole line, not a prefix: an inner "---junk" line is block text

- **Behavior:** `"---junk"` is not a fence, so a block containing it runs on to the next real `---`
  and every intervening line, `"---junk"` included, is block text. `read` reports it as such.
- **Trigger:** `---` / `doc.type: Note` / `---junk` / `more` / `---` / `Body`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:362` — `agrees with read that an inner line starting with "---" is block text, and refuses it`

### EDT-013 — A block that is not parseable YAML is refused frontmatter_integrity / frontmatter_unparsed

- **Behavior:** `edit` refuses to write a note whose frontmatter block exists but does not parse as
  YAML, with `frontmatter_integrity` and `data.reason = "frontmatter_unparsed"`. The note is left
  byte-identical so it can be repaired.
- **Trigger:** A frontmatter op on the `---junk` note above.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:362` — `agrees with read that an inner line starting with "---" is block text, and refuses it`
- **Note:** The parity intent is explicit: read and edit agree on the block's *extent*; only edit goes
  on to parse it, so divergence in outcome is the contract holding, not breaking.

### EDT-014 — A "----" line is not a fence, so an opening "---" it "closes" closes nothing

- **Behavior:** A four-dash line does not close a block. A `---` that nothing closes is a thematic
  break, not an opening fence, so the note has **no frontmatter at all** and `read` returns the whole
  file as body with an empty frontmatter part.
- **Trigger:** `---` / `doc.type: Note` / `----` / `Body`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:385` — `agrees with read that a "----" closing line closes nothing`
- **Note:** There is **no note-level "unclosed fence" state** in this design — that concept was
  retired. See EDT-015.

### EDT-015 — apparent_fence: refusing to prepend a block above the user's apparent metadata

- **Behavior:** When a note has no frontmatter block but *looks* like it has one (leading `---` with
  metadata-shaped lines below), a frontmatter op is refused `frontmatter_integrity` with
  `data.reason = "apparent_fence"` rather than prepending a new block that would strand the apparent
  metadata below it as prose.
- **Trigger:** Any frontmatter op on such a note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:385` and `:1079` — `errors frontmatter_integrity on a note whose first line is a fence nothing closes`
- **Note:** Two distinct reason values live under `frontmatter_integrity`: `apparent_fence` and
  `frontmatter_unparsed` (EDT-013). A redesign that collapses them, or that reintroduces an
  `unclosed_fence` reason, diverges.

### EDT-016 — A note with no frontmatter block reports an empty resultingFrontmatter

- **Behavior:** For a body-only write against a note carrying no frontmatter block, `resultingFront
  matter` is the empty string and `frontmatterChanged` is false while `bodyChanged` is true.
- **Trigger:** A patch against `Inbox/Note-Loose-Thought.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:402` — `returns an empty resultingFrontmatter for a note that has no frontmatter block`

### EDT-017 — A frontmatter op creates the block when the note has none

- **Behavior:** An op that needs a frontmatter block on a note that has none creates the block (and
  the fences), returning a non-empty `resultingFrontmatter`.
- **Trigger:** `add_directive use_skill` against `Projects/Widget/Tasks.md` (fence-less).
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:402` — `returns an empty resultingFrontmatter for a note that has no frontmatter block`
- **Note:** Contrast EDT-015: creation is fine on a note with no *apparent* metadata; it is only
  refused when a leading `---` makes prepending destructive.

### EDT-018 — opsApplied preserves array order with per-op changed flags

- **Behavior:** `opsApplied` is reported in the caller's submitted order, one element per submitted
  op, each with its own `changed` flag — so a mixed applied/no-op array is fully attributable.
- **Trigger:** `[set_field doc.type, remove_field doc.archived]` where the second addresses nothing.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:414` — `reports opsApplied in order with per-op changed flags`

### EDT-019 — A whole-body replacement replaces everything after the closing fence

- **Behavior:** The `body` facet replaces the entire body — everything after the frontmatter block's
  closing fence — leaving the block untouched. It requires `expected_sha`.
- **Trigger:** `-Body "# Replaced\n" -expected_sha <current>`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:425` — `replaces the whole body when body is supplied with expected_sha`
- **Note:** Byte-exact result shows the body begins immediately after the closing fence with no
  inserted blank line — the supplied string *is* the body verbatim.

### EDT-020 — An empty body string truncates the body; unset leaves it alone

- **Behavior:** `body = ""` truncates the body to nothing, leaving the frontmatter block and its
  closing fence intact. An *unsupplied* body leaves the body untouched. The two are distinguishable.
- **Trigger:** `-Body '' -expected_sha <current>`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:433` — `truncates the body on an empty body string and leaves the block intact`
- **Note:** Implementation detail (read from `edit.ps1:195-202`) worth carrying: a parameter bound to
  an explicit JSON **null** counts as *unsupplied*, not as empty. That three-way distinction
  (absent / null / empty) applies to `body`, `old_string`, `new_string`; `expected_sha` treats
  whitespace-only as unsupplied.

### EDT-021 — A patch and frontmatter ops apply in the same call

- **Behavior:** One call may carry both frontmatter ops and one body facet; both apply, both
  `Changed` flags come back true, and the file is written once.
- **Trigger:** `-frontmatter_ops [set_field] -old_string 'alpha line' -new_string 'ALPHA LINE'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:439` — `applies a patch and a frontmatter op in one call`

### EDT-022 — An empty new_string deletes the matched span

- **Behavior:** `new_string = ""` with a matching `old_string` deletes exactly the matched span,
  including a trailing newline if the anchor included one.
- **Trigger:** `-old_string "alpha line\n" -new_string ''`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:448` — `deletes the matched span when new_string is empty`

### EDT-023 — A patch anchor matching nothing is patch_target_not_found with zero mutation

- **Behavior:** Zero matches for `old_string` in the body is `patch_target_not_found`, carrying
  `data.oldString` (the anchor as supplied), and nothing is written.
- **Trigger:** `-old_string 'gamma line'` against a body that lacks it.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:455` — `errors patch_target_not_found when old_string matches nothing`

### EDT-024 — A patch anchor matching more than once is patch_ambiguous with zero mutation

- **Behavior:** More than one match is `patch_ambiguous`, carrying `data.matchCount` (> 1), and
  nothing is written. The tool refuses rather than picking one; the caller widens the anchor.
- **Trigger:** `-old_string 'repeat'` against a body carrying `repeat` twice.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:463` — `errors patch_ambiguous when old_string matches more than once`
- **Note:** `matchCount` is asserted only as `> 1`, not as an exact count — the implementation
  (`Measure-VaultEditPatchMatch`, `edit.ps1:217`) stops counting at two, so the reported number may
  be a saturating 2 rather than the true total.

### EDT-025 — A patch cannot reach the frontmatter

- **Behavior:** An anchor that is unique *within the frontmatter block* and absent from the body is
  `patch_target_not_found`. The patch facet searches the body facet only.
- **Trigger:** `-old_string 'doc.type: Note'` against a note where that text lives only in the block.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:471` — `cannot reach the frontmatter: an anchor unique in the block is not_found for a patch`
- **Note:** Stated rationale: body-confinement is what keeps every frontmatter mutation on the
  schema-validated, capability-classified ops path. This is a security-shaped invariant, not a
  convenience.

### EDT-026 — A body occurrence matches even when the same text is also in the frontmatter

- **Behavior:** Text appearing once in the frontmatter and once in the body patches successfully —
  it is exactly one match, because the frontmatter copy is outside the searched facet. The
  frontmatter copy is left untouched.
- **Trigger:** A note with `doc.type: Note` in both the block and the body.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:478` — `matches a body occurrence even when the same text also appears in the frontmatter`
- **Note:** The other direction of EDT-025: confinement must not merely refuse, it must also not
  count block occurrences toward ambiguity.

### EDT-027 — A duplicate add_directive is a no-op reporting changed:false

- **Behavior:** Adding a directive entry that already exists verbatim succeeds with
  `opsApplied[0].changed = false` and `frontmatterChanged = false`, and the file is byte-identical.
- **Trigger:** `add_directive pinned "Never exfil this"` on a note already carrying it.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:487` — `records changed:false for a duplicate add_directive`

### EDT-028 — An idempotent agentic op asserts no capability

- **Behavior:** An op that would be agentic but changes nothing produces an **empty**
  `capabilitiesUsed`. Classification is outcome-based (measured over the composed diff), not
  intent-based.
- **Trigger:** The duplicate `add_directive` above.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:487` — `records changed:false for a duplicate add_directive`
- **Note:** Reinforced independently for nav ops at `:569` and `:578`. A redesign classifying on the
  op *name* would diverge here and nowhere else visible.

### EDT-029 — remove_field against an absent-but-recognized key is changed:false

- **Behavior:** Removing a registry-recognized field the note does not carry succeeds as a no-op with
  `changed: false` and `frontmatterChanged: false`.
- **Trigger:** `remove_field doc.archived` on a note without it.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:497` — `records changed:false for a remove_field against an absent field`
- **Note:** The comment draws the distinction sharply: an *unrecognized* key is **refused**, not
  reported absent, "because removal is a write and the tooling does not delete what it cannot
  author." See EDT-071.

### EDT-030 — An unmatched pinned strip is changed:false and echoes its kind

- **Behavior:** `strip_directive pinned` naming text no entry carries is a no-op with
  `changed: false`; the file is byte-identical. `opsApplied[0].kind` echoes `pinned`.
- **Trigger:** `strip_directive pinned "Not present"` against a note carrying a different pinned rule.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:506` — `records changed:false for an unmatched pinned strip`

### EDT-031 — add_nav_entry through the tool: opsApplied shape and resultingFrontmatter echo

- **Behavior:** `add_nav_entry` reports `op`, `field` = the nav key addressed (`nav.contents`), and
  `changed: true`; `frontmatterChanged` is true; and `resultingFrontmatter` echoes the inserted
  `link`, `description` and `load_when` lines, matching what landed on disk.
- **Trigger:** `add_nav_entry key=nav.contents link=[[Gizmo-Reference]] description=... load_when=...`
  against `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:522` — `applies add_nav_entry through edit.ps1: opsApplied shape and resultingFrontmatter echo the inserted entry`

### EDT-032 — opsApplied omits `kind` entirely for an op that carries none

- **Behavior:** `kind` is per-op optional in `opsApplied` — an op with no kind omits the property
  rather than emitting an empty string or null the caller has to interpret.
- **Trigger:** Any `add_nav_entry` / `set_field` result element.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:522` — `applies add_nav_entry through edit.ps1: ...`
- **Note:** Parallel to EDT-006's absent-vs-null reasoning; this design consistently omits rather than
  nulls.

### EDT-033 — Values are quoted on need, by the emitter, not by the caller

- **Behavior:** The YAML emitter decides quoting: a wikilink is single-quoted because a leading `[`
  is the flow-sequence indicator (`link: '[[Gizmo-Reference]]'`), while ordinary prose is written
  unquoted (`description: Reference for gizmo parts`). The caller never supplies quoting.
- **Trigger:** `add_nav_entry` with a wikilink and a prose description.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:522` — `applies add_nav_entry through edit.ps1: ...`
- **Note:** Single quotes, specifically. The suite's `BeforeAll` comment (`:29`) says several
  assertions deliberately read the *parsed tree* rather than emitted text precisely because the
  emitter now owns quoting — so the exact quote character is the softer half of this case, but it is
  asserted literally here.

### EDT-034 — add_nav_entry then remove_nav_entry restores the frontmatter byte for byte

- **Behavior:** Adding a nav entry and then removing it by the same target returns the frontmatter to
  exactly its prior bytes. `remove_nav_entry` reports `field` = the nav key and `changed: true`.
- **Trigger:** An add/remove pair against `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:545` — `round-trips remove_nav_entry: removing the added entry restores the frontmatter byte for byte`
- **Note:** Important secondary behavior the test structure encodes: **the first frontmatter op on any
  note reformats the whole block**, because the op re-emits it through the one composer. Emit is
  idempotent, so this is a one-time change. The test runs a warm-up add/remove cycle before
  baselining, which is the only way the round-trip claim is about the round trip rather than about the
  fixture's authored bytes. A redesign preserving authored quoting would diverge.

### EDT-035 — A duplicate add_nav_entry is a no-op and does not update the existing entry

- **Behavior:** `add_nav_entry` is idempotent **on the target value**: adding an entry whose link
  already exists is `changed: false` with an empty `capabilitiesUsed`, even when the submitted
  `description` differs from the existing one. The existing entry is not updated.
- **Trigger:** `add_nav_entry link=[[Widget/Tasks]] description="a different description"` where
  `[[Widget/Tasks]]` is already present.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:569` — `records changed:false through the tool for a duplicate add_nav_entry, and asserts nothing`
- **Note:** The schema states the consequence: rewriting an entry is a remove plus an add in the same
  ordered array. A redesign making `add` an upsert would silently pass a naive test and break this.

### EDT-036 — remove_nav_entry against an absent target is a no-op

- **Behavior:** Removing a nav entry whose target matches nothing is `changed: false`,
  `frontmatterChanged: false`, empty `capabilitiesUsed`.
- **Trigger:** `remove_nav_entry key=nav.contents folder=Nowhere`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:578` — `records changed:false through the tool for an absent-target remove_nav_entry, and asserts nothing`

### EDT-037 — frontmatter_ops is a JSON *string* parameter

- **Behavior:** The ops arrive as a serialized JSON string, not as a structured array parameter. A
  string produced by ordinary object serialization is accepted.
- **Trigger:** `ConvertTo-Json`-built ops string.
- **Kind:** `artifact`
- **Source:** `tools/Tests/edit.Tests.ps1:587` — `accepts the ops as a JSON string built via ConvertTo-Json - frontmatter_ops's actual parameter shape`
- **Note:** Marked artifact because the string-vs-array parameter typing is a transport decision of
  this design. But the *consequences* — `malformed_op` on unparseable JSON, on a bare object, and the
  need to distinguish a one-element array from an object — are semantic and harvested separately
  (EDT-064..066).

### EDT-038 — add_nav_entry accepts a folder target and nav.see_also as a key

- **Behavior:** A nav entry may target a folder rather than a link, and `nav.see_also` is an
  addressable key alongside `nav.contents`. The written entry carries `folder: Archive`.
- **Trigger:** `add_nav_entry key=nav.see_also folder=Archive description="Completed work"`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:587` — `accepts the ops as a JSON string built via ConvertTo-Json ...`

### EDT-039 — expected_sha mismatch is state_mismatch with zero mutation

- **Behavior:** When the file's current digest differs from `expected_sha`, the call fails
  `state_mismatch` carrying `data.expectedSha` and `data.actualSha`, and the intervening writer's
  content survives verbatim.
- **Trigger:** A body replace with a digest taken before another writer landed.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:693` — `errors state_mismatch with zero mutation when the file changed underneath the caller`
- **Note:** The script's help adds that recovery is a re-read, never a retry with the reported
  `actualSha` — the digest is reported for diagnosis, not as a token to replay.

### EDT-040 — expected_sha is honoured on a non-body facet too

- **Behavior:** `expected_sha` is optional on frontmatter ops and on patches, but when supplied it is
  checked there as well.
- **Trigger:** A frontmatter op with a matching `expected_sha`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:707` — `accepts a matching expected_sha on a non-body facet too`

### EDT-041 — resultingSha chains: it is the digest of what is now on disk

- **Behavior:** A second edit may pass the first edit's `resultingSha` as its `expected_sha` with no
  interleaved read and succeed. `resultingSha` is byte-identical to an independently computed SHA-256
  over the file's raw bytes, and changes when the file changes.
- **Trigger:** Two consecutive body replaces, the second chaining the first's `resultingSha`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:713` — `chains: a second edit passes the first edit's resultingSha with no interleaved read`
- **Note:** `expected_sha` and `resultingSha` and `read part: sha` are one interchangeable token —
  SHA-256 over raw file bytes, lowercase hex, no decoding or newline translation in the digest.

### EDT-042 — A stale chained resultingSha is refused after another edit lands

- **Behavior:** Reusing a `resultingSha` after a third-party edit intervened is `state_mismatch` —
  the chain is invalidated by any write, including one made through the tool itself.
- **Trigger:** Body replace → frontmatter op → body replace reusing the first's `resultingSha`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:723` — `errors state_mismatch when a chained resultingSha is reused after another edit landed`

### EDT-043 — A frontmatter op normalizes a CRLF note to LF throughout

- **Behavior:** A frontmatter op recomposes the whole note through one composer, which writes LF
  unconditionally. A CRLF note comes back with **no CR anywhere** — frontmatter and body alike.
- **Trigger:** A `set_field` on an all-CRLF note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:733` — `normalizes a CRLF note to LF throughout when a frontmatter op recomposes it`
- **Note:** Explicitly a **retired-and-replaced** assertion: this used to assert CRLF *survived*. LF
  normalization is now locked policy, not a regression. What is preserved is body **content** — every
  byte except the terminators. A reconciler seeing a redesign preserve CRLF should treat that as a
  divergence from the last agreed policy, not as a fix.

### EDT-044 — A body patch also flattens the whole file to LF, frontmatter included

- **Behavior:** LF normalization is unconditional and reaches even the frontmatter block that a
  body-only write does not otherwise touch. A body splice does not preserve the terminators it found.
- **Trigger:** A patch against an all-CRLF note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:749` — `flattens a CRLF note to LF through a body patch, frontmatter included`
- **Note:** Called out as the path that would most plausibly have been left preserving what arrived,
  since it never reaches the emitter — which is exactly why it is pinned.

### EDT-045 — A mixed-ending note is flattened to LF, not kept locally consistent

- **Behavior:** A note mixing CRLF and LF is written back all-LF. There is no per-terminator
  consistency rule.
- **Trigger:** A `set_field` on a note with mixed terminators.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:761` — `flattens a mixed-ending note to LF through a frontmatter op`
- **Note:** Also retired-and-replaced; the old rule (keep a mixed file locally consistent, terminator
  by terminator) was the most intricate part of the model that was deliberately dropped.

### EDT-046 — An agentic op is denied insufficient_capability with zero disk change

- **Behavior:** When the injected op-class set lacks `agentic:write`, an op whose outcome moves an
  agentic key fails `insufficient_capability` with `data.requiredClass = "agentic:write"`, and the
  file's bytes are unchanged. The classification happens in the validate phase.
- **Trigger:** `add_hint` with `VaultMcpOpClasses = @('content:write')`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:775` — `denies an agentic op with insufficient_capability and zero disk change when the key is absent`

### EDT-047 — A nav.* removal is denied for the same reason

- **Behavior:** `nav.*` keys are in the agentic layer: `remove_field nav.contents` without
  `agentic:write` is `insufficient_capability`, zero disk change.
- **Trigger:** `remove_field nav.contents` against `Widget.md` with only `content:write`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:785` — `denies a nav.* removal for the same reason`

### EDT-048 — The denial carries agenticKeysChanged naming the keys the outcome moved

- **Behavior:** `insufficient_capability` carries `data.agenticKeysChanged` — the list of agentic
  keys the composed diff would have moved (e.g. `nav.contents`) — so the denial is attributable.
- **Trigger:** `add_nav_entry` without `agentic:write`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:794` — `denies add_nav_entry with insufficient_capability and zero disk change when the class set lacks agentic:write`
- **Note:** The comment makes the design explicit: "the outcome diff is what asserts the key, not the
  op name" — `add_nav_entry` moves `nav.contents` exactly as a generic `set_field` against it would.

### EDT-049 — remove_nav_entry is denied the same way

- **Behavior:** `remove_nav_entry` without `agentic:write` is `insufficient_capability`,
  `requiredClass = agentic:write`, zero disk change.
- **Trigger:** `remove_nav_entry key=nav.contents link=[[Widget/Tasks]]` with only `content:write`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:807` — `denies remove_nav_entry the same way, with zero disk change`

### EDT-050 — The nav denial is not vacuous: the same op succeeds once the class is granted

- **Behavior:** Granting `agentic:write` lets the identical `add_nav_entry` through, with
  `capabilitiesUsed = ['agentic:write']` and `changed: true`.
- **Trigger:** Same op with `VaultMcpOpClasses = @('content:write','agentic:write')`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:817` — `proves the nav-entry denial is not vacuous: the same add_nav_entry succeeds once the class is granted`
- **Note:** Deliberate control case — without it, a denial actually caused by a bad op shape would
  read identically to a real capability gate.

### EDT-051 — An injected-but-empty class set denies (authenticated "holds nothing")

- **Behavior:** An empty injected class set is a *present* set holding nothing, and denies. It is not
  treated as "no host context".
- **Trigger:** `VaultMcpOpClasses = @()` with `add_hint`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:826` — `DENIES on an injected-but-empty class set (authenticated "holds nothing")`
- **Note:** The sharp pair with EDT-054 (no injected set at all degrades **open**). Empty-set-denies
  vs absent-set-allows is exactly the distinction a reimplementation is most likely to collapse.

### EDT-052 — A body-only write never asserts the agentic key

- **Behavior:** A whole-body replacement produces an empty `capabilitiesUsed` even with an empty
  injected class set — a body write cannot move an agentic key, so nothing is asserted and nothing is
  denied.
- **Trigger:** `-Body ... -expected_sha ...` with `VaultMcpOpClasses = @()`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:838` — `never asserts the key for a body-only write, even with an empty injected set`

### EDT-053 — A non-agentic doc.* scalar set never asserts the agentic key

- **Behavior:** `set_field doc.type` succeeds with an empty class set and empty `capabilitiesUsed`.
  The agentic layer is `agent.*` / `nav.*` only.
- **Trigger:** `set_field doc.type=Corpus` with `VaultMcpOpClasses = @()`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:845` — `never asserts the key for a non-agentic doc.* scalar set`

### EDT-054 — With no injected class set at all, the agentic assertion degrades open

- **Behavior:** A standalone run with no host context (no injected op-class set) allows an agentic op
  and still reports `capabilitiesUsed = ['agentic:write']`. The write lands.
- **Trigger:** `pwsh -File edit.ps1 ... add_hint` in a separate process.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1338` — `degrades OPEN on an agentic op with no injected op-class set`
- **Note:** Explicitly parallel to how an unscoped scope-roots map behaves. `capabilitiesUsed` is
  reported regardless of whether anything gated it — it is an outcome record, not a permission record.

### EDT-055 — A missing note is not_found; edit never creates

- **Behavior:** Editing a path that does not exist is `not_found` with `data.path` echoing the
  supplied path. Creation is a different tool's job.
- **Trigger:** Any facet against `Inbox/Nonexistent-Note.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:853` — `errors not_found on a missing note (edit never creates)`

### EDT-056 — A folder target is kind_mismatch

- **Behavior:** `edit` is note-semantic: a path resolving to a folder fails `kind_mismatch` with
  `data.requiredKind = "note"` and `data.resolvedKind = "folder"`.
- **Trigger:** `-Path 'Projects/Widget'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:859` — `rejects a folder target as kind_mismatch (edit is note-semantic)`

### EDT-057 — An existing non-.md file is kind_mismatch

- **Behavior:** A path resolving to an existing file that is not a `.md` note is `kind_mismatch` with
  `data.resolvedKind = "file"`.
- **Trigger:** `-Path 'Inbox/attachment.txt'` against a real text file.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:866` — `rejects an existing non-md file as kind_mismatch`

### EDT-058 — An extensionless path is invalid_argument naming the .md form

- **Behavior:** There is no `.md` inference. An extensionless path fails `invalid_argument`, with the
  message naming the `.md` form and `data.suggestedPath` carrying it, so the miss is retryable in one
  call rather than an ambiguous `not_found`.
- **Trigger:** `-Path 'Inbox/Edit-Case'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:873` — `refuses an extensionless path as invalid_argument naming the .md form`
- **Note:** Shared Path Convention with `read`. The script's help adds the normalization rules that
  *do* apply: backslashes fold to `/`, a leading `/` is trimmed, and the path is otherwise verbatim.

### EDT-059 — A whitespace-only path is invalid_argument

- **Behavior:** A path that is only whitespace is `invalid_argument`.
- **Trigger:** `-Path '   '`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:882` — `errors invalid_argument on a whitespace path`

### EDT-060 — A vault-escaping path is invalid_argument

- **Behavior:** A path that resolves outside the vault root is `invalid_argument`.
- **Trigger:** `-Path '../outside.md'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:887` — `errors invalid_argument on a vault-escaping path`

### EDT-061 — A call supplying no facet is invalid_argument ("Nothing to do")

- **Behavior:** A call with a path and no frontmatter ops, no body, and no patch pair is
  `invalid_argument` with a message matching "Nothing to do". It is not a successful no-op.
- **Trigger:** `-Path <note>` alone.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:892` — `errors invalid_argument when no facet is supplied`

### EDT-062 — An empty ops array with no body form is invalid_argument

- **Behavior:** `frontmatter_ops = '[]'` does not count as a supplied facet; with no body form it is
  `invalid_argument`, same as supplying nothing.
- **Trigger:** `-frontmatter_ops '[]'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:898` — `errors invalid_argument for an empty ops array with no body form`

### EDT-063 — body and the patch pair are mutually exclusive

- **Behavior:** Supplying `body` together with `old_string`/`new_string` is `invalid_argument` with a
  message matching "mutually exclusive". At most one body facet per call.
- **Trigger:** `-Body ... -expected_sha ... -old_string ... -new_string ...`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:903` — `errors invalid_argument when body accompanies a patch pair`

### EDT-064 — Half a patch pair is invalid_argument, naming the missing half

- **Behavior:** `old_string` without `new_string` is `invalid_argument` with a message naming
  `new_string`; `new_string` without `old_string` is `invalid_argument` naming `old_string`.
- **Trigger:** Either parameter supplied alone.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:910` and `:916` — `errors invalid_argument on half a patch pair (old_string alone)` / `(new_string alone)`
- **Note:** Two `It` blocks, one rule, but the *message names the missing counterpart* in each
  direction — that specificity is the harvestable part.

### EDT-065 — An empty old_string is invalid_argument (an anchor that cannot self-verify)

- **Behavior:** `old_string = ""` with a `new_string` is `invalid_argument` with a message matching
  "old_string is empty". An empty anchor cannot satisfy the exactly-once rule, so it is refused up
  front rather than treated as an insertion.
- **Trigger:** `-old_string '' -new_string 'x'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:922` — `errors invalid_argument on an empty old_string (an anchor that cannot self-verify)`

### EDT-066 — body without expected_sha is invalid_argument

- **Behavior:** The whole-body replacement facet **requires** `expected_sha`; omitting it is
  `invalid_argument` with a message naming `expected_sha`. Rationale: a whole-body replace is the one
  facet that can destroy content the caller never saw.
- **Trigger:** `-Body "# X\n"` with no digest.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:928` — `errors invalid_argument when body is supplied without expected_sha`

### EDT-067 — expected_sha is NOT required for a patch or for frontmatter ops

- **Behavior:** A patch and a frontmatter op each succeed with no `expected_sha`. A patch
  self-verifies through its exactly-once anchor; frontmatter ops are surgical.
- **Trigger:** A patch, and a `set_field`, each with no digest.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:934` — `does NOT require expected_sha for a patch or for frontmatter ops`
- **Note:** The complement of EDT-066 and the reason the requirement is per-facet rather than
  tool-wide. A redesign requiring the digest everywhere would be strictly safer and still divergent.

### EDT-068 — An existing but unreadable note is read_failure, not not_found

- **Behavior:** When the target exists but cannot be opened (another process holds it exclusively),
  the error is `read_failure` with `data.path`, distinct from `not_found`.
- **Trigger:** An exclusive `FileShare.None` handle held over the note during the edit.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:939` — `errors read_failure (not not_found) when the note exists but is locked`
- **Note:** Windows-shaped trigger, but the distinction (exists-but-unreadable ≠ absent) is portable.

### EDT-069 — Unparseable frontmatter_ops JSON is malformed_op

- **Behavior:** A `frontmatter_ops` string that is not valid JSON is `malformed_op` with
  `data.reason` mentioning "valid JSON".
- **Trigger:** `-frontmatter_ops '{not json'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:953` — `errors malformed_op on frontmatter_ops that is not valid JSON`

### EDT-070 — A bare op object rather than an array is malformed_op

- **Behavior:** `frontmatter_ops` must be a JSON **array**. A bare object is `malformed_op` with
  `data.reason` mentioning "array", distinguishable from a one-element array.
- **Trigger:** `-frontmatter_ops '{"op":"set_field",...}'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:959` — `errors malformed_op when frontmatter_ops is a bare object rather than an array`

### EDT-071 — A one-element ops array is accepted as an array

- **Behavior:** A single-element array is not collapsed to a bare object; it applies normally with
  `opsApplied.Count = 1`.
- **Trigger:** `-frontmatter_ops '[{...}]'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:966` — `accepts a one-element ops array (not collapsed to a bare object)`
- **Note:** The pair EDT-070/EDT-071 is a real interoperability hazard in any language with array
  unwrapping; the suite calls out `-NoEnumerate` as what keeps them distinguishable, and a comment at
  `:221` records that the same class of bug ("the -AsArray bug") was retired once before.

### EDT-072 — An unknown op is malformed_op with opIndex and the offending name

- **Behavior:** An unrecognized `op` fails `malformed_op` carrying `data.opIndex` (the 0-based
  position in the submitted array) and a `data.reason` naming the unknown op. Earlier valid ops in the
  same array do not apply — the failure is whole-call.
- **Trigger:** `[{set_field ...},{op:"reticulate_splines"}]` — opIndex 1.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:971` — `errors malformed_op with data.opIndex on an unknown op`

### EDT-073 — An op missing a required parameter is malformed_op with opIndex

- **Behavior:** An op omitting a member its shape requires is `malformed_op` with `data.opIndex`.
- **Trigger:** `[{"op":"set_field","field":"doc.type"}]` (no `value`) — opIndex 0.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:981` — `errors malformed_op with data.opIndex on an op missing a required parameter`

### EDT-074 — A value carrying a line break lands inside its own key and mints no second key

- **Behavior:** A scalar value containing a newline is written as part of that key's value; it does
  **not** create a second frontmatter key, and it reads back exactly as submitted.
- **Trigger:** `set_field scope = "personal\nnav.contents: smuggled"` (a key whose registry pattern
  permits any string).
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:987` — `writes a value carrying a line break INSIDE its own key rather than minting one`
- **Note:** **Retired-and-replaced twice.** It previously asserted a *refusal* that existed only
  because a line-splicing emitter could not escape a newline. `edit.ps1`'s own `.PARAMETER
  frontmatter_ops` prose and its `set_field.value` schema description **still say** a line break is
  `malformed_op` — the documentation is stale relative to the tests. A reconciler should decide which
  is the intended contract before treating either as a gap.

### EDT-075 — A set_field value is enforced against its key's registry pattern

- **Behavior:** Values, not just key names, are validated against the key registry. A value failing
  its key's pattern is `malformed_op`.
- **Trigger:** `set_field doc.type = "Note\nnav.contents: smuggled"` — `doc.type` has a constrained
  pattern.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1004` — `refuses a set_field value that fails its key's registry pattern`
- **Note:** Stated rationale is explicitly a security one: "a caller blocked from inventing a key must
  not be able to put arbitrary text into a legitimate one." Together with EDT-074 this is the injection
  boundary — permissive keys let newlines through safely because the emitter escapes them; constrained
  keys refuse the value outright.

### EDT-076 — An unrecognized key already in a note is left intact and stays unwritable

- **Behavior:** `edit` validates only the keys an op **touches**. A note carrying an unregistered key
  (e.g. Obsidian's `obsidianUIMode`) stays editable, keeps that key verbatim through the rewrite, and
  the key itself remains unwritable — both `set_field` and `remove_field` against it are
  `malformed_op`, and each refusal is zero-mutation.
- **Trigger:** A note with `obsidianUIMode: preview`; `set_field doc.type` succeeds, ops naming
  `obsidianUIMode` are refused.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1012` — `leaves an unrecognized key already in the note intact, and still refuses to write it`
- **Note:** "The extent split." This is the rule that makes the registry usable against a real vault
  containing foreign metadata. A redesign validating the whole resulting block against the registry
  would render six real `Tasks/` notes unwritable.

### EDT-077 — Directive text carrying a double quote round-trips

- **Behavior:** A `"` inside directive text is accepted, survives the write, and reads back exactly as
  submitted (`bad" text`), as a single entry.
- **Trigger:** `add_directive pinned text='bad" text'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1030` — `round-trips directive text carrying a double quote (the retired emit hole)`
- **Note:** Also retired-and-replaced: the old double-quote refusal was a bespoke format's limitation
  leaking into the tool contract. Same stale-documentation caveat as EDT-074 — the schema descriptions
  still advertise the refusal.

### EDT-078 — A field name outside the frontmatter key pattern is malformed_op

- **Behavior:** Field names are restricted to letters, digits, `.` and `_`. A name with a space is
  `malformed_op`.
- **Trigger:** `set_field field="doc created"`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1045` — `refuses a field name outside the frontmatter key pattern`

### EDT-079 — A generic remove_field against agent.instructions is protected_directive_violation

- **Behavior:** `agent.instructions` is reachable only through the directive ops. A generic
  `remove_field` against it is `protected_directive_violation` with `data.field =
  "agent.instructions"` and a message naming `strip_directive` as the correct op.
- **Trigger:** `remove_field agent.instructions`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1050` — `errors protected_directive_violation for a generic remove_field against agent.instructions`
- **Note:** The message routing the caller to the right op is asserted, not incidental. Rationale in
  the schema: dropping one of the user's binding rules has to name it.

### EDT-080 — protected_directive_violation takes precedence over field_shape_mismatch

- **Behavior:** When a call would trigger both, `protected_directive_violation` wins. Naming the right
  op (routing) is more actionable than naming the shape.
- **Trigger:** `set_field agent.instructions = "x"` against a note where `agent.instructions` is a
  block list — both rules apply.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1058` — `takes protected_directive_violation over field_shape_mismatch when both would fire`
- **Note:** An explicit error-precedence rule. Precedence between error identities is exactly the kind
  of thing a spec-derived suite tends to omit.

### EDT-081 — A flat-scalar set against a detected block field is field_shape_mismatch

- **Behavior:** `set_field` targeting a key that currently holds a block list is `field_shape_mismatch`
  with `data.field` naming it. Shape is detected from the note's current content, not declared.
- **Trigger:** `set_field nav.contents = "x"` against `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1065` — `errors field_shape_mismatch for a flat-scalar set against a detected block field`

### EDT-082 — remove_field routes to the block primitive by detected shape

- **Behavior:** `remove_field` against a key holding a block list removes the whole block — key and
  children — rather than orphaning the children. Here it empties the frontmatter entirely, and
  `resultingFrontmatter` is the empty string.
- **Trigger:** `remove_field nav.contents` against `Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1071` — `routes a block-field removal to the block primitive rather than orphaning children`
- **Note:** Asymmetric with EDT-081: `remove_field` is shape-polymorphic, `set_field` is not. And
  removing the last key leaves an empty `resultingFrontmatter` — the suite does not pin whether the
  fences themselves survive on disk.

### EDT-083 — A note with an apparent fence is patchable as the body it is

- **Behavior:** A note whose leading `---` closes nothing has no frontmatter, so the *whole file* is
  the body facet and a patch may match anywhere in it — including the metadata-looking lines. The
  result reports `bodyChanged: true`, `frontmatterChanged: false`, and an empty
  `resultingFrontmatter`; no block is minted.
- **Trigger:** `-old_string 'doc.type: Note' -new_string 'doc.type: Corpus'` on `---`/`doc.type:
  Note`/`# Body`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1092` — `patches such a note as the body it is, since the fence never made a block`
- **Note:** Retired-and-replaced: this used to assert a refusal. Body confinement is unchanged in
  principle — it means the patch matches the body *facet*, and here that facet is the whole file. Note
  the sharp asymmetry with EDT-015: the same note refuses a frontmatter op and accepts a patch.

### EDT-084 — A body replacement that would mint a frontmatter block is frontmatter_integrity

- **Behavior:** Supplying a `body` whose text opens with a `---` fence block is refused
  `frontmatter_integrity`; the note is untouched. Authoring frontmatter through the body facet is not
  a supported route.
- **Trigger:** `-Body "---\nnav.contents: smuggled\n---\n# Tasks\n"` against a fence-less note.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1107` — `errors frontmatter_integrity when a body replace would mint a frontmatter block`
- **Note:** The other half of the facet boundary — EDT-025 stops a patch reading into frontmatter,
  this stops a body write *creating* it. Both keep frontmatter mutation on the capability-classified
  ops path. The chosen smuggling payload (`nav.contents`) makes the threat model explicit.

### EDT-085 — The whole resulting frontmatter is schema-validated, not just the touched keys

- **Behavior:** An op is refused `schema_validation_failed` when the *resulting* frontmatter violates
  the schema anywhere, even in a part the op did not touch. The error carries `data.field`,
  `data.rule`, and `data.entryIndex`. Validate-phase: nothing is written.
- **Trigger:** `set_field doc.created` on a note whose pre-existing `nav.see_also` entry lacks a
  description → `field: nav.see_also`, `rule: missing_description`, `entryIndex: 1`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1115` — `errors schema_validation_failed when the resulting frontmatter violates the schema`
- **Note:** Tension with EDT-076 worth flagging to the reconciler: *registry* validation is confined
  to touched keys, but *schema* validation is over the whole block. Two different extents, and the
  distinction is only visible by reading both cases. `entryIndex` is **1-based** here (the sole entry
  in a one-entry list is index 1) — worth pinning explicitly.

### EDT-086 — A path-dependent schema rule applies: Tasks.md must carry its use_skill directive

- **Behavior:** `edit` passes the note's own vault-relative path to the validator, enabling the check
  that needs the note's *name* rather than its content: a `Tasks.md` left without its required
  directive is `schema_validation_failed` with `data.rule = "tasks_missing_use_skill"`.
- **Trigger:** `set_field doc.type=Tasks` on `Projects/Widget/Tasks.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1126` — `runs the path-dependent Tasks.md check, so edit refuses a Tasks.md left without its directive`

### EDT-087 — The same edit is accepted when the call itself supplies the required directive

- **Behavior:** Validation runs against the composed result of the whole ordered op array, so a call
  that both adds the required directive and makes the offending change succeeds. Ordering within the
  array is not required to be "fix first" in any way the test distinguishes, but the array is applied
  in order.
- **Trigger:** `[add_directive use_skill vault-tasks, set_field doc.type=Tasks]`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1134` — `accepts the same Tasks.md edit when the call also supplies the required directive`
- **Note:** This is the atomicity contract made observable: validation sees the end state, not each
  intermediate.

### EDT-088 — A path outside the ambient scope roots is out_of_scope

- **Behavior:** When ambient scope roots are injected, a target outside them fails `out_of_scope` with
  `data.path`, refused ahead of any in-memory mutation so nothing reaches the disk.
- **Trigger:** `VaultMcpScopeRoots = @{ scratch = @('Projects') }` with a target in `Inbox/`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1143` — `errors out_of_scope when the path falls outside the ambient scope roots`
- **Note:** The comment records that this pre-check is **mandatory and is the only thing that produces
  this code** — the write chokepoint's own scope assertion throws plain text, so without the
  tool-level pre-check the caller would get an uncoded error. Relevant if a redesign relies on the
  chokepoint alone.

### EDT-089 — A path inside the ambient scope roots edits normally

- **Behavior:** With scope roots injected, a target inside them edits without incident.
- **Trigger:** Same scope roots, a patch against `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1154` — `edits normally when the path falls inside the ambient scope roots`

### EDT-090 — An unconfigured vault name is unknown_vault

- **Behavior:** `-Vault 'nope'` with no such vault configured is `unknown_vault` with
  `data.vault` echoing the name.
- **Trigger:** `-Vault 'nope'`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1160` — `refuses an unconfigured vault with unknown_vault`

### EDT-091 — A configured vault whose root is missing is vault_unavailable

- **Behavior:** A vault that *is* configured but whose root directory does not exist is
  `vault_unavailable` — distinct from `unknown_vault`.
- **Trigger:** A `vaults.json` naming a vault at a nonexistent path, then `-Vault` on it.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1166` — `refuses a configured vault whose root is missing with vault_unavailable`

### EDT-092 — A contended write lock is lock_timeout with the configured timeout echoed

- **Behavior:** When another process holds the per-vault write lock beyond the configured timeout, the
  call fails `lock_timeout` with `data.timeoutSeconds` echoing the configured value, and the file is
  unchanged.
- **Trigger:** A background process holding the lock; `VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS=1`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1178` — `fails lock_timeout when the write lock is held by another process`
- **Note:** Windows-only (`-Skip:(-not $IsWindows)`), and the test needs a genuinely separate process
  because the mutex is reentrant for the calling thread. The comment notes the chokepoint raises the
  coded shape itself and `edit` does not catch it — so this asserts the code surfaces unchanged
  through the tool.

### EDT-093 — edit declares content:write as its op-class floor

- **Behavior:** The tool declares `OpClass: content:write` in its metadata, which is the floor the
  dispatcher gates against. Only `agentic:write` is asserted dynamically on top of it.
- **Trigger:** N/A — a metadata assertion.
- **Kind:** `artifact`
- **Source:** `tools/Tests/edit.Tests.ps1:1229` — `declares the content:write floor in its .NOTES comment-help (the loader-parsed op class)`
- **Note:** Marked artifact because the assertion is a regex over the script's comment-help text — a
  loading mechanism specific to this design. The *rule* (edit is a content:write tool, floor-gated at
  dispatch) is semantic and survives any redesign; the spelling does not.

### EDT-094 — Every parameter is documented, which the loader requires

- **Behavior:** The tool documents `path`, `frontmatter_ops`, `body`, `old_string`, `new_string`,
  `expected_sha`, `vault` — the complete argument surface.
- **Trigger:** N/A — a metadata assertion.
- **Kind:** `artifact`
- **Source:** `tools/Tests/edit.Tests.ps1:1239` — `documents every parameter in comment-help, which the loader requires to load the tool`
- **Note:** The value to a reconciler is the **argument list itself**: exactly seven parameters, no
  `apply`/preview parameter (see EDT-108).

### EDT-095 — A frontmatter op under .claude/skills is frontmatter_path_excluded

- **Behavior:** Notes under `.claude/skills/` are excluded from frontmatter writes: the op fails
  `frontmatter_path_excluded` and the file is byte-identical.
- **Trigger:** `set_field doc.created` on `.claude/skills/temp-skill/SKILL.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1258` — `refuses a frontmatter op under .claude/skills, leaving the file byte-identical`
- **Note:** The fixture is built in the scratch copy rather than committed, deliberately: a committed
  one would sit in the repo as a file nothing may write.

### EDT-096 — A body patch under .claude/skills is refused the same way

- **Behavior:** A body-only edit to a skill file is *also* `frontmatter_path_excluded` — the file is
  not writable through this tool at all.
- **Trigger:** A patch against `.claude/skills/temp-skill/SKILL.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1266` — `refuses a body patch too, because the chokepoint is the whole write path`
- **Note:** Described as "a consequence of where the check lives rather than a second rule" — every
  edit routes through the frontmatter write chokepoint, body-only ones included. The error *code*
  therefore names frontmatter for a body-only refusal, which is arguably a misleading identity; the
  suite defends it as the right answer for a file whose body is as much harness contract as its
  frontmatter. Flagging as a candidate a redesign might reasonably have spelled differently.

### EDT-097 — The exclusion holds however the path is spelled

- **Behavior:** The exclusion is matched after path normalization and case-insensitively:
  `.CLAUDE/Skills/...`, backslash-separated `.claude\skills\...`, and a traversal spelling
  `Inbox/../.claude/skills/...` are all refused.
- **Trigger:** Three spellings of the same excluded path.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1276` — `refuses however the excluded path is spelled`
- **Note:** A bypass-resistance case. Note the traversal spelling is *normalized and then excluded*,
  not rejected as an escaping path (contrast EDT-060, where traversal leaving the vault is
  `invalid_argument`).

### EDT-098 — The exclusion is skills, not the dot directory: .claude/guides stays editable

- **Behavior:** Other paths under `.claude/` are editable normally — a frontmatter op on
  `.claude/guides/Temp-Guide.md` applies with `changed: true`.
- **Trigger:** `set_field doc.created` on `.claude/guides/Temp-Guide.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1284` — `leaves .claude/guides editable - the exclusion is skills, not the dot directory`
- **Note:** The non-vacuity control for EDT-095.

### EDT-099 — The tool runs standalone via a literal "pwsh -File" invocation

- **Behavior:** The tool is invocable outside the MCP server — no runspace context, no ambient scope,
  no host environment beyond the vault config — exiting 0 and emitting the same single JSON object on
  stdout, with the write landing on disk.
- **Trigger:** A separate process running the script with `-Path` and `-frontmatter_ops`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1319` — `runs via a literal "pwsh -File edit.ps1" invocation (the Tool Authoring Standard spelling)`
- **Note:** A standing authoring standard across all tools, not specific to `edit`. It is also what
  proves the caller-carried staleness guard works with no host at all — the property that decided the
  design against host-side read tracking.

### EDT-100 — The staleness guard works with no host present

- **Behavior:** `expected_sha` is enforced in a standalone run: a bogus digest is `state_mismatch`,
  nonzero exit, file unchanged. Staleness is entirely caller-carried and needs no host state.
- **Trigger:** Standalone body replace with `expected_sha = '0' * 64`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1357` — `enforces the caller-carried staleness guard with no host present`

### EDT-101 — Errors surface as the shared JSON {code, message, data?} shape on stderr with a nonzero exit

- **Behavior:** Under a standalone invocation, a failure writes the shared JSON error object to
  **stderr** (not stdout) and exits nonzero. Success writes JSON to stdout and exits 0.
- **Trigger:** A `not_found` in a standalone run.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:1373` — `surfaces the shared JSON error shape on stderr under pwsh -File`
- **Note:** The `{code, message, data?}` triple — with `data` optional — is the shared error contract
  every case above instantiates.

---

## The projection seam: `edit`'s `.SCHEMA frontmatter_ops` side-car

The suite's "advertised frontmatter_ops contract" Context is a **derivational drift guard**. Nothing
in it names an op: the branch list comes out of `edit.ps1`'s own `.SCHEMA` block (parsed at Pester
discovery time using ScriptToolLoader's two regexes copied verbatim), each op instance is built from
its branch's own `required` list and property nodes, and the expected branch set comes out of the
substrate's dispatch AST (`powershell/VaultAccess/Private/FrontmatterOps.ps1`). Cases EDT-102..EDT-107
are that machinery's assertions; EDT-109..EDT-118 are the projection rules the block encodes.

### EDT-102 — The parameter advertises an array of op objects with its own description

- **Behavior:** The `frontmatter_ops` property node is `type: array`, carries a non-empty description
  of its own, and its items declare at least one `oneOf` branch.
- **Trigger:** N/A — a schema-shape assertion.
- **Kind:** `artifact`
- **Source:** `tools/Tests/edit.Tests.ps1:623` — `advertises an array of op objects with a description of its own`
- **Note:** "Its own, not the `.PARAMETER` text": the block *replaces* the whole property node, so
  nothing of the prose help reaches the client for this parameter. A redesign where the parameter
  description and the schema description are the same string diverges from this.

### EDT-103 — Every branch is pinned on a const op and every property is described

- **Behavior:** Each `oneOf` branch is `type: object`, pins `op` with `const` (not `enum`, not a bare
  type — that is what makes the branches mutually exclusive), lists `op` in `required`, and gives
  every declared property a non-empty description.
- **Trigger:** N/A.
- **Kind:** `artifact`
- **Source:** `tools/Tests/edit.Tests.ps1:631` — `pins every branch on a const op and describes every property it declares`
- **Note:** The stated reason descriptions are mandatory is semantic: the value conventions that no
  JSON Schema keyword can carry (link/folder exclusivity, the `[[Note-Name]]` bracket form) live
  *only* in the descriptions, so an undescribed property reaches no caller.

### EDT-104 — The advertised branch set is exactly the op shapes the substrate implements

- **Behavior:** The set of documented shapes equals the set of implemented shapes, in both
  directions — an advertised shape the substrate refuses fails, and an implemented op with no branch
  fails.
- **Trigger:** N/A — set comparison of derived lists.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:645` — `declares exactly the op shapes the substrate implements`
- **Note:** Marked semantic though the mechanism is an artifact: the *contract* that the advertised
  vocabulary is neither larger nor smaller than the implemented one is portable, and it is the whole
  point of the seam. A "shape" is an op name, or an op name plus its `kind` where the op branches on
  kind — because the required members differ per kind.

### EDT-105 — The nav key enum is exactly the substrate's allow-list

- **Behavior:** Every branch declaring a `key` property enumerates exactly the nav block-list keys the
  substrate allows (`nav.contents`, `nav.see_also`), in the same order, read from the module's own
  exported accessor rather than restated.
- **Trigger:** N/A.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:655` — `enumerates the nav block-list keys the substrate allows, and no others`
- **Note:** Called out as the one place the schema restates a list the substrate owns, because a JSON
  literal cannot call a function. Any redesign hand-writing this enum inherits the same hazard.

### EDT-106 — Every advertised shape dispatches successfully and does real work

- **Behavior:** For each documented branch, an op object built from that branch's own `required` list
  goes through the real tool and is not merely accepted but **applied**: `opsApplied.Count = 1`, `op`
  and `kind` echoed, `changed: true`, `frontmatterChanged: true`.
- **Trigger:** Nine generated ops (one per branch) against a seeded `Inbox/Drift-Probe.md`.
- **Kind:** `semantic`
- **Source:** `tools/Tests/edit.Tests.ps1:670` — `dispatches the <Shape> branch through edit.ps1 and the substrate applies it`
- **Note:** The `changed: true` requirement is deliberate — `changed: false` would mean the shape
  parsed and then addressed nothing, the half-proof a drift test must not settle for. The nine shapes
  are: `set_field`, `remove_field`, `add_hint`, `add_directive/pinned`, `add_directive/use_skill`,
  `strip_directive/pinned`, `strip_directive/use_skill`, `add_nav_entry`, `remove_nav_entry`.

### EDT-107 — The drift fixture: a note seeded with a target for every strip/remove branch

- **Behavior:** `Inbox/Drift-Probe.md` carries `doc.type: Note`, an `agent.instructions` list with a
  seeded `pinned` entry and a `use_skill` entry, and a `nav.contents` list with a seeded link entry —
  and none of the values an add branch would insert.
- **Trigger:** N/A — fixture construction.
- **Kind:** `fixture`
- **Source:** `tools/Tests/edit.Tests.ps1:231` — the `$DriftFixture` discovery-phase literal
- **Note:** Included because EDT-106's `changed: true` claim depends entirely on this seeding.

### EDT-108 — The schema advertises but does not enforce

- **Behavior:** The advertised schema is documentation, not validation. A payload satisfying the
  schema can still be refused `malformed_op` by the script or the substrate; and conversely a payload
  the script accepts need not be described by the schema (an extra member, a value convention).
- **Trigger:** N/A — a stated contract.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:47` — the `.SCHEMA` block's own top-level description; `tools/Tests/edit.Tests.ps1:611-621` — the Context's header comment
- **Note:** Only one direction is testable, by design: a payload the schema permits and the script
  refuses is a lie the drift guard catches; the converse is not.

### EDT-109 — Projection rule: link/folder exclusivity on the nav entry ops

- **Behavior:** `add_nav_entry` and `remove_nav_entry` take **exactly one** of `link` or `folder`.
  Both, or neither, is `malformed_op`. The JSON Schema deliberately does not express this — it is
  stated in the property descriptions instead — and neither is listed in `required`.
- **Trigger:** A nav entry op carrying both discriminators or none.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:117-118, 131-132` (schema descriptions); enforcement is exercised
  indirectly at `tools/Tests/edit.Tests.ps1:203` — the drift builder supplies a discriminator
  precisely because the substrate refuses an op carrying neither
- **Note:** **Not directly asserted by any `It` block.** The suite's own test helper compensates for
  it in `New-DriftOpInstance` and a comment names it as "the plan's Out of Scope". A reconciliation
  should treat this as an un-tested rule in the old suite — a gap on *both* sides is plausible.

### EDT-110 — Projection rule: a nav wikilink uses the [[Note-Name]] bracket form

- **Behavior:** The `link` value is a wikilink in `[[Note-Name]]` form. This is a **convention**, not
  a validated type — to the schema a wikilink is a plain string.
- **Trigger:** Any nav entry op.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:117, 131`; exercised throughout, e.g. `tools/Tests/edit.Tests.ps1:522`
- **Note:** Because it is convention rather than validation, a redesign that *enforces* the bracket
  form is a behavior change, not just a tightening.

### EDT-111 — Projection rule: nav keys are matched case-insensitively, written canonically

- **Behavior:** `key` is matched case-insensitively against the allow-list and always **written** in
  the canonical spelling (`nav.contents` / `nav.see_also`).
- **Trigger:** A nav entry op with a differently-cased key.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:116, 130` (schema descriptions)
- **Note:** No `It` block exercises a non-canonical spelling. Un-tested in the old suite.

### EDT-112 — Projection rule: add_nav_entry requires a non-empty description

- **Behavior:** `description` is required and non-empty on `add_nav_entry`. An entry without one is a
  schema violation, so the op refuses it up front rather than writing a note it knows the validator
  will report.
- **Trigger:** `add_nav_entry` with no description.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:119, 124`; the resulting validator rule is seen at
  `tools/Tests/edit.Tests.ps1:1115` (`rule: missing_description`)
- **Note:** Interesting design point: the op-level requirement exists to pre-empt a schema rule that
  would otherwise fire later. The two are separately reachable — EDT-085 shows the validator rule
  firing on a *pre-existing* entry that the op never touched.

### EDT-113 — Projection rule: add_nav_entry position anchors before/after

- **Behavior:** `add_nav_entry` accepts an optional `before` or `after` naming an existing entry's
  target value (its link or its folder, whichever it carries) and inserts immediately adjacent to it.
  `before` and `after` are mutually exclusive. An anchor matching **no** entry or **several** is
  `malformed_op` — not a silent fallback to the default position.
- **Trigger:** `add_nav_entry` with `before` or `after`.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:121-122` (schema descriptions)
- **Note:** **This is the largest un-asserted surface in the area.** No `It` block in `edit.Tests.ps1`
  supplies `before` or `after` — the drift builder never generates them because they are optional. The
  substrate's own `Frontmatter*` suites are said to cover `Add-VaultNavEntry` in full, so the behavior
  is presumably tested *there*; but as `edit` argument surface it is unexercised here. High-value
  reconciliation candidate.

### EDT-114 — Projection rule: default nav insert position is a pure function of the list

- **Behavior:** With no `before`/`after` anchor, position is deterministic and derived from the list:
  a link entry lands at the end of the link run, a folder entry at the end of the list.
- **Trigger:** `add_nav_entry` with no anchor.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:121` (schema description)
- **Note:** Not asserted positionally by any `It` block here — the nav cases assert presence and
  round-trip equality, not ordering. The "link run precedes folders" ordering rule is the harvestable
  claim.

### EDT-115 — Projection rule: remove_nav_entry addresses by target value, never by index

- **Behavior:** Entries are removed by their target value. There is no index-based addressing,
  deliberately: ops are ordered, so an index would name a different entry the moment an earlier op
  shifted the list. Matching is case-insensitive, and a target matching more than one entry is
  `malformed_op` rather than a coin flip.
- **Trigger:** `remove_nav_entry`.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:129, 131`
- **Note:** The multiple-match refusal is un-asserted in this suite. The "no index addressing" rule is
  a design constraint a redesign might not know to reproduce.

### EDT-116 — Projection rule: a removal that empties a list drops the key

- **Behavior:** `remove_nav_entry` and `strip_directive` drop the key itself when the removal leaves
  nothing under it.
- **Trigger:** Removing the last entry from a nav list or the last directive.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:98, 107, 129`; observable at `tools/Tests/edit.Tests.ps1:545` (the
  round-trip restores the exact prior block) and `:1071` (removal empties the frontmatter)
- **Note:** Asserted only transitively via byte-equality, never as its own case.

### EDT-117 — Projection rule: strip_directive is deliberately asymmetric across kinds

- **Behavior:** `strip_directive kind=pinned` takes a `text` and removes **exactly the one** matching
  entry, matched **case-sensitively** (a pinned rule is the user's own prose). `strip_directive
  kind=use_skill` takes **no target at all** and removes **every** `use_skill` entry at once.
- **Trigger:** Either strip kind.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:98-111`; the two shapes are dispatched at `tools/Tests/edit.Tests.ps1:670`
- **Note:** Called "deliberately asymmetric" in the schema itself. The case-sensitivity of the pinned
  match, and the all-at-once semantics of the use_skill strip, are both un-asserted here beyond the
  drift dispatch — and both are exactly the sort of thing a redesign would normalize into symmetry.

### EDT-118 — Projection rule: use_skill directives are distinguished by their `when`, and carry an optional note

- **Behavior:** Two `use_skill` entries differing only in `when` are **distinct** entries (so
  duplicate detection is over skill+when, not skill alone). `add_directive/use_skill` also accepts an
  optional free-text `note`, which is omitted from the written entry when empty. `add_nav_entry`'s
  `load_when` is likewise omitted when empty.
- **Trigger:** `add_directive kind=use_skill` with varying `when`; either op with an empty optional.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:90-91, 120`
- **Note:** The "omitted when empty rather than written as an empty value" rule appears on both
  `note` and `load_when` — it is a general convention of the op vocabulary, not a per-op quirk. Neither
  the distinctness rule nor the omit-when-empty rule has its own `It` block.

### EDT-119 — Ops apply to one in-memory string, written once; a failure anywhere means zero mutation

- **Behavior:** The whole ordered array — and the body facet with it — is applied to a single
  in-memory representation which is written exactly once. Each op sees the result of the previous one.
  Order and repetition are semantic. A failure at any point, in any op, leaves the disk untouched;
  there is no partial-op state.
- **Trigger:** Any multi-part edit; failure at any stage.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:26, 47` (stated contract); instantiated at
  `tools/Tests/edit.Tests.ps1:971` (op 1 fails, op 0 does not land), `:1115`, `:775`, `:1143`, `:693`
- **Note:** This is the atomicity contract the whole area rests on, and it has **no dedicated `It`
  block** — it is asserted piecemeal through the "zero mutation" clause attached to roughly a dozen
  error cases. A reconciler should treat it as a first-class requirement even though no single old
  test names it.

### EDT-120 — edit has no apply/preview parameter, deliberately

- **Behavior:** `edit` has no dry-run or preview mode. It is a single-target tool taking
  caller-supplied content, so it has nothing to discover and a preview would be ceremony — the
  Assess-Before-Mutate convention does not apply to it.
- **Trigger:** N/A — a negative contract.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:171-173` (`.NOTES`); the parameter list is pinned at
  `tools/Tests/edit.Tests.ps1:1239`
- **Note:** Harvested explicitly because the task asks about "the preview convention". The convention
  exists in this system for other tools; `edit`'s answer is a reasoned **opt-out**, and the
  substitute for preview is the `expected_sha` staleness guard plus the exactly-once patch anchor.
  A redesign that added a preview to `edit` would not be *wrong* so much as divergent from a recorded
  decision.

### EDT-121 — edit cannot change a path, create, or delete

- **Behavior:** `edit` mutates content and frontmatter only. It cannot rename or relocate a note
  (that also requires rewriting every reference to it), cannot create, and cannot delete.
- **Trigger:** N/A — scope boundary.
- **Kind:** `semantic`
- **Source:** `tools/edit.ps1:19-23` (`.SYNOPSIS`); the create half is asserted at
  `tools/Tests/edit.Tests.ps1:853`
- **Note:** Also names lint-suppression state as living in opaque files under `.vault/state/` and
  therefore outside `edit` — a passing mention of an out-of-scope tool, noted per the brief's rule 3.

### EDT-122 — The fixture: a fresh scratch copy of test-vault per test, with a byte-precise working note

- **Behavior:** Each test copies `test-vault/` to a temp scratch root, writes a `vaults.json` naming
  `scratch` as the default vault, points `VAULT_MCP_VAULTS_JSON` at it, and writes
  `Inbox/Edit-Case.md` with LF terminators and known frontmatter and body.
- **Trigger:** N/A — fixture construction.
- **Kind:** `fixture`
- **Source:** `tools/Tests/edit.Tests.ps1:250` — the `Describe 'edit'` `BeforeEach`
- **Note:** `AfterEach` clears `VaultMcpScopeRoots` and `VaultMcpOpClasses` globals — those two are
  the *only* ambient host inputs the tool reads, which is itself worth knowing.

### EDT-123 — The fixture notes several cases depend on

- **Behavior:** `Projects/Widget/Widget.md` carries a real `nav.contents` block including a
  `[[Widget/Tasks]]` entry; `Projects/Widget/Tasks.md` has no frontmatter block and is subject to the
  path-dependent `tasks_missing_use_skill` rule; `Inbox/Note-Loose-Thought.md` has no frontmatter;
  `Resources/Gizmo-Reference.md` contains the text "quorvium alloy" and is the standalone suite's
  target.
- **Trigger:** N/A.
- **Kind:** `fixture`
- **Source:** `tools/Tests/edit.Tests.ps1:522, 569, 1126, 409, 1366`
- **Note:** A redesign's fixture will differ; these are listed so a reconciler can tell a
  fixture-shaped gap from a behavior-shaped one.
