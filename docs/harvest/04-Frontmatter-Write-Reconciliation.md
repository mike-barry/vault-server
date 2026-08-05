# Reconciliation: The frontmatter write path and the operation vocabulary

Harvest source: `04-Frontmatter-Write-Harvest.md` (90 cases)

Implementation read:
`src/VaultShared/Frontmatter/FrontmatterWrite.cs`,
`src/VaultShared/Frontmatter/FrontmatterOperation.cs`,
`src/VaultShared/Frontmatter/FrontmatterChecks.cs`,
`src/VaultShared/Frontmatter/FrontmatterTree.cs`,
`src/VaultShared/Frontmatter/FrontmatterComposer.cs`,
`src/VaultShared/Frontmatter/NoteSplit.cs`,
`src/VaultShared/Frontmatter/FrontmatterValidator.cs`,
`src/VaultShared/Frontmatter/FrontmatterVocabulary.cs`,
`src/VaultShared/Frontmatter/FrontmatterRegistry.cs`,
`src/VaultShared/Tools/EditArguments.cs`,
`src/VaultShared/Tools/EditTool.cs`,
`src/VaultShared/Tools/CreateTool.cs`

Tests read:
`tests/VaultShared.Tests/Frontmatter/FrontmatterWritePathTests.cs`,
`FrontmatterOperationTests.cs`,
`FrontmatterRefusalTests.cs`,
`FrontmatterAuthorityTests.cs`,
`FrontmatterTreeTests.cs` (scanned)

Design/spec consulted: `docs/design/Design-Frontmatter-Surface.md` (§The Write Path, §What This Closes),
`docs/spec/Spec-Frontmatter-Registry.md`, `docs/spec/Spec-Frontmatter-Substrate.md`,
`docs/spec/Spec-Access-Layer-Capabilities.md:44`, `docs/spec/Spec-Tool-Capabilities.md:84`,
`docs/Build-Decisions.md`

## Summary

Cases: 90. MATCH 75 · IMPROVED 10 · N/A 3 · REGRESSION 0 · GAP 2 · UNRESOLVED 0

**Nothing in this area is a regression.** The two bypasses the harvest singled out are both closed,
and closed harder than before. FMW-077 (a supplied block that will not parse) is refused at
`FrontmatterWrite.cs:215-221`; I confirmed against the shipped `YamlDotNet.dll` that the harvest's
exact fixture text raises `SemanticErrorException`, which derives from `YamlException` and is
therefore the caught case, so the parse genuinely fails rather than silently yielding an empty map.
FMW-078 (an agentic key written in quotes) is closed **by construction** rather than by a check: the
block is parsed by a real YAML parser onto the node layer, and I confirmed against the same assembly
that `"agent.instructions":` parses to the key `agent.instructions`, which then matches the registry
declaration and the namespace prefix in `KeyAuthority` (`FrontmatterWrite.cs:637-653`). Neither
bypass has a splicing path left to exploit — `SuppliedTree` reduces the caller's text to a parse
result and emits from the tree, so no second reading of the caller's bytes exists to disagree with
the first. Better still, the supplied block is asserted **whole and independently of the diff**
(`FrontmatterWrite.cs:237-240`), so supplying an elevated key that happens to match what is already
on disk still asserts — a case the old system did not have.

The other emphasised cases check out. FMW-088's stage ordering is real, not incidental:
`RefusePathExclusion` is the first statement of the validate phase and precedes the supplied-block
parse, and `FrontmatterRefusalTests.PathExcluded_IsDecidedBeforeAnythingElseAboutTheCall` asserts it
by feeding an unparsable block *and* an unknown key under an excluded path. FMW-010/011 hold: a write
that changes nothing returns `request.Content` by reference (`Assert.Same` in three tests), and a
body-only write splices at `split.BodyOffset` without going near the composer. FMW-036 vs FMW-080 is
intact and the tempting tightening was **not** made — violations are collected only over the keys the
operations touched (`FrontmatterWrite.cs:85-87`), while a supplied block is checked key by key with
every finding refusing (`:224-233`).

FMW-049 is **reproduced, deliberately**. A duplicate add is a pure no-op that does not update the
entry's other fields (`FrontmatterWrite.cs:505-508`), asserted by
`FrontmatterOperationTests.AddEntry_ADuplicateIdentityIsAPureNoOpAndDoesNotUpdateOtherFields`. It is
not the old silent bug carried forward by accident: the code names the sanctioned rewrite path (a
removal plus an addition in one ordered batch) and a test pins that too. So: not fixed, but no longer
undocumented.

The one thing to look at first is **GAP FMW-020** — the path-conditioned schema check has no
mechanism in the new declaration at all. It is arguably area 05's to own; flagged here because this
is where the harvest recorded it, and the operator should count it once.

## Findings

### GAP — FMW-020 — The path-dependent schema check has no counterpart

- **Old behavior:** Schema validation included at least one rule keyed on the note's vault-relative
  path: a note named `Tasks.md` outside an `Archive` segment with no `use_skill` directive produced
  a `tasks_missing_use_skill` violation. `-RelativePath` was passed into the schema check for
  exactly this.
- **New behavior:** `FrontmatterValidator.Validate` takes content and registry only, and its own
  summary states it "needs nothing else - no path, no filesystem"
  (`src/VaultShared/Frontmatter/FrontmatterValidator.cs:5`). The write path *does* hold
  `RelativePath`, but uses it only for the dot-segment refusal and `Policy.MutationRequires`
  (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:53`, `:626`). `FrontmatterRegistry` declares
  keys, namespaces, value constraints and entry schemas — there is no path-conditioned rule to
  declare (`src/VaultShared/Frontmatter/FrontmatterRegistry.cs:19-125`).
- **What is lost:** A note whose path implies a required directive can be written, and read back as
  clean, with the directive missing. Nothing in the vocabulary can express the requirement, so this
  is not a rule someone forgot to port — there is no place to put it. Mitigating: `lint`, the
  consumer that would surface such a finding at scale, is held back from the first pass, so today
  the cost is a report nobody is asking for yet.
- **Covered by a test?** No test asserts it, and none could — the concept is absent.
- **Recorded anywhere?** Nothing records the omission.
  `docs/spec/Spec-Frontmatter-Substrate.md:9` scopes this surface to "one note's frontmatter, **its
  vault-relative path**, and the declaration", which reads as admitting a path-conditioned check
  rather than excluding one; the deferral sentence beside it defers only checks needing a directory
  listing or consumer configuration, which this is not.
  `Design-Frontmatter-Surface.md`'s `What This Closes` does not mention it.
  The registry harvest covers the substance as `REG-066`–`REG-070`, so the operator should treat
  this and those as one item.

### GAP — FMW-043 — The default insert point for an added entry no longer follows any convention

- **Old behavior:** `add_nav_entry` with no anchor did not append to the end of the list. A link
  entry was inserted at the end of the *run of link entries*, ahead of any folder entries; a folder
  entry went at the very end (FMW-044). The harvest flags the grouping as "the subtle part".
- **New behavior:** With no anchor the insertion index is `entries.Count` — the end of the list,
  unconditionally (`src/VaultShared/Frontmatter/FrontmatterWrite.cs:511`).
  `FrontmatterOperation.cs:52-53` records the delegation: "Placement is either explicit or an
  append; ordering conventions belong to the tool composing the write."
- **What is lost:** No tool composes that convention. I grepped every `src/` project for `Anchor` /
  `AnchorPlacement` outside the frontmatter namespace; the only uses are
  `EditArguments.Decode`/`DecodeAnchor`, which pass the caller's own anchor straight through. So an
  agent that adds a link entry without naming an anchor lands it after the folder entries, and a
  nav list maintained through repeated edits drifts out of the link-then-folder grouping the vault's
  notes carry. Recoverable by the caller — `before`/`after` are both present and correct (FMW-045
  matches) — but the caller must now know the convention and read the list to apply it.
- **Covered by a test?** `FrontmatterOperationTests.AddEntry_AppendsAbsentAnAnchor` asserts the new
  append-at-end behavior, so the change is pinned as intended rather than accidental.
- **Recorded anywhere?** The code comment above records the *delegation*, not that the delegate
  does not exist. Nothing in `Design-Frontmatter-Surface.md` or `Design-Tool-Vocabulary.md` assigns
  the convention an owner.

## Present in code, unasserted by the suite

- **The quoted-agentic-key case (FMW-078/079) has no test.** The behavior holds by construction
  through the YAML parser, but nothing in the suite feeds a supplied block carrying
  `"agent.instructions":` in quotes, so a future swap of the parse layer would not be caught here.
  Given the harvest labels this "permanently pinned", it is the one cheap test worth adding.
- **A supplied block that parses to a YAML non-mapping** (`TreeParse.NotAMapping`, the old
  `frontmatter_not_a_map`) refuses with its own message at `FrontmatterWrite.cs:217-221`; the suite
  covers only the unparsable branch on the supplied side. The note-side branch *is* covered
  (`NotAMapping_AStoredBlockThatIsNotAMappingRefuses`).
- **`FrontmatterWrite` does not normalize `request.NewBody`.** Content is normalized at
  `FrontmatterWrite.cs:47` and `NoteSplit.Of` normalizes internally, but a CRLF `NewBody` handed
  straight to `Apply` would be composed as given. Both real consumers normalize first (`EditTool`
  via `NoteSplit.Normalize`, `CreateTool` via `NoteSplit.Of`), so no caller can reach it today.
- **FMW-036's measured rationale** — real notes carrying Obsidian's `obsidianUIMode` must stay
  writable — is preserved in behavior and asserted with a generic `undeclared.key` fixture. The
  measurement itself is not recoverable from this repo, and no comment at the touched-keys loop
  names it.

## Dismissed

- **FMW-012** — the per-op `Applied`/`Changed` record is now `AppliedOperations`, the indices of the
  operations that changed something — result-shape redesign; the same information, and the index
  addresses the caller's own list.
- **FMW-017** — `remove_field` still routes by the key's *detected* shape, but block removal now
  additionally requires every entry to belong to a variant the declaration marks removable wholesale
  — strictly narrower, and it stops a mistyped removal destroying person-authored entries.
- **FMW-028** — the control-character class now refuses tab as well (`char.IsControl` minus CR/LF,
  `OperationText.cs:198`) — stricter, and documented as deliberate: YAML cannot carry a tab as
  indentation at all.
- **FMW-039** — no dedicated `protected_directive_violation` code; a generic removal of a directive
  key is refused by the wholesale-clearable declaration and, where it would apply, by the elevated
  authority the changed key requires — the protection is declared per variant rather than hardcoded
  to one key name.
- **FMW-040** — the precedence between the protected-target code and the shape code is moot: neither
  code exists in that form, and the reasons are a closed enumeration with one refusal per cause.
- **FMW-052** — the nav-op allow-list is replaced by the declared shape (only a `KeyShape.BlockList`
  key takes entry operations) plus the outcome-based authority gate — authoring a directive through
  an entry operation still requires the elevated class, which is what the allow-list was protecting.
- **FMW-053** — the applied record reports an operation index rather than the canonical key name;
  key canonicalization in the *written content* is preserved (`working.Canonicalize`, asserted by
  `SetScalar_CanonicalizesTheKeyToTheSpellingAlreadyInTheNote`).
- **FMW-054** — `link`/`folder` mutual exclusivity is now the declaration's `ExactlyOneOf`, checked
  in both directions by `FrontmatterChecks.CheckEntry` and asserted by
  `ValueRefused_AnExclusiveGroupIsCheckedInBothDirections` — declared rather than compiled in.
- **FMW-055** — a blank description is now a required-field plus value-constraint finding from the
  declaration rather than a hardcoded nav rule — same refusal, one mechanism.
- **FMW-056** — `before` and `after` together is unrepresentable: `EntryAnchor` carries one
  `AnchorPlacement`, so the bad state has no spelling — enforced by construction.
- **FMW-060** — the wire vocabulary was redesigned whole (`set`/`remove`/`add_entry`/`remove_entry`
  with a structured `entry` object); PowerShell hashtable-vs-PSCustomObject has no counterpart.
- **FMW-072** — the fail-open on absent host capability context is gone; every invocation carries a
  grant and holding nothing denies — the operator's canonical improvement on a containment boundary.
- **FMW-090** — `.claude/guides` is no longer writable: the exclusion is now every dot segment, not
  the skills prefix. A capability narrowing, not a protection loss, and mandated by
  `Spec-Access-Layer-Capabilities.md:44` and `Spec-Tool-Capabilities.md:84` ("No path under a
  dot-directory is reachable by the tool set"), with the code-not-declared placement recorded in
  `Design-Frontmatter-Surface.md:146`.
