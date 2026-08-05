# Reconciliation: Frontmatter core — block grammar, parsing, note split, preservation

Harvest source: `03-Frontmatter-Core-Harvest.md` (131 cases, `FMC-001`–`FMC-131`)

Implementation read:
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\NoteSplit.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\FrontmatterTree.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\FrontmatterComposer.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\FrontmatterWrite.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\FrontmatterOperation.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\FrontmatterChecks.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\EntryResolver.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\FrontmatterRegistry.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Frontmatter\ValueConstraint.cs`
- `C:\Repos\vault-server-redesign\src\VaultShared\Access\VaultFiles.cs` (BOM and terminator handling only)
- `C:\Repos\vault-server-redesign\frontmatter-registry.json` (the committed fixture declaration)

Tests read:
- `C:\Repos\vault-server-redesign\tests\VaultShared.Tests\Frontmatter\NoteSplitTests.cs`
- `C:\Repos\vault-server-redesign\tests\VaultShared.Tests\Frontmatter\FrontmatterComposerTests.cs`
- `C:\Repos\vault-server-redesign\tests\VaultShared.Tests\Frontmatter\FrontmatterTreeTests.cs`
- `C:\Repos\vault-server-redesign\tests\VaultShared.Tests\Frontmatter\FrontmatterWritePathTests.cs`
- `C:\Repos\vault-server-redesign\tests\VaultShared.Tests\Frontmatter\FrontmatterOperationTests.cs`
- `C:\Repos\vault-server-redesign\tests\VaultShared.Tests\Frontmatter\FrontmatterRefusalTests.cs`
- `C:\Repos\vault-server-redesign\tests\VaultShared.Tests\Frontmatter\SyntheticVocabulary.cs`

Design/spec consulted: `docs/spec/Spec-Frontmatter-Substrate.md`, `docs/spec/Spec-Frontmatter-Registry.md`,
`docs/design/Design-Frontmatter-Surface.md`.

Verification beyond reading: a throwaway console project outside the repository, referencing
`VaultShared.csproj` unmodified, was used to run six probes through `FrontmatterWrite.Apply`. Nothing
in the repository was changed. The probe outputs are quoted verbatim in the findings below.

## Summary

Cases: 131. MATCH 109 · IMPROVED 15 · N/A 0 · REGRESSION 2 · GAP 5 · UNRESOLVED 0

This area carried over unusually faithfully, and the preservation invariants — the cluster the
operator asked me to concentrate on — are all intact and, in two places, stronger than the old
system's.

- **FMC-033 / FMC-051 hold at the byte level, not the equivalence level.** `FrontmatterWrite.cs:105`
  returns `request.Content` itself on a no-op, before normalization and before any compose, and
  `FrontmatterWritePathTests.cs:132` asserts it with `Assert.Same` — reference identity, which is the
  strongest form of the claim available. A duplicate add, a set to the value already there, a removal
  of an absent key, and an operation list that applies nothing all route through that one return.
- **FMC-126 holds, and the latent defect the harvest flagged beside it is closed.** The locator is a
  line-level scan (`NoteSplit.cs:70-93`) with no YAML awareness, so a `---` inside a block scalar
  still closes. The composition the old suite never tested — a value whose text contains a bare `---`
  line — now round-trips: the emitter writes it as an indented folded scalar, the column-zero fence
  rule cannot see the indented `---`, and the write path's re-split guard (`FrontmatterWrite.cs:126`)
  would refuse it anyway if it ever did.
- **FMC-118 and FMC-119 hold exactly.** `...` closes (`NoteSplit.cs:138`); trailing whitespace is
  discounted and leading whitespace never is (`NoteSplit.cs:130-135`), so no indented closer is a
  fence at any width, which is what FMC-119's correction established against the old source.

The one thing to look at first is the **regression on empty text in a block-list entry field**. The
old system refused a blank instruction and a blank nav description; the new write path applies its
emptiness test to keys, anchors, removal targets and bare-scalar payloads, but not to a mapping
payload's field values. The result is an entry that can be created and then cannot be removed by any
operation — verified, not inferred.

The three GAPs are lower-grade and two of them are arguably the tool layer's to answer, not this
one's; I have said so in each.

## Findings

### REGRESSION — FMC-052, FMC-079 — An entry field may be added empty, and the entry is then unremovable

- **Old behavior:** An instruction entry with no text, or with all-blank text, was refused
  (`malformed_op`, whitespace-sensitive per FMC-052's note). A nav entry whose description was
  whitespace-only was refused as if it were missing (FMC-079, asserted with `'   '`).
- **New behavior:** `FrontmatterWrite.ValidatePayload` (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:333-341`)
  applies `OperationText.IsEmpty` to a mapping field's **name** and never to its **value**; only the
  bare-scalar payload gets the value test (`:330`). `EntryFieldValue` trims the value to `""`
  (`FrontmatterOperation.cs:108`), and `FrontmatterChecks.CheckEntry` treats a present-but-empty
  field as present, because the required-field test is `entry.Field(name) is null`
  (`FrontmatterChecks.cs:211`). A `StringConstraint` — what the committed declaration gives
  `description` and `rule` (`frontmatter-registry.json`) — admits `""`.

  Probe, against a `pinned`-style variant declared `required: true` with a string constraint:

  ```
  AddEntry("agent.instructions", Mapping[("pinned", "   ")])
    → ACCEPTED: "---\nagent.instructions:\n  - pinned: ''\n---\nBody.\n", violations = 0
  RemoveEntry(..., Identity(""))
    → REFUSED ValueRefused ("The removal names no entry.")
  ```
- **What is lost:** Two things. First, the refusal itself: a caller can write `description: ''` or
  `pinned: ''` into a real note and the outcome reports **zero violations**, so even a single-note
  tool that treats a non-empty violation list as failure sees nothing. Second, and worse, the entry
  is **permanently unaddressable**. `FrontmatterChecks.IdentityOf` skips an identity field whose
  value is empty (`FrontmatterChecks.cs:92`) and returns null, so the entry has no identity;
  `EntryTarget.Identity("")` is refused by the emptiness test that the additive side did not apply;
  and wholesale variant removal is refused for any variant declaring `removable_wholesale: false`,
  which is the setting on exactly the human-authored variants this affects. The only remaining exit
  is `RemoveField` on the whole key, itself refused when any entry under it is not clearable
  wholesale (`FrontmatterWrite.cs:467`). The note becomes uneditable at that key.

  This is also the one place the substrate spec's own rule is not met:
  `Spec-Frontmatter-Substrate.md:113` — "**A value that is null, empty, or whitespace-only is
  empty**, and the additive and subtractive forms of an operation apply that one test to the same
  argument. Under two tests the same value is refused when added and silently does nothing when
  removed." Here the asymmetry runs the other way — accepted when added, refused when removed — but
  it is the same two-tests defect the rule exists to prevent.
- **Covered by a test?** No test asserts this in either direction. `FrontmatterOperationTests` and
  `FrontmatterRefusalTests` exercise the emptiness test only through `EntryPayload.Scalar` and
  through the removal target; no case constructs a mapping payload with an empty field value.
- **Recorded anywhere?** Nothing records it. The design document's `What This Closes` does not
  mention emptiness, and `docs/Build-Decisions.md` carries no entry on it. This reads as an
  omission in applying a rule the spec states, not a decision.

### GAP — FMC-057, FMC-058, FMC-059 — Default nav placement is specified away and implemented nowhere

- **Old behavior:** With no explicit anchor, a link-typed nav entry was inserted at the end of the
  leading run of link entries — immediately before the first folder entry — and a folder-typed entry
  was appended at the end of the whole list. This reproduced the convention every folder note in the
  vault follows, as a pure function of the existing list, with nothing for the caller to specify.
- **New behavior:** `FrontmatterWrite.Add` sets `var at = entries.Count;`
  (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:511`) and only an explicit `EntryAnchor` moves
  it. Both specs relocate the rule deliberately — `Spec-Frontmatter-Substrate.md:97` ("Placement is
  explicit or it is an append. Ordering conventions belong to the tool composing the write, which
  knows the note's purpose, reads the list, and computes the anchor") and
  `Spec-Frontmatter-Registry.md:49`. **But no tool computes one.** The only construction of an
  `EntryAnchor` anywhere in `src/` is `EditArguments.cs:342-343`, which decodes an anchor the caller
  supplied; grepping `src/` for `nav.contents`, `nav.see_also` or any folder/link ordering logic
  returns nothing.
- **What is lost:** A caller adding a link to a folder note gets it appended after the folder
  entries, so a vault-wide ordering convention that used to be maintained automatically now depends
  on every caller reading the list, working out the first folder entry's target, and passing it as a
  `before` anchor. An agent that does not do this quietly degrades the convention note by note.
- **Covered by a test?** Not applicable — the behavior is absent by design at this layer.
  `FrontmatterOperationTests.cs:150` asserts the append as the intended default.
- **Recorded anywhere?** The *relocation* is recorded twice (the two spec lines above) and in
  `FrontmatterOperation.cs:53`. What is recorded nowhere is that the destination was never built.
  Strictly this is the tool layer's reconciliation to close, and I flag it here only because the
  substrate is where the harvest cases sit and where the trail ends.

### GAP — FMC-128, FMC-129 — The grammar is hand-restated, with no oracle and no corpus check

- **Old behavior:** What counted as a frontmatter block was not the implementation's own invention.
  Every row of a 23-shape degenerate table, plus every `.md` file in the repository's fixture vault,
  was compared at test time against Markdig's `YamlFrontMatterBlock` — block or no block, and where
  the closing fence ends. The single deliberate departure (the empty fence pair) was itself asserted,
  along with the oracle's inconsistency that motivated it, so a runtime upgrade that moved the
  grammar would fail loudly.
- **New behavior:** `NoteSplit.Of` states the grammar in code and `NoteSplitTests.cs` asserts it
  against hand-written expectations. There is no Markdig reference anywhere in the repository (grep
  over `*.cs` and `*.csproj` returns only the harvest document), and no test walks a corpus of real
  notes. `Spec-Frontmatter-Substrate.md:22` nevertheless names Markdig the arbiter: "Frontmatter has
  no official standard. **Markdig's convention is the reference**, and it settles anything this doc
  leaves open."
- **What is lost:** The spec defers to an authority that nothing checks against. I compared the
  hand-written scan against every grammar case the harvest records and found no divergence except
  the one the spec deliberately chose (the empty fence pair, dismissed below) and one hair-splitting
  difference: `IsFence` trims with `char.IsWhiteSpace` (`NoteSplit.cs:131`), which admits any Unicode
  whitespace, where the old scan trimmed `" \t\r"` only — so `---\u00A0` is a fence here and was not
  there. That is not worth fixing on its own; the point is that nothing would have told me, and
  nothing will tell the next person when a rule drifts. The corpus half is also absent: the committed
  `test-vault/` is never swept for grammar agreement, so "the rules are the ones the vault's notes
  actually exercise" is unverified.
- **Covered by a test?** `NoteSplitTests` covers the shapes; nothing covers the *agreement*.
- **Recorded anywhere?** Nothing records dropping the oracle. `Design-Frontmatter-Surface.md:46`
  says only "Detection is a bounded fence scan, with no markdown library involved", which is a
  statement about the *runtime* dependency — the old system agreed with that, and used the oracle at
  test time only.

## Present in code, unasserted by the suite

- **FMC-114, the body-byte checklist.** `Compose` writes the body verbatim
  (`FrontmatterComposer.cs:37`), so it holds structurally, but no test walks the old checklist across
  a frontmatter mutation: trailing whitespace on a line, literal tabs, non-ASCII, a fenced code
  block, a `---` in prose, a line shaped like a key, and the absence of a final newline. The old
  suite treated this as a checklist rather than one assertion.
- **FMC-115, the emitter fixed point.** No test applies the same mutation twice to an
  arbitrarily-quoted note and compares the two results byte for byte.
  `ComposingAnUntouchedTreeReproducesABlockThatWasAlreadyCanonical` is the closest and is weaker.
- **FMC-116's yes/no half.** `ADateShapedScalarComesBackAsTheTextThatWasWritten` covers dates and
  `flagged: true`; nothing covers a `no`/`yes` value surviving a *write*. Probed: `doc.title: no`
  survives a reserialize as plain `no`. Note the emitted form differs from the old system's, which
  re-emitted such values defensively quoted; within this build nothing coerces, because the node
  layer carries text.
- **FMC-030 / FMC-055 end to end.** `ACallerValueCannotBecomeStructure`
  (`FrontmatterComposerTests.cs:202`) covers the tree-and-composer path. No test drives a value or
  entry field carrying a newline through `FrontmatterWrite.Apply`. Probed and correct: it emits as
  `>-` with the content indented, and re-reads equal.
- **BOM.** `VaultFiles.Decode` reads with `detectEncodingFromByteOrderMarks: true` and writes UTF-8
  with no BOM (`VaultFiles.cs:410, 431`), so a BOM'd note's opening fence is recognized. The harvest
  lists BOM handling as an explicit gap in the old suite, where a BOM would have disqualified the
  fence — this is a **genuine improvement**, and it is untested from the frontmatter side.
- **The `...` closer through a mutation.** Asserted for the split; nothing asserts that a note closed
  with `...` comes back closed with `---` after an operation. It does.

## Dismissed

- **FMC-002, FMC-010** — the read side now normalizes CRLF to LF on entry (`NoteSplit.cs:41,59`)
  rather than handing block text back raw, and `BodyOffset` indexes the normalized string — the
  reason is recorded at `Spec-Frontmatter-Substrate.md:43` and in the `Normalize` remarks: the write
  path compares the block before and after composing, and normalizing between those reads would trip
  the guard on terminators alone; the offset's actual guarantee (a body splice disturbs no
  frontmatter byte) is unchanged and tested.
- **FMC-006, FMC-127** — an empty `---`/`---` pair now reads as a *block with empty frontmatter*
  rather than as no block, under both terminators — decided explicitly at
  `Spec-Frontmatter-Substrate.md:20` and commented at `FrontmatterTree.cs:99`; the producer half of
  the old rule is untouched (an empty tree emits no fence, `FrontmatterComposer.cs:37`), so the shape
  still cannot be created, and a note carrying one is now editable rather than refused.
- **FMC-014** — a present-but-empty value now reads as `""` where an absent key reads as null
  (`FrontmatterTree.cs:180`), so the read *can* distinguish them — strictly more information, and the
  three-answer shape query it was said to justify is still there and still the thing operations
  branch on.
- **FMC-029** — the control-character class now refuses tab as well as the old class
  (`OperationText.HasControlCharacter`, with the reasoning that YAML cannot carry a tab as
  indentation) — a refusal where the old accepted, which is the safe direction.
- **FMC-031, FMC-038** — a value's *quoting style* now survives a reserialize, because the node layer
  carries it (`FrontmatterComposerTests.cs:134`), where the old emitter rewrote `"[[X]]"` as
  `'[[X]]'` — the old case asserted the churn; the new code does not churn.
- **FMC-041** — there is one `RemoveField` operation choosing the scalar or block path from the
  field's *detected* shape (`FrontmatterWrite.cs:429-436`) rather than two named removals over one
  internal one; the refusal FMC-041 said must survive (a flat set against a block-shaped field) is
  present twice over, from the declaration and from the note.
- **FMC-064, FMC-093, FMC-094** — supplying both anchors, both discriminators, or neither is now
  unrepresentable: `EntryAnchor` carries one `AnchorPlacement`, `EntryTarget` is a closed hierarchy,
  and exactly-one-of is declared per variant (`ExactlyOneOf`) — enforced by construction rather than
  by a runtime check.
- **FMC-074, FMC-092** — the nav-key allowlist is gone; what an entry operation may address is now
  `KeyShape.BlockList` in the loaded declaration (`FrontmatterWrite.cs:309-315`), so naming a scalar
  key is still refused and the rule is no longer a hardcoded list of two.
- **FMC-106** — a pinned text appearing twice is now refused as an ambiguous identity
  (`FrontmatterWrite.cs:561`) rather than removing one occurrence; the old asymmetry was deliberate
  and so is this one (`Spec-Frontmatter-Substrate.md:94`, "An identity matching **more than one**
  entry is a refusal"). Worth knowing that it costs something: a note that already carries two
  identical human-authored entries cannot have either removed through the tool, and the wholesale
  form is refused for that variant by declaration. It is a refusal, not a corruption, so it is not a
  regression — but it is the same "created and then unremovable" shape as the finding above, reached
  from a different direction.
