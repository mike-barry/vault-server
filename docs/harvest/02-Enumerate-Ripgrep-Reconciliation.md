# Reconciliation: Enumeration and content search

Harvest source: `02-Enumerate-Ripgrep-Harvest.md` (51 cases)
Implementation read: `src/VaultShared/Access/SearchEngine.cs`, `src/VaultShared/Access/VaultSearch.cs`,
`src/VaultShared/Access/VaultAccess.cs`, `src/VaultShared/Access/SearchOutcome.cs`,
`src/VaultShared/Access/VaultWidePass.cs`, `src/VaultShared/Access/VaultPath.cs`,
`src/VaultShared/Tools/GlobTool.cs`, `src/VaultShared/Tools/GrepTool.cs`,
`src/VaultShared/Tools/ToolRefusalEnvelope.cs`, `src/VaultShared/Tools/ToolCodes.cs`,
`src/VaultShared/Scope/VaultScope.cs`, `src/VaultShared/Auth/Grant.cs`,
`src/VaultCli/CliHost.cs`, `src/VaultCli/CliSettings.cs`, `src/VaultCli/CliFailure.cs`
Tests read: `tests/VaultShared.Tests/VaultEnumerationTests.cs`, `tests/VaultShared.Tests/VaultSearchTests.cs`
Design/spec consulted: `docs/spec/Spec-Access-Layer-Capabilities.md` (Enumeration, Content Search),
`docs/spec/Spec-Vault-Access-Design.md`, `docs/design/Design-Core-Access-Layer.md`
(Enumeration And Search, What This Closes), `docs/Build-Decisions.md`
(slice: enumeration and content search)

Also verified empirically against ripgrep 15.2.0 in a scratch tree, using the exact argument vectors
`VaultSearch` builds, for the escape-glob probes, the leading-hyphen pattern, and terminal escapes in
piped output. No repository file was modified.

## Summary

Cases: 51. MATCH 35 · IMPROVED 9 · N/A 3 · REGRESSION 4 · GAP 0 · UNRESOLVED 0

This area carried over faithfully, and in several places the rewrite closed a hazard the harvest
flagged as latent. The four checks the task named all came out well:

- **ENU-034** — the abort-on-unreadable pin survives, structurally and with tests on all three
  granularities (`VaultSearchTests.cs:147,162`). `VaultSearch.Read` fails the call whole on anything
  written to the error stream, whatever the exit code, and the search granularities deliberately ask
  the engine for a completion marker so "matched nothing" cannot be confused with "failed". The
  rewrite also removed a false-positive the old design had: with dot entries off, the walk is pruned
  at the engine, so a `.git` object file a concurrent git operation holds locked no longer fails a
  search that was never going to include it (`VaultSearchTests.cs:174`).
- **ENU-046 / ENU-030** — safe. CliWrap is handed an argument list, never a composed command string,
  and no shell is involved (`SearchEngine.cs:47-53`); the pattern reaches the engine as
  `--regexp <pattern>` and the glob as `--iglob <pattern>`, both option values, with `--` before the
  `.` operand. A leading-hyphen pattern is matched as data (verified). Still untested, exactly as in
  the old suite.
- **ENU-026 vs ENU-017** — the asymmetry survived. Glob is `--iglob`, unconditionally
  case-insensitive; content search passes no case flag, so the engine's case-sensitive default
  stands, and `--no-config` now means an ambient config file cannot inject `-i` behind the layer's
  back. Only the glob half is tested.
- **ENU-009** — closed rather than inherited. `--no-ignore` is one of the three always-arguments and
  is spec-mandated, with a test that a gitignored file still appears in the census
  (`VaultEnumerationTests.cs:59`). The two-enumerator split is gone entirely: both surfaces are the
  same `VaultSearch.Enumerate` call differing only in a predicate, so ENU-011 holds by construction.

The one thing to look at first is the only finding: **a misconfigured or missing search binary is now
reported as `search_failed`, a code the catalogue declares retryable, and is never validated at
startup.** The old system validated the configured path eagerly and reported it as
`dependency_unavailable`. Nothing outside the four-outcome list records the loss.

## Findings

### REGRESSION — ENU-013, ENU-035, ENU-039, ENU-041 — A missing or misconfigured search binary is reported as a retryable operational failure and is never validated

- **Old behavior:** An explicit or configured ripgrep path was validated as an existing leaf at
  resolution time, before any invocation, and a path naming nothing failed immediately with a
  `dependency_unavailable`-coded error carrying `pathTried`. Both the file census and content search
  reported it that way, and a broken configured path deliberately did **not** fall through to `PATH`.
- **New behavior:** `SearchEngine` holds the location and validates nothing
  (`src/VaultShared/Access/SearchEngine.cs:21-26`). The failure to launch surfaces per call as a
  `Win32Exception`/`IOException` caught at `SearchEngine.cs:59-63`, becomes
  `SearchOutcome.Failed`, and is translated to `ToolCodes.SearchFailed` at
  `src/VaultShared/Tools/ToolRefusalEnvelope.cs:190-193`. `ToolCodes.SearchFailed`
  (`src/VaultShared/Tools/ToolCodes.cs:131-133`, and the retryability paragraph at
  `ToolCodes.cs:28-33`) is declared operational and retryable. `CliHost.Load` builds the engine from
  `RipgrepPath` with no existence check (`src/VaultCli/CliHost.cs:87`), and
  `src/VaultMcpServer/Program.cs:96` does the same.
- **What is lost:** Two things. (1) Retryability is a property of the code, and callers are told to
  decide from the code alone — so an automated caller told `search_failed` will retry a call that can
  never succeed until an operator edits configuration. `dependency_unavailable` exists in the
  catalogue (`ToolCodes.cs:193`) and means exactly "a prerequisite the call cannot influence", but no
  search path spends it. (2) The failure moved from process start to first search: an operator who
  mistypes `RipgrepPath` gets a green `health_check` and a working CLI until the first `glob` or
  `grep` call, where the diagnostic is the platform's raw "cannot find the file specified" text in
  `data.detail`. The "does not silently fall through to `PATH`" half of ENU-041 **is** preserved —
  `SearchEngine.Command` uses the configured path as given.
- **Covered by a test?** The new behavior is asserted, not the old one:
  `VaultEnumerationTests.cs:191` and `VaultSearchTests.cs:189` both pin an unrunnable engine as
  `Failed`, i.e. as `search_failed`. No test asserts a distinct dependency condition, and none exists
  to assert.
- **Recorded anywhere?** Partially, and the part that is recorded does not cover the consequence.
  `Spec-Access-Layer-Capabilities.md:Content Search` fixes four outcomes and puts "anything else the
  engine reports" in the operational bucket, and `Design-Core-Access-Layer.md:Enumeration And Search`
  restates it, so folding launch failure into `Failed` **inside the layer** is deliberate.
  `Build-Decisions.md` records only "an unhanded engine location means `rg` on `PATH`". Nothing
  records dropping eager validation of a configured path, and nothing records that the resulting
  wire code is one the catalogue marks retryable. This is a tool-vocabulary question the layer's
  four-outcome rule does not settle; it may be worth confirming rather than treated as closed.

## Present in code, unasserted by the suite

- Arguments reach the engine as a list with no shell involved, and a leading-hyphen pattern is data
  (ENU-030, ENU-046) — no test supplies a hyphen-leading or metacharacter-bearing pattern. Untested
  in the old suite too, and the harvest called it the highest-value untested behavior in the area.
- Content search is case-sensitive by default (ENU-026) — every search test uses an all-lowercase
  pattern against all-lowercase content, so nothing would fail if `-i` were added tomorrow. The glob
  half (ENU-017) *is* tested at `VaultEnumerationTests.cs:87`.
- Escape-shaped glob patterns match nothing (ENU-019, ENU-037) — no test in the new suite supplies
  `../**`, `C:/Windows/**` or `**/../../*` to `EnumerateNotes`/`SearchFiles`, though escaping *paths*
  and escaping *scope roots* are both well covered elsewhere. Verified by hand against rg 15.2.0:
  all five old probes exit 1 with empty output, so the outcome is an empty `Results`. The second
  guard is stronger than the old one — any emitted text that will not resolve as a `VaultPath` fails
  the whole call (`VaultSearch.cs:231-232`) rather than being dropped.
- Emitted output carries no terminal color escapes (ENU-031) — and unlike the old implementation this
  rests on the engine's `--color auto` seeing a pipe rather than on an explicit `--color never`.
  `--no-config` blocks the one ambient route to forcing color. Verified clean for `--files`,
  `--files-with-matches --null --stats` and `--count --null --stats`. Were it ever to break, the
  failure is loud (a path that will not parse fails the call), not silent.
- Symbolic links are never followed (ENU-048) — `-L`/`--follow` is simply never passed, which is the
  same mechanism the old system relied on, and there is still no fixture containing a symlink.
- The engine's working directory is per-child and touches no process-global state (ENU-047) — no
  concurrency test exists here either.
- `--no-config` keeps ambient engine configuration out (recorded in `Build-Decisions.md`) — no test
  sets `RIPGREP_CONFIG_PATH` and checks it is ignored.

## Dismissed

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
