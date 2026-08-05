# Reconciliation: The `edit` tool

Harvest source: `08-Edit-Harvest.md` (123 cases)
Implementation read: `src/VaultShared/Tools/EditTool.cs`, `src/VaultShared/Tools/EditArguments.cs`,
`src/VaultShared/Tools/MutationSupport.cs`, `src/VaultShared/Tools/ToolExecution.cs`,
`src/VaultShared/Tools/ToolPaths.cs`, `src/VaultShared/Tools/VaultWriteLock.cs`,
`src/VaultShared/Frontmatter/FrontmatterWrite.cs`, `src/VaultShared/Frontmatter/NoteSplit.cs`,
`src/VaultShared/Frontmatter/EntryResolver.cs`, `src/VaultShared/Frontmatter/FrontmatterOperation.cs`,
`src/VaultShared/Frontmatter/FrontmatterComposer.cs`, `src/VaultShared/Access/VaultFiles.cs`,
`frontmatter-registry.json`
Tests read: `tests/VaultShared.Tests/Tools/EditToolTests.cs`,
`tests/VaultShared.Tests/Tools/EditArgumentTests.cs`, `tests/VaultShared.Tests/Tools/EditSchemaTests.cs`,
`tests/VaultShared.Tests/Frontmatter/FrontmatterOperationTests.cs`,
`tests/VaultShared.Tests/Frontmatter/FrontmatterWritePathTests.cs`,
`tests/VaultShared.Tests/Frontmatter/FrontmatterRefusalTests.cs`,
`tests/VaultMcpServer.Tests/EditDispatchTests.cs`
Design/spec consulted: `docs/spec/Spec-Frontmatter-Substrate.md`, `docs/spec/Spec-Frontmatter-Registry.md`,
`docs/spec/Spec-Tool-Capabilities.md`, `docs/spec/Spec-Access-Layer-Capabilities.md`,
`docs/design/Design-Frontmatter-Surface.md`, `docs/design/Design-Tool-Vocabulary.md`,
`docs/Build-Decisions.md`

## Summary

Cases: 123. MATCH 85 · IMPROVED 16 · N/A 20 · REGRESSION 0 · GAP 2 · UNRESOLVED 0

This area carried over faithfully, and in several places got stronger. Nothing in it is a regression.

The three things the task flagged came out clean. **EDT-113** — the position anchors — is fully
present and is the best-implemented case in the area: `before`/`after` are mutually exclusive *by
construction* (one optional `Anchor` object whose `op` is the placement, `EditArguments.cs:176`),
and an anchor resolving to zero or several entries is a refusal rather than a fallback
(`FrontmatterWrite.cs:514-521`, `UnresolvedIdentity` / `AmbiguousIdentity`). Both refusals are
tested at the substrate (`FrontmatterOperationTests.cs:202,213`) and the zero case again through the
tool (`EditToolTests.cs:444`). The rule is closed in `Spec-Frontmatter-Substrate.md:96`. The old
suite tested none of this; the new one does.

**EDT-119**, the atomicity contract, genuinely holds rather than merely appearing to. Every refusal
in `FrontmatterWrite.Apply` precedes composition, authority is asserted before anything is
serialized, `EditTool.Invoke` calls `Store` exactly once with the single composed string, and a
composed result equal to the stored bytes is not written at all (`EditTool.cs:301-306`). The write
path's phase ordering is stated as a contract in its own type comment, not left implicit.

**EDT-074 / EDT-077 and the retired-and-replaced set: none of the retired refusals came back.** A
line break in a value is explicitly exempted from the control-character refusal
(`FrontmatterOperation.cs:198`, with the comment saying so); quotes are explicitly out of scope; LF
normalization is unconditional and on the way in (`NoteSplit.Normalize`), which is EDT-043/044/045
exactly; and the apparent-fence note still refuses a frontmatter op while accepting a patch
(EDT-015 / EDT-083), which falls out of the guard's `ran` flag rather than from two rules.

The one thing to look at first is **EDT-085**: the write path computes the schema violations under
the keys an operation touched, and `edit` discards them. EDT-076's half of the extent split holds
perfectly; EDT-085's half is gone in both its forms — the refusal and any reporting.

## Findings

### GAP — EDT-085 — Schema violations are computed and then thrown away

- **Old behavior:** An op was refused `schema_validation_failed` when the resulting frontmatter
  violated the schema, carrying `field`, `rule` and a 1-based `entryIndex`, in the validate phase
  with nothing written. The harvest's example: `set_field doc.created` refused because a
  pre-existing `nav.see_also` entry lacked a description.
- **New behavior:** `FrontmatterWrite.Apply` collects violations for the touched keys
  (`FrontmatterWrite.cs:82-87`) and returns them on `WriteOutcome.Violations`. `EditTool.Invoke`
  never reads that member, and `EditResult` (`EditTool.cs:34-40`) has no field for it. Its remark
  states the choice: "There is deliberately no member reporting schema violations, and an edit never
  refuses over one."
- **What is lost:** Not the refusal — dropping that is right, and `Spec-Frontmatter-Registry.md:87`
  and `Spec-Frontmatter-Substrate.md:145` close it (whole-block validation on the edit path makes a
  note unwritable because someone else broke an entry in it). What is lost is the **signal**. A
  caller that adds an entry to `nav.contents` whose existing entries are malformed gets a clean
  success and no indication that the list it just wrote into is broken. The old system was the only
  thing that would ever tell it. Nothing else in the tool surface reports per-note violations on a
  write, and the data is already sitting in the outcome one field away.
- **Covered by a test?** No test asserts either behavior — neither that a pre-existing violation
  fails to refuse, nor that it is reported. `EditToolTests` has no violation case at all.
- **Recorded anywhere?** The *refusal* half is closed by the two spec sections above and by the
  `EditResult` remark. The *reporting* half is recorded nowhere and argued nowhere: the remark
  argues only against refusing. `Spec-Frontmatter-Substrate.md:143` says "a single-note tool treats
  a non-empty list as failure", which `edit` does not do and which no design document reconciles.

### GAP — EDT-040 — The staleness guard can no longer be applied to a frontmatter op or a substitution

- **Old behavior:** `expected_sha` was *optional* on frontmatter ops and patches (EDT-067) but
  honoured when supplied (EDT-040), so a caller could make any facet conditional on the file being
  unchanged since its read.
- **New behavior:** `expected_content_hash` sent without a wholesale body replacement is an argument
  fault — refused, not ignored (`EditArguments.cs:79-84`), and tested
  (`EditArgumentTests.cs:103`). The only guarded facet is `body.content`.
- **What is lost:** A caller can no longer express a guarded read-modify-write on a scalar. Read a
  note, decide `set doc.type` from what it said, and the set now lands on whatever the file says at
  write time — with no way to say "abort if it moved". Entry operations are largely covered by
  identity addressing, so the exposure is real but narrow: it is `set` and `remove` on a scalar
  whose new value was derived from the old one.
- **Covered by a test?** The refusal is asserted; the lost capability is by definition not.
- **Recorded anywhere?** Yes, and deliberately: `Design-Tool-Vocabulary.md:192` ("required for a
  wholesale body replacement, and no other argument record takes one") and `Build-Decisions.md:148`
  (both halves of the shape rule are argument faults). Raised anyway because the stated reasoning —
  `EditArguments.cs:55-56`, "a hash without a wholesale replacement cannot change any outcome" — is
  not correct: it changes the outcome by refusing, which is the whole point of a guard. The
  operator may well still want the narrower surface; the justification is the part that does not
  hold up.

## Present in code, unasserted by the suite

- A body-only edit against a note whose frontmatter block will not parse now succeeds
  (`FrontmatterWrite.cs:58-63`). The comment justifies it; no test pins it.
- The dot-segment exclusion (EDT-095..EDT-097) is asserted at the access layer and at
  `FrontmatterRefusalTests.cs:99`, but no case drives it through `EditTool` — so nothing pins that a
  body-only edit to an excluded path is refused, which is EDT-096's exact claim.
- `Store`'s skip-the-write-when-nothing-changed path (`EditTool.cs:303`) is asserted for the hash it
  answers (`ANoOpAnswersTheHashTheFileAlreadyHad`) but not for the file's timestamp being left alone,
  which is what the comment claims it is for.
- The `AmbiguousIdentity` refusal for an *anchor* naming several entries is tested at the substrate
  but not through the tool; only the zero case reaches `EditToolTests`.

## Dismissed

- **EDT-003** — `opsApplied` echoed op/field/changed; now `applied_operations` is the indices of the
  operations that changed something — the caller holds its own list, so the echo was redundant.
- **EDT-004** — `capabilitiesUsed`/`filesSkippedUnreadable` always-present arrays — envelope shape,
  redesigned wholesale; `asserted_authority` is always present and empty-when-empty.
- **EDT-005** — `filesSkippedUnreadable` structurally empty for edit — the field does not exist.
- **EDT-006** — `lockWarning` omitted unless abandoned — the inherited-lock signal is a host callback
  (`ToolExecution`'s `onInheritedLock`) rather than a result member.
- **EDT-008** — `resultingFrontmatter` echoed the block — dropped; `changed_keys` plus
  `applied_operations` serve the stated motivation (telling a silently-idempotent op from an applied
  one) more directly, at the cost of a second read for the block's text.
- **EDT-016** — empty `resultingFrontmatter` on a block-less note — no such field.
- **EDT-018** — per-op `changed` flags in submitted order — same information as the applied-index
  list.
- **EDT-024** — `matchCount` saturating at 2 — the new count is exact (`EditTool.cs:277`) and is in
  the payload.
- **EDT-032** — `kind` omitted rather than nulled in `opsApplied` — no such member.
- **EDT-037** — `frontmatter_ops` as a JSON string — operations are structured on the wire, which
  removes the whole class of parse failure.
- **EDT-039** — `state_mismatch` carried `expectedSha`/`actualSha` — the new refusal carries no
  payload, deliberately, so a caller cannot retry into content it never read
  (`Spec-Tool-Capabilities.md:170`).
- **EDT-051** — an injected-but-empty class set denied — the grant is always present and its classes
  are checked; there is no empty-vs-absent distinction to collapse.
- **EDT-052** — a body-only write never asserted the agentic key — it now can, where path policy's
  `mutation_requires` names the note, because such a file's body *is* the instruction
  (`Spec-Tool-Capabilities.md:Authority`). Strictly stronger.
- **EDT-054** — no injected class set degraded open — the fail-open is gone. Per the operator's
  ruling, an improvement on a containment boundary.
- **EDT-069** — unparseable `frontmatter_ops` JSON was `malformed_op` — the transport is structured;
  a shape mismatch is a binder refusal above the tool.
- **EDT-070** — a bare object rather than an array — same; the binder types it.
- **EDT-071** — a one-element array not collapsed — the array-unwrapping hazard does not exist here.
- **EDT-080** — `protected_directive_violation` beat `field_shape_mismatch` — neither identity
  exists; the closed `RefusalReason` set has no overlapping pair to order.
- **EDT-086** — the path-dependent `Tasks.md` rule — the validator takes no path
  (`FrontmatterValidator.Validate(content, registry)`) and the declaration has no path-conditional
  rule kind. No counterpart by architecture.
- **EDT-093** — the `content:write` floor declared in comment-help — the rule survives
  (`EditTool.Declaration.OpClassFloor`); the comment-help parsing does not.
- **EDT-094** — every parameter documented, seven of them — the argument record is the surface and
  `CS1573` enforces the documentation; the parameter list differs by design.
- **EDT-098** — `.claude/guides` stayed editable — every dot segment is now unreachable
  (`Spec-Access-Layer-Capabilities.md:44`). A narrowing, fixed in code rather than declared.
- **EDT-099** — standalone `pwsh -File` invocation — the CLI front-end is the standalone surface, and
  parity between the two is asserted by `FrontEndParityTests`.
- **EDT-100** — the staleness guard with no host present — same; the guard is caller-carried and the
  CLI runs the identical execute path.
- **EDT-101** — errors as JSON on stderr with a nonzero exit — front-end rendering, redesigned.
- **EDT-102** — the `.SCHEMA` side-car's own description — the schema is derived from the argument
  record and the vault's declaration; there is no hand-authored side-car to drift.
- **EDT-103** — branches pinned on a `const` op — variants are told apart by which field is present,
  and `anyOf` is used rather than `oneOf` on purpose (`EditTool.cs:119-122`): two declarations
  describing the same shape would make an exclusive alternation advertise a valid payload as invalid.
- **EDT-104** — advertised branch set equals the implemented set — true by construction now: the
  branches are generated from the loaded declaration the write path validates against, so they
  cannot disagree.
- **EDT-105** — the nav key enum restated the substrate's allow-list — the branch names its accepting
  keys from the declaration (`EditTool.Accepted`), so nothing is restated.
- **EDT-107** — the drift-probe fixture — no drift guard to seed.
- **EDT-110** — the `[[Note-Name]]` bracket form as convention, not validation — it is now a declared
  value constraint kind (`"kind": "wikilink"`), so it is enforced. A tightening the harvest predicted.
- **EDT-114** — default insert position grouped links before folders — placement is now explicit or
  an append, with ordering conventions belonging to the composing tool
  (`Spec-Frontmatter-Substrate.md:97`). Anchors give a caller the position the rule used to guess.
- **EDT-117** — `strip_directive`'s asymmetry across kinds — generalized: identity comparison and
  whether a variant clears wholesale are both declared per variant (`identity_match`,
  `removable_wholesale`), so the asymmetry is data rather than a hardcoded pair.
- **EDT-118** — `use_skill` entries distinguished by their `when` — `identity` is a field *list* in
  the declaration, so `["use_skill","when"]` is expressible; the committed fixture happens to declare
  identity on the skill alone. The declaration's business, not the code's.
- **EDT-122** — the per-test scratch vault fixture — fixture shape.
- **EDT-123** — the specific fixture notes several cases depend on — fixture shape.
