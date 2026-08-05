# Reconciliation: The read-class tools — `read`, `glob`, `grep`

Harvest source: `07-Read-Glob-Grep-Harvest.md` (91 cases)
Implementation read: `src/VaultShared/Tools/ReadTool.cs`, `GlobTool.cs`, `GrepTool.cs`, `ToolPaths.cs`,
`ArgumentRules.cs`, `EnumArgument.cs`, `ToolExecution.cs`, `ToolRefusalEnvelope.cs`;
`src/VaultShared/Access/VaultAccess.cs`, `VaultPath.cs`, `VaultFiles.cs`, `VaultSearch.cs`,
`SearchOutcome.cs`; `src/VaultShared/Scope/ScopePath.cs`, `VaultScope.cs`;
`src/VaultShared/Frontmatter/NoteSplit.cs`; `src/VaultCli/CliRendering.cs`
Tests read: `tests/VaultShared.Tests/Tools/ReadToolTests.cs`, `SearchToolTests.cs`;
`tests/VaultCli.Tests/ArgumentBindingTests.cs`, `FailClosedTests.cs`
Design/spec consulted: `docs/spec/Spec-Tool-Capabilities.md`, `Spec-Access-Layer-Capabilities.md`,
`Spec-Frontmatter-Substrate.md`; `docs/design/Design-Tool-Vocabulary.md`,
`Design-Core-Access-Layer.md`; `docs/Build-Decisions.md`

## Summary

Cases: 91. MATCH 67 · IMPROVED 13 · N/A 9 · REGRESSION 0 · GAP 2 · UNRESOLVED 0

This area carried over faithfully, and **nothing here is a regression**. The two properties the task
singled out as most likely to be lost both survived, and both survived by construction rather than by
a check somebody remembered to write:

- **The scope asymmetry (RDT-043 / RDT-062 / RDT-063 / RDT-064) is intact, and it was not unified in
  either direction.** `read` refuses a named out-of-scope path as `out_of_scope` with no payload
  (`ReadTool.cs:100` → `ToolPaths.ReadNote` → `ToolRefusalEnvelope.cs:149`), while `glob` and `grep`
  filter silently through `VaultAccess.Admits` (`VaultAccess.cs:180-184`). The sharpest case, RDT-064
  — the caller naming an out-of-scope folder to `grep` — is now unreachable: the new `grep` has no
  path operand at all, only `path_glob`, which is a pattern whose results are scope-filtered like any
  other. The rule is spec'd at `Spec-Tool-Capabilities.md:104` ("Filtering is silent, gating is
  loud") and restated at `ToolPaths.cs:9-14`.
- **RDT-086 holds:** an unreadable file fails the whole `grep` rather than returning the readable
  subset as complete (`VaultSearch.cs:159-168`, `SearchOutcome.cs:41-48`), asserted at all three
  granularities in `SearchToolTests.cs:196`. The new code goes further and scrubs the unreadable
  file's path out of the diagnostic, since the engine meets it before scope filtering.
- **RDT-088 holds:** there is no cap, offset, page or truncation anywhere in the three tools or the
  search layer. The new suite invents none either.

The operand-shape ordering (RDT-027/029/030/031/032) is where the design genuinely diverged, and it
diverged *deliberately and on the record*: the new `read` runs **no filesystem probe at all**, so the
extension rule is decided from the caller's own text before resolution, before scope, and before
existence (`ToolPaths.cs:20-37`, `155-169`; recorded at `docs/Build-Decisions.md:215`). Four of the
five old orderings still produce the old answer; the one that does not is RDT-027, where an existing
folder now answers `invalid_path` + `suggested` instead of `kind_mismatch`/`resolvedKind=folder`.
That is a diagnostic trade the build made knowingly, not a lost guarantee — see the Dismissed list.

The one thing to look at first is **RDT-024**: `read "/Projects/Widget/Widget.md"` is now refused as
malformed, because `ScopePath.Normalize` treats any leading separator as a rooted path. Backslash
folding still works; only the leading-slash spelling changed, and nothing records the change.

## Findings

### GAP — RDT-024 — a leading separator on a vault-relative path is now malformed

- **Old behavior:** `read -Path '\Projects\Widget\Widget.md'` resolved to the note. Separators folded
  to forward slash **and a leading slash was trimmed**, so the rooted-looking spelling of a
  vault-relative path was accepted as the canonical unrooted one.
- **New behavior:** `ScopePath.Normalize` refuses any path for which `Path.IsPathRooted` is true
  (`src/VaultShared/Scope/ScopePath.cs:27-28`), which on Windows includes both `/Projects/...` and
  `\Projects\...`. `VaultPath.TryCreate` reports it as malformed (`VaultPath.cs:81-99`) and the tool
  answers `invalid_path` (`ToolRefusalEnvelope.cs:141`). Backslash folding without a leading
  separator (`Projects\Widget\Widget.md`) still resolves, so only the leading-separator spelling is
  affected.
- **What is lost:** a spelling a model produces naturally — anchoring a vault-relative path at the
  vault root with a leading slash — is refused rather than normalized. The refusal carries no
  `suggested` payload (unlike the extensionless refusal, which does), so the caller gets
  "must be vault-relative, not rooted" and has to work out that the remedy is dropping one character.
  It costs a round trip on a path the old system read on the first call. The check itself is worth
  keeping for genuinely rooted paths (`C:/Windows/...`); what is lost is the distinction between
  "absolute" and "anchored at the vault root". Note also that the two spellings behave differently by
  platform: `\Projects` is rooted on Windows and not on Linux.
- **Covered by a test?** No test asserts either the old leniency or the new refusal.
  `ReadToolTests.cs:207` covers `../Outside.md` only.
- **Recorded anywhere?** Nothing records it. `VaultPath.cs:70-75` states rooted text is malformed as a
  general rule, but no document weighs that against the leading-slash spelling of a relative path.

### GAP — RDT-076 — grep has no case-insensitivity option

- **Old behavior:** `grep` matched case-sensitively by default (RDT-075) with an opt-in `ignore_case`
  boolean that made matching case-insensitive.
- **New behavior:** `GrepArguments` (`src/VaultShared/Tools/GrepTool.cs:30-38`) declares `pattern`,
  `vault`, `granularity`, `path_glob` and nothing else. The pattern reaches the engine as `--regexp`
  with no `-i` (`VaultSearch.cs:69,84,98`), so the case-sensitive default is preserved and there is no
  way to switch it off through an argument.
- **What is lost:** the affordance, not the capability — a caller can still write `(?i)needle`,
  because the engine's regex syntax carries inline flags. But nothing in the tool's description or
  schema says so (`GrepTool.cs:31`: "Regular expression, in the search engine's syntax"), so a caller
  that wants a case-insensitive content search has to already know the engine is Rust-regex-flavored.
  For the one search surface in the system, that is a real usability cost on a common query; it is
  also the one place where `glob` (case-insensitive always) and `grep` (case-sensitive always) differ
  with no way to bridge them.
- **Covered by a test?** No test asserts case sensitivity in either direction for `grep`.
- **Recorded anywhere?** Nothing records this. `Spec-Tool-Capabilities.md:28` specifies `grep` only as
  "the only content-search surface, at the three granularities the layer offers"; no spec or design
  section mentions case, and `Design-Tool-Vocabulary.md` does not list it among what it closes.

## Present in code, unasserted by the suite

- `grep`'s scope filtering (RDT-063). `SearchToolTests.cs:95` asserts it for `glob` only; `grep`
  reaches the same `VaultAccess.Filter` predicate, but no test drives a scoped grant through `grep`.
  This is the same thin spot the harvest flagged in the old suite, mirrored onto the other tool.
- Result ordering (RDT-087). `SearchOutcome.Results` documents "in the engine's order" and the code
  preserves it, but every assertion in `SearchToolTests.cs` sorts first (`Lines(...)` calls `.Order()`),
  so a future implementation that sorted would satisfy the whole suite.
- The absence of any cap or truncation (RDT-088). Nothing asserts it; it holds only because no such
  argument exists.
- Digest stability across a no-op re-read (RDT-021) and change-on-frontmatter-only-change (RDT-023's
  first half). `ReadToolTests.cs:108` asserts the hash covers the whole file whichever part was read,
  which is the load-bearing half; the stability pair is not asserted.
- A path whose leaf is a *directory* named `Something.md` passes the extension rule and answers
  `read_failed` from the platform. No test covers it.

## Dismissed

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
