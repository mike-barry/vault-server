# Harvest: Enumeration and ripgrep helpers

Sources:
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\Enumerate.Tests.ps1` (232 lines)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\RipgrepHelpers.Tests.ps1` (117 lines)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\TestHelpers.ps1` (47 lines, fixture)

Read the implementation for:
- `powershell/VaultAccess/Public/Enumerate.ps1` — to say precisely how rg argument vectors are
  built (flag order, `-e`, `--`, `--color never`), how the exit-code switch classifies, and how
  output paths are re-resolved and split at the first colon. The tests assert the observable
  results but not the argument construction.
- `powershell/VaultAccess/Private/RipgrepHelpers.ps1` — to confirm the precedence chain in
  `Resolve-VaultRgPath` and that `Invoke-VaultRipgrep` never throws on non-zero exit.
- `powershell/VaultAccess/Public/Scope.ps1` — to disambiguate ENU-005 (`Resolve-VaultScopeRoots`
  is what makes the "no leak between vaults" case work, and what an *empty* entry means).
- `test-vault/` contents — to explain the fixture counts (6 real notes) and why `quorvium`
  matches twice in `Resources/Gizmo-Reference.md`.

## Summary

This area covers three public surfaces and one private helper pair.

- `Get-VaultNotePath` — the note-only (`.md`) enumeration, walked by the filesystem, not ripgrep.
- `Get-VaultFilePath` — the file-general census via ripgrep `--files`, doubling as the *glob*
  tool's filename-search surface when given a `-Pattern`.
- `Search-VaultContent` — the *grep* tool's content-search surface, ripgrep with a regex.
- `Resolve-VaultRgPath` / `Invoke-VaultRipgrep` — shared binary resolution and invocation.

Three assumptions run through nearly every case and are worth reading first.

1. **Two independent enumerators are pinned to agree.** `Get-VaultNotePath` walks the filesystem
   and prunes dot-directories itself; `Get-VaultFilePath` shells to ripgrep and inherits rg's
   default hiding of dot-directories. The suite asserts the two return the *same set* (ENU-011)
   precisely so the two mechanisms cannot drift. A reimplementation with one enumerator satisfies
   this trivially, but the *reason* it exists — move's basename-uniqueness pass and glob must
   agree on "what exists" — is the harvestable behavior.
2. **Scope filtering is always silent.** Every enumeration surface drops out-of-scope results
   rather than raising `out_of_scope`. This is asserted separately for notes, files, glob matches,
   and grep matches (ENU-004, ENU-013, ENU-021, ENU-036) — four assertions of one rule, because
   each surface filters at its own site.
3. **Vault-root containment is tested by probe, not by assertion of a refusal.** Escape patterns
   (`../**`, `C:/Windows/**`, `**/../../*`) are asserted to return *nothing* — they are not errors.
   The mechanism is structural (rg rooted at the vault dir searching `.`) plus a per-result
   re-resolution guard, but the *behavior* is "returns empty", which is what a reconciler should
   look for.

Two things looked thin. **Ordering and determinism are never asserted.** No test pins the order of
enumeration output; the one comparison between the two enumerators explicitly `Sort-Object`s both
sides first (ENU-011), which is an admission that neither order is guaranteed. A reconciler should
treat "enumeration order" as an *open* question in the old suite, not a settled one. And
`Get-VaultNotePath` has no ripgrep dependency, so it has **no `dependency_unavailable` case** —
the note enumeration keeps working when rg is missing, while file and content enumeration do not.
That asymmetry is never asserted, only implied by the absence of a test.

Kind calls on argument construction, per the task: flag *names* (`-l`, `-c`, `-n --no-heading`,
`--glob-case-insensitive`) are `artifact` — they are about invoking that binary that way. But the
*effect* each produces (one path per file; `path:count`; `path:line:text`; case-insensitive glob
matching) is `semantic` and harvested separately. Two argument choices are `semantic` despite
looking like flags, and I say why at each: `-e` before the pattern (ENU-030) and `--color never`
(ENU-031) both determine what the caller sees for inputs the caller controls.

## Cases

### ENU-001 — Note enumeration returns every markdown file in the vault

- **Behavior:** Enumerating a vault's notes returns every `.md` file beneath the vault root,
  recursively, at any depth.
- **Trigger:** `Get-VaultNotePath` with no arguments against the fixture vault.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:14` — `enumerates fixture notes plus injected dupes`
- **Note:** The count `8` is fixture-bound: 6 real notes in `test-vault/` plus the injected
  `A/Dupe.md` and `B/Dupe.md`. The injected `.obsidian/junk.md` is a 9th `.md` file on disk and is
  excluded — the count is the assertion that it was excluded. See ENU-002 and ENU-042.

### ENU-002 — Dot-directories are excluded from note enumeration

- **Behavior:** A note in any directory whose name begins with `.` — `.obsidian`, `.git`,
  `.claude` — is never returned by note enumeration.
- **Trigger:** A `.md` file exists at `.obsidian/junk.md`; enumerate notes.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:18` — `prunes dot-directories`
- **Note:** The test only exercises `.obsidian`, but the rule is general: the implementation checks
  every *directory* segment of the relative path for a leading `.` and prunes on any hit. A
  dot-prefixed *file* at the vault root (e.g. `.hidden.md`) is **not** excluded by this rule —
  only directory segments are checked. That asymmetry is not tested either way; a reconciler
  should treat it as unpinned.

### ENU-003 — Enumerated paths are vault-relative with forward slashes

- **Behavior:** Every enumerated note path is relative to the vault root and uses `/` as the
  separator, regardless of the host platform's native separator.
- **Trigger:** Enumerate notes on Windows, where the filesystem yields `\`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:22` — `returns forward-slash relative paths`
- **Note:** Asserted by membership of `Projects/Widget/Widget.md`, which pins both the relativity
  (no drive letter, no vault root prefix) and the separator in one string.

### ENU-004 — A scoped grant filters note enumeration to the scope roots

- **Behavior:** When the caller's grant carries a non-empty set of scope roots for the vault,
  note enumeration returns only notes at or beneath one of those roots.
- **Trigger:** Enumerate notes with scope roots `{ test = ['Projects/Widget'] }`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:28` — `filters to notes under the scope root`
- **Note:** Asserted two ways: the in-scope note is present, and *nothing* outside the prefix is.

### ENU-005 — Out-of-scope notes are dropped silently, not raised as an error

- **Behavior:** A note outside the grant's scope is absent from the enumeration result. The call
  succeeds; it does not fail with a scope violation.
- **Trigger:** Enumerate notes scoped to `Projects/Widget` while `Resources/Gizmo-Reference.md`
  exists.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:34` — `drops out-of-scope notes rather than erroring`
- **Note:** This is the load-bearing posture for the whole area — an enumeration under a scoped
  grant is a naturally narrowed result set, not a series of refusals. Contrast with a *targeted*
  read of an out-of-scope path, which does refuse. The enumeration operation itself is gated on
  the literal path `.`, which is always permitted even under a scoped grant.

### ENU-006 — An unscoped call returns the whole vault

- **Behavior:** With no scope roots in play, note enumeration returns every note, unfiltered.
- **Trigger:** `Get-VaultNotePath` with no `-ScopeRoots`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:39` — `an unscoped call (no ScopeRoots) still returns everything`
- **Note:** "No scope roots" here means the *absent* map (a direct or standalone caller with no
  grant), which degrades open. Distinct from ENU-007's *present but empty* entry, which reaches
  the same result by a different route.

### ENU-007 — A scope root configured for a different vault does not leak into this one

- **Behavior:** When a grant carries per-vault scope roots, only the entry keyed to the vault
  being enumerated applies. A restriction configured for another vault the same grant can reach
  has no effect on this vault's results.
- **Trigger:** Enumerate vault `test` with scope roots `{ test = []; scratch = ['Projects/Widget'] }`;
  `Resources/Gizmo-Reference.md` must still be returned.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:43` — `a scope root configured for a different vault does not
  leak into this vault`
- **Note:** Read the implementation (`Public/Scope.ps1`) to state this precisely. An *empty* entry
  for a vault means "unscoped, full access" — it is not the same as a missing entry, which is a
  grant-configuration invariant violation and throws. The test's `test = @()` is the deliberate
  empty entry. The vault-name key is matched case-insensitively.

### ENU-008 — File enumeration returns every file, not only notes

- **Behavior:** The file census enumerates files of any extension beneath the vault root, not
  only `.md`.
- **Trigger:** `Get-VaultFilePath` with no arguments.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:52` — `enumerates every fixture file, including non-note files
  if present`
- **Note:** Weakly tested. The fixture contains only `.md` files, so the count (8) is identical to
  the note count and the "not only notes" claim is asserted by *intent* rather than by evidence.
  The test's own comment admits this. A reconciler should treat "a non-markdown file appears in
  the file census but not the note enumeration" as an **untested** behavior in the old suite,
  even though it is clearly the design.

### ENU-009 — Dot-directories are excluded from file enumeration too

- **Behavior:** The file census excludes files under dot-prefixed directories, matching the note
  enumeration's exclusion.
- **Trigger:** `.obsidian/junk.md` exists; run the file census.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:59` — `prunes dot-directories, matching Get-VaultNotePath`
- **Note:** The mechanism differs from ENU-002: here it is ripgrep's own default hidden-file
  behavior, not an explicit segment check. The observable rule is the same, which is the point of
  ENU-011. Ripgrep's default *also* respects `.gitignore`, which the note walker does not — that
  divergence is never tested, and a vault with a `.gitignore` could make the two enumerators
  disagree. Flagging as a latent gap rather than a bug, since no test covers it.

### ENU-010 — File census paths are vault-relative with forward slashes

- **Behavior:** File census results are vault-relative and use `/`, identically to note
  enumeration.
- **Trigger:** Run the file census on Windows.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:63` — `returns forward-slash vault-relative paths`
- **Note:** Ripgrep emits `.\Projects\Widget\Widget.md` on Windows; the `.\` prefix and the
  backslashes are both normalized away. The normalization goes through a full re-resolution
  against the vault root, not a string strip (see ENU-016).

### ENU-011 — The two enumerators agree exactly on the note set

- **Behavior:** For a vault containing only markdown, the file census and the note enumeration
  return the same set of paths.
- **Trigger:** Compare both enumerations, sorted.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:67` — `agrees with Get-VaultNotePath on the note set, so move
  and glob cannot drift`
- **Note:** Two things here. First, the *purpose* stated in the test name is the harvestable part:
  the move tool's basename-uniqueness check and the glob tool must not develop independent notions
  of "every file in the vault". Second — and important for the reconciliation — **both sides are
  explicitly sorted before comparison**. This is the closest the suite comes to an ordering
  assertion, and it deliberately declines to make one. No test in this area pins enumeration
  order or requires determinism across runs.

### ENU-012 — Scope filtering applies to the file census identically

- **Behavior:** A scoped grant narrows the file census to files under the scope roots, the same
  way it narrows note enumeration.
- **Trigger:** File census with scope roots `{ test = ['Projects/Widget'] }`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:71` — `filters to notes under the scope root, mirroring
  Get-VaultNotePath scoping`
- **Note:** Also asserts the result is non-empty, so the filter cannot pass by returning nothing.

### ENU-013 — A missing ripgrep binary surfaces as a distinct dependency error

- **Behavior:** When the search binary the file census depends on cannot be resolved, the call
  fails with a `dependency_unavailable`-coded error rather than an opaque execution failure.
- **Trigger:** File census with an explicit binary path pointing at a nonexistent file.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:77` — `throws dependency_unavailable-shaped when ripgrep cannot
  be resolved`
- **Note:** Marked `semantic` because the *contract* — a missing external dependency is reported
  as an identifiable, coded condition and not confused with "no results" or "bad pattern" — should
  survive any reimplementation, even one that does not shell to ripgrep. The literal code string
  `dependency_unavailable` and the `pathTried` data member are the artifact part.

### ENU-014 — A bare basename pattern matches at any depth via `**`

- **Behavior:** A glob pattern of the form `**/Name.md` matches a file with that basename
  anywhere in the vault, at any depth.
- **Trigger:** Pattern `**/Widget.md`; matches `Projects/Widget/Widget.md`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:83` — `matches a bare basename anywhere via **`
- **Note:** Asserted as an exact single-element result, so it also pins that no other file matched.

### ENU-015 — An anchored pattern constrains matches to that location

- **Behavior:** A pattern with a leading path segment (e.g. `Projects/**/*.md`) matches only files
  beneath that location; the pattern's leading segments are anchored at the vault root.
- **Trigger:** Pattern `Projects/**/*.md` against the fixture; expects exactly 3 matches, all
  under `Projects/`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:88` — `honors an anchored (location-expressing) pattern`
- **Note:** The count 3 is fixture-bound: `Projects/Widget/Widget.md`, `Projects/Widget/Tasks.md`,
  `Projects/Widget/Inbox/Plan-Widget-Assembly.md`. Anchoring at the *vault root* rather than at
  any depth is the real assertion — it follows from rg being rooted at the vault directory and
  searching `.`, which is what makes patterns match vault-relative paths.

### ENU-016 — A root-level file is matched by `**/Name.md` and returned without a path prefix

- **Behavior:** A file directly at the vault root is matched by a `**/`-prefixed pattern, and its
  returned path is the bare filename with no leading `./` or other prefix.
- **Trigger:** Pattern `**/Test-Vault.md`; result must be exactly `Test-Vault.md`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:94` — `matches the root-level manifest-style file, mirroring
  the spec probe`
- **Note:** Two behaviors folded into one assertion, both worth having: `**/` matches zero
  directories (a root-level file is reachable), and the emitted path carries no `./` artifact from
  the search root. The test comment says this mirrors a vault-root-containment probe run against
  the real vault using `**/Vault-Manifest.md`.

### ENU-017 — Glob matching is case-insensitive

- **Behavior:** A glob pattern matches files whose names differ from it only in letter case,
  including in the extension.
- **Trigger:** Pattern `**/test-vault.MD` matches `Test-Vault.md`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:100` — `is case-insensitive (--glob-case-insensitive), matching
  native Glob on Windows`
- **Note:** The pattern varies case in *both* the stem and the extension, so it pins insensitivity
  across the whole name. The reason given is parity with the platform's native glob behavior on
  Windows. The specific flag `--glob-case-insensitive` is the artifact; the case-insensitivity is
  the semantic. Note this is a deliberate, unconditional choice, not a platform-conditional one —
  the flag is passed whenever a pattern is supplied.

### ENU-018 — A pattern matching nothing yields an empty result, not an error

- **Behavior:** A well-formed glob pattern that matches no file returns an empty result. This is a
  success, not a failure condition.
- **Trigger:** Pattern `**/NoSuchFileXYZ.md`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:104` — `returns empty output (not an error) when nothing matches`
- **Note:** This is the caller-visible half of ENU-039 (ripgrep exit 1 means no matches). Pairs
  with ENU-020: "no matches" and "bad pattern" must not collapse into one another.

### ENU-019 — Patterns that attempt to escape the vault match nothing

- **Behavior:** A glob pattern expressing a location outside the vault root returns no results.
  Enumeration is confined to the vault subtree, and an escape attempt is an empty result rather
  than an error or a leak.
- **Trigger:** Each of the patterns `../**`, `../../**`, `/Windows/**`, `C:/Windows/**`,
  `**/../../*`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:109` — `confines <_> to the vault, matching zero files`
  (`-ForEach` over 5 patterns)
- **Note:** One case, five rows, because it is one rule. The rows cover four distinct escape
  shapes worth keeping: relative ascent (`../**`), multi-level ascent (`../../**`), absolute
  rooted (`/Windows/**`), drive-qualified absolute (`C:/Windows/**`), and ascent buried mid-pattern
  after a wildcard (`**/../../*`). The last is the interesting one — the escape is not at the head
  of the pattern where a naive prefix check would catch it. Per the implementation there are two
  independent guards: structural (the search is rooted at the vault and targets `.`) and a
  per-result re-resolution that drops anything landing outside the root, which is what covers
  Windows junctions and reparse points. Symlinks are never followed because the follow flag is
  simply never passed.

### ENU-020 — A malformed glob pattern is reported as an invalid-pattern error carrying the
underlying diagnostic

- **Behavior:** A syntactically invalid glob fails with an `invalid_pattern`-coded error whose
  data carries a non-empty diagnostic message explaining the syntax problem. It is not silently
  treated as "no matches".
- **Trigger:** Pattern `[` (an unterminated character class).
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:114` — `maps a malformed pattern (rg exit 2) to invalid_pattern
  carrying stderr`
- **Note:** Marked `semantic`: distinguishing "your pattern is broken" from "your pattern matched
  nothing" is a contract a caller acts on, and a reimplementation must preserve it. The artifact
  parts are the JSON-in-exception-message envelope, the field spelled `data.stderr`, and the
  mapping being keyed on exit code 2. Note the classification here is **exit-code-only** — for the
  file census, *any* exit 2 while a caller pattern is in play is `invalid_pattern`. That differs
  from content search, which additionally inspects the diagnostic text (ENU-033); read the
  implementation to establish this asymmetry, as no test contrasts the two.

### ENU-021 — Glob matches are scope-filtered silently

- **Behavior:** Glob results are narrowed to the grant's scope roots after matching, with
  out-of-scope matches dropped silently rather than refused.
- **Trigger:** Pattern `**/*.md` with scope roots `{ test = ['Projects/Widget'] }`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:123` — `applies the scope post-filter to matches, dropping
  out-of-scope files silently`
- **Note:** Asserted three ways: result non-empty, every result under the scope root, and one
  named out-of-scope file absent. The filter is a *post*-filter — the pattern matches first
  against the whole vault, then results are narrowed. Not observable here, but it matters for a
  reimplementation tempted to push the scope into the pattern.

### ENU-022 — Content search emits path, line number, and matching text per match

- **Behavior:** In the default output mode, a content search emits one line per match, formatted
  as the vault-relative path, the 1-based line number, and the full text of the matching line,
  colon-separated.
- **Trigger:** Search for `quorvium`; one result must be exactly
  `Resources/Gizmo-Reference.md:11:The gizmo is cast from quorvium alloy, …`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:133` — `content mode emits path:line:text with vault-relative
  forward-slash paths`
- **Note:** The exact-string assertion pins several things at once: the line number is 1-based and
  correct; the *entire* source line is emitted, not just the matched span; and no truncation
  occurs. The second assertion in the block (`^[^:\\]+(/[^:\\]+)*:\d+:`) additionally pins that
  no emitted path contains a backslash or a colon — the latter is the load-bearing assumption
  behind splitting the line at its first colon to recover the path.

### ENU-023 — Every matching line in a file is emitted, not just the first

- **Behavior:** When a file contains multiple matching lines, content search emits one output line
  for each. It does not stop at the first hit per file.
- **Trigger:** `quorvium` appears on two lines of `Resources/Gizmo-Reference.md`; expect 2 result
  lines for that file.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:139` — `accumulates every matching line in a file (no first-hit
  break)`
- **Note:** The test name reads as a regression pin against an implementation that broke out of
  the per-file loop early. Multiple matches *on the same line* are not covered by any test.

### ENU-024 — Files-with-matches mode emits each matching file exactly once

- **Behavior:** In files-with-matches mode, the result is one bare path per file containing at
  least one match — never repeated for a file with several matches, and carrying no line number
  or text.
- **Trigger:** Search `quorvium` in files-with-matches mode; `Resources/Gizmo-Reference.md`
  (which has 2 matches) must appear exactly once.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:144` — `files_with_matches mode emits one path per file`
- **Note:** The de-duplication is asserted against a file known to match twice (ENU-023), so it is
  a real test of collapsing rather than an incidental pass.

### ENU-025 — Count mode emits a per-file match count

- **Behavior:** In count mode, the result is one line per matching file, formatted as the
  vault-relative path, a colon, and the number of matches in that file.
- **Trigger:** Search `quorvium` in count mode; expect `Resources/Gizmo-Reference.md:2`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:150` — `count mode emits path:count per file`
- **Note:** The count is 2, matching ENU-023's line count — so the counted unit is *matching
  lines*, not matching occurrences. Both `quorvium` hits in that file are on separate lines, so
  this test cannot distinguish the two definitions; a reconciler should treat "lines vs.
  occurrences" as unpinned.

### ENU-026 — Content search is case-sensitive by default

- **Behavior:** A content search matches case-sensitively unless the caller asks otherwise. A
  pattern differing only in case from vault text yields no results.
- **Trigger:** Search `QUORVIUM` against text containing `quorvium`; expect empty.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:157` — `is case-sensitive by default`
- **Note:** Deliberately opposite to glob matching, which is unconditionally case-*in*sensitive
  (ENU-017). Both cite parity with the corresponding native tool as the reason. A reconciler
  should keep the two apart: this is the one place in the area where two surfaces answer the same
  question differently, and it is intentional.

### ENU-027 — An ignore-case option makes content search case-insensitive

- **Behavior:** With the ignore-case option set, a content search matches regardless of letter
  case.
- **Trigger:** Search `QUORVIUM` with ignore-case; expect non-empty results.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:161` — `matches case-insensitively with -IgnoreCase`

### ENU-028 — A path argument narrows the search, and output stays vault-relative

- **Behavior:** Supplying a vault-relative folder narrows the search to that subtree. Emitted
  paths remain relative to the *vault root*, not to the narrowed folder.
- **Trigger:** Search `quorvium` under path `Resources`; results must begin `Resources/`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:165` — `narrows the walk to -Path, output still vault-relative`
- **Note:** The second half is the subtle one. Ripgrep emits paths relative to its search target,
  so under a narrowed target it prints `Gizmo-Reference.md`, not `Resources/Gizmo-Reference.md`;
  the implementation re-resolves each emitted path against the vault root to restore the
  vault-relative form. A reimplementation that naively passes rg output through would fail this.
  Read the implementation to confirm the mechanism. Per its documentation, the path argument is
  assumed already validated as non-rooted and non-escaping *by the caller* — this function does
  not itself reject an escaping path argument, and no test covers one.

### ENU-029 — A glob filter restricts which files a content search examines

- **Behavior:** A file-filter glob restricts a content search to files matching it; matches in
  other files are not returned.
- **Trigger:** Search `quorvium` with glob `Tasks.md`; expect exactly
  `Projects/Widget/Tasks.md`, excluding `Resources/Gizmo-Reference.md` which also matches the
  pattern.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:171` — `restricts matches to files the -Glob selects`
- **Note:** The glob `Tasks.md` is a bare basename with no `**/` prefix, yet matches a file at
  `Projects/Widget/Tasks.md` — so a bare basename glob matches at any depth here. That differs
  from the anchoring behavior in ENU-015, where a pattern with a leading path segment is anchored
  at the root. Worth flagging: the rule appears to be "a pattern containing no separator matches
  by basename anywhere; a pattern containing a separator is anchored". No test states this
  directly for the content-search surface, and the file-census surface never tests a bare basename
  without `**/`.

### ENU-030 — The content pattern is passed as data, never interpretable as an option

- **Behavior:** A search pattern beginning with `-` is treated as a pattern, not as a command
  option.
- **Trigger:** Any pattern with a leading hyphen.
- **Kind:** `semantic`
- **Source:** `Public/Enumerate.ps1:221` — no `It` block; harvested from the implementation
- **Note:** **Untested** in the old suite — no case supplies a leading-hyphen pattern. Harvested
  because it is a caller-visible contract, not an invocation detail: the caller controls the
  pattern, so this determines whether a legitimate input is misinterpreted. Marked `semantic` on
  that basis, per the tie-break rule. The mechanism (`-e <pattern>` and a `--` terminator before
  the search target) is the artifact half. The same reasoning covers the `--` before the search
  target, which keeps a path from being read as an option. Flagging as a likely gap in *both*
  suites.

### ENU-031 — Search output carries no terminal color escapes

- **Behavior:** Emitted result lines contain plain text only — no ANSI escape sequences around
  matched spans — regardless of whether the process is attached to a terminal.
- **Trigger:** Any content search.
- **Kind:** `semantic`
- **Source:** `Public/Enumerate.ps1:218` — no `It` block; harvested from the implementation
- **Note:** **Untested** directly, though ENU-022's exact-string assertion would fail if escapes
  leaked in, so it is incidentally covered in content mode. Marked `semantic` rather than
  `artifact` because it determines what the caller receives — an escape-laden line breaks both
  the colon-splitting path recovery and any consumer parsing the text. The specific flag
  (`--color never`) is the artifact half. Worth an explicit test since the default behavior is
  environment-dependent.

### ENU-032 — A content search matching nothing yields an empty result, not an error

- **Behavior:** A well-formed pattern present nowhere in the vault returns an empty result and
  succeeds.
- **Trigger:** Search `zzz-token-not-in-vault-zzz`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:176` — `returns empty output (not an error) when nothing matches`

### ENU-033 — A malformed regex is reported as an invalid-pattern error naming the parse failure

- **Behavior:** A syntactically invalid search regex fails with an `invalid_pattern`-coded error
  whose diagnostic identifies it as a regex parse failure. The pattern is rejected before any
  search runs.
- **Trigger:** Pattern `(` (unclosed group).
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:189` — `maps a malformed regex (rg exit 2, regex-parse
  signature) to invalid_pattern`
- **Note:** The assertion that the diagnostic matches `regex parse error` is `artifact` — that is
  ripgrep's own wording, and the implementation *keys its classification on that exact string*.
  This is the asymmetry noted in ENU-020: for content search, exit 2 alone is not enough; the
  stderr text must carry the regex-parse signature, or it is classified as a general failure
  instead. That text-sniffing is fragile — a ripgrep version change to the wording would silently
  reclassify malformed regexes as `ripgrep_failed`. Flagging as a latent fragility, not a bug: no
  test asserts robustness to it.

### ENU-034 — An unreadable file aborts the whole search rather than silently narrowing it

- **Behavior:** If any file cannot be read during a content search, the entire search fails with a
  `ripgrep_failed`-coded error — **even when other files matched successfully**. A partial result
  is never returned as if it were complete.
- **Trigger:** Open `Resources/Gizmo-Reference.md` with an exclusive lock, then search `quorvium`
  (which also matches `Projects/Widget/Tasks.md`, a readable file).
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:198` — `aborts as ripgrep_failed on a per-file read error even
  when other files matched (pin)`
- **Note:** The single most consequential case in this area, and marked `(pin)` in its own name —
  the alternative (drop the unreadable file, return what matched) is the natural implementation
  and is explicitly rejected. Additional assertions: the error data carries exit code `2`; the
  data carries a `stderr` member **whose presence is asserted before its content**, with a test
  comment explaining why (an absent member reads as null and would vacuously pass a bare
  "does not match" check); and that diagnostic must *not* carry the regex-parse signature, pinning
  that this exit-2 case is classified apart from ENU-033. The test self-skips if an exclusive lock
  cannot be constructed on the machine, so it may be silently absent on non-Windows.

### ENU-035 — A missing search binary fails content search as a dependency error

- **Behavior:** Content search reports an unresolvable search binary as a
  `dependency_unavailable`-coded error, identically to the file census.
- **Trigger:** Content search with an explicit binary path pointing at a nonexistent file.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:218` — `throws dependency_unavailable-shaped when ripgrep
  cannot be resolved`
- **Note:** Same rule as ENU-013 on a second surface; both go through shared resolution.

### ENU-036 — Content-search matches are scope-filtered silently

- **Behavior:** Content-search results are narrowed to the grant's scope roots, with out-of-scope
  matches dropped silently — no scope violation is ever surfaced.
- **Trigger:** Search `quorvium` with scope roots `{ test = ['Projects/Widget'] }`; the match in
  `Resources/` must be absent while the match under `Projects/Widget/` remains.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:225` — `drops out-of-scope matches, never surfacing out_of_scope`
- **Note:** The test name names the error code that must *not* appear. Because the filter runs on
  the emitted lines, it operates on the path recovered from each line — so it applies uniformly
  across all three output modes, though only the default mode is tested.

### ENU-037 — A content-search glob that can only match outside the vault yields nothing

- **Behavior:** A file-filter glob expressing a location outside the vault selects no in-vault
  file, so the search returns no matches. It is not an error and does not reach outside.
- **Trigger:** Search `quorvium` with glob `../**`, and with glob `/Windows/**`.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:182` — `confines <_> to the vault` (`-ForEach` over 2 patterns)
- **Note:** The content-search analogue of ENU-019, with a thinner probe set (2 patterns vs. 5) —
  notably missing the mid-pattern ascent `**/../../*` that ENU-019 covers. A reconciler may want
  the fuller set on both surfaces.

### ENU-038 — An explicitly supplied binary path takes precedence and is used as given

- **Behavior:** When the caller names an existing search-binary path explicitly, that path is used
  and returned unchanged, overriding any configured setting and any search of the environment.
- **Trigger:** Resolve with an explicit path to a real binary.
- **Kind:** `semantic`
- **Source:** `RipgrepHelpers.Tests.ps1:24` — `returns an explicit -RgPath that exists`
- **Note:** Marked `semantic` because dependency-resolution precedence is a deployment contract an
  operator relies on, portable to any implementation that shells out. The function name and the
  private/`InModuleScope` access are `artifact`.

### ENU-039 — An explicit binary path that does not exist fails as unavailable, not at exec time

- **Behavior:** An explicitly supplied binary path is validated as an existing file at resolution
  time; a nonexistent path fails immediately with a `dependency_unavailable`-coded error rather
  than producing an opaque failure when the binary is later invoked.
- **Trigger:** Resolve with an explicit path to a nonexistent file.
- **Kind:** `semantic`
- **Source:** `RipgrepHelpers.Tests.ps1:32` — `throws dependency_unavailable for an explicit
  -RgPath that does not exist`
- **Note:** Per the implementation, the check is specifically that the path is an existing *leaf*
  (a file), so a directory path also fails — untested.

### ENU-040 — A configured setting supplies the binary path when none is passed explicitly

- **Behavior:** With no explicit path from the caller, a configured server setting names the
  search binary, and that path is used.
- **Trigger:** Set the `VAULT_MCP_RG_PATH` environment variable to a real binary; resolve with no
  explicit path.
- **Kind:** `semantic`
- **Source:** `RipgrepHelpers.Tests.ps1:40` — `prefers the VAULT_MCP_RG_PATH configured setting
  when no explicit path is given`
- **Note:** The variable *name* is `artifact`; that a configured setting sits between the explicit
  override and environment discovery is `semantic`. Per the implementation this variable is the
  surfaced form of a `RipgrepPath` application setting, by the same mechanism the vault
  configuration file's path is surfaced.

### ENU-041 — A configured binary path that does not exist fails as unavailable

- **Behavior:** A configured search-binary path is validated the same way an explicit one is; when
  it names no existing file, resolution fails `dependency_unavailable`. A broken configured path
  does **not** silently fall through to environment discovery.
- **Trigger:** Set the configured path variable to a nonexistent file; resolve.
- **Kind:** `semantic`
- **Source:** `RipgrepHelpers.Tests.ps1:55` — `throws dependency_unavailable when the configured
  VAULT_MCP_RG_PATH does not exist`
- **Note:** The "does not fall through" half is the real content — a misconfigured deployment
  fails loudly instead of quietly using whatever binary happens to be on the path.

### ENU-042 — With nothing configured, the binary is discovered on the environment path

- **Behavior:** When neither an explicit nor a configured path is supplied, the search binary is
  located by name on the environment's executable search path. A caller with no host-provided
  configuration still works.
- **Trigger:** Clear the configured path variable; resolve; the result must end in `rg` or
  `rg.exe`.
- **Kind:** `semantic`
- **Source:** `RipgrepHelpers.Tests.ps1:69` — `falls back to PATH when nothing is configured`
- **Note:** The stated rationale is that a standalone run with no host has no configured setting
  and must degrade to discovery — deliberately parallel to how an absent grant degrades unscoped
  (ENU-006). The regex allows both the bare and `.exe` forms, so the assertion is
  platform-portable. The failing case — nothing configured *and* nothing on the path — throws
  `dependency_unavailable` naming `rg` as the path tried, per the implementation; untested.

### ENU-043 — A successful invocation returns exit code zero with output lines and no diagnostics

- **Behavior:** The shared invocation primitive returns a structured result carrying the exit code,
  the output split into lines, and the diagnostic stream — with exit 0, one line per file found,
  and empty diagnostics on success.
- **Trigger:** Invoke with a file-listing argument vector against a scratch tree containing
  `Alpha.md` and `Sub/Beta.md`; expect exit 0 and 2 output lines.
- **Kind:** `artifact`
- **Source:** `RipgrepHelpers.Tests.ps1:84` — `returns exit 0 and stdout lines when files match`
- **Note:** Marked `artifact`: this asserts the shape of an internal helper's return value
  (`ExitCode`/`Stdout`/`Stderr`), which a redesign need not have. The *behaviors it enables* are
  already harvested at the public surfaces. The scratch tree here is separate from the main
  fixture — it is built fresh in this file's setup with two files and one subdirectory, and the
  count of 2 also incidentally confirms recursion into `Sub/`.

### ENU-044 — Exit code one with no output means "no matches", and is not a failure

- **Behavior:** When the search finds nothing, the invocation returns exit code 1 with empty
  output, and this is returned as a normal result — not raised as a failure by the invocation
  layer.
- **Trigger:** Invoke with a glob matching nothing (`**/zzz-none.md`) against the scratch tree.
- **Kind:** `artifact`
- **Source:** `RipgrepHelpers.Tests.ps1:96` — `returns exit 1 and no stdout when a glob matches
  nothing`
- **Note:** `artifact` for the exit-code number and the helper's return shape, but this is the
  mechanism beneath the `semantic` ENU-018 and ENU-032. The reconciler should look for those two,
  not this. Read the implementation to confirm the deliberate part: the invocation layer disables
  PowerShell's mapping of non-zero native exit codes to terminating errors, precisely so exit 1
  and 2 arrive as classifiable results.

### ENU-045 — Exit code two with a diagnostic is returned, never thrown, and the diagnostic is
never discarded

- **Behavior:** A malformed pattern produces exit code 2 with a non-empty diagnostic describing
  the problem, returned to the caller for classification rather than thrown from the invocation
  layer. The diagnostic stream is captured, not discarded.
- **Trigger:** Invoke with a malformed glob (`[`) against the scratch tree; expect exit 2 and a
  diagnostic mentioning `glob`.
- **Kind:** `artifact`
- **Source:** `RipgrepHelpers.Tests.ps1:107` — `returns exit 2 and stderr for a malformed glob,
  never throwing`
- **Note:** The "never throwing" and "never discarded" halves are the point, and the
  implementation comment says an earlier version discarded stderr with a redirect-to-null, which
  both threw diagnostics away and misfiled genuine failures. Every case that asserts a
  `data.stderr` member is non-empty — ENU-020, ENU-033, ENU-034 — depends on this. Worth
  harvesting the underlying rule as portable even though this particular case is artifact: *the
  diagnostic from an external dependency is captured and carried into the error surfaced to the
  caller, never swallowed.*

### ENU-046 — Arguments reach the search binary without shell interpretation

- **Behavior:** A glob pattern or search regex is passed to the external search binary as a
  discrete argument and is never subject to shell expansion, quoting, or interpretation. A pattern
  containing shell metacharacters means to the binary exactly what the caller wrote.
- **Trigger:** Any invocation; particularly a pattern containing `*`, quotes, or other
  shell-significant characters.
- **Kind:** `semantic`
- **Source:** `Private/RipgrepHelpers.ps1:65,98` — no `It` block; harvested from the
  implementation
- **Note:** **Untested** — no case supplies a shell-metacharacter-bearing pattern and checks it
  survives intact. Marked `semantic` rather than `artifact` per the tie-break rule: the caller
  supplies the pattern, so whether it is shell-interpreted determines both what they get back and
  whether a crafted pattern can do something other than search. The mechanism (an argument array
  rather than a composed command string) is the artifact half. A likely gap in a spec-derived
  suite, and the highest-value untested behavior in this area.

### ENU-047 — The search is rooted at the vault directory without disturbing global process state

- **Behavior:** The search runs with the vault directory as its working directory, and doing so
  does not alter state shared with other concurrent operations — two searches against different
  vaults running at once do not interfere.
- **Trigger:** Concurrent enumeration or search calls against different vaults.
- **Kind:** `semantic`
- **Source:** `Private/RipgrepHelpers.ps1:76-78` — no `It` block; harvested from the
  implementation
- **Note:** **Untested** — no concurrency case exists anywhere in this area. Harvested because the
  implementation documents it as a deliberate choice (a scoped location push affecting only the
  local execution context, explicitly *not* the process-global current directory, so pooled
  concurrent execution contexts do not collide). The rooting itself is what makes patterns match
  vault-relative paths (ENU-015) and provides the structural half of containment (ENU-019), so it
  is load-bearing; the concurrency-safety half is the untested part.

### ENU-048 — Symbolic links are never followed during enumeration or search

- **Behavior:** Enumeration and content search do not traverse symbolic links, so a link pointing
  outside the vault cannot surface external files as vault content.
- **Trigger:** A symlink inside the vault pointing to a directory outside it.
- **Kind:** `semantic`
- **Source:** `Public/Enumerate.ps1:60-66,155` — no `It` block; harvested from the implementation
- **Note:** **Untested** — no fixture contains a symlink. Documented in the implementation as the
  one real vault-escape vector, guarded by simply never opting into link-following. Junctions and
  reparse points are covered instead by the per-result re-resolution guard, which *is* exercised
  indirectly by the escape probes (ENU-019). Symlink creation typically needs elevation on
  Windows, which likely explains the absent test. A reconciler should decide whether this becomes
  a real test or a documented constraint.

### ENU-049 — The enumeration operation itself is permitted under a scoped grant

- **Behavior:** A grant scoped to a subfolder may still *perform* an enumeration or search over
  the vault; the scoping narrows the results rather than refusing the operation.
- **Trigger:** Any scoped enumeration — implied by every scoped case (ENU-004, ENU-012, ENU-021,
  ENU-036) succeeding rather than throwing.
- **Kind:** `semantic`
- **Source:** `Enumerate.Tests.ps1:28,71,123,225` — implied by four passing scoped cases
- **Note:** Harvested separately because it is a distinct decision from ENU-005, and the
  implementation makes it explicitly: the operation is gated on the literal path `.`, which is
  always permitted even under a scoped grant, with per-result filtering applied afterward. A
  reimplementation that gates enumeration on the vault root being in scope would refuse every
  scoped enumeration and break all four cases above.

### ENU-050 — The module fixture: a temp root with two vault copies and injected files

- **Behavior:** The suite runs against a temp root containing two full copies of `test-vault`
  named `test` and `scratch`, with `.obsidian/junk.md`, `A/Dupe.md` and `B/Dupe.md` injected into
  `test` only, and a vault configuration naming `test` as the default.
- **Trigger:** Suite setup; torn down after the suite.
- **Kind:** `fixture`
- **Source:** `TestHelpers.ps1:17` — `Initialize-VaultAccessTestFixture`
- **Note:** Every count in this area derives from it: 6 real notes (`Test-Vault.md`,
  `Inbox/Note-Loose-Thought.md`, `Projects/Widget/Widget.md`, `Projects/Widget/Tasks.md`,
  `Projects/Widget/Inbox/Plan-Widget-Assembly.md`, `Resources/Gizmo-Reference.md`) plus 2 injected
  dupes = 8 (ENU-001, ENU-008); 3 under `Projects/` (ENU-015). The `scratch` copy exists only so
  ENU-007 has a second vault to configure. `quorvium` is a token deliberately unique to the
  fixture, appearing twice in `Resources/Gizmo-Reference.md` and once in
  `Projects/Widget/Tasks.md` — which is what makes ENU-023, ENU-024, ENU-025 and ENU-029 work. The
  `A/Dupe.md` + `B/Dupe.md` pair supports basename-collision cases elsewhere; in this area they
  only contribute to the counts.

### ENU-051 — The ripgrep-helper fixture: an isolated two-file scratch tree

- **Behavior:** The invocation cases run against their own temp tree containing `Alpha.md` and
  `Sub/Beta.md`, independent of the vault fixture, with the real binary located on the environment
  path during setup.
- **Trigger:** `RipgrepHelpers.Tests.ps1` setup.
- **Kind:** `fixture`
- **Source:** `RipgrepHelpers.Tests.ps1:9-17` — `BeforeAll`
- **Note:** This file does **not** call the shared vault fixture builder — it imports the module
  and builds its own tree. So these cases exercise the invocation primitive with no vault, no
  configuration, and no grant in play. If the real binary cannot be found on the path during
  setup, this entire file fails at setup rather than skipping.
