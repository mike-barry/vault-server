# Harvest: The frontmatter write path and the operation vocabulary

Sources: `C:\Repos\vault-server\powershell\VaultAccess\Tests\FrontmatterWrite.Tests.ps1` (761 lines)

Read the implementation for:
- `powershell/VaultAccess/Public/FrontmatterWrite.ps1` (245 lines) — to pin the order of the
  stages (path exclusion, LF normalization, supplied-block parse, ops, body splice, fence guard,
  schema violations, agentic diff, capability assertion) and the exact set of returned facets,
  which several tests assert only one field of at a time.
- `powershell/VaultAccess/Private/FrontmatterOps.ps1` (235 lines) — for the complete operation
  vocabulary and each op's required/optional arguments, since the suite exercises some argument
  shapes only indirectly.
- `powershell/VaultAccess/Private/FrontmatterHelpers.ps1` (lines 80–207) — for the three
  `frontmatter_integrity` reason identities (`frontmatter_unparsed`, `apparent_fence`,
  `unclosed_fence`/`content_after_fence`) and the control-character class the hygiene gate uses.

## Summary

This area is one function, `Invoke-VaultFrontmatterWrite`, plus the exported classification
predicate `Get-VaultAgenticFrontmatterDiff` it is built on. The function is a pure in-memory
compose-and-validate step: the caller owns the read, the lock, and the write. Three production
consumers (`edit`, `create`, `lint`) reach it with no branch of their own, and the suite asserts
that each of the three shapes works through the one path.

Things a reconciler should know before reading the cases:

- **Four hard refusals, one soft one.** `frontmatter_path_excluded`, `frontmatter_integrity`,
  `malformed_op`, `protected_directive_violation`, `field_shape_mismatch` and
  `insufficient_capability` all throw. **Schema violations are returned, not thrown** — that is a
  deliberate design turn (`lint` must be able to fix provenance dates on notes that already carry
  `agent.*` problems) and the suite pins it twice.
- **Classification is outcome-based.** The `agentic:write` capability is asserted on a *diff of the
  before and after content*, not on what the ops nominally targeted. Several cases exist only to
  pin injection paths that a target-based classifier would miss.
- **Parse–validate–emit, never splice.** A caller-supplied `-Frontmatter` block is parsed to a
  tree, validated whole, then re-serialized. Two cases (FMW-078, FMW-081) are explicitly
  "permanently pinned" regression reproductions of bypasses that splicing allowed.
- **The capability gate is driven by the ambient `$Global:VaultMcpOpClasses`**, injected by the
  host. Empty array = authenticated but holds nothing = DENY. Variable absent entirely = standalone
  run = degrade OPEN. Every capability case clears it in `AfterEach`.
- **Two extents of registry validation**, measured and deliberate: `edit` validates only the keys
  an op *touches*, `create` validates the *whole* supplied block. This is what keeps real notes
  carrying Obsidian's `obsidianUIMode` writable.
- **LF is normalized at the door**, on both `-Content` and `-Body`, so the guarantee covers the
  body splice as well as the emitter.
- Ops are applied **in order, each seeing the previous op's result**; repetition is meaningful; the
  whole call is all-or-nothing (any op's refusal throws before the caller is handed any content).
- **Vocabulary not asserted by this suite** (found in the implementation, listed so the reconciler
  does not mistake absence for non-existence): `add_directive kind=use_skill` accepts an optional
  `note` argument alongside the required `skill` and `when`; `strip_directive kind=use_skill` takes
  no `text` and removes *every* `use_skill` entry at once — a deliberate asymmetry with
  `kind=pinned`, which removes only the named one. Also unasserted here: the
  `frontmatter_not_a_map` reason (a supplied block that parses to a YAML non-mapping).

## Cases

### FMW-001 — An untouched agentic layer produces an empty diff

- **Behavior:** Comparing two versions of a note that differ only in non-agentic keys reports no
  changed agentic keys.
- **Trigger:** Before and after differ only in `doc.type`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:28` — `reports nothing when the agentic layer is untouched`

### FMW-002 — An added agentic key is reported

- **Behavior:** An agentic key (`nav.*` / `agent.*`) present in the after version and absent from
  the before version is reported as changed.
- **Trigger:** `nav.contents` added.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:32` — `reports a key that was added`

### FMW-003 — A removed agentic key is reported

- **Behavior:** An agentic key present before and absent after is reported as changed.
- **Trigger:** `nav.contents` removed.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:37` — `reports a key that was removed`

### FMW-004 — An agentic key whose value changed is reported

- **Behavior:** A key present in both versions whose value differs is reported as changed.
- **Trigger:** `nav.contents` entry's link changes from `[[X]]` to `[[Y]]`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:42` — `reports a key whose value changed`

### FMW-005 — A quoted top-level key is a key, so a smuggled directive reaches the diff

- **Behavior:** A top-level frontmatter key written in quotes (`"agent.instructions":`) is
  recognized as a top-level key, so a directive introduced under it is reported by the diff.
- **Trigger:** After version adds `"agent.instructions":` with a `pinned:` child under an existing
  `doc.created` key.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:47` — `sees a quoted top-level key as a key, so a smuggled directive reaches the diff`
- **Note:** Explicitly a pinned regression. The prior hand-rolled parser matched top-level keys by
  a pattern a quoted key could not satisfy, absorbed the directive as a child of `doc.created`,
  reported no agentic key, and never ran the gate. Paired with FMW-078/FMW-079.

### FMW-006 — Requoting alone is not an agentic mutation

- **Behavior:** The diff compares parsed values, not bytes, so a block whose only difference is
  quoting style reports no change.
- **Trigger:** Hand-quoted `nav.contents` block vs. the emitter's own quoting of the same values.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:56` — `reports no diff when only the block's quoting changed`
- **Note:** Motivated by a one-time corpus reformat and by any later reserialization; without this
  every reserialize would read as an agentic write.

### FMW-007 — With no original, every agentic key present is a diff

- **Behavior:** Diffing against empty before-content reports every agentic key present in the after
  content. This is the degenerate case of the same rule, not a second rule for creation.
- **Trigger:** Before content is `''`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:63` — `treats a create (no original) as every agentic key present being a diff`

### FMW-008 — Ops apply in order and compose byte-exact content

- **Behavior:** Ops are applied in the order given; the composed content, and the frontmatter facet
  reported back, are byte-exact. A `set_field` on an absent key appends it after the existing keys;
  a `set_field` on a present key updates it in place, preserving key order.
- **Trigger:** `set_field doc.created` then `set_field doc.type` against a note carrying
  `doc.type`. Result orders `doc.type` first (its original position), `doc.created` appended.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:69` — `applies ops in order and returns the composed content byte-exactly`
- **Note:** Also pins `FrontmatterChanged` true and `BodyChanged` false for a frontmatter-only
  write, and that the returned `Frontmatter` facet is the block's inner text with no fences and no
  trailing newline.

### FMW-009 — A CRLF note is normalized to LF end to end, body included

- **Behavior:** A note whose content arrives with CRLF endings is written out with LF endings
  throughout, in the body as well as the frontmatter.
- **Trigger:** CRLF note plus one `set_field`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:80` — `normalizes a CRLF note to LF end to end, body included`

### FMW-010 — A body-only write leaves the frontmatter block's bytes alone

- **Behavior:** When no frontmatter op ran, the existing block is preserved byte for byte — the
  caller's own quoting is not rewritten — and `FrontmatterChanged` is false.
- **Trigger:** `-Body` supplied, no `-Ops`, against a note whose `doc.type` value is hand-quoted.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:86` — `leaves the frontmatter block's bytes alone on a body-only write`
- **Note:** Rationale recorded in the test: reserializing here would report a changed hash for a
  note whose meaningful content did not change.

### FMW-011 — An all-no-op op list also leaves the block's bytes alone

- **Behavior:** When every op turned out to be an idempotent no-op, the frontmatter block is
  returned unchanged byte for byte and `FrontmatterChanged` is false.
- **Trigger:** `add_directive pinned "Same"` against a note already carrying exactly that pinned
  entry, hand-quoted.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:97` — `leaves the frontmatter block's bytes alone when every op was an idempotent no-op`

### FMW-012 — Each op is recorded with applied and changed

- **Behavior:** The result carries one record per op, in order, naming the op and the field it
  touched, with `Applied` true and `Changed` reflecting whether the content actually differed. An
  op that is an idempotent no-op records `Changed` false.
- **Trigger:** `set_field doc.created` (changes) followed by `remove_field doc.archived` against a
  note with no `doc.archived` (no-op).
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:104` — `records applied and changed per op, with changed false on an idempotent no-op`
- **Note:** Also establishes that `remove_field` against an absent key is a no-op rather than a
  refusal — provided the key is one the registry carries (see FMW-035).

### FMW-013 — No ops yields an empty applied list

- **Behavior:** With an empty op list the applied-ops record is empty (not null).
- **Trigger:** `-Ops @()` with a `-Body`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:117` — `returns @() for OpsApplied when there are no ops`

### FMW-014 — The body facet is replaced and the block is untouched

- **Behavior:** Supplying a body replaces the note's body entirely and leaves the frontmatter
  block in place; `BodyChanged` true, `FrontmatterChanged` false.
- **Trigger:** `-Body "# Replaced\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:121` — `replaces the body facet and leaves the frontmatter block untouched`

### FMW-015 — An empty body truncates the body and leaves the block intact

- **Behavior:** `-Body ''` produces a note that is the frontmatter block and nothing after it.
- **Trigger:** `-Body ''`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:128` — `truncates the body on an empty -Body and leaves the block intact`
- **Note:** Distinguishing "empty body" from "body not supplied" is load-bearing; see FMW-016.

### FMW-016 — An unbound body leaves the body untouched

- **Behavior:** When no body is supplied at all, the original body survives unchanged and
  `BodyChanged` is false.
- **Trigger:** Frontmatter ops only, no `-Body` parameter bound.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:132` — `leaves the body untouched when -Body is not bound at all`
- **Note:** The distinction between "parameter absent" and "parameter empty" is the contract; a
  reimplementation needs a tri-state (absent / empty / value), not a nullable string.

### FMW-017 — remove_field routes by the key's detected shape

- **Behavior:** `remove_field` against a key currently holding a block list removes the whole
  block, including its child lines, leaving the rest of the frontmatter intact.
- **Trigger:** `remove_field nav.contents` against a note whose `nav.contents` is a list of
  mappings.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:138` — `routes remove_field to block removal by detected shape`
- **Note:** Same op verb handles scalar and block keys; the shape is detected from the note, not
  declared by the caller.

### FMW-018 — Schema violations are returned alongside composed content, never thrown

- **Behavior:** A composed result that violates the vault schema is still composed and returned;
  the violation appears in a returned list naming the rule.
- **Trigger:** A note whose `nav.contents` entry lacks a description, plus a `set_field doc.created`
  op. Result: one violation with rule `missing_description`, and the content carries the new field.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:145` — `returns the violation list and still composes the content`
- **Note:** The stated rationale is `lint`: a hard throw would refuse the provenance-date fix on
  exactly the notes that already have `agent.*` problems. Mentions `lint` in passing only; the
  behavior harvested is the write path's.

### FMW-019 — A clean note returns an empty violation list

- **Behavior:** A note with no schema problems produces an empty violation list.
- **Trigger:** Plain note plus one `set_field`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:155` — `returns an empty violation list for a clean note`

### FMW-020 — The path-dependent schema check runs when the relative path is supplied

- **Behavior:** Schema validation includes rules that depend on the note's path; a note at a
  `Tasks.md` path with no `use_skill` directive produces the `tasks_missing_use_skill` violation.
- **Trigger:** `-RelativePath 'Projects/Widget/Tasks.md'`, no ops, a body.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:159` — `runs the path-dependent Tasks.md check when RelativePath is supplied`

### FMW-021 — A body write that mints a frontmatter block is refused

- **Behavior:** A body write whose text would introduce a complete frontmatter block on a note that
  had none is refused with `frontmatter_integrity`.
- **Trigger:** `-Content "# Plain note\n"`, `-Body "---\nnav.contents: smuggled\n---\n# Body\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:166` — `refuses a body write that mints a frontmatter block on a fence-less note`
- **Note:** Without this the body replace would create a block no op authored, bypassing schema
  validation and the agentic key assertion entirely. Error identity is a JSON payload with a `code`
  member — the JSON-in-exception-message carrier is an artifact; the refusal and its code name are
  the semantics.

### FMW-022 — A body opening a fence it never closes is allowed — that is prose

- **Behavior:** A body whose first line is `---` with no closing fence does not mint a block; the
  write succeeds, reports no frontmatter change and no agentic keys changed.
- **Trigger:** `-Body "---\nnav.contents: smuggled\n"` on a fence-less note.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:173` — `allows a body write that opens a fence it never closes - that is prose, not a block`
- **Note:** Grounded in the Markdown standard: an unterminated `---` is a thematic break followed
  by a paragraph. The guard fires on a real block, not on a lone fence line.

### FMW-023 — A frontmatter op against a note whose first line is an unclosed fence is refused (apparent_fence)

- **Behavior:** A frontmatter op against a note whose first line is `---` with no closing fence is
  refused with `frontmatter_integrity` and reason `apparent_fence`.
- **Trigger:** `-Content "---\ndoc.type: Corpus\n# Body"` plus a `set_field` op.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:181` — `refuses a frontmatter op against a note whose first line is an unclosed fence`
- **Note:** Such a note has no frontmatter to the parser, so writing would prepend a fresh block
  above the user's stranded apparent metadata. The refusal moved from a fence *state* to the
  *write*: reading such a note is fine, writing frontmatter to it is not.

### FMW-024 — A supplied frontmatter block that never closes is refused (unclosed_fence)

- **Behavior:** A caller-supplied frontmatter block opening with `---` and never closing is refused
  with `frontmatter_integrity`, reason `unclosed_fence`.
- **Trigger:** `-Frontmatter "---\ndoc.type: Corpus\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:190` — `refuses a supplied frontmatter block that never closes`; reason identity asserted again at `:685` within `keeps the supplied-text integrity refusals`
- **Note:** Distinct identity from FMW-023's `apparent_fence`: here the caller *declared* the text
  to be frontmatter, so text that will not hold as such is a caller error.

### FMW-025 — A supplied block carrying content past its closing fence is refused (content_after_fence)

- **Behavior:** A caller-supplied frontmatter block with text after its closing `---` is refused
  with `frontmatter_integrity`, reason `content_after_fence`.
- **Trigger:** `-Frontmatter "---\ndoc.type: Corpus\n---\n# smuggled body\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:194` — `refuses a supplied frontmatter block carrying content past its closing fence`; reason identity asserted at `:687`

### FMW-026 — A body merely containing `---` below its first line is allowed

- **Behavior:** A body containing a `---` line that is not the first line is written verbatim and
  reports no frontmatter change.
- **Trigger:** `-Body "# Heading\n---\nnot frontmatter\n"` on a fence-less note.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:198` — `allows a body write that merely contains --- below the first line`

### FMW-027 — A field name outside the key pattern is refused, naming the op index

- **Behavior:** A field name that does not match the frontmatter key pattern is refused with
  `malformed_op`, and the error names the zero-based index of the offending op.
- **Trigger:** Two ops; the second sets field `doc created` (with a space). Error carries
  `opIndex: 1`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:206` — `refuses a field name outside the key pattern with malformed_op and the op index`
- **Note:** The op index is described elsewhere in the suite as "the actionable half of the answer"
  for an ordered list. This also implicitly pins all-or-nothing: op 0 succeeded but the whole call
  throws and the caller receives no content.

### FMW-028 — A field value carrying a control character is refused

- **Behavior:** A `set_field` value containing a control character (e.g. an ANSI escape) is refused
  with `malformed_op`.
- **Trigger:** `set_field doc.type` with value `"Corpus\e[31m"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:216` — `refuses a value carrying a control character`
- **Note:** Implementation's control-character class (read to disambiguate): `[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]`
  — i.e. everything except tab, LF and CR. It refuses rather than strips, because silently
  rewriting a caller's value would report success while changing what they asked for.

### FMW-029 — Directive text carrying a control character is refused

- **Behavior:** The hygiene gate binds every op that carries caller text, not just the field ops:
  `add_directive` text with a control character is refused with `malformed_op`.
- **Trigger:** `add_directive kind=pinned text="bad\e[31m"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:221` — `refuses directive text carrying a control character, which the spec's field-only hygiene would have missed`
- **Note:** The test title records that a field-only hygiene rule was the specified behavior and
  was found insufficient; `agent.instructions` text is the most agent-facing text in the vault.

### FMW-030 — Hint text carrying a control character is refused

- **Behavior:** `add_hint` text with a control character is refused with `malformed_op`.
- **Trigger:** `add_hint text="a\e[31m"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:228` — `refuses hint text carrying a control character`

### FMW-031 — strip_directive text carrying a control character is refused

- **Behavior:** `strip_directive` text with a control character is refused with `malformed_op` —
  the gate applies to removal targets too, not only to text being written.
- **Trigger:** `strip_directive kind=pinned text="bad\e[31m"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:233` — `refuses strip_directive text carrying a control character`

### FMW-032 — A value carrying a line break and a quote is accepted and stays inside its own key

- **Behavior:** On a key whose registry pattern permits any string, a value containing a newline
  and double quotes is accepted, round-trips exactly, and cannot become structure: no agentic key
  is reported as changed.
- **Trigger:** `set_field scope` with value `personal "quoted"\nnav.contents: smuggled`; reading the
  field back yields the value exactly.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:238` — `accepts a value carrying a line break and a quote, keeping it inside its own key`
- **Note:** Records that two earlier refusals (newline, quote) existed only because a line-splicing
  writer could not escape, and were retired once emission went through the tree. On a patterned key
  the registry refuses the same value a step earlier — that is FMW-033.

### FMW-033 — A set_field value failing its key's registry pattern is refused

- **Behavior:** Values are validated against the key's declared pattern, not just key names: a
  caller blocked from inventing a key must not be able to put arbitrary text into a legitimate one.
  Refused with `malformed_op` naming the op index.
- **Trigger:** `set_field doc.type` with value `"Note\nnav.contents: smuggled"`; `opIndex: 0`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:249` — `refuses a set_field value that fails its key's registry pattern`

### FMW-034 — set_field against a key the registry does not carry is refused

- **Behavior:** A field op can only write a key the vault's registry declares; an unrecognized key
  is refused with `malformed_op`.
- **Trigger:** `set_field obsidianUIMode`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:258` — `refuses a set_field against a key the registry does not carry`

### FMW-035 — remove_field against a key the registry does not carry is refused

- **Behavior:** A removal is a write: an unrecognized key already present in the note cannot be
  deleted by a field op. Refused with `malformed_op` even though the key exists in the note.
- **Trigger:** `remove_field obsidianUIMode` against a note that carries `obsidianUIMode`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:263` — `refuses a remove_field against a key the registry does not carry`
- **Note:** Stated rule: the pass-through guarantee is that a foreign key *survives* an edit, not
  that a tool may delete what it cannot author. Unlike `set_field`, `remove_field` validates the
  key only (a removal names no value).

### FMW-036 — An unrecognized key already in the note passes through untouched

- **Behavior:** A write validates only the keys its ops touch, so a note carrying a foreign key
  stays writable, keeps the foreign key with its value, and produces no schema violation.
- **Trigger:** Note carrying `obsidianUIMode: preview`; op sets `doc.type`. Both keys read back
  correctly afterwards; zero violations.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:270` — `passes an unrecognized key already in the note through untouched`
- **Note:** The test records this as a *measured* extent decision: six real `Tasks/` notes carry
  Obsidian's `obsidianUIMode`, and "tightening" edit to whole-block validation would make them
  unwritable. Contrast FMW-080, where `create` validates the whole supplied block.

### FMW-037 — An unknown op verb is refused

- **Behavior:** An op naming a verb outside the vocabulary is refused with `malformed_op`.
- **Trigger:** `@{ op = 'reticulate_splines' }`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:281` — `refuses an unknown op`
- **Note:** Implementation also refuses an op with a missing or blank `op` member with
  `malformed_op` reason "missing 'op' member"; the suite does not exercise that separately.

### FMW-038 — An op missing a required parameter is refused

- **Behavior:** An op that omits an argument its verb requires is refused with `malformed_op`.
- **Trigger:** `set_field` with a `field` but no `value`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:285` — `refuses an op missing a required parameter`
- **Note:** Required-argument map read from the implementation, for the reconciler:
  `set_field` needs `field` + `value`; `remove_field` needs `field`; `add_hint` needs non-blank
  `text`; `add_directive` needs `kind` ∈ {pinned, use_skill}, then `text` (pinned) or `skill` +
  `when` (use_skill, with optional `note`); `strip_directive` needs `kind` ∈ {pinned, use_skill},
  and `text` for pinned only; `add_nav_entry` needs `key`, exactly one of `link`/`folder`, and a
  non-blank `description`, with optional `load_when` and at most one of `before`/`after`;
  `remove_nav_entry` needs `key` and exactly one of `link`/`folder`.

### FMW-039 — A generic field op against agent.instructions is refused (protected_directive_violation)

- **Behavior:** `agent.instructions` holds binding directives and cannot be dropped by a generic
  field op; the refusal has its own code, `protected_directive_violation`, and names the correct op
  (`strip_directive`).
- **Trigger:** `remove_field agent.instructions` against a note carrying a pinned directive.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:291` — `refuses a generic field op against agent.instructions, naming the routing fix`

### FMW-040 — protected_directive_violation wins over field_shape_mismatch

- **Behavior:** When a generic field op against `agent.instructions` would trip both the protected
  target rule and the shape rule, the protected-target code is the one returned.
- **Trigger:** `set_field agent.instructions value=x` against a note whose `agent.instructions` is
  a block list.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:297` — `takes protected_directive_violation over field_shape_mismatch when both would fire`
- **Note:** Stated precedence rationale: naming the right op is more actionable than naming the
  shape. This is a genuine error-identity precedence rule, not an implementation accident.

### FMW-041 — A flat-scalar set against a block field is refused (field_shape_mismatch)

- **Behavior:** A `set_field` against a key currently holding a block list is refused with
  `field_shape_mismatch`, and the error names the field.
- **Trigger:** `set_field nav.contents value=x` against a note whose `nav.contents` is a list;
  error data carries `field: nav.contents`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:304` — `refuses a flat-scalar set against a detected block field with field_shape_mismatch`
- **Note:** Shape is *detected from the note*, so the same op is legal against the same key on a
  note where it is a scalar. This refusal is what makes the nav entry ops necessary (see FMW-046).

### FMW-042 — strip_directive removes one named pinned entry

- **Behavior:** `strip_directive kind=pinned` with text removes exactly the pinned entry whose text
  matches, leaving the other entries in the list.
- **Trigger:** Note with pinned `Keep` and pinned `Drop`; `strip_directive pinned "Drop"` leaves
  only `Keep` (re-emitted unquoted by the emitter's rules).
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:311` — `strips a named pinned entry through strip_directive, the explicit path`
- **Note:** The result also shows the block is reserialized when a frontmatter op ran: the
  surviving entry's hand-quoting (`"Keep"`) becomes the emitter's form (`Keep`). Asymmetry worth
  flagging: per the implementation, `strip_directive kind=use_skill` takes no text and removes
  *every* `use_skill` entry at once — not asserted by this suite.

### FMW-043 — add_nav_entry appends a link entry at the end of the link run

- **Behavior:** With no anchor, a link entry is inserted at the end of the run of link entries —
  before any folder entries — and the applied record names the nav key as the op's field with
  changed true. No schema violation results.
- **Trigger:** `add_nav_entry key=nav.contents link='[[Design]]' description='The design'` on a note
  whose list is `link [[Tasks]]`, then `folder Inbox`; the new entry lands between them.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:327` — `adds a link entry at the end of the link run and reports the nav key as the op field`
- **Note:** Link-run grouping is the subtle part: the default insert point for a link is *not* the
  end of the list. Contrast FMW-044.

### FMW-044 — add_nav_entry appends a folder entry at the end of the list, with load_when

- **Behavior:** With no anchor, a folder entry is appended at the very end of the list; the
  optional `load_when` argument is emitted as a third member of the entry, after `description`.
- **Trigger:** `add_nav_entry key=nav.contents folder=Archive description='Completed work'
  load_when='referencing past work'`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:337` — `adds a folder entry at the end of the list, with load_when`
- **Note:** Entry member order in the emitted YAML is `link|folder`, `description`, `load_when`.

### FMW-045 — before and after anchor the insertion against an existing entry

- **Behavior:** `before` places the new entry immediately ahead of the entry naming that target;
  `after` places it immediately behind. The anchor is named by target value (link text or folder
  name), not by index.
- **Trigger:** `before = '[[Tasks]]'` puts the new link first in the list; `after = 'Inbox'` puts
  the new link straight after the `Inbox` folder entry.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:344` — `honours before and after against an existing entry target`
- **Note:** The `after` case shows an anchor overrides the link-run grouping of FMW-043 — a link
  entry can land after a folder entry when anchored.

### FMW-046 — add_nav_entry creates the key when the note carries none

- **Behavior:** `add_nav_entry` against a note with no such key creates the key with the single
  entry, appended after the existing frontmatter keys, with no schema violation.
- **Trigger:** `add_nav_entry key=nav.contents link='[[Tasks]]' description='Task list'` on a note
  carrying only `doc.type`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:353` — `creates the key when the note carries none - lint's folder-note completeness case`
- **Note:** The test records the gap these ops close: `set_field` against a block key is
  `field_shape_mismatch` by design (FMW-041), and a body patch is body-confined, so before these
  ops existed there was no way to create a nav list at all.

### FMW-047 — remove_nav_entry removes by target and drops the key when the list empties

- **Behavior:** An entry is addressed by its target value and removed; when that leaves the list
  empty the key itself is dropped from the frontmatter. The record names the nav key with changed
  true.
- **Trigger:** `remove_nav_entry key=nav.contents link='[[X]]'` on a one-entry list; result carries
  only `doc.type`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:362` — `removes an entry addressed by its target and drops the key when that empties the list`
- **Note:** Related: per the implementation, an entirely emptied frontmatter dictionary emits no
  fence at all rather than an empty `---`/`---` pair (see FMW-076 for the create-side assertion).

### FMW-048 — Order and repetition are semantic; each op sees the previous op's result

- **Behavior:** Multiple nav ops in one call apply in sequence, each against the result of the
  last, including an `after` anchor naming an entry an earlier op in the same call added.
- **Trigger:** add `[[A]]`, then add `[[B]] after '[[A]]'`, then remove `[[Tasks]]`. Three records
  returned; final list is A, B, then the pre-existing folder entry.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:370` — `honours order and repetition: each op sees the previous op's result`
- **Note:** This is why entries are addressed by target rather than index — an index would name a
  different entry once an earlier op shifted the list.

### FMW-049 — A duplicate nav add is idempotent: nothing mutated, nothing asserted

- **Behavior:** Adding an entry whose target already exists in the list changes nothing (even when
  the op supplies a *different* description), records changed false, and asserts no capability.
- **Trigger:** `add_nav_entry link='[[Tasks]]' description='a different description'` against a list
  already carrying `[[Tasks]]`; content byte-identical to the input, `CapabilitiesUsed` empty even
  though the caller holds only `content:write`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:380` — `is idempotent on a duplicate add: changed false, nothing mutated, nothing asserted`
- **Note:** Idempotence is by target identity only — the differing description is *not* applied and
  is *not* an error. A reconciler should check whether the new suite treats this as an update.

### FMW-050 — An absent-target nav removal is idempotent

- **Behavior:** Removing an entry whose target is not present changes nothing, records changed
  false, and asserts no capability — it is not a refusal.
- **Trigger:** `remove_nav_entry key=nav.contents folder='Nowhere'`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:389` — `is idempotent on an absent-target removal: changed false, nothing asserted`

### FMW-051 — An ambiguous nav removal is refused rather than guessed

- **Behavior:** A removal target matching more than one entry under the key is refused with
  `malformed_op` naming the op index, rather than removing one of them.
- **Trigger:** A note with two `[[A]]` link entries; ops are an add then `remove_nav_entry link
  '[[A]]'`; error carries `opIndex: 1`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:398` — `refuses an ambiguous removal with malformed_op and the op index, rather than guessing`
- **Note:** Also all-or-nothing evidence: the earlier successful add is discarded when a later op
  throws.

### FMW-052 — A key outside the nav block-list allow-list is refused

- **Behavior:** The nav entry ops reach only the declared nav block-list keys; any other key,
  including `agent.instructions`, is refused with `malformed_op` naming the op index.
- **Trigger:** `add_nav_entry key='agent.instructions'`; `opIndex: 0`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:409` — `refuses a key outside the nav block-list allow-list, with the op index`
- **Note:** Closes the obvious route around FMW-039: directives cannot be authored through the nav
  entry ops either.

### FMW-053 — The nav key is canonicalized in both the written content and the record

- **Behavior:** However the caller cases the key, the canonical spelling is what is written to the
  note and what is reported in the applied-ops record.
- **Trigger:** `key = 'Nav.See_Also'`; content carries `nav.see_also:` and the record's field is
  `nav.see_also`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:417` — `reports and writes the canonical key spelling whatever case the op used`
- **Note:** Canonicalization happens before anything reads or reports the key, so a caller's casing
  updates an existing key rather than minting a second one beside it.

### FMW-054 — link and folder are mutually exclusive in both directions, in both nav ops

- **Behavior:** Supplying both `link` and `folder`, or neither, is refused with `malformed_op`.
  Holds for `add_nav_entry` and `remove_nav_entry` alike.
- **Trigger:** Four rows: add with both; add with neither; remove with both; remove with neither.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:424` — `refuses link and folder together, and neither, in both ops`

### FMW-055 — A missing or blank nav description is refused

- **Behavior:** `add_nav_entry` requires a description that is non-empty and not whitespace-only;
  otherwise `malformed_op`.
- **Trigger:** Two rows: `description` omitted entirely; `description = '  '`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:435` — `refuses a missing or blank description`

### FMW-056 — before and after together are refused

- **Behavior:** The two anchor arguments are mutually exclusive; supplying both is `malformed_op`.
- **Trigger:** `before='[[Tasks]]'` and `after='Inbox'` on one add.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:440` — `refuses before and after together`

### FMW-057 — An anchor naming no existing entry is refused

- **Behavior:** An anchor target that matches no entry in the list is refused with `malformed_op`,
  rather than falling back to the default insert position.
- **Trigger:** `before = '[[Nope]]'`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:446` — `refuses an anchor naming no existing entry`

### FMW-058 — A control character in any nav caller-text field is refused

- **Behavior:** The hygiene gate applies to every caller-text field of the nav ops, not just the
  ones that are written into the note. Each is `malformed_op`.
- **Trigger:** Eight rows — `add_nav_entry`: `link`, `folder`, `description`, `load_when`,
  `before`, `after`; `remove_nav_entry`: `link`, `folder`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:452` — `refuses a control character in any caller-text field`
- **Note:** `before`, `after` and the removal targets are *lookup* values that never reach disk, and
  they are still gated — the rule is per-field and exhaustive, not "fields that get written".

### FMW-059 — A line break in a nav caller-text field stays inside its entry

- **Behavior:** A newline in a description cannot become frontmatter structure: the pre-existing
  `doc.type` is unchanged, and only the nav key the op touched is reported as an agentic change.
- **Trigger:** `add_nav_entry key=nav.see_also link='[[X]]' description="x\ndoc.type: smuggled"`;
  reading `doc.type` back gives `Corpus`; agentic keys changed is exactly `nav.see_also`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:467` — `keeps a line break in a caller-text field inside its own entry - the op takes fields, not block text`
- **Note:** Records the design reason the op is structured rather than taking pre-rendered block
  text: each field is a value in the tree the emitter escapes.

### FMW-060 — Ops may arrive as deserialized JSON objects, not only native maps

- **Behavior:** An ops array deserialized from JSON is accepted equivalently to one built natively,
  including the snake_case `load_when` member.
- **Trigger:** JSON `[{"op":"add_nav_entry","key":"nav.see_also","link":"[[Y]]","description":"why","load_when":"asked about Y"}]`.
- **Kind:** `artifact`
- **Source:** `FrontmatterWrite.Tests.ps1:476` — `accepts an ops array deserialized from JSON, not just hashtables`
- **Note:** Marked artifact because "hashtable vs. PSCustomObject" is a PowerShell type concern. The
  portable residue is real though: **op member names are snake_case on the wire** (`load_when`,
  `set_field`, `add_nav_entry`), and the write path is fed straight from a tool's JSON arguments.

### FMW-061 — A mixed-ending note is written out as LF throughout

- **Behavior:** A note whose lines mix CRLF and LF endings — inside the frontmatter block as well
  as the body — is emitted entirely with LF.
- **Trigger:** Mixed-ending note plus one `add_nav_entry`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:484` — `writes a mixed-ending note out as LF throughout, body included`
- **Note:** Also shows the reserialize on a frontmatter op normalizes hand-quoting throughout the
  block, not only in the entry the op touched.

### FMW-062 — A nav mutation asserts agentic:write with no per-op wiring

- **Behavior:** A nav entry op that changes the note reports the nav key as an agentic key changed
  and records `agentic:write` as the capability used; the same key is what the standalone diff
  predicate reports for the same before/after pair.
- **Trigger:** Caller holds `content:write` and `agentic:write`; `add_nav_entry folder=Archive`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:490` — `asserts agentic:write on a nav mutation - the ops inherit the gate with no wiring`
- **Note:** The point is that the gate is outcome-based, so a newly added op verb inherits it
  automatically. Worth pinning in a redesign: a target-based gate would need per-op wiring and would
  regress silently when a verb is added.

### FMW-063 — A nav mutation without the class is denied with zero mutation

- **Behavior:** Without `agentic:write` the nav mutation is refused with `insufficient_capability`
  naming the required class, and the caller is handed nothing at all.
- **Trigger:** Caller holds only `content:write`; `remove_nav_entry folder=Inbox`. Error data
  carries `requiredClass: 'agentic:write'`; the result variable stays null.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:499` — `denies a nav mutation with insufficient_capability and zero mutation without the class`

### FMW-064 — Adding an agentic key asserts agentic:write

- **Behavior:** An op that introduces an agentic key reports it as changed and records
  `agentic:write` as used.
- **Trigger:** `add_directive kind=pinned` on a note with no `agent.instructions`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:516` — `asserts agentic:write when a key is added`

### FMW-065 — Changing an agentic key's value asserts agentic:write

- **Behavior:** An op that changes the value of an existing agentic key reports it as changed.
- **Trigger:** `add_directive pinned 'B'` on a note whose `agent.instructions` holds pinned `A` —
  i.e. an add that appends a second entry.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:524` — `asserts agentic:write when a key changes value`
- **Note:** Incidentally pins that `add_directive` appends rather than replaces when the key exists
  with different text.

### FMW-066 — Removing an agentic key asserts agentic:write

- **Behavior:** An op that removes an agentic key reports it as changed and records `agentic:write`.
- **Trigger:** `remove_field nav.contents`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:531` — `asserts agentic:write when a key is removed`

### FMW-067 — A body-only write asserts nothing

- **Behavior:** Replacing only the body of a note that carries agentic keys reports no agentic key
  changed and uses no capability, even though the note's agentic layer is present.
- **Trigger:** Caller holds only `content:write`; `-Body` against a note with `nav.contents`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:538` — `asserts nothing for a body-only write`

### FMW-068 — A non-agentic doc.* scalar set asserts nothing

- **Behavior:** Setting a non-agentic scalar on a note that carries agentic keys reports no agentic
  change and uses no capability.
- **Trigger:** Caller holds only `content:write`; `set_field doc.created` on a note with
  `nav.contents`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:545` — `asserts nothing for a non-agentic doc.* scalar set - lint's whole fix class`
- **Note:** Mentions `lint` in passing; the behavior harvested is the write path's.

### FMW-069 — A duplicate directive add asserts nothing

- **Behavior:** An `add_directive` whose entry already exists verbatim changes nothing, records
  changed false, and uses no capability — so an idempotent replay does not require `agentic:write`.
- **Trigger:** Caller holds only `content:write`; `add_directive pinned 'Same'` on a note already
  carrying pinned `Same`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:552` — `is idempotent-safe: a duplicate directive add changes nothing and asserts nothing`
- **Note:** Idempotence here is on the *emitted* form — the fixture note hand-quotes `"Same"` and
  the op still compares equal, which means the comparison is post-serialization, not on raw bytes.

### FMW-070 — A denial is zero-mutation: the caller gets nothing and its own content is untouched

- **Behavior:** When the capability check fails, the caller receives no composed content at all and
  the content string it passed in is unmodified. The error names the required class.
- **Trigger:** Caller holds only `content:write`; `remove_field nav.contents`. Result null, original
  content byte-identical.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:560` — `denies with insufficient_capability and zero content change when the key is absent`
- **Note:** The check runs *after* composition but *before* the result is returned, so no partial
  content can escape. In-memory only; no file I/O is involved at any point.

### FMW-071 — An injected-but-empty class set denies

- **Behavior:** A caller that is authenticated but holds no op classes is denied
  `insufficient_capability` for an agentic write. "Holds nothing" is not "no context".
- **Trigger:** Op-class set injected as an empty collection; `add_hint text='Do X'`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:573` — `DENIES on an injected-but-empty class set (authenticated "holds nothing")`
- **Note:** Pairs with FMW-072 — the empty/absent distinction is the whole rule and is easy to lose
  in a reimplementation that models this as a nullable collection.

### FMW-072 — No injected host context at all degrades open

- **Behavior:** When no op-class context was injected (a standalone run outside the host), the
  agentic write proceeds and reports `agentic:write` as used.
- **Trigger:** Op-class variable removed entirely; `add_hint text='Do X'`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:579` — `degrades OPEN when no host context was injected at all (standalone run)`
- **Note:** Carrier is an ambient global (`$Global:VaultMcpOpClasses`) — that spelling is an
  artifact, shared with the Scope suite; the open/closed rule is the semantics. Also pins that
  `add_hint` writes into `agent.instructions` (as a bare entry, per the implementation).

### FMW-073 — The edit shape: content plus ops plus a resolved body, in one call

- **Behavior:** Content, ops and a replacement body compose in a single call: the new directive is
  appended to the frontmatter (after the existing keys, in emitted form) and the body is replaced,
  with `FrontmatterChanged` and `BodyChanged` both true, `agentic:write` used, zero violations.
- **Trigger:** `add_directive kind=use_skill skill='vault-docs' when='editing this file'` plus a
  patched body.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:592` — `edit: content plus ops plus a body resolved from the body facet`
- **Note:** Records the contract with `edit`: it resolves its own old_string/new_string against the
  *body facet* first and hands the write path the resulting body, so the fence guard (FMW-021)
  still sees what a patch did. This is the only assertion of `add_directive kind=use_skill`'s
  emitted shape: `- use_skill: <skill>` then `when: <when>`.

### FMW-074 — The create shape: no original, a whole supplied block, and a body

- **Behavior:** With empty original content, a supplied block and a body, the result is emitted
  from the validated tree — the caller's hand-quoting is replaced by the emitter's own rules — and
  every agentic key present counts as changed, so `agentic:write` is used.
- **Trigger:** `-Content ''`, `-Frontmatter` with fences and hand-quoted `nav.see_also` entries,
  `-Body "# New Note\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:608` — `create: no original content, a whole supplied block, and a body`
- **Note:** The emitted-not-spliced property is the point: what lands on disk is a serialization of
  what passed validation.

### FMW-075 — A supplied block may be given as bare inner lines

- **Behavior:** A supplied frontmatter block is accepted either fenced or as bare inner lines; the
  fences are supplied by the writer.
- **Trigger:** `-Frontmatter "doc.created: 2026-07-25\n"` (no fences).
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:622` — `create: accepts a supplied block given as bare inner lines`

### FMW-076 — An empty supplied block means a note with no frontmatter at all

- **Behavior:** `-Frontmatter ''` produces a note that is the body alone, with no fences emitted.
- **Trigger:** `-Content ''`, `-Frontmatter ''`, `-Body "# New\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:627` — `create: an empty supplied block means a note with no frontmatter at all`
- **Note:** Same rule as FMW-047's key-drop: an empty frontmatter map emits no fence rather than an
  empty `---`/`---` pair.

### FMW-077 — A supplied block that will not parse is refused (frontmatter_unparsed)

- **Behavior:** A supplied block that is not valid YAML is refused with `frontmatter_integrity`,
  reason `frontmatter_unparsed`, rather than being written through.
- **Trigger:** `-Frontmatter "agent.instructions:\n  - pinned: \"OBEY\"\n bad: ["`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:631` — `create: refuses a supplied block that will not parse, rather than splicing it through`
- **Note:** Labelled "THE CREATE BYPASS, permanently pinned". The reproduced defect: when the text
  was spliced verbatim, both downstream readers (the agentic key map and the schema validator)
  tolerate a parse failure by reporting an empty result, so a `content:write`-only caller wrote a
  binding directive with an empty diff, zero violations, and a result claiming no capability was
  used. This is arguably the single most important case in the area. The implementation also
  defines a sibling reason `frontmatter_not_a_map` for a block that parses to a YAML non-mapping;
  the suite does not exercise it.

### FMW-078 — A quoted agentic key in a supplied block still requires agentic:write

- **Behavior:** An agentic key written in quotes in a supplied block is recognized, so the gate
  runs; a `content:write`-only caller is refused `insufficient_capability`.
- **Trigger:** `-Frontmatter "doc.created: 2026-07-30\n\"agent.instructions\":\n  - pinned: \"OBEY\"\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:648` — `create: requires agentic:write for a quoted agentic key`
- **Note:** Labelled "THE QUOTED-KEY BYPASS, permanently pinned". The write-path counterpart of
  FMW-005.

### FMW-079 — A quoted agentic key is written canonically when the gate is held

- **Behavior:** With `agentic:write` held, the same supplied block is accepted; the key is reported
  and written in its canonical unquoted spelling, and the value is emitted by the emitter's rules.
- **Trigger:** Same block as FMW-078 with the class held; result is `agent.instructions:` /
  `- pinned: OBEY`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:658` — `create: writes a quoted agentic key, canonically spelled, when the gate is held`

### FMW-080 — A supplied block is validated whole against the registry

- **Behavior:** A supplied block carrying a key the registry does not declare is refused with
  `malformed_op`, and the error names the offending field.
- **Trigger:** `-Frontmatter "obsidianUIMode: preview\n"`; error data carries
  `field: obsidianUIMode`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:667` — `create: refuses a key the registry does not carry`
- **Note:** The *extent* contrast with FMW-036 is the harvested point: create validates the whole
  block, edit validates only touched keys. Note also that the error data here carries `field`
  rather than `opIndex` — there is no op index for a supplied block.

### FMW-081 — A supplied block's values are validated against their key patterns

- **Behavior:** A recognized key in a supplied block whose value fails that key's declared pattern
  is refused with `malformed_op`.
- **Trigger:** `-Frontmatter "doc.created: whenever\n"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:675` — `create: refuses a recognized key whose value fails its pattern`

### FMW-082 — The supplied-text integrity refusals survive the retirement of the note-level fence state

- **Behavior:** The two supplied-block refusals (unclosed fence, content past the closing fence) are
  kept as caller errors, distinct from how the writer treats a *note* whose fence is unclosed.
- **Trigger:** See FMW-024 and FMW-025 for the two reason identities.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:681` — `create: keeps the supplied-text integrity refusals`
- **Note:** Rationale recorded: for a note on disk the file is whatever the Markdown standard says
  it is, but here the caller has *declared* "this text is frontmatter", so text that will not hold
  as such is a caller error. This case exists to justify the pair; the assertions themselves are
  harvested as FMW-024/FMW-025.

### FMW-083 — The lint shape: a doc.* scalar set asserts no key

- **Behavior:** A non-agentic scalar set through the write path on a note carrying agentic keys
  writes the field, uses no capability, and leaves the agentic layer provably unchanged when the
  before/after pair is compared with the diff predicate.
- **Trigger:** Caller holds `content:read` and `content:write`; `set_field doc.archived`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:691` — `lint: a doc.* scalar set through the wrapper, asserting no key`
- **Note:** Mentions `lint` in passing (out-of-scope tool), but the assertion is about the write
  path. The test deliberately asserts the property over the fix itself rather than over a denial,
  because a denial varies with the caller's grant.

### FMW-084 — The relative path is required, so the path refusal cannot be skipped by omitting it

- **Behavior:** The note's vault-relative path is a required input to any frontmatter write;
  omitting it is a binding failure, not a silently un-run check.
- **Trigger:** Inspecting the parameter's mandatory attribute.
- **Kind:** `artifact`
- **Source:** `FrontmatterWrite.Tests.ps1:708` — `requires -RelativePath, so the refusal cannot be skipped by omitting it`
- **Note:** The assertion mechanism is PowerShell reflection, hence artifact — but the rule it
  encodes is portable and worth restating: *an optional parameter may gate an optional check but
  never a refusal, because absence would read as permission.*

### FMW-085 — An edit-shaped write under the excluded prefix is refused

- **Behavior:** A write whose path falls under an excluded prefix is refused with
  `frontmatter_path_excluded`, and the error names the path.
- **Trigger:** `-RelativePath '.claude/skills/vault-docs/SKILL.md'` plus a `set_field` op; error
  data carries the path.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:717` — `refuses an edit-shaped write under .claude/skills with frontmatter_path_excluded`
- **Note:** The implementation records why this is its own code rather than a reused out-of-scope
  code: out-of-scope means the caller's *grant* does not reach the path and widening it would help,
  which is exactly the wrong thing to tell a caller here. The error data also carries the full list
  of excluded prefixes.

### FMW-086 — A create-shaped write under the excluded prefix is refused

- **Behavior:** The refusal covers creation as well as editing.
- **Trigger:** `-RelativePath '.claude/skills/new-skill/SKILL.md'`, `-Content ''`, a supplied block
  and a body.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:725` — `refuses a create-shaped write under .claude/skills`

### FMW-087 — A body-only write under the excluded prefix is refused

- **Behavior:** The refusal is about the file, not about what the write would have done to it: a
  write touching no frontmatter key at all is still refused.
- **Trigger:** `-RelativePath '.claude/skills/vault-mode/SKILL.md'` with `-Body` only.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:731` — `refuses a body-only write under .claude/skills, which touches no key at all`

### FMW-088 — The path refusal runs before anything is composed, parsed, or asserted

- **Behavior:** The excluded-path check is the first thing that happens: input that would otherwise
  produce a different refusal (an unparseable supplied block) still comes back as the path refusal.
- **Trigger:** Excluded path plus the unparseable block of FMW-077; the code is
  `frontmatter_path_excluded`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:738` — `refuses before anything is composed, parsed, or asserted`
- **Note:** Explicitly "zero work, not merely zero mutation" — the refusal-ordering is observable
  and is asserted as such. A reconciler should treat this as a rule about *stage order*, which is
  the kind of thing a spec-derived suite typically omits.

### FMW-089 — The exclusion holds however the path is spelled

- **Behavior:** The excluded-prefix match is case-insensitive, separator-insensitive, and applied
  after normalizing traversal segments.
- **Trigger:** Three rows, each refused: `.CLAUDE/Skills/x/SKILL.md` (case),
  `.claude\skills\x\SKILL.md` (backslash separators), `Areas/../.claude/skills/x/SKILL.md`
  (`..` traversal into the prefix).
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:748` — `refuses however the excluded path is spelled`

### FMW-090 — A sibling directory under the same dot-directory stays writable

- **Behavior:** The exclusion is the skills prefix specifically, not the whole dot-directory: a
  write under `.claude/guides/` succeeds.
- **Trigger:** `-RelativePath '.claude/guides/Vault-Guide.md'` plus a `set_field`.
- **Kind:** `semantic`
- **Source:** `FrontmatterWrite.Tests.ps1:756` — `leaves .claude/guides writable - the exclusion is skills, not the dot directory`
- **Note:** The negative case that fixes the prefix's boundary; without it, "exclude `.claude/`"
  would pass every other case in the group.
