# Harvest: Wikilink resolution, the write lock, the tool-result shape, and the shared case corpus

Sources:
- `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1` (156 lines)
- `powershell/VaultAccess/Tests/WriteLock.Tests.ps1` (196 lines)
- `powershell/VaultAccess/Tests/ToolResult.Tests.ps1` (145 lines)
- `powershell/VaultAccess/Tests/SharedCases.Tests.ps1` (131 lines)
- `powershell/VaultAccess/Tests/TestHelpers.ps1` (47 lines)
- `tests/cases/path-cases.json` (8 rows), `tests/cases/scope-cases.json` (19 rows)

Read the implementation for:
- `powershell/VaultAccess/Public/LinkResolve.ps1` (146) and `powershell/VaultAccess/VaultAccess.psm1`
  (the compiled link regex, line 54) — to recover the grammar's actual character classes, since
  the tests only exercise three example strings.
- `powershell/VaultAccess/Private/LinkHelpers.ps1` (47) — `Get-VaultRelativeLinkPath`, the
  relative-path constructor whose output length is the ranking key. It is not directly tested;
  its behavior is only observable through `Resolve-VaultBareLink`.
- `powershell/VaultAccess/Public/WriteLock.ps1` (111) — the lock-name construction and the
  timeout default, neither of which the tests spell out.
- `powershell/VaultAccess/Public/ToolResult.ps1` (153) — the name-convention rule and the
  documented error-code vocabulary.
- `tools/Tests/move.Tests.ps1` lines 470-560 — **read only to establish scope boundaries**, not
  harvested here. See the Summary.

## Summary

Four unrelated sub-areas share this file. The largest by value is link resolution, which in the
**module** suite is four small pure functions: the wikilink regex, the "same name" key
normalizer, a component-aligned suffix test, and the bare-link ranker. There is no vault fixture
in `LinkResolve.Tests.ps1` at all — every case is a string-in/string-out assertion.

Two boundary facts a reconciler must know:

1. **Several facets named in the harvest scope are not asserted in this area.** Aliases, headings,
   embeds, and extensions appear here only as *grammar* — the regex separates them and the
   documented contract is that only the `target` group is ever rewritten while embed/heading/alias
   are preserved verbatim. The behavior of *whole-path-wins over another note's stem*, of *alias
   refresh*, of *emitting a full path when a basename is non-unique*, and of *pinning a bystander
   link to its pre-move target* live in `tools/Tests/move.Tests.ps1` (the move area), which builds
   its own `A/Dupe.md` + `B/Dupe.md` pair. I confirmed those exist and deliberately did not
   harvest them; if the move area's harvest omits them, they are lost. The **unresolved** case in
   this area is only `Resolve-VaultBareLink` returning `$null` for an empty candidate set
   (LNK-020).
2. **The `A/Dupe.md` + `B/Dupe.md` fixture that `TestHelpers.ps1` injects is not consumed by any
   of my four suites.** `LinkResolve.Tests.ps1` uses `A/Dupe.md` / `B/Dupe.md` as *literal
   strings*, never touching the filesystem. The injected files exist for the other module suites
   (enumeration/census) and for move. Every ambiguity case those literals force is captured below
   as LNK-021 through LNK-027.

The Obsidian ranking rule is explicitly **empirical**, mapped by hand against real Obsidian and
re-confirmed on 2026-07-23 (T11, T12). The tests carry the empirical scenario labels. That makes
LNK-024 through LNK-027 the least likely rules to be re-derived correctly from a written spec —
they are counter-intuitive and only a measurement justifies them.

`SharedCases.Tests.ps1` is **load-bearing beyond this suite**: `tests/cases/path-cases.json` and
`tests/cases/scope-cases.json` are consumed *identically* by the C# xUnit suite
(`tests/VaultMcpServer.Tests`), and the file's own header states that any change to mirrored
semantics must land as a shared case that **both** implementations then pass. A behavior in
LNK-054 onward is therefore asserted twice, by two independent implementations. Path and scope
semantics may also be harvested by a path/scope area; overlap is intended.

`ToolResult` looked thin on the error *vocabulary*: only five codes are actually asserted
(`not_found`, `out_of_scope`, `kind_mismatch`, `unknown_vault`, `vault_unavailable`,
`malformed_op`); the rest of the vocabulary is documented prose in the implementation, recorded
as LNK-053.

## Cases

### Sub-area A — Wikilink grammar and resolution

#### LNK-001 — The wikilink grammar is one shared compiled pattern

- **Behavior:** Every request for the vault's wikilink pattern yields the identical pattern
  instance, compiled once rather than per call.
- **Trigger:** Two successive requests for the link pattern.
- **Kind:** `artifact`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:15` — `returns the same compiled instance on every call`
- **Note:** Reference equality of a `[regex]` object is a PowerShell-implementation shape. The
  portable content is only "one grammar, shared by every consumer" — the test's own header says
  the point is that a third consumer (`lint`) reads the grammar through this contract rather than
  copying it. `lint` is out of scope, but this case is not about lint.

#### LNK-002 — A wikilink decomposes into embed marker, target, heading, and alias

- **Behavior:** A link of the form `[[Folder/Note#Heading|Display]]` is parsed into four
  separately-addressable parts: the embed marker (empty for a plain link), the target
  (`Folder/Note`), the heading (`#Heading`, leading `#` retained), and the alias (`|Display`,
  leading `|` retained).
- **Trigger:** Matching the pattern against `see [[Folder/Note#Heading|Display]] here` — i.e. a
  link embedded in surrounding prose.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:22` — `captures target, heading, and alias separately and leaves embed empty for a plain link`
- **Note:** The heading and alias delimiters are captured *with* their sigils, not stripped. Any
  reimplementation that returns a bare `Heading` / `Display` changes what a consumer must
  reassemble.

#### LNK-003 — Only the target is rewritable; embed, heading, and alias are preserved verbatim

- **Behavior:** When a consumer rewrites a link (e.g. because its target moved), the embed marker,
  the `#heading` and the `|alias` are carried through unchanged; only the target text is replaced.
- **Trigger:** Any link rewrite.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/LinkResolve.ps1:17-27` — `Get-VaultLinkPattern` synopsis (implementation read; not asserted in this suite)
- **Note:** Stated as the grammar's contract, not asserted here. The end-to-end assertions for it
  are in the move and delete tool suites, per the test file's own header comment at line 11.

#### LNK-004 — An embed (transclusion) is distinguishable from a plain link

- **Behavior:** `![[diagram.png]]` is recognized as a transclusion: the embed marker is `!` and the
  target is `diagram.png`. A plain `[[...]]` yields an empty embed marker.
- **Trigger:** A wikilink prefixed with `!`.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:31` — `marks a transclusion through the embed group`
- **Note:** The stated purpose is that a consumer which must not treat an attachment embed as a
  note link can tell the two apart. Note the target here carries a real extension (`.png`) — see
  LNK-009.

#### LNK-005 — Malformed or non-wikilink syntax does not match

- **Behavior:** An unclosed link (`[[Unclosed`) is not a wikilink, and a standard Markdown inline
  link (`[Not-A-Wikilink](x.md)`) is not a wikilink.
- **Trigger:** Scanning text containing either form.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:37` — `does not match an unclosed or single-bracket link`
- **Note:** Single-bracket Markdown links are outside the grammar entirely — a link census
  therefore does not see them. Whether that is wanted is not judged here.

#### LNK-006 — The target stops at the link-structural characters

- **Behavior:** A link target may contain any character except `[`, `]`, `|`, and `#`, so it
  captures either a bare basename or a vault-relative path but terminates at the heading sigil,
  the alias sigil, or the closing brackets. The heading segment excludes `[`, `]`, `|`; the alias
  segment excludes `[`, `]`.
- **Trigger:** Any link whose target would otherwise run into a following segment.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/VaultAccess.psm1:54` — the compiled pattern (implementation read; the tests exercise only three example strings)
- **Note:** Consequences worth pinning that no test states: a target may contain spaces; a heading
  may be empty (`[[Note#]]` matches with heading `#`); an alias may be empty (`[[Note|]]` matches);
  a target may **not** be empty (`+` quantifier), so `[[]]` does not match. Also `[[A#B#C]]` — the
  heading group admits further `#` characters, so a nested-heading path is one heading segment.

#### LNK-007 — A block reference is carried in the heading segment

- **Behavior:** An Obsidian block reference (`[[Note#^block-id]]`) parses as a target plus a
  heading segment of `#^block-id`; there is no separate block-reference concept.
- **Trigger:** A link addressing a block rather than a heading.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/VaultAccess.psm1:54` — derived from the heading class `#[^\[\]\|]*` (implementation read; no test asserts block references at all)
- **Note:** **No test in this suite mentions block references.** I record it because the harvest
  scope names them and because the grammar admits them by accident of the character class rather
  than by design. A reimplementation that models block refs separately is not contradicted by any
  assertion here.

#### LNK-008 — A trailing `.md` is stripped so a filename and a bare link share one key

- **Behavior:** The normalization that decides whether two things are "the same name" strips a
  trailing `.md`, so the file `Gizmo-Reference.md` and the link target `Gizmo-Reference` normalize
  to the same key.
- **Trigger:** Keying a filename or a link target.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:44` — `strips a trailing .md so a filename and a bare link share one key`
- **Note:** This one function keys move's basename census, the moved item's old/new identity,
  delete's deleted set, and lint's note index — so all consumers agree on name identity.

#### LNK-009 — Any extension other than `.md` is kept

- **Behavior:** `diagram.png` keys as `diagram.png`. An attachment is linked by its **full**
  filename, unlike a note.
- **Trigger:** Keying a non-Markdown filename or a link target bearing another extension.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:49` — `keeps any other extension - an attachment is linked by its full filename`

#### LNK-010 — Backslashes fold to forward slashes and a trailing slash is trimmed

- **Behavior:** `Areas\Homelab\Note.md` keys as `Areas/Homelab/Note`, and `Areas/Homelab/` keys as
  `Areas/Homelab`. Forward slash is the canonical separator in the link namespace.
- **Trigger:** Keying a Windows-separated path or a folder path with a trailing separator.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:53` — `folds backslashes to forward slashes and trims a trailing slash`
- **Note:** Both transforms happen before the `.md` strip, so `Areas\Homelab\Note.md` gets both.

#### LNK-011 — The extension match is case-insensitive; the rest of the name is preserved as written

- **Behavior:** `Note.MD` keys as `Note` — the extension is recognized regardless of case — but no
  other part of the name is case-folded, so the key preserves the name's own casing.
- **Trigger:** Keying a name whose `.md` extension is upper- or mixed-case.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:58` — `is case-insensitive about the extension only, preserving the name as written`
- **Note:** Important pairing with LNK-016: keys preserve case, but *comparison* of path
  components elsewhere is case-insensitive. A reimplementation that lowercases the key would pass
  comparison tests and silently corrupt any rewrite that emits the key back into a document.

#### LNK-012 — An empty name is a legal input and keys as empty

- **Behavior:** Keying an empty name yields an empty key rather than an error.
- **Trigger:** An empty string.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:62` — `accepts an empty name`
- **Note:** The grammar cannot produce an empty target (LNK-006), so this is defensive tolerance
  for a caller, not a link form.

#### LNK-013 — Surrounding whitespace is trimmed before keying

- **Behavior:** Leading and trailing whitespace on a name or link target is removed before the key
  is computed, so `[[ Note ]]` keys the same as `[[Note]]`.
- **Trigger:** A link target written with padding inside the brackets.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/LinkResolve.ps1:48` — `$Name.Trim()` (implementation read; no test asserts it)
- **Note:** Untested in this suite but user-visible: Obsidian tolerates `[[ Note ]]`, and without
  the trim such a link would never key to any file. Flagged because a spec-derived
  reimplementation would likely omit it.

#### LNK-014 — A path-qualified link matches a component-aligned suffix of a file's path

- **Behavior:** A link target that names only the tail of a path (`Foo/Note`) matches a file at
  `Deep/Foo/Note`, because the target's components are a component-aligned suffix of the file's.
- **Trigger:** A link written in Obsidian's "shortest path when possible" style.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:68` — `matches a component-aligned suffix`
- **Note:** This is *the address form* between a bare basename and a full vault path. Callers pass
  only slash-bearing candidates here; a single-component candidate goes through the bare-basename
  rule (LNK-019 onward) instead.

#### LNK-015 — Suffix matching compares whole components, never substrings

- **Behavior:** `Foo/Note` does **not** match a file at `Deep/Foobar/Note`. A component must match
  in full; a string prefix of a component is not a match.
- **Trigger:** A candidate whose component is a proper prefix of the target's component.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:76` — `refuses a partial component match`
- **Note:** Same class of defect the scope corpus guards against (LNK-065) — a naive
  `StartsWith`/`EndsWith` implementation passes almost every other case and fails only this one.

#### LNK-016 — Suffix matching is case-insensitive

- **Behavior:** `foo/NOTE` matches a file at `Deep/Foo/Note`.
- **Trigger:** A link whose casing differs from the file's.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:72` — `is case-insensitive`
- **Note:** Justified in the implementation as a Windows-filesystem property. On a case-sensitive
  filesystem this is a deliberate choice, not a platform consequence.

#### LNK-017 — A candidate longer than the target is not a suffix of it

- **Behavior:** `Deep/Foo/Note` does not match a file at `Foo/Note`. Suffix matching is
  directional: a more-qualified link cannot match a less-qualified path.
- **Trigger:** A link target with more components than the file's path.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:80` — `refuses a candidate longer than the target`

#### LNK-018 — An empty candidate component list is refused

- **Behavior:** Suffix matching against an empty component list is not a match.
- **Trigger:** Zero-component candidate.
- **Kind:** `artifact`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:83-85` — comment in place of an `It` block
- **Note:** **Deliberately not tested.** The test file records that PowerShell's parameter binder
  rejects `@()` before the function runs, so the function's own empty guard is unreachable and
  defensive. A reimplementation in a language without that binding behavior needs the guard
  itself — the returned value is "no match", not an error.

#### LNK-019 — A single candidate resolves to itself

- **Behavior:** When exactly one file bears the linked basename, that file is the target, without
  regard to distance.
- **Trigger:** A bare link with one candidate.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:94` — `returns the sole candidate unchanged`

#### LNK-020 — No candidate means the link is unresolved

- **Behavior:** A bare link with no same-basename file anywhere in the vault resolves to nothing —
  a null result, not an error and not a guess.
- **Trigger:** An empty candidate set.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:98` — `returns null for an empty candidate set`
- **Note:** This is the whole of the "unresolved" behavior asserted in this area. What a *consumer*
  does with an unresolved link (lint's broken-wikilinks pass — out of scope; move's census) is not
  asserted here.

#### LNK-021 — Among same-basename candidates, the shortest relative path from the linking file wins

- **Behavior:** A bare link resolves to the candidate whose relative path *from the linking file's
  own directory* is shortest by raw character count. From a file at the vault root,
  `Left/Dup.md` (11 characters) beats `Right/Dup.md` (12).
- **Trigger:** A bare link with two same-basename candidates at differing distances.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:102` — `Left-vs-Right: the shorter relative path wins`
- **Note:** The rule is **empirical** — reverse-engineered from real Obsidian behavior, documented
  in the source vault as `Resources/Software/Obsidian-Link-Behavior.md`. Character count, not
  segment count. Note the deciding difference here is one character of *folder name length*, which
  is why this specific case is convincing.

#### LNK-022 — An exact-length tie is broken alphabetically, ordinal and case-insensitive

- **Behavior:** When two candidates score identically, the winner is the alphabetically-first by
  ordinal case-insensitive comparison. From `Inbox/Refs.md`, `../A/Dupe.md` and `../B/Dupe.md` are
  both 12 characters; `A/Dupe.md` wins.
- **Trigger:** Two same-basename candidates at equal relative-path length.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:108` — `breaks an exact-length tie alphabetically (ordinal, case-insensitive)`
- **Note:** This is the canonical `A/Dupe.md` + `B/Dupe.md` ambiguity case, and the reason the
  fixture injects that pair. "Ordinal" matters: a culture-aware comparison can order differently
  for non-ASCII names.

#### LNK-023 — Resolution is order-independent

- **Behavior:** The winner does not depend on the order the candidates were discovered in. Ranking
  `A/Dupe.md, B/Dupe.md` and `B/Dupe.md, A/Dupe.md` both yield `A/Dupe.md`.
- **Trigger:** The same candidate set presented in either order.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:114` — `is order-independent (same winner regardless of candidate order)`
- **Note:** A total order, so filesystem enumeration order cannot change which file a link opens.
  Worth keeping as its own case: a strict `<` comparison in the tie-break makes this true, a `<=`
  makes it false, and no other case detects the difference.

#### LNK-024 — A candidate inside the linking file's own subtree beats a globally-shorter path elsewhere (T3)

- **Behavior:** Resolution is measured relatively, not vault-globally. From
  `Tests/T3/Deep/T3-Links-B.md`, the in-subtree candidate `Tests/T3/Deep/Down/T3-Dup.md`
  (relative: `Down/T3-Dup.md`, 14 chars) beats `Tests/T3/North/T3-Dup.md` (relative:
  `../North/T3-Dup.md`, 18 chars) — even though the winner's *vault-absolute* path is longer.
- **Trigger:** A bare link where the deeper candidate is nearer.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:121` — `a candidate inside the linking file's own subtree beats a globally-shorter path elsewhere (T3 Links-B)`
- **Note:** Empirical scenario T3. **High reconciliation risk** — an implementation that ranks by
  vault-absolute path length is a natural reading of "shortest path" and gets this backwards.

#### LNK-025 — Fewer characters beat fewer folder hops (T11)

- **Behavior:** The score is a pure character count with no weighting for the number of path
  segments. From `Tests/T11/T11-Links.md`, two hops down at `A/B/T11-Dup.md` (14 chars) beats one
  hop down at `Alphabet-Zoo-Folder/T11-Dup.md` (30 chars).
- **Trigger:** A bare link where the shallower candidate sits under a long folder name.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:130` — `fewer characters beat fewer folder hops (T11)`
- **Note:** Empirical, **confirmed against Obsidian on 2026-07-23**. The implementation records
  that this closed an open refinement question — whether `..` components carry extra weight. They
  do not. **High reconciliation risk**: "distance" reads naturally as hop count.

#### LNK-026 — A tie breaks on the candidates' vault paths, not their relative paths (T12)

- **Behavior:** When scores tie, the alphabetical comparison is made on the candidates'
  **vault-relative** paths, not on the `..`-spelled relative paths. From
  `Tests/T12/Sub/T12-Links.md`, `../T12-Dup.md` and `Al/T12-Dup.md` are both 13 characters;
  comparing relative paths would pick the parent (`.` sorts before `A`), but the subtree copy
  `Tests/T12/Sub/Al/T12-Dup.md` wins, matching an ordinal comparison of the vault paths
  (`Tests/T12/S...` < `Tests/T12/T...`).
- **Trigger:** A tie where the two orderings disagree.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:138` — `ties break on the candidate vault path, not the relative path (T12)`
- **Note:** Empirical, **confirmed against Obsidian on 2026-07-23**. This is the subtlest rule in
  the area: the *score* is computed on the relative path but the *tie-break* is computed on the
  vault path. **Highest reconciliation risk of any case in this file** — using one path form for
  both is the obvious implementation and it is wrong.

#### LNK-027 — Distance is measured from the linking file, so moving a candidate changes the winner

- **Behavior:** The origin of measurement is the linking file's directory, not the vault root. From
  `A/B/Link.md`, `[[Dup]]` resolves to `A/Dupe.md` (`../Dupe.md`, 10) over `Far/Dupe.md`; once a
  same-basename file exists at `A/B/Dupe.md` (`Dupe.md`, 7), that becomes the winner.
- **Trigger:** The same link text in the same file, against two different candidate sets.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/LinkResolve.Tests.ps1:148` — `measures distance from the linking file, not the vault root (moving closer changes the winner)`
- **Note:** Two assertions in one `It` block, both listed. This is the property that makes a move
  able to silently re-point an unrelated bystander link — the pinning behavior built on top of it
  lives in the move suite.

#### LNK-028 — The ranking key is the spelled-out relative path, `..` components included

- **Behavior:** The score is the character length of the relative path *as it would be written*,
  with a literal `..` segment (two characters plus a separator) for every level walked up out of
  the common ancestor — e.g. `../North/T3-Dup.md`.
- **Trigger:** Any ranking where the candidate is not under the linking file's directory.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/LinkResolve.ps1:86-99` and `powershell/VaultAccess/Private/LinkHelpers.ps1:7-47` (implementation read; observable only through LNK-024/025/026)
- **Note:** The exact spelling *is* the rule, because the rule is a character count of it. Two
  spellings that a reader would call equivalent (`./Foo.md` vs `Foo.md`) score differently. The
  implementation emits no leading `./`.

#### LNK-029 — The relative path is built from the longest common directory prefix, compared case-insensitively

- **Behavior:** The relative path walks up from the linking file's directory to the longest common
  leading directory run shared with the candidate, then down into the candidate's remaining
  directories. Directory-segment comparison when finding that common run is case-insensitive.
- **Trigger:** Ranking any candidate.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Private/LinkHelpers.ps1:32-36` (implementation read; no direct test — the helper is private and untested)
- **Note:** Untested even indirectly for the case-insensitive branch: no fixture has two directory
  segments differing only in case. A reimplementation could compare case-sensitively and pass every
  test in this suite while scoring differently on a real vault.

#### LNK-030 — Ranking does not filter; candidates are assumed to share a basename

- **Behavior:** The ranker only chooses among the candidates it is given; it does not verify that
  they share a name with the link target. Name matching is the caller's census.
- **Trigger:** Any ranking call.
- **Kind:** `artifact`
- **Source:** `powershell/VaultAccess/Public/LinkResolve.ps1:88-90` — `.DESCRIPTION` (implementation read)
- **Note:** A layering fact rather than a vault behavior — recorded because it explains why no test
  here passes mismatched candidates, and because a redesign that fuses census and ranking into one
  step has no test to tell it where the seam was.

### Sub-area B — The write lock

#### LNK-031 — The write-lock identity is stable for a vault

- **Behavior:** Deriving the write-lock identity for the same vault twice yields exactly the same
  identity.
- **Trigger:** Two derivations for one vault.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:19` — `is deterministic for the same vault`

#### LNK-032 — Vaults with different roots get different locks

- **Behavior:** Two vaults rooted at different directories derive different lock identities and
  therefore do not serialize against each other.
- **Trigger:** Deriving lock identities for the `test` and `scratch` fixture vaults.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:23` — `differs between vaults with different roots`

#### LNK-033 — The lock identity derives from the vault root path, not the vault name

- **Behavior:** Two *differently-named* configured vaults pointing at the **same** directory derive
  the identical lock identity, so they contend for one lock. A config with `test` and `twin` both
  naming one root must serialize writes across both names.
- **Trigger:** A config aliasing two names to one root.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:27` — `derives from the root path, not the vault name`
- **Note:** The test's own header calls the lock name a **cross-process contract with VaultWatcher**
  — a separate process that constructs the identical identity string to contend for the same lock.
  That makes the derivation rule (LNK-035) externally observable, not an internal detail: changing
  it silently stops two processes from serializing.

#### LNK-034 — The lock identity is a legal OS mutex name

- **Behavior:** The derived identity is under 260 characters and contains no backslash after the
  reserved namespace prefix.
- **Trigger:** Deriving a lock identity for any vault, including one with a long root path.
- **Kind:** `artifact`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:41` — `is a legal mutex name - no backslash outside the reserved prefix, within length limit`
- **Note:** Marked `artifact` because 260-and-no-backslash is a Win32 named-object constraint. The
  *semantic* content underneath is real and portable: an arbitrarily-long, separator-bearing
  filesystem path must be reduced to a bounded, separator-free identifier — which is why the
  derivation hashes.

#### LNK-035 — The lock name is `Global\VaultWrite-<32 hex of SHA-256 of the lowercased, trimmed root>`

- **Behavior:** The identity is built by trimming trailing directory separators from the resolved
  root, lowercasing it, taking the SHA-256 of its UTF-8 bytes, and using the first 32 hex
  characters. The `Global\` prefix is applied on Windows only.
- **Trigger:** Any lock-identity derivation.
- **Kind:** `artifact`
- **Source:** `powershell/VaultAccess/Public/WriteLock.ps1:6-40` (implementation read; the tests assert only the properties in LNK-031..034, never the literal form)
- **Note:** Recorded in full because LNK-033 says an external process reproduces this string
  byte-for-byte. **The lowercasing is on every platform, deliberately** — so a case-only difference
  between two configs cannot leave two processes unserialized. `Global\` scopes the lock across
  login sessions.

#### LNK-036 — The locked block's output passes through unchanged

- **Behavior:** Running an operation under the write lock returns the operation's own result to the
  caller.
- **Trigger:** A locked operation producing a value.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:49` — `runs the scriptblock and passes its output through`

#### LNK-037 — The lock is released when the operation completes

- **Behavior:** After a locked operation finishes normally, a subsequent locked operation on the
  same vault acquires immediately.
- **Trigger:** Two sequential locked operations on one vault.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:53` — `releases the lock after the block completes`

#### LNK-038 — The lock is released when the operation throws, and the error propagates

- **Behavior:** An operation that fails under the lock still releases it: the failure surfaces to
  the caller unchanged, and the next locked operation on that vault acquires normally.
- **Trigger:** A locked operation that raises an error.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:58` — `releases the lock when the block throws`
- **Note:** The test states the design reason directly: the lock is only offered as a wrapper
  around an operation, never as an acquire/release pair, so **no caller can leak the lock on an
  error path because no caller writes the release**. That API shape is itself the behavior —
  see LNK-045.

#### LNK-039 — The lock is reentrant on the same thread

- **Behavior:** A locked operation may itself take the same vault's write lock; the inner
  acquisition succeeds rather than deadlocking, and each level releases its own hold.
- **Trigger:** Nested locked operations on one vault.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:65` — `is reentrant on the same thread`

#### LNK-040 — Locks on different vaults do not serialize against each other

- **Behavior:** Holding one vault's write lock does not block acquiring another vault's. A nested
  acquisition of a *different* vault's lock succeeds within the timeout.
- **Trigger:** A locked operation on `test` taking `scratch`'s lock inside it.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:71` — `does not serialize against a different vault`
- **Note:** The 5-second timeout on the inner acquisition is what makes this a real assertion
  rather than a deadlock — without per-vault scoping the test hangs, then fails.

#### LNK-041 — A clean acquisition reports no abandonment

- **Behavior:** When the lock is acquired without a prior owner having died, the abandonment signal
  reads false.
- **Trigger:** A normal, uncontended acquisition.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:77` — `reports not-abandoned on a clean acquisition`

#### LNK-042 — Contention past the timeout raises a coded `lock_timeout` error

- **Behavior:** When another **process** holds the vault's write lock and it cannot be acquired
  within the timeout, the operation fails with the structured error shape: code `lock_timeout`, a
  message naming the timeout and the write lock (matching `*Timed out*waiting for the vault write
  lock*`), and `data.timeoutSeconds` carrying the timeout that elapsed.
- **Trigger:** A second process holding the lock; a 2-second acquisition attempt.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:130` — `times out when another process holds the lock, raising the coded lock_timeout shape`
- **Note:** The test explains that the code is raised **at the chokepoint** precisely so no tool has
  to re-derive it by matching message text; tools do not catch it and it surfaces unchanged. The
  test file needs a real second process because the lock is reentrant (LNK-039), so a same-thread
  contention attempt would simply succeed.

#### LNK-043 — A lock abandoned by a dead process is acquired, not failed, and the recovery is reported

- **Behavior:** When the process holding the write lock dies without releasing it, the next waiter
  **acquires** the lock (it does not fail or wait out the timeout) and is told the previous owner
  died mid-operation. The operation then runs normally.
- **Trigger:** Killing a process that holds the lock, then acquiring.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:157` — `acquires and reports an abandoned lock when the holding process dies, and stays usable after`
- **Note:** The design intent stated in the test and implementation: this **self-healing replaces a
  release timeout**. Recovery is *signalled*, not inferred from elapsed time, because a hung (but
  live) holder never abandons — a timeout could not distinguish the two. The implementation also
  emits a warning telling the operator the vault may be torn and that `git status` / `git diff` is
  the diagnosis. Windows-only test (`-Skip:(-not $IsWindows)`).

#### LNK-044 — The abandonment signal is consumed, not sticky

- **Behavior:** After one waiter has been told the lock was abandoned, the very next acquisition of
  that same lock reports a clean acquisition.
- **Trigger:** Two successive acquisitions following an owner's death.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:186-189` — same `It` block, second half
- **Note:** The suite gives *each* cross-process test its own isolated vault (hence its own lock)
  specifically because abandonment is a **one-shot** signal the OS clears once consumed — tests
  sharing one lock would leak that state into each other and pass or fail on ordering. That is a
  fixture consequence of this behavior, worth carrying: it constrains how the behavior can be
  tested at all.

#### LNK-045 — The write lock is only available as a wrapper around a whole operation

- **Behavior:** There is no separate acquire and release; the lock is taken by handing an operation
  to it. The lock is meant to wrap the entire logical operation, not individual mutating calls — a
  move issues N+1 mutations (one write per backlinked file, then the file move), and a lock taken
  inside each of those would acquire and release N+1 times without ever serializing the operation.
- **Trigger:** Any write-class tool operation.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/WriteLock.ps1:44-66` — `.SYNOPSIS` (implementation read; the property is *demonstrated* by LNK-038 but the API-shape rule is only documented)
- **Note:** Semantic rather than artifact because "the unit of serialization is the logical
  operation" is a portable contract, not a PowerShell idiom.

#### LNK-046 — The acquisition timeout defaults to 60 seconds and is environment-overridable

- **Behavior:** Absent an explicit timeout, acquisition waits 60 seconds, overridable via the
  `VAULT_MCP_WRITE_LOCK_TIMEOUT_SECONDS` environment variable. The timeout is a liveness guard
  only — it fails the request rather than hanging it — and is explicitly **not** the recovery
  mechanism.
- **Trigger:** A locked operation with no timeout specified.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/WriteLock.ps1:71` (implementation read; no test asserts the default — every test passes an explicit timeout or relies on an uncontended lock)
- **Note:** The 60-second default and the env var name are both untested. Flagged as a likely gap
  in any reconstructed suite.

### Sub-area C — The tool-result envelope and error vocabulary

#### LNK-047 — A tool error is `{ code, message }`, with `data` absent unless supplied

- **Behavior:** An error carries a machine-routable `code` and a human/model-readable `message`.
  When no structured payload is supplied, the `data` field is **absent from the envelope entirely**
  rather than present-and-null.
- **Trigger:** Building an error with no payload.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:12` — `builds the {code, message} shape`
- **Note:** The absence is asserted explicitly (the property name must not be present), and the
  implementation states the reason: a caller tests for the field's *presence*, not its nullness. A
  reimplementation that serializes `"data": null` fails this.

#### LNK-048 — `data` carries an arbitrary structured payload when supplied

- **Behavior:** When a payload is supplied it appears under `data` with its own field structure
  preserved — e.g. `data.path` naming the offending path.
- **Trigger:** Building an error with a payload.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:20` — `includes data only when supplied`

#### LNK-049 — The error envelope is the failure's own text

- **Behavior:** A raised tool error surfaces the serialized envelope as the failure's message, so
  the same shape reaches the caller whether the tool is invoked through the dispatcher or run
  standalone (where it appears on stderr).
- **Trigger:** Raising a built error.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:26` — `is throwable directly, surfacing the JSON as the exception message`
- **Note:** The "one error shape regardless of which layer raised it, and regardless of invocation
  path" property is the real content; that PowerShell achieves it by throwing a string is the
  artifact part.

#### LNK-050 — Vault resolution succeeds with a name-and-root pair

- **Behavior:** Tool-facing vault resolution returns the resolved vault's canonical name and root
  directory, identical to what core resolution returns.
- **Trigger:** Resolving a configured, available vault by name.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:52` — `returns the same Name/Root object Resolve-VaultRoot returns on success`
- **Note:** The name is compared **case-exactly** (`BeExactly`), so the returned name is the
  configured spelling.

#### LNK-051 — Omitting the vault name resolves the configured default

- **Behavior:** A tool invoked without a vault name operates on the configured default vault.
- **Trigger:** Resolution with no name; also with an empty/whitespace name (implementation).
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:58` — `resolves the default vault when no name is supplied`

#### LNK-052 — An unconfigured vault name fails as `unknown_vault`, listing the configured vaults

- **Behavior:** Resolving a name no configuration entry names fails with code `unknown_vault`,
  `data.vault` echoing the requested name, and `data.configuredVaults` listing the names that *are*
  available. The message carries the core layer's own wording (matching `Unknown vault`) rather
  than replacing it.
- **Trigger:** Resolving a name absent from the config.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:62` — `refuses an unconfigured name with unknown_vault`
- **Note:** `configuredVaults` lists only the *available* vaults — a skipped vault (LNK-053) is not
  in that list, so a user who misspells a name is not offered a vault they cannot use.

#### LNK-053 — A configured-but-skipped vault fails as `vault_unavailable`, carrying the skip reason

- **Behavior:** Resolving a vault that *is* configured but whose root was missing at load time
  fails with the distinct code `vault_unavailable`, `data.vault` naming it, and `data.reason`
  carrying why it was skipped (matching `does not exist`). The message says the vault is
  `currently unavailable`.
- **Trigger:** Resolving a vault whose configured root directory does not exist.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:72` — `refuses a configured-but-skipped vault with vault_unavailable, carrying the skip reason`
- **Note:** The two codes exist precisely to discriminate "you named something that does not exist"
  from "the thing you named is offline right now" — different remedies. The implementation notes
  the classification is made from **config state** (which map holds the name), never by matching
  the message text, so the codes stay distinct even if the prose changes.

#### LNK-054 — The default vault is classified the same way when the default itself is unavailable

- **Behavior:** When the *default* vault's root is missing, resolving with no name supplied fails
  as `vault_unavailable` naming the default vault — not as a generic or unknown-vault failure.
- **Trigger:** A config whose `default_vault` names a vault with a missing root; resolution with no
  name.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:81` — `classifies the default vault too when the default itself is skipped`
- **Note:** Requires the default name to be substituted *before* classification, or the lookup has
  no name to classify.

#### LNK-055 — Core vault resolution stays uncoded

- **Behavior:** The core (non-tool-facing) resolution failure carries plain prose only — its
  message says `Unknown vault` and does **not** contain the machine code `unknown_vault`. The code
  is added by the tool-facing layer, not by the core.
- **Trigger:** Core resolution of an unconfigured name.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:97` — `leaves Resolve-VaultRoot itself throwing the mirrored plain text`
- **Note:** This is a **layering** assertion with a cross-implementation reason: the C# side raises
  the same wording and has no shared case file for it, so the two implementations' parity holds
  only while the core keeps its prose. A redesign that pushes codes down into the core breaks a
  mirror this test is guarding.

#### LNK-056 — Config-load failures are not vault-resolution failures and carry no code

- **Behavior:** A missing or malformed configuration file is not classified as a vault-resolution
  refusal; it propagates uncoded from the config layer.
- **Trigger:** Tool-facing resolution against a broken config file.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Public/ToolResult.ps1:58-59` — `.SYNOPSIS` (implementation read; not asserted in this suite)

#### LNK-057 — A write-class tool returns exactly one result object

- **Behavior:** A write-class tool's result is a single JSON object whose fields survive
  serialization — e.g. `{ oldPath, newPath, referencesRewritten }`, with a numeric zero preserved
  as `0`.
- **Trigger:** Serializing a single result object.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:108` — `serializes a single hashtable result as a JSON object`
- **Note:** The implementation names this the Tier-2 return convention: the single JSON summary
  object a write-class tool emits as its text content, in place of a read-class tool's
  native-format output. `sync` and `lint` are named among the write-class tools; both are out of
  scope and the case is harvested regardless (rule 3).

#### LNK-058 — Array-valued fields nested inside the result object are preserved

- **Behavior:** A result field that legitimately holds a list (e.g. `filesRewritten`) round-trips
  as an array of the right length. The single-object rule constrains the top level only.
- **Trigger:** A result containing a list-valued field.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:115` — `preserves array-valued fields nested inside the single result object`

#### LNK-059 — A result that is itself a collection fails loudly as `malformed_op`

- **Behavior:** If a tool's result is a collection rather than a single object, serialization fails
  with code `malformed_op` rather than emitting an array where every caller and schema expects an
  object.
- **Trigger:** Serializing a two-element result.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:121` — `throws malformed_op when the result is an array (stray success-stream output arrayified the return value)`
- **Note:** The specific failure guarded against is **PowerShell's implicit output stream**: a stray
  `Write-Output` or an un-suppressed cmdlet call silently turns the return value into an array. In
  a language without an implicit output stream this guard has no failure mode to catch — the
  clearest `artifact`-leaning case in this file, kept `semantic` because "a tool's result is one
  object and a violation fails rather than ships" is a portable contract. The implementation's
  message includes the offending count.

#### LNK-060 — The vault name convention is Title-Case-With-Hyphens

- **Behavior:** A name conforms when it is one or more hyphen-separated segments, each beginning
  with an uppercase letter or a digit and continuing with letters or digits only. Spaces,
  underscores, lowercase-initial segments, a leading or trailing hyphen, a doubled hyphen, an empty
  name, and a whitespace-only name all fail.
- **Trigger:** Checking a proposed file or folder name. Rows asserted:
  - valid: `Foo-Bar`, `Foo`, `Vault-MCP-Server` (all-caps acronym segment),
    `Maui-Trip-2026` (digit-initial segment), `Foo-Bar.md`, `Foo-Bar.MD`
  - invalid: `foo-bar` (lowercase initial), `Foo bar` (space), `Foo_Bar` (underscore),
    `-Foo-Bar` (leading hyphen), `Foo-Bar-` (trailing hyphen), `Foo--Bar` (doubled hyphen),
    `` (empty), `   ` (whitespace only)
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:127` — `<name>`, 14 test cases
- **Note:** Digit-initial segments are admitted deliberately so a year (`2026`) is legal, and
  all-caps segments so an acronym (`MCP`) is. The implementation states consumers **warn** on a
  false result and never fail the operation over it — move reports it as a `namingWarnings` field;
  only link-breaking or OS-invalid characters, checked elsewhere, block a move. That
  warn-don't-fail disposition is not asserted here (it belongs to the move suite) but is the
  reason this check returns a boolean rather than raising.

#### LNK-061 — The convention check ignores a trailing `.md`, so one check serves files and folders

- **Behavior:** A trailing `.md` is stripped case-insensitively before the name is checked, so
  `Foo-Bar.md` and `Foo-Bar.MD` are as valid as `Foo-Bar` and the same check applies to folder
  names.
- **Trigger:** Checking a filename with an extension.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/ToolResult.Tests.ps1:132-133` — rows `Foo-Bar.md` / `Foo-Bar.MD`
- **Note:** Separated from LNK-060 because it is a distinct rule, and because it matches the same
  `.md`-stripping convention as the link key (LNK-008, LNK-011) — two independent places in the
  system agree that `.md` is invisible.

### Sub-area D — The shared case corpus (cross-checked against the C# suite)

> **These cases are load-bearing.** `tests/cases/path-cases.json` and `tests/cases/scope-cases.json`
> are consumed **identically by two independent implementations** — this PowerShell suite and the
> C# xUnit suite at `tests/VaultMcpServer.Tests` — so each row below has been asserted twice, over
> two codebases, and is a *parity contract* rather than one suite's opinion. The file's header
> states the rule: any change to mirrored semantics lands here first as a shared case, and both
> implementations must then pass it. The `Vault config skip semantics` block (LNK-062..065) is part
> of the same mirrored family but is hand-written rather than data-driven, because the behavior is
> expressed through whole configurations rather than a single-call case row; its C# counterpart is
> `VaultConfigTests.cs` / `VaultConfigLoadTests`.

#### LNK-062 — A configured vault whose root is missing is skipped, and the load still succeeds

- **Behavior:** Loading a configuration in which one vault's root directory does not exist does not
  fail the load. The good vault is available and resolves; the bad one is absent from the available
  set and present in a skipped set with a reason stating the root does not exist. Skipping is
  per-entry.
- **Trigger:** A two-vault config where one root is missing.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:40` — `skips a configured vault whose root does not exist, without failing the load`
- **Note:** Mirrors the C# `VaultConfig.Load`/`SkippedVaults`. The reason string is carried, not
  discarded — it is what LNK-053 surfaces to a caller as `data.reason`.

#### LNK-063 — Resolving a skipped vault is a distinct failure from resolving an unknown one

- **Behavior:** A skipped vault fails to resolve as `currently unavailable` and its message also
  names why (`does not exist`); it does **not** say `Unknown vault`. A name that is not configured
  at all fails as `Unknown vault` and does **not** say `currently unavailable`. The two failures
  are mutually exclusive in wording.
- **Trigger:** Resolving a skipped name, then an unconfigured name, against the same config.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:55` — `fails resolving a skipped vault as "currently unavailable", distinct from unknown`
- **Note:** The negative half of each assertion is the load-bearing part — it is what stops the two
  failure modes from converging on one generic message. Pairs with LNK-052/LNK-053, which put codes
  on the same distinction one layer up.

#### LNK-064 — A missing default-vault root does not fail the load, only the resolution

- **Behavior:** A configuration whose `default_vault` names a vault with a missing root loads
  successfully; the failure appears only when the default is actually resolved, as `currently
  unavailable`.
- **Trigger:** A config whose default names the missing-root vault.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:75` — `loads successfully when default_vault's root is missing, but resolving the default fails as unavailable`
- **Note:** Deliberate: the server can start with an unmounted default vault, and the other vaults
  remain usable. Failure is deferred to use.

#### LNK-065 — A configuration in which every vault root is missing still loads

- **Behavior:** When no configured vault's root exists, the load succeeds, the available set is
  empty, every entry appears in the skipped set, and resolving the default fails as `currently
  unavailable`.
- **Trigger:** A two-vault config with both roots missing.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:84` — `loads successfully even when every vault root is missing`
- **Note:** The degenerate case: zero available vaults is a legal loaded state, not a startup
  error. A reimplementation that treats "no usable vaults" as fatal fails here.

#### LNK-066 — A vault-relative path resolves to an absolute location inside the vault

- **Behavior:** Path resolution accepts a vault-relative path and yields a rooted absolute path.
  Accepted forms, from the shared corpus:
  - `Inbox/Note-Loose-Thought.md` — a simple relative path
  - `Inbox\Note-Loose-Thought.md` — backslash separators are equivalent
  - `Projects/../Test-Vault.md` — a `..` component that stays inside the vault
  - `Not/Yet/Written.md` — a path that does not exist yet still resolves
- **Trigger:** Resolving each accepted form.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:98` — `<name>`, rows 1-4 of `tests/cases/path-cases.json`
- **Note:** "Nonexistent still resolves" is the notable one: resolution is a *naming* operation, not
  an existence check — a create must be able to resolve the path it is about to write.

#### LNK-067 — An absolute or rooted path is refused

- **Behavior:** A path that is already rooted (e.g. `C:/Windows/win.ini`) is refused rather than
  resolved. Caller-supplied paths are always vault-relative.
- **Trigger:** `C:/Windows/win.ini`.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:98` — row `rooted path rejected`

#### LNK-068 — A path that escapes the vault root is refused, at any depth

- **Behavior:** A path whose `..` components take it outside the vault is refused — both the direct
  form (`../outside.md`) and the form that descends first and then over-ascends
  (`Projects/../../outside.md`). The check is on the *resolved* location, not on the textual
  presence of `..` (compare LNK-066, where an interior `..` is accepted).
- **Trigger:** `../outside.md`; `Projects/../../outside.md`.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:98` — rows `single dot-dot escape rejected`, `nested dot-dot escape rejected`
- **Note:** The containment boundary. The nested form is the one a naive "reject any `..`" or a
  naive "reject a leading `..`" implementation gets wrong in opposite directions.

#### LNK-069 — A whitespace-only path is refused

- **Behavior:** A path consisting only of whitespace (`"   "`) is refused rather than treated as
  the vault root.
- **Trigger:** `"   "`.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:98` — row `whitespace-only path rejected`

#### LNK-070 — A grant with no scope roots for a vault permits any path in it

- **Behavior:** When a grant configures no scope roots for a vault, every path in that vault is in
  scope.
- **Trigger:** An empty root list for the vault being checked.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — row `unscoped grant allows anything`

#### LNK-071 — A path under a scope root is in scope, at any depth and for any root depth

- **Behavior:** Containment is by path, not by immediate parentage. Rows:
  - `Projects/Widget/Widget.md` under a single-segment root `Projects`
  - `Projects/Vault-MCP-Server/Corpus/Server-Structure.md` under the multi-segment root
    `Projects/Vault-MCP-Server` (two levels below it)
  - `Projects/Vault-MCP-Server/Tasks.md` — exactly one segment below its root
- **Trigger:** Each path/root pair above.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — rows 2-4 of `tests/cases/scope-cases.json`

#### LNK-072 — A path outside every scope root is out of scope

- **Behavior:** A path under an unrelated top-level folder (`Areas/Personal/Health.md` against root
  `Projects/Vault-MCP-Server`) is out of scope.
- **Trigger:** A path sharing no prefix with any root.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — row `path under a different top-level folder is out of scope`

#### LNK-073 — Scope containment is component-aligned: a shared string prefix is not containment

- **Behavior:** Against the root `Projects/Vault-MCP-Server`, both of these are **out of scope**:
  - `Projects/Vault-MCP-Server-Old/Notes.md` — a sibling *folder* whose name string-prefixes the
    root's last segment
  - `Projects/Vault-MCP-Server.md` — a *note* whose name string-prefixes the root but which sits
    beside it, not inside it
- **Trigger:** Either path against that root.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — rows `sibling folder sharing a string prefix is out of scope`, `note whose name string-prefixes the root but sits outside it is out of scope`
- **Note:** The security-relevant pair. A `path.StartsWith(root)` implementation passes every other
  scope row and grants access to both of these. Directly analogous to LNK-015 in link matching —
  the same component-alignment rule, enforced independently in two subsystems.

#### LNK-074 — With several scope roots, containment by any one of them suffices

- **Behavior:** Given roots `Projects/Vault-MCP-Server` and `Areas/Homelab`, the path
  `Areas/Homelab/Servers.md` is in scope (it matches the second); `Areas/Personal/Health.md`
  matches neither and is out of scope.
- **Trigger:** A multi-root grant.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — rows `path matches when any of several roots contains it`, `path matches none of several roots`

#### LNK-075 — Scope matching is case-insensitive per component

- **Behavior:** `projects/WIDGET/widget.md` is in scope for the root `Projects/Widget`.
- **Trigger:** A path differing from its root only in casing.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — row `case-insensitive segment match`
- **Note:** Consistent with LNK-016. Note the direction of risk here is the reverse of link
  matching: case-insensitive scope matching *widens* what a grant admits.

#### LNK-076 — Backslash-separated paths normalize identically for scope purposes

- **Behavior:** `Projects\Widget\Widget.md` is in scope for `Projects/Widget`, exactly as the
  forward-slash spelling is.
- **Trigger:** A Windows-separated path against a forward-slash root.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — row `backslash-separated path normalizes the same as forward-slash`

#### LNK-077 — A scope root containing an interior `..` normalizes before matching

- **Behavior:** The root `Projects/Foo/../Widget` normalizes to `Projects/Widget` and admits
  `Projects/Widget/Widget.md`.
- **Trigger:** A root written with a `..` component that stays inside the vault.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — row `root with a trailing dot-dot that stays inside the vault normalizes cleanly`
- **Note:** Roots go through the same normalization as paths (LNK-066), so the two sides of the
  comparison cannot disagree about spelling.

#### LNK-078 — An unscopeable scope root is an error, not a silent denial or a silent allow

- **Behavior:** A configured scope root that cannot be interpreted as a vault-relative folder makes
  the scope check *fail* rather than resolve to in-scope or out-of-scope. Rows:
  - `../outside` — a root escaping the vault
  - `C:/Windows` — a rooted/absolute root
  - `""` — an empty-string root
- **Trigger:** A grant containing any of those roots.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — rows `root that escapes the vault is unscopeable`, `rooted root path is unscopeable`, `empty-string root is unscopeable`
- **Note:** Deliberately distinct from `outOfScope`: a misconfigured grant is a configuration fault
  the operator must see, not a quiet denial they would debug as a permissions problem. Note the
  contrast with LNK-070, where an **empty list** means unscoped — an empty *string* inside the list
  is an error, not the same thing.

#### LNK-079 — One malformed root poisons a grant even when another root would have matched

- **Behavior:** Given roots `Projects/Widget` (which contains the path) and `../outside` (which is
  unscopeable), checking `Projects/Widget/Widget.md` still fails. The malformed root is not skipped
  in favor of the matching one.
- **Trigger:** A grant mixing a valid, matching root with an invalid one.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — row `one malformed root among otherwise-matching roots still errors`
- **Note:** Fail-loud over fail-permissive: validation of the whole root set precedes matching, so a
  configuration error cannot hide behind a lucky match. An implementation that short-circuits on
  the first matching root passes every other scope row and fails this one.

#### LNK-080 — Scope roots are keyed per vault and never leak between vaults

- **Behavior:** A grant's scope roots are a per-vault map; only the entry for the vault under check
  applies. Rows:
  - A grant with roots for `primary` and an **explicit empty list** for `test`: a path in `test` is
    in scope (fully unscoped), even though `primary` is scoped.
  - The same grant checked against `primary` itself still restricts `primary`
    (`Areas/Personal/Health.md` out of scope) — the per-vault keying does not weaken the vault that
    *is* scoped.
  - A grant with **different** roots for `primary` and `test`: checking `Projects/Widget/Widget.md`
    in `test` is out of scope, because only `test`'s own root (`Areas/Homelab`) applies —
    `primary`'s matching root does not leak in.
- **Trigger:** Each of the three grants above.
- **Kind:** `semantic`
- **Source:** `powershell/VaultAccess/Tests/SharedCases.Tests.ps1:112` — rows 18-20 of `tests/cases/scope-cases.json`
- **Note:** The test's inline comment says the conversion to a keyed lookup exists precisely to
  prove "a root configured for a different vault never leaks into this one", and mirrors the C#
  side's dictionary-keyed lookup. The middle row is the control that stops the first from being
  satisfied by an implementation that simply ignores scope for unlisted vaults.

### Fixture

#### LNK-081 — The module suite's fixture: two vault copies, hidden folder, and an injected duplicate-basename pair

- **Behavior:** The module test fixture builds a temp root containing two independent copies of
  `test-vault` named `test` and `scratch`, injects into `test` a `.obsidian/junk.md` (a dot-folder
  file), and injects `A/Dupe.md` and `B/Dupe.md` (two files sharing one basename at equal depth in
  differently-named sibling folders). A `vaults.json` names `test` as the default. Each suite
  initializes against it and removes it afterward.
- **Trigger:** Suite setup.
- **Kind:** `fixture`
- **Source:** `powershell/VaultAccess/Tests/TestHelpers.ps1:17` — `Initialize-VaultAccessTestFixture`
- **Note:** Three purposes, in order of how much they shape cases elsewhere: the `A/Dupe.md` +
  `B/Dupe.md` pair exists to force the same-basename ambiguity the bare-link ranker resolves (the
  equal-length, alphabetically-broken tie of LNK-022); `.obsidian/junk.md` exists to force the
  dot-folder exclusion rule (an enumeration behavior, harvested in another area); the two vault
  copies exist so cross-vault behaviors (LNK-032, LNK-040, LNK-080) have a second vault. **None of
  my four suites read these injected files** — `LinkResolve.Tests.ps1` is pure and uses the same
  names only as literal strings. Recorded so a reconciler can trace the fixture to the rule it was
  built for.

#### LNK-082 — Cross-process lock tests require a per-test isolated vault

- **Behavior:** Each cross-process write-lock test creates its own vault directory and its own
  configuration, so it contends on a lock no other test touches.
- **Trigger:** The contention and abandonment tests.
- **Kind:** `fixture`
- **Source:** `powershell/VaultAccess/Tests/WriteLock.Tests.ps1:99-108` — `New-IsolatedVault`
- **Note:** Not cosmetic isolation — it follows from LNK-044. Because abandonment is a one-shot
  signal the OS clears on consumption, two tests sharing a lock would pass or fail on ordering. Any
  reimplemented suite covering abandonment inherits this constraint. The abandonment test
  additionally holds an independent handle on the lock across the child process's death, because
  the OS destroys the lock object when its last handle closes — without it the test races the
  child's teardown and the recovery signal can vanish.
