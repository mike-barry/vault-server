# Reconciliation: Wikilink resolution and ranking, the write lock, the tool-result envelope, the shared case corpus

Harvest source: `06-Links-Lock-Result-Harvest.md` (82 cases, prefix `LNK`)

Implementation read:
`src/VaultShared/Access/Wikilink.cs`, `WikilinkRanking.cs`, `WikilinkTarget.cs`, `VaultName.cs`,
`VaultResolution.cs`, `Vault.cs` (`LockName`), `src/VaultShared/Links/WikilinkUniverse.cs`,
`WikilinkMaintenance.cs` (skim), `src/VaultShared/Tools/VaultWriteLock.cs`, `ToolExecution.cs`,
`ToolResult.cs`, `ToolRefusal.cs`, `ToolRefusalEnvelope.cs`, `ToolCodes.cs`,
`src/VaultShared/Frontmatter/ValueConstraint.cs` (wikilink constraint),
`src/VaultCli/CliRendering.cs`, `CliHost.cs`, `src/VaultMcpServer/Program.cs`,
`src/VaultWatcher/Program.cs` (stub).

Tests read:
`tests/VaultShared.Tests/WikilinkRecognitionTests.cs`, `WikilinkMatchingTests.cs`,
`WikilinkRankingTests.cs`, `WikilinkAnchoringTests.cs` (index), `VaultResolutionTests.cs`,
`VaultConfigTests.cs`, `VaultNameTests.cs`, `ScopePathTests.cs`, `VaultScopeTests.cs`,
`tests/VaultShared.Tests/Tools/VaultWriteLockTests.cs`, `ToolRefusalEnvelopeTests.cs`,
`ToolSerializationTests.cs`, `ToolExecutionTests.cs` (lock section), `tests/VaultShared.Tests/Links/` (index).

Design/spec consulted: `docs/spec/Spec-Obsidian-Link-Behavior.md`, `docs/spec/Spec-Tool-Capabilities.md`
(name validation, wikilink maintenance), `docs/spec/Spec-Vault-Access-Design.md`,
`docs/design/Design-Tool-Vocabulary.md` (error catalogue, failure ordering),
`docs/Build-Decisions.md` (wikilink slice, lines 47-59; tool layer, lines 118-121),
`git log` for `af545bb`, `95a3070`, `b81a7a2`, `1d50411`, `1e636f8`, `7ea825f`.

## Summary

Cases: 82. MATCH 59 · IMPROVED 15 · N/A 7 · REGRESSION 0 · GAP 1 · UNRESOLVED 0

This area carried over well, and in several places the rewrite is materially stronger than what it
replaced. The write lock's identity derivation is byte-for-byte the old rule (`Global\VaultWrite-` +
32 hex of SHA-256 of the lowercased, separator-trimmed root) and is now *directly asserted* by five
tests in `VaultResolutionTests.cs` where the old suite only asserted properties of it. The scope and
path corpus (LNK-066..080) survives row for row, including all three security-relevant rows — the
component-alignment pair and the poisoned-grant row. The two vault-resolution failure modes are now
separate record types rather than two message spellings, so `unknown` versus `unavailable` cannot
converge.

All four of the "high reconciliation risk" ranking cases the harvest flagged were checked. **The
ranking model was deliberately replaced**, not missed: `af545bb` and `95a3070` corrected the layer
against a 2026-08-04 re-measurement, and the rule now lives in `Spec-Obsidian-Link-Behavior.md`.
Character counting is gone entirely; distance is shared folder-prefix depth, then descent depth,
then ordinal comparison of vault paths. LNK-024 (in-subtree beats globally shorter) and LNK-026
(tie-break on the vault path, not the relative path) both survive and are explicitly tested —
LNK-026's rule is recorded in `docs/Build-Decisions.md:56`. LNK-021, LNK-022 and LNK-027 reach the
same winners by the new route.

**The one ranking case the re-measurement reverses is LNK-025 (T11).** Under the old rule a
two-hop-down candidate under short folder names beat a one-hop-down candidate under a long folder
name, because the score was a character count. Under the new rule the shallower candidate wins, so
`Alphabet-Zoo-Folder/T11-Dup.md` now takes the link that `A/B/T11-Dup.md` used to. That is the
intended outcome of the correction and `WikilinkRanking.Compare`'s remarks state it as a rule, but
it is a direct contradiction of a case the old system measured *and re-confirmed* — worth one
sanity pass before cutover, since only a measurement can settle which is right.

The other two flagged cases resolve cleanly: LNK-013's whitespace trim is present and now tested
(`PaddingAroundTheTargetIsNotPartOfIt`); LNK-046's 60-second default and env-var override are gone,
replaced by a 30-second constant with `VaultWriteLock`'s remarks and `docs/Build-Decisions.md:120`
recording why no configuration reaches it. The lock's identity derivation landed in
`src/VaultShared/Access/Vault.cs:63` — shared code, reachable — though `src/VaultWatcher` is still a
stub with no reference to `VaultShared` at all, so nothing yet consumes it from that side.

The tool-result sub-area is what the task predicted: the envelope was redesigned wholesale and most
of it dismisses. The one guarantee I looked hard for and did not find lost is the `data`-absent-not-
null rule (LNK-047), which is preserved and asserted verbatim.

The single finding is small and is in the write lock's operator-facing diagnostic, not in any
behavior a caller can observe.

## Findings

### GAP — LNK-043 — The inherited-lock warning lost its remediation content and no longer names the vault

- **Old behavior:** When a process holding the write lock died, the next waiter acquired it, and the
  implementation emitted a warning telling the operator **that the vault may be torn** and that
  `git status` / `git diff` is the diagnosis.
- **New behavior:** The acquire-and-proceed behavior is intact (`src/VaultShared/Tools/VaultWriteLock.cs:69-71`),
  and the signal is surfaced by both front-ends (`src/VaultCli/CliHost.cs:106`,
  `src/VaultMcpServer/Program.cs:159`). But the message is only
  `Inherited the vault write lock "<lockName>" from an owner that died without releasing it. Proceeding.`
  It says nothing about the vault's possible state, and `<lockName>` is the SHA-256-derived mutex
  name (`Global\VaultWrite-<32 hex>`), not a vault name or root.
- **What is lost:** Two things. First, an operator reading this line is not told the vault may hold a
  half-applied change — and it genuinely may: a `move` rewrites N backlinked notes and then moves the
  file, all under one hold (`docs/design/Design-Tool-Vocabulary.md:228`), so a process killed mid-hold
  leaves exactly the tear the old warning existed to announce. `ToolCodes.PartialMutation` reports a
  tear this process caused; nothing reports a tear a *dead* process caused. Second, with several
  vaults configured the operator cannot tell from the message which vault to inspect, because the
  message carries only the hash. Both were free in the old message.
- **Covered by a test?** `VaultWriteLockTests.Acquire_AfterAnOwnerDiedWithoutReleasing_ProceedsAndReports`
  asserts the message contains the lock name and nothing more, so no test would catch the loss.
- **Recorded anywhere?** Nothing records it. `VaultWriteLock`'s remarks argue the *acquire* decision
  ("there is no journal to replay and no state to repair, so refusing would leave the vault
  permanently unwritable"), which is right and is not what is missing; what is missing is telling the
  operator to go look. `docs/Build-Decisions.md:119` covers only the delegate's type.

## Present in code, unasserted by the suite

- `WikilinkRanking.SharedDepth` compares folder segments `OrdinalIgnoreCase`, but no fixture has two
  folder segments differing only in case (this was LNK-029's untested branch in the old system too,
  and it survived the rewrite untested).
- Ranking's total order (LNK-023, order-independence) holds by construction — `Compare` falls through
  to `string.CompareOrdinal` on distinct paths — but no test presents the same candidate set in two
  orders.
- The lock's re-entrancy on one thread (LNK-039) and the one-shot nature of the abandonment signal
  (LNK-044) are both OS properties the code relies on and no test exercises. Nothing nests a hold in
  the new design, so re-entrancy is no longer load-bearing.
- `Vault.LockName`'s cross-process contract has no consumer: `src/VaultWatcher/Program.cs` is a stub
  and `VaultWatcher.csproj` carries no reference to `VaultShared`. Nothing can drift yet, and nothing
  proves the two sides agree either.
- `WikilinkTarget.Address`'s climb-past-root clamping and empty-component rules are covered by
  `WikilinkAnchoringTests.cs`; the `//Delta` / trailing-separator forms are asserted through
  `WikilinkMatchingTests.ARootedOrEmptyComponentMatchesNothing` rather than through `Address` itself.

## Dismissed

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
