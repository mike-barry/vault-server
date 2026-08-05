# Semantic Harvest — index and orchestrator's notes

Harvested 2026-08-04 from the preserved old repository at `C:\Repos\vault-server`, by ten agents
working one area each. **944 cases.**

Scope: the whole old Pester suite except `lint`, `lint_suppressions`, `sync`, and
`tools/Tests/RemedyContract.ps1`, which serves only those two suites. Tests were the primary
source; the implementation was read only to disambiguate, and every case that needed it says so.

No agent read this repository's specs, designs, or tests. The harvest is independent of them by
construction, which is what makes the reconciliation informative.

Each `NN-Area-Harvest.md` has an `NN-Area-Reconciliation.md` beside it, recording what this build
does with every case in it. `../Harvest-Reconciliation.md` is where those ten converge: it holds
only the behaviors that were lost, and is the document to read first. This index describes the
harvest alone and is written in the present tense of the day it was taken.

| File | Area | Cases | Prefix |
|---|---|---|---|
| `01-Scope-Path-Io-Config-Harvest.md` | Scope, path resolution, I/O, config | 71 | `SCP` |
| `02-Enumerate-Ripgrep-Harvest.md` | Enumeration, ripgrep helpers | 51 | `ENU` |
| `03-Frontmatter-Core-Harvest.md` | Block grammar, parsing, note split, preservation | 131 | `FMC` |
| `04-Frontmatter-Write-Harvest.md` | Write path, operation vocabulary | 90 | `FMW` |
| `05-Registry-Schema-Harvest.md` | Key registry, entry schemas | 73 | `REG` |
| `06-Links-Lock-Result-Harvest.md` | Link resolution, write lock, result envelope, shared corpus | 82 | `LNK` |
| `07-Read-Glob-Grep-Harvest.md` | `read`, `glob`, `grep` | 91 | `RDT` |
| `08-Edit-Harvest.md` | `edit` | 123 | `EDT` |
| `09-Create-Delete-Harvest.md` | `create`, `delete` | 128 | `CRD` |
| `10-Move-Harvest.md` | `move`, link repair on relocation | 104 | `MOV` |

Roughly 88% classify `semantic`. Per-area splits are in each file's header.

## Rules found twice, independently

These were harvested by different agents from different suites without knowledge of each other.
Independent corroboration makes them settled behavior rather than one test author's habit, and they
are the highest-confidence material here.

- **Null capability set degrades open; empty capability set denies.** `SCP-049/050` and
  `EDT-051/054`. An absent host context — a standalone `pwsh -File` run or a direct test call —
  is allowed through; an injected but empty set refuses everything. Confirmed in source at
  `Public/Scope.ps1:120-152`, where it is a documented deliberate posture. **This is a fail-open on
  the containment boundary and wants an explicit ruling in the redesign either way.**
- **A search aborts rather than degrading when a file cannot be read.** `ENU-034` and `RDT-086`,
  both labelled spec-pinning tests in the old suite. Partial results are never returned as
  complete, even when matches were already found.
- **Content search is case-sensitive by default; glob matching is unconditionally
  case-insensitive.** `ENU-026`/`ENU-017` and `RDT-075`/`RDT-059`. Each cites parity with a
  different native tool. Unifying them breaks one.
- **Validation extent differs by layer.** `FMW-036`/`FMW-080` and `EDT-076`/`EDT-085`. The key
  registry validates only keys an operation touches, so notes carrying foreign Obsidian metadata
  stay writable and keep it; schema validation runs over the whole resulting block, so an untouched
  malformed `nav.see_also` blocks an unrelated write. The old suite records this as measured
  against real notes and warns that tightening the first to whole-block validation is the tempting
  wrong move.
- **The classifier bypasses that force parse–validate–emit.** `FMW-077/078` and `CRD-043/047`. A
  quoted top-level agentic key and an unparseable supplied block both defeated the old classifier
  silently, letting a `content:write`-only caller land a binding directive with an empty diff and a
  result reporting no capability used. These are checks that did not run, not checks that answered
  wrongly, and they are why the write path re-emits rather than splices.

## Where the two systems have no common ground

Not gaps. Places where the reconciliation will find nothing to compare, and should not read the
silence as agreement.

- **The registry is a hardcoded table of seven keys** — `doc.created`, `doc.archived`, `doc.type`,
  `scope`, `agent.instructions`, `nav.contents`, `nav.see_also` — returned by a function
  (`Public/FrontmatterRegistry.ps1:66`). There is no declaration file and no loader, so the whole
  declaration-loading surface of the redesign has no old counterpart. Only the seven keys' own
  semantics are harvestable.
- **`create` performs no link maintenance at all** (`CRD-069`) — no pinning, no census, no link
  field in its return. Its only link-aware rule is refusing `[ ] # ^ |` in a basename.
- **`move` has no cross-vault refusal**, and no mechanism for one: a single `-vault` parameter makes
  it unexpressible (`MOV-100`).
- **Nothing anywhere paginates, caps, or truncates** (`RDT-088`). If the new suite has such a rule,
  it is a spec addition rather than a harvest gap.
- **Enumeration order is never pinned** (`ENU-011`) — the cross-check between the two enumerators
  sorts both sides first, which reads as a deliberate refusal to settle it.

## Divergence to examine

The old `move` copies a link's `|alias` through verbatim on every rewrite path
(`tools/move.ps1:310` and `:464`); the alias is display text and is never re-derived from the new
basename (`MOV-044`). The redesign refreshes a repaired link's alias when it was the note's own
name. That is a real behavioral divergence. It may well be the correct one — it came out of
re-measuring Obsidian, which the old implementation never did — but it is the reconciliation's to
adjudicate, not the harvest's.

## Contract questions to settle before reconciling

- **`edit.ps1`'s own documentation is stale against its tests.** `.PARAMETER frontmatter_ops` and
  several schema descriptions still claim a line break, control character, or `"` in a value is
  `malformed_op`; those refusals were retired and the tests assert the opposite (`EDT-074`,
  `EDT-077`). Decide which is the intended contract before treating either as a gap.
- **Several cases are marked retired and replaced** — CRLF preservation became unconditional LF
  normalization, and the apparent-fence case went from refusing a patch to accepting one. A
  redesign that "fixed" these back would look like a bug fix and would be a divergence from a
  recorded decision.

## Suspected defects, harvested as-is

Per the brief's rule 4 these were recorded rather than dropped. None is a finding about the
redesign.

- **`FMW-049`** — `add_nav_entry` on an existing link carrying a *different* description is a silent
  no-op reporting `Changed false`, not an update and not an error. Verified literally at
  `FrontmatterWrite.Tests.ps1:380`. Description updates appear unreachable through that op.
- **`FMC-126` composed with `FMC-030`** — a `---` line inside a YAML block scalar still closes the
  block (the locator is a line-level scan, and this agrees with the standard), while a multi-line
  value is emitted *as* a block scalar. Nothing tests the composition.
- **`ENU-009`** — ripgrep honours `.gitignore` while the note walker's dot-directory prune does not,
  so a vault containing a `.gitignore` can make the two supposedly-agreeing enumerators disagree.

## What the old suite itself never asserted

Absence here is the old suite's gap, not evidence the behavior was absent. Recorded so the
reconciliation does not read silence as agreement.

- Config load-time refusals: missing `default_vault`, missing `vaults`, empty root path, config not
  found, the env-var config-path override. `Config.Tests.ps1` is 36 lines.
- `add_nav_entry`'s `before`/`after` position anchors (`EDT-113`) — fully specified in the schema
  block, mutually exclusive, zero-or-several matches is `malformed_op`, and supplied by no test.
- YAML comments inside a block, blank-line *preservation* (recognition only is tested), BOM handling
  anywhere, nested-mapping shape classification, nav-target case-insensitivity.
- Shell non-interpretation of ripgrep arguments and the leading-`-` pattern guard (`ENU-046`,
  `ENU-030`) — both documented deliberate choices, neither tested, both on caller-controlled input.
- The bare-link bystander rule on delete (`CRD-096`) — the tool fixture has no duplicate basenames.
- Link-key whitespace trimming and the 60s lock timeout with its env-var override (`LNK-013`,
  `LNK-046`), recovered from the implementation.

## Orchestrator corrections to the harvest

- **`FMC-119` was rewritten.** The original read a graded 3-vs-4-space indentation tolerance into a
  differential table row. That table asserts only that the scan and the Markdig oracle agree; no row
  states an outcome. Resolved against `Private/FrontmatterHelpers.ps1:54`: the candidate closing
  line is matched after `TrimEnd(" \t\r")` only, so leading whitespace is never stripped and an
  indented closer is not a fence at any width. **General hazard: any case harvested from that table
  has to be resolved against the implementation before it means anything.**
- Three agents corrected my area briefs, correctly, and I verified each against source: the registry
  has no loader, `create` does no link maintenance, and `move` has no cross-vault concept. The
  briefs had assumed the redesign's shape.

## Verification performed

I read the governing sources directly for every claim reproduced in this index —
`Public/Scope.ps1`, `Public/FrontmatterRegistry.ps1`, `Private/FrontmatterHelpers.ps1`,
`tools/move.ps1`, and the cited test files. Claims inside the ten area files beyond those are the
agents' and are cited to `file:line` for spot-checking; they were not each independently confirmed.
