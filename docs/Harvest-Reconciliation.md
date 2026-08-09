# Harvest Reconciliation

`harvest/` holds 944 behavioral cases taken from the deleted PowerShell implementation, harvested
blind against the preserved old repository. This document reconciles that inventory against what
this build implements, and holds **only what was lost**: a behavior the old system guaranteed that
this one does not, or a behavior absent with nothing in its place.

A difference where the new design chose differently and chose well is not a finding. Those are in
`Improvements Taken` at the end, one line each, so the filter can be audited rather than trusted.

**This is a work list, not a record.** When a finding is fixed, delete it: the fix lives in the code,
its test, and the commit message, and a decision worth keeping goes to the design document for its
area. An entry left here after it is closed becomes a second source of truth competing with the
code. The file is archived at cutover and should be near-empty by then.

**Not a spec reconciliation.** Nothing here proposes a spec amendment. Several findings are places
the specs are silent, and that silence is itself an input to the spec pass, which is separate work.

## Coverage, and the files behind it

`harvest/` holds twenty-one files and about 700 KB. **Do not read them whole.** Every case in them
that mattered is already stated above and below; the files are a lookup surface for one case at a
time, not reading material. Opening the largest three costs more context than this entire document
and will not tell you anything this document omits on purpose.

Each area is a pair — `NN-Area-Harvest.md` records what the old system did, `NN-Area-Reconciliation.md`
records what this build does about it — plus `harvest/INDEX.md`, which describes the harvest alone.

| Area file stem | Prefix | Cases | Harvest lines | Match | Improved | N/A | Findings |
|---|---|---|---|---|---|---|---|
| `01-Scope-Path-Io-Config` | `SCP` | 71 | 833 | 58 | 8 | 5 | 0 |
| `02-Enumerate-Ripgrep` | `ENU` | 51 | 745 | 35 | 9 | 3 | 1 |
| `03-Frontmatter-Core` | `FMC` | 131 | 1442 | 109 | 15 | 0 | 3 |
| `04-Frontmatter-Write` | `FMW` | 90 | 960 | 75 | 10 | 3 | 2 |
| `05-Registry-Schema` | `REG` | 73 | 784 | 47 | 7 | 9 | 2 |
| `06-Links-Lock-Result` | `LNK` | 82 | 1015 | 59 | 15 | 7 | 1 |
| `07-Read-Glob-Grep` | `RDT` | 91 | 759 | 67 | 13 | 9 | 2 |
| `08-Edit` | `EDT` | 123 | 1324 | 85 | 16 | 20 | 2 |
| `09-Create-Delete` | `CRD` | 128 | 1308 | 78 | 15 | 29 | 4 |
| `10-Move` | `MOV` | 104 | 708 | 66 | 13 | 18 | 5 |
| **Total** | | **944** | **9878** | **679** | **121** | **103** | **20** |

Findings are counted after merging: several old cases describing one rule reconcile to one finding,
and three findings were found from two areas at once and are recorded once.

### Looking something up

A case ID names its file: the prefix maps to an area in the table above. Every ID cited anywhere in
this document is resolvable that way.

**One harvest case, in full.** Each case is a heading followed by its `Behavior`, `Trigger`, `Kind`,
`Source` and an optional `Note`. Fifteen lines of context covers the longest of them:

```bash
grep -A15 -E '^#+ FMW-049( |$)' docs/harvest/04-Frontmatter-Write-Harvest.md
```

**What this build does about a case.** A case ID appears in its reconciliation file either in a
finding heading or as one line in the dismissed appendix. This finds it either way:

```bash
grep -n -B2 -A12 'FMW-049' docs/harvest/04-Frontmatter-Write-Reconciliation.md
```

**Every case an area covers, one line each** — the cheap way to find the case you want before
fetching it:

```bash
grep -E '^#+ [A-Z]{3}-' docs/harvest/09-Create-Delete-Harvest.md
```

**One area's verdict summary**, without the case-by-case detail:

```bash
sed -n '/^## Summary/,/^## Findings/p' docs/harvest/10-Move-Reconciliation.md
```

**Every finding across all ten areas**, as headings — the unfiltered form of the two sections below,
including the ones that merged:

```bash
grep -h '^### ' docs/harvest/*-Reconciliation.md
```

Three things will trip a mechanical search. `06-Links-Lock-Result-Harvest.md` puts its cases at
`####` because it is divided into four sub-areas, which is why the patterns above match `#+` rather
than a fixed depth. `10-Move-Harvest.md` numbers four fixture cases `MOV-F01`–`MOV-F04`, outside the
three-letter-plus-digits shape. And a reconciliation file's `Dismissed` section is the only place
most case IDs appear at all — 679 of the 944 matched outright and are counted, never listed, so a
search returning nothing usually means the case matched rather than that the ID is wrong.

The containment boundary came through with nothing lost, and several of its old refusals are now
unrepresentable rather than checked. Every finding the pass produced is closed; what is left here is
the untested-property list the suite sweep works from, and the appendix recording the filter.

## Containment properties with no test

Behavior present and correct in the code, with nothing asserting it. Listed only where the property
is a containment or safety one; general coverage is the test pass's subject, not this one.

- The delete census runs vault-wide (`DeleteTool.cs:114`), but no delete test narrows scope to prove
  it.
- A `move`'s repair is not scope-limited (`MoveTool.cs:123`), but no `move`-level test drives it
  under a narrow grant.
- Search arguments reach the binary without shell interpretation, and a leading-hyphen pattern is
  matched as data. Untested here as in the old suite.

## Improvements Taken

Every divergence judged deliberate and not a loss, as recorded by the reconciling pass for its own
area. This is the audit trail for the filter: a judgment that is not here was not made. Entries are
verbatim from the per-area reconciliations in `harvest/`, each of which also carries its own
case-by-case verdicts and the cases that matched outright.

### Scope, path resolution, I/O, configuration

- **SCP-018** — the old string-prefix-plus-trailing-separator containment check against the vault
  root has no counterpart — `VaultPath` holds segments and composes onto the root, so landing in a
  sibling directory is unrepresentable rather than guarded. The old suite had no row for this;
  the new design cannot fail it.
- **SCP-019** — the case-insensitive comparison against the vault root is gone because there is no
  string comparison against the root to make. Removes the Windows-filesystem assumption the harvest
  marked `artifact`; nothing on a case-sensitive filesystem now behaves differently.
- **SCP-020** — an empty scope-root set meaning "unscoped" is not representable: `VaultScope.Of([])`
  throws and whole-vault access is `VaultScope.Unscoped` (config spells it `["*"]`). The old
  short-circuit-before-normalizing is also gone: `VaultAccess.Resolve` refuses malformed text before
  consulting scope, so a malformed path is refused under an unscoped grant too — which closes the
  hole the harvest flagged as cutting against SCP-035.
- **SCP-036** — an explicit *empty* scope entry meaning "unscoped for that vault" is replaced by an
  explicit `VaultScope.Unscoped` entry; `GrantFileLoader` refuses an empty list at load with a
  message naming `["*"]` as the only spelling. Same authority, but it can no longer be arrived at by
  dropping an entry or filtering a list to nothing.
- **SCP-039** — a null grant scope map meaning "unscoped for every vault" is gone: `Grant.ScopeRoots`
  is always fully populated (enforced by `GrantFileLoader` and `GrantStore.Mint`), and there is no
  no-grant caller. This is the scope-side twin of the SCP-049 fail-open and is dropped for the same
  reason.
- **SCP-045** — the `.` enumeration-gate carve-out has no counterpart because enumeration takes the
  vault as its operand rather than a gating path (`VaultAccess.EnumerateNotes`/`EnumerateFiles` take
  a glob, never a path). `.` is refused everywhere as a path, and the harvest's worry — that the
  carve-out could be reached with something that is not a listing — cannot arise because the
  carve-out does not exist.
- **SCP-046** — the empty-scope-roots no-op gate follows SCP-036: `VaultScope.Unscoped` admits
  everything, and an empty root set cannot be constructed to reach the same outcome by accident.
- **SCP-049** — the fail-open on an absent host capability context is dropped. Ruled an improvement
  by the operator; a session with no recognized grant degrades to `GrantStore.MinimumGrant`, which
  holds no op classes and no vaults.
- **SCP-057** — the scope check no longer runs before path resolution: `VaultAccess.Resolve` refuses
  malformed, then dot-segment, then scope. A path both malformed and out of scope is now reported
  malformed rather than out-of-scope. No disclosure, since malformedness is decidable from the
  caller's own text, and the refusal still carries only the path the caller wrote.
- **SCP-066** — unwrapping a not-found from inside a `MethodInvocationException` has no counterpart:
  the .NET file APIs raise `FileNotFoundException`/`DirectoryNotFoundException` directly, so there
  is no causal chain to walk. The rule the harvest called portable ("inspect the chain, not the
  outermost error") is satisfied because the chain is one link long.
- **SCP-069** — the two-vault-copy fixture is a PowerShell-suite arrangement. The rewrite's
  equivalents (`TestVaultFixture`, `ScratchVault`, `ProgramHostFixtureBase`) provide the same
  isolation per test class.
- **SCP-070** — configuration-restore-after-mutation has no counterpart: `VaultConfigLoader.Load`
  returns a value rather than setting process-global state, so there is nothing to restore and the
  latent fragility the harvest flagged (an inline restore that does not run if the assertion above
  it fails) cannot occur.
- **SCP-071** — clearing the injected ambient capability context between tests has no counterpart:
  there is no ambient capability carrier. A `VaultAccess` is constructed with its grant, and
  `VaultAccess` documents that no member may take a caller-supplied scope.

### Enumeration and content search

- **ENU-002** — the dot rule now tests every segment including the filename, so a root-level
  `.hidden.md` is dropped where the old walker checked directory segments only — the harvest calls the
  asymmetry unpinned, and `Spec-Access-Layer-Capabilities.md:Enumeration` states the rule as "a
  dot-prefixed segment at any depth"; broader exclusion with an explicit switch to see them.
- **ENU-006** — "no scope roots" no longer exists as a state: every grant carries an entry per vault
  and whole-vault access is an explicit `VaultScope.Unscoped` — the absent-map degrade-open route is
  gone, which is the same containment improvement as the dropped `open` fail-open.
- **ENU-009** — ripgrep's ignore-file behavior is disabled outright via `--no-ignore` rather than
  inherited — removes the latent enumerator disagreement the harvest flagged, spec-mandated and tested.
- **ENU-011** — the two enumerators are one call differing only by a predicate — the agreement move
  and glob depend on is structural instead of pinned by a sorted comparison.
- **ENU-020** — the file census no longer classifies bad-pattern by exit code alone; both surfaces use
  the same output-presence discriminator — one rule instead of two, and the residual edge (`--stats`
  ignored with `--files`) is named in `Build-Decisions.md`.
- **ENU-022** — matching lines come back as structured `MatchLine` records parsed from `--json`, and
  `path:line:text` is rendered at the tool layer — the old colon-splitting recovery and its "no path
  contains a colon" assumption are gone rather than reimplemented.
- **ENU-027** — there is no ignore-case switch; a caller writes the engine's own inline `(?i)` flag,
  which the `pattern` argument documents as "the search engine's syntax" — the capability is reachable
  with nothing in the layer having to own a second case rule.
- **ENU-028** — there is no path-narrowing argument; the glob does it (`Notes/**`), and because the
  engine always runs against `.` at the vault root there is no narrowed-target case in which emitted
  paths could come back relative to anything else — the mechanism that made re-resolution necessary
  no longer exists.
- **ENU-033** — classification no longer sniffs the engine's `regex parse error` wording — removes the
  fragility the harvest flagged, where a reworded diagnostic would silently reclassify.
- **ENU-043** — `EngineRun` is the equivalent internal shape; `artifact` case about a PowerShell
  helper's return value.
- **ENU-050** — the old module fixture is replaced by `ScratchVault`; fixture case with no counterpart.
- **ENU-051** — the ripgrep-helper scratch tree has no counterpart; the invocation primitive is
  exercised through the public surfaces only.

### Frontmatter core — block grammar, parsing, note split, preservation

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

### The frontmatter write path and the operation vocabulary

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

### The frontmatter key registry and entry schemas

- **REG-001** — the recognized set and its order were a hardcoded seven-key ordered table; the new set is whatever a vault declares, and key order in a note is the note's own — no counterpart.
- **REG-002** — "exactly these three keys are agentic" was a property of the table; authority is now per-key `write_requires` plus namespace rules the operator declares — no counterpart.
- **REG-003** — "non-agentic keys carry a pattern, agentic keys none" is now enforced by shape: the loader refuses a `block_list` key that declares a `value` and a `scalar` key that names an `entry` (`FrontmatterRegistryLoader.cs:130-133`), which is the same invariant by construction and additionally permits a constrained scalar key inside a protected namespace.
- **REG-005** — which two keys are nav block lists is the declaration's, not the code's — no counterpart.
- **REG-018** — whether `doc.type` is a character class or an enumeration is now the declaration's choice; both kinds exist (`CharClassConstraint`, `EnumerationConstraint`) — no counterpart.
- **REG-022** — "found a list" and "found a mapping" collapse into one `BlockWhereScalarDeclared` rule; the distinction was message granularity, and the corpus filter that depended on it (REG-031) has no counterpart either.
- **REG-030** — the corpus check parsed every note under `test-vault` and `C:\Repos\vault-primary`; this repository cannot reach a vault, by design — no counterpart.
- **REG-031** — same: no corpus to validate the declaration against.
- **REG-032** — same: the "exactly six unowned keys" diff is a claim about a corpus this build cannot see.
- **REG-033** — there is no exclusion list to hold `.claude/skills`; `Spec-Tool-Capabilities.md:84` fixes the rule in code as "nothing under a dot-directory", which covers that prefix and needs no declaration — no counterpart.
- **REG-038** — a path leaving the prefix via `..` is now excluded rather than admitted, because the whole dot directory is excluded; the old rule's purpose was to stop a prefix-string match over-matching, and the new rule has no prefix string.
- **REG-039** — `.claude/guides/*.md` and `.claude/settings.json` are now excluded; a deliberate widening fixed in `Spec-Tool-Capabilities.md:84`, at the cost the harvest names (those guides are vault notes carrying vault frontmatter, and nothing can write them now).
- **REG-040** — the dot rule applies to any segment at any depth, so a nested `Areas/Homelab/.claude/skills/x.md` is now excluded where the old root-anchored prefix left it alone; deliberate per the same spec line, and it fails closed.
- **REG-041** — an empty or absent path throws `ArgumentException` rather than answering "not excluded" (`FrontmatterWrite.cs:170`), because the path is mandatory and an absent one would read as permission on exactly the files the exclusion protects; stronger, and asserted by `ThePathIsMandatory`.
- **REG-045** — the entry schema no longer "ignores" `doc.*` keys; one validator checks every declared key against its declaration, which reports strictly more than the two-validator split did.
- **REG-050** — a valueless block-list key (`agent.instructions:` with nothing under it) reports `InlineValueWhereBlockListDeclared` rather than `empty_block`; the violation still fires and refuses on the caller-supplied path, only the rule name and its prose differ.

### Wikilink resolution and ranking, the write lock, the tool-result envelope, the shared case corpus

- **LNK-002** — heading is no longer a separately addressable capture; an occurrence is three extents (whole link, target, alias) — the target extent stops at the first `|` or `#` so everything after it survives a substitution byte-for-byte, which is the guarantee LNK-003 actually needs; `docs/Build-Decisions.md:48` records it.
- **LNK-004** — no `IsEmbed` flag; the whole-link extent covers the leading `!` and a consumer tests for it (`ValueConstraint.cs:200`) — the distinction is still available and no maintenance consumer needs it, since `WikilinkUniverse` indexes notes only, so an attachment embed resolves to nothing and is never rewritten.
- **LNK-010** — a backslash in a link *target* is now an ordinary character that names nothing, rather than folding to `/` — measured Obsidian behavior (`Spec-Obsidian-Link-Behavior.md:9`); path normalization still folds separators, so only the link namespace changed.
- **LNK-012** — no function keys an arbitrary name, so there is no empty-name input to tolerate; `WikilinkName.StemOf` is total. N/A.
- **LNK-015** — the leading component of a target may now be cut mid-character (`sign/Spec-Tool` names `.../Redesign/Spec-Tool`) — a measured widening recorded in `Spec-Obsidian-Link-Behavior.md:13`; the defect LNK-015 guarded against (a partial *final* component) is still refused and tested (`TheFinalComponentIsNeverPartial`), and interior components still cannot be cut (`OnlyTheLeadingComponentMayBeCut`).
- **LNK-018** — an empty candidate component list cannot be represented: `VaultPath` always carries at least one segment. Enforced by construction. N/A.
- **LNK-022** — the tie-break is now `string.CompareOrdinal` (case-sensitive) where the old was ordinal case-*insensitive* — matches `Spec-Obsidian-Link-Behavior.md:15`; it changes only pairs differing solely by case, which is a state a case-insensitive filesystem barely admits, and `TheTieBreakIsOrdinalRatherThanCultural` pins it.
- **LNK-025** — **reversed** by the 2026-08-04 re-measurement: character count is gone, and among candidates climbing equally far the shallower wins, so T11's winner flips — deliberate, spec'd (`Spec-Obsidian-Link-Behavior.md:15`) and commented at `WikilinkRanking.cs:66-69`; called out in the summary because it contradicts a case the old system had measured twice.
- **LNK-028** — no relative path is ever spelled, so its character length cannot be a key — replaced by shared-prefix depth; the property LNK-028 existed to pin (that the spelling *is* the rule) has no analogue and nothing depends on it. N/A.
- **LNK-029** — the longest-common-directory-prefix walk is gone as a path constructor and survives only as `SharedDepth`'s counter, which keeps the case-insensitive comparison. N/A.
- **LNK-046** — the 60-second default and `VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS` are replaced by a 30-second constant with no configuration path — `VaultWriteLock.cs:19-25` and `docs/Build-Decisions.md:120` both state the reason (a tunable liveness diagnostic gets raised until it stops reporting), and the timeout parameter that remains exists only so the refusal is testable.
- **LNK-052** — `unknown_vault` no longer carries `data.configuredVaults` — `ToolCodes`' second payload rule forbids a refusal disclosing what the caller does not hold, and the configured/skipped vault lists are available through the health tool (`HealthCheckTool.cs:73-74`), so the affordance moved rather than vanished.
- **LNK-053** — the skip reason is carried in the message text rather than as `data.reason` — the classification is still made structurally from `VaultResolution.Unavailable` rather than by matching prose, which is the property that case protected.
- **LNK-055** — the core no longer "stays uncoded" so much as never carries a message to code: `Vault.Resolve` returns `Resolved`/`Unknown`/`Unavailable` records and `ToolExecution.Resolve` (`:166-179`) is the only site that attaches a code — the layering LNK-055 guarded is enforced by types, and the cross-implementation mirror it protected no longer exists.
- **LNK-056** — a config that will not load is now recorded fatal and both front-ends refuse every request (503 / CLI refusal) before resolution runs, rather than letting an uncoded config error propagate through a tool call — stronger, and `Resolve_AgainstAConfigThatWouldNotLoadIsUnknown` pins the degenerate resolution behind it.
- **LNK-057** — the single-object rule is now a type: `ToolResult.Data(object)` wrapping a concrete record per tool, serialized under one naming policy — the "exactly one object" shape is structural rather than checked.
- **LNK-059** — `malformed_op` has no failure mode to catch: C# has no implicit output stream, so a tool's return cannot be silently arrayified by a stray write. N/A.
- **LNK-060** — the name convention is now a hard refusal (`invalid_name`, with a derived suggestion) rather than a `namingWarnings` field, per `Spec-Tool-Capabilities.md:294` ("refused, not warned"); the grammar also differs — it admits `+` and `&`, and only the *first* segment must start upper-case or a digit, so `Foo-bar` is now accepted where the old check flagged it. Worth an eye at spec-reconciliation time, but a widened house-style grammar enforced as a refusal is not weaker than a narrow one enforced as a warning.
- **LNK-061** — one check no longer serves files and folders: `IsNoteName` and `IsFolderName` are separate, and the extension must be lower-case `.md` for a name being *created*, so `Foo-Bar.MD` is refused where the old check accepted it — deliberate (`VaultName.cs:20`), and existing on-disk names are never checked.
- **LNK-070** — an empty root list no longer means unscoped: whole-vault authority has one spelling, `VaultScope.Unscoped`, and `VaultScope.Of([])` throws — a caller that filters a root list down to nothing gets an error instead of the widest possible grant. Strictly safer; the corpus row is preserved with the explicit spelling.
- **LNK-081** — the PowerShell module fixture has no counterpart; the ranking and matching suites are pure and pass paths as values, and the duplicate-basename ambiguity is expressed inline. N/A.
- **LNK-082** — cross-process lock tests are gone; isolation is achieved by a fresh GUID mutex name per test rather than a per-test vault, which serves the same one-shot-abandonment constraint without a child process. The abandonment path is exercised by a thread that exits holding the mutex. N/A.
### One note on the harvest itself
Sub-area D's framing — that LNK-066..080 are a *parity contract* asserted by two independent
implementations over `tests/cases/path-cases.json` and `tests/cases/scope-cases.json` — no longer
holds in this repository: the PowerShell side is deleted and the JSON case files do not exist here.
Every row survives, but as inline `TheoryData` in `VaultScopeTests.cs` and `[InlineData]` in
`ScopePathTests.cs`, asserted once. That is not a loss of behavior, but a reader of the harvest
should not expect to find the shared corpus mechanism.

### The read-class tools — `read`, `glob`, `grep`

- **RDT-009 / RDT-010 / RDT-012** — an empty fence pair now reads as a block with empty frontmatter,
  so `body` is what follows it rather than the whole file including fences — the opposite reading from
  the old locator, and an explicit spec decision at `Spec-Frontmatter-Substrate.md:20` ("a fence pair
  with nothing between them is a block with empty frontmatter"), implemented once in `NoteSplit.Of`.
- **RDT-011** — the terminator-independence the old rule existed to guarantee is kept, and kept more
  simply: `NoteSplit.Normalize` folds CRLF to LF before the shape is read, so a CRLF spelling cannot
  read differently from its LF spelling. Consequence worth knowing: `part=body`/`frontmatter` return
  LF-normalized text while `part=all` returns the stored bytes' text; anchoring is unaffected because
  `edit` normalizes its `find` text too (`EditTool.cs:256`).
- **RDT-018** — `part: sha` is gone; the digest is `with_content_hash` on the same read
  (`ReadTool.cs:116`). Same algorithm, same raw-byte input, same lowercase hex. Better: the hash and
  the content it describes come from one read, so a hash cannot describe bytes the caller never got.
- **RDT-027** — no folder probe: an existing folder answers `invalid_path` + `suggested "X.md"`
  instead of `kind_mismatch`/`resolvedKind=folder`. Deliberate and recorded at
  `docs/Build-Decisions.md:215` and `ToolPaths.cs:20-25` — the extension discriminates the two kinds
  with no filesystem probe anywhere. The trade is one lost diagnostic ("that is a folder") against a
  read path that touches the disk once.
- **RDT-028** — an existing non-`.md` file answers `invalid_path` with no suggestion rather than
  `kind_mismatch`/`resolvedKind=file`; same decision as RDT-027, and the presence-of-an-extension
  discriminator that decides whether a suggestion is offered is preserved exactly
  (`ToolPaths.cs:160-168`).
- **RDT-031** — a leaf that is all extension (`.placeholder`) is now treated as extensionless
  (`name.LastIndexOf('.') <= 0`), so it draws a suggestion of `.placeholder.md`. Both systems refuse;
  the new suggestion is unusable because a dot segment is refused anyway, which is cosmetic in a path
  no caller may name.
- **RDT-038 / RDT-039 / RDT-040 / RDT-041** — the duplicate error surface `part: sha` carried is gone
  by construction: the hash is a flag on the one read path, so there is no second vocabulary that
  could drift from the first.
- **RDT-045** — scope no longer "degrades open"; an empty root set cannot be constructed at all and
  whole-vault access is the distinct `VaultScope.Unscoped` (`VaultScope.cs:26-37`). A behavior
  enforced by construction, and strictly safer.
- **RDT-066 / RDT-085** — `invalid_pattern` survives for both a malformed glob and a malformed regex,
  but the engine's diagnostic now rides in the message rather than in `data.stderr`, and the payload
  is empty. Deliberate: `Design-Tool-Vocabulary.md:169` — a payload exists to be lifted into a retry,
  and a caller's own malformed pattern has nothing to lift. No diagnostic text is lost.
- **RDT-067** — visibility defaults changed in one direction: dot entries are still excluded (now
  pruned in the walk *and* re-checked in the predicate), symlinks are still not followed, but
  `.gitignore` is no longer honoured (`--no-ignore`). Recorded at `Design-Core-Access-Layer.md:99` and
  `VaultSearch.cs:20-25`: an ignored file is still on disk and still destroyable, so the census must
  see it. Scope still bounds what a caller sees.
- **RDT-068** — `glob` is note-only now rather than file-general, and so is `grep`. Spec decision at
  `Spec-Tool-Capabilities.md:40-52`: "Non-markdown vault content is the editor's... A boundary, not a
  gap." The file-general enumeration still exists on the layer (`VaultAccess.EnumerateFiles`) for the
  census that needs it.
- **RDT-077 / RDT-078 / RDT-083 / RDT-084** — `grep`'s `path` subtree operand is gone; narrowing is
  expressed as `path_glob` (`Resources/**`). Nothing is lost — output stays vault-relative, a subtree
  with no matches is still empty output — and the rooted/escaping refusals become unnecessary because
  a pattern cannot name a location outside the vault (the engine is rooted at the vault root and every
  emitted path is re-parsed as a `VaultPath`). This is also what makes RDT-064 unreachable rather than
  merely unimplemented.
- **RDT-090** — the PowerShell fixture harness is replaced by `ScratchVault`; no vault behavior.
- **RDT-091** — the tool-loader directory-layout artifact has no counterpart; the native tools are
  types in an assembly.

### The `edit` tool

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

### The `create` and `delete` tools

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

### The `move` tool and link repair on relocation

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
