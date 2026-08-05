# Harvest: The frontmatter registry and entry schemas

Sources:
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\FrontmatterRegistry.Tests.ps1` (305 lines)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\FrontmatterSchema.Tests.ps1` (235 lines)
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\TestHelpers.ps1` (47 lines) — read for fixture context; neither suite actually uses it.

Read the implementation for:
- `powershell/VaultAccess/Public/FrontmatterRegistry.ps1` (281 lines) — to recover the exact value patterns
  behind `doc.type` / `scope` / the date keys, the `Expectation` strings the refusal messages quote, and the
  `..`-normalization rule the exclusion tests probe indirectly.
- `powershell/VaultAccess/Public/FrontmatterSchema.ps1` (226 lines) — to recover the entry-field allowlists
  (`link`/`folder`/`description`/`load_when`; `pinned`; `use_skill`/`when`/`note`) that the tests only sample,
  and the Archive/Tasks.md matching rule.
- `powershell/VaultAccess/Private/FrontmatterHelpers.ps1:185-207` — the control-character rule's exact class.

## Summary

This area is two cooperating layers with a deliberately sharp boundary.

**Layer 1 — the key registry.** A *positive allowlist* of exactly seven top-level frontmatter keys, with per-key
flags: agentic (gated behind an `agentic:write` capability), nav-list (addressable by the nav entry ops), and a
value pattern (non-agentic keys only). It fails closed: an unrecognized key is a refusal, never something
implicitly classified as safe. The single most load-bearing behavior in the area is the *asymmetry of the
namespace backstop*: registry membership is the classifier, and the `agent.*` / `nav.*` prefix survives only as a
backstop that can **escalate** a key to agentic, never **clear** one to non-agentic (REG-010, REG-011, REG-028).

**Layer 2 — the entry schema.** A per-note, in-memory check of the `agent.*` / `nav.*` block lists' internal
shape, returning a list of *structured* violations (`Rule` / `Field` / `EntryIndex` / `Detail` / `Reason`) rather
than a boolean or prose. `Rule` is the contract; `Reason` is explicitly declared rewordable.

Three things a reconciler should know before reading the cases:

1. **The task framing does not match what exists.** There is no declaration *loader* and no loader failure mode:
   the registry is a hardcoded ordered table returned by a function, not something read from a file. There is
   therefore no case for "one bad declaration poisons the registry" — the concept does not exist in the old
   design. What *does* exist and is the nearest analogue is REG-027/REG-028: one bad key anywhere in a submitted
   frontmatter tree aborts the whole write with nothing serialized.
2. **Two extents, deliberately.** `create` validates the *whole* block; `edit` validates only the keys an op
   touches and passes an untouched unrecognized key through. That asymmetry keeps notes carrying Obsidian's
   `obsidianUIMode` writable. Only `create`'s extent is directly tested here (REG-024..REG-029); the edit-side
   extent belongs to another harvest area, but the registry's design depends on it.
3. **A corpus block holds the table to the real vault** (REG-030..REG-032). It parses every frontmatter-bearing
   `.md` under `test-vault` and `C:\Repos\vault-primary` and asserts the registry accepts every value actually in
   use, and that exactly six keys sit outside it. This is partly a fixture case and partly a real semantic claim
   ("the registry must not make an existing note unwritable"), and it silently no-ops if `vault-primary` is
   absent.

Thin spots: the `empty_placeholder` rule has two arms (`[]` and `''`) and only the `[]` arm is tested; the
`load_when` entry field is exercised only in passing; and the exclusion list is asserted to have exactly one
member, so nothing tests multi-prefix behavior.

## Cases

### REG-001 — The recognized key set is exactly seven keys, in a fixed order

- **Behavior:** The frontmatter key registry recognizes exactly seven top-level keys and no others:
  `doc.created`, `doc.archived`, `doc.type`, `scope`, `agent.instructions`, `nav.contents`, `nav.see_also`. The
  set is ordered, and that order is asserted.
- **Trigger:** Enumerating the registry.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:16` — `carries exactly the seven keys the vault's tooling recognizes`
- **Note:** The order assertion itself is closer to `artifact` (it reflects an ordered dictionary), but the
  membership is squarely semantic. Order does leak into the refusal message, which lists the known keys in this
  order (REG-014).

### REG-002 — Exactly the `agent.*` / `nav.*` registry keys are flagged agentic

- **Behavior:** Of the seven recognized keys, exactly three are in the agentic set: `agent.instructions`,
  `nav.contents`, `nav.see_also`. The four `doc.*`/`scope` keys are not.
- **Trigger:** Reading the agentic flag of each registry key.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:23` — `flags exactly the agent.* / nav.* keys as agentic`

### REG-003 — Every non-agentic key carries a value pattern; every agentic key carries none

- **Behavior:** A registry key that is non-agentic always declares a value pattern its scalar value must match.
  An agentic key never declares one — its value shape is the entry schema's business, not the registry's.
- **Trigger:** Inspecting each registry entry's pattern.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:28` — `gives every non-agentic key a value pattern and every agentic key none`
- **Note:** This is an invariant over the table rather than a behavior of a call, but it is the structural rule
  that makes REG-023 (no value check on an agentic key) hold for future keys too.

### REG-004 — The nav-list key accessor reads the registry rather than restating it

- **Behavior:** The list of nav block-list keys is derived from the registry's own nav-list flags, so the nav
  entry ops' allowlist cannot drift from the registry.
- **Trigger:** Comparing the nav-key accessor's result against the registry's nav-flagged keys.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:38` — `returns the registry's NavList-flagged keys`
- **Note:** The test comment records that the return is comma-wrapped so an empty result survives as an empty
  array, and that every production caller assigns it rather than piping it. That wrapping detail is `artifact`;
  the single-source rule is not.

### REG-005 — The nav block-list keys are `nav.contents` and `nav.see_also`

- **Behavior:** Exactly two keys are nav block lists, and they are the pair the nav entry operations address.
- **Trigger:** Asking for the nav list keys.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:46` — `is the pair the nav entry ops address`

### REG-006 — A caller's casing resolves to the registry's canonical spelling

- **Behavior:** Key lookup is case-insensitive and returns the vault's canonical spelling: `DOC.Type` resolves to
  `doc.type`, `Nav.See_Also` to `nav.see_also`. The vault's casing is canonical, not the caller's, so a write
  updates the existing key rather than minting a second one beside it.
- **Trigger:** Resolving a key name whose casing differs from the canonical form.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:53` — `resolves a caller's casing to the canonical spelling`
- **Note:** Asserted with exact-match string comparison, so the returned casing matters, not just success.

### REG-007 — An unregistered key and an empty key name both resolve to nothing

- **Behavior:** Resolving a key the registry does not carry (e.g. `obsidianUIMode`) yields no canonical key, and
  so does resolving an empty key name. Neither throws.
- **Trigger:** Resolving `obsidianUIMode`; resolving `''`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:58` — `returns $null for a key the registry does not carry`

### REG-008 — Agentic classification is case-insensitive

- **Behavior:** A registry key is classified agentic regardless of the casing the caller spells it with —
  `agent.instructions` and `NAV.Contents` are both agentic.
- **Trigger:** Classifying an agentic key in non-canonical casing.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:65` — `classifies the registry's agentic keys as agentic, whatever their casing`

### REG-009 — The core lifecycle keys classify as non-agentic

- **Behavior:** `doc.created` and `scope` are not agentic, so writing them does not demand the agentic capability.
- **Trigger:** Classifying a core key.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:70` — `classifies the registry's core keys as non-agentic`

### REG-010 — An unregistered key inside the `agent.*` / `nav.*` namespaces still classifies as agentic

- **Behavior:** A key the registry does not carry but which sits in the `agent.` or `nav.` namespace (e.g.
  `agent.something_new`, `nav.elsewhere`) is still classified as agentic. The namespace prefix is a backstop that
  can only *escalate*: a foreign agentic-namespace key already present in a note must count as agentic layer for
  the write's outcome diff, so that nothing gets cleared as non-agentic merely by failing to match a pattern.
- **Trigger:** Classifying an unrecognized key beginning `agent.` or `nav.`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:75` — `still classifies an unregistered agent.* / nav.* key as agentic`
- **Note:** This is the inversion the whole registry exists for. The prior design's prefix scan classified
  anything it could not parse as non-agentic, silently waving unanticipated keys through. Couples to REG-028.

### REG-011 — An unrecognized non-namespaced key classifies as non-agentic, and that is not permission

- **Behavior:** An unrecognized key outside the agentic namespaces (e.g. `obsidianUIMode`) classifies as
  non-agentic — but the classification admits nothing, because the registry lookup refuses the key outright.
- **Trigger:** Classifying and then validating `obsidianUIMode`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:83` — `classifies an unrecognized non-namespaced key as non-agentic, which leaves it unwritable`
- **Note:** The `It` asserts both halves in one block: the classification *and* that the key produces a
  violation. A reimplementation that merges "is agentic" and "is allowed" into one answer would fail this.

### REG-012 — A recognized key named with no value is accepted

- **Behavior:** Validating a recognized key without supplying a value yields no violation. This is what a key
  *removal* needs: it names a key without supplying a value.
- **Trigger:** Validating `doc.archived` with no value bound.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:92` — `accepts a recognized key named alone`
- **Note:** Distinguishing "no value supplied" from "value supplied as empty" is load-bearing — an empty-string
  value for a date key *is* a violation (REG-017). A reimplementation using a nullable value parameter rather
  than parameter-boundness would collapse the two.

### REG-013 — An empty field name is refused

- **Behavior:** Validating an empty key name produces a violation.
- **Trigger:** Validating `''`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:96` — `refuses an empty field name`
- **Note:** Implementation reason string is `field name is empty`; only its non-emptiness is asserted.

### REG-014 — An unregistered key is refused with a message that names the known set

- **Behavior:** Refusing an unregistered key (`status`) produces a reason stating it is not a frontmatter key the
  vault's tooling recognizes, and that reason enumerates the known keys — the message includes `doc.created`.
- **Trigger:** Validating `status`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:100` — `refuses a key the registry does not carry, naming the known set`
- **Note:** The refusal being *self-describing* (it tells the caller what it could have written) is the semantic
  part; the exact prose `not a frontmatter key` is `artifact`.

### REG-015 — The six keys the corpus carries but the vault does not own are all refused

- **Behavior:** Each of `obsidianUIMode`, `status`, `title`, `name`, `description`, `disable-model-invocation` is
  refused as a key the tooling may write. These exist in real notes but belong to Obsidian, to a superseded
  convention, and to Claude Code's skill schema respectively — none is vault metadata.
- **Trigger:** Validating each of the six key names.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:106` — `refuses the keys the corpus carries that the vault deliberately does not own`
- **Note:** Six rows of the same rule (unregistered ⇒ refused), harvested as one case. What makes them worth
  listing is that they are *known to exist in live notes* and are nonetheless refused for writing — the notes
  stay readable and writable via the edit path's narrower extent, but nothing can author these keys.

### REG-016 — Both date keys accept a `YYYY-MM-DD` string

- **Behavior:** `doc.created` and `doc.archived` accept a value of the form `2026-07-30`.
- **Trigger:** Validating either date key with a well-formed date string.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:114` — `accepts a YYYY-MM-DD date string for the two date keys`

### REG-017 — The date rule is a format check on text, and rejects a wrong format, prose, and empty

- **Behavior:** The date keys are validated as a *format* over a string, not as a date type — because the YAML
  parse (1.2 core schema) yields `2026-07-30` as text. `30-07-2026`, `whenever`, and `''` are all refused.
- **Trigger:** Validating `doc.created` with a non-ISO date, an arbitrary word, or an empty string.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:119` — `checks the date FORMAT on a string, because the parse yields a string`
- **Note:** Format-only: nothing asserts the date is *real*, so `2026-13-45` would pass. Harvested as observed.
  The dependency on YAML 1.2 core-schema parsing (dates arrive as strings) is a cross-area coupling worth
  flagging — a reimplementation whose parser yields a real date object breaks this check.

### REG-018 — `doc.type` is validated as a character class, not an enum

- **Behavior:** `doc.type` accepts any value matching its shape, including a value never seen before
  (`Corpus`, `Some-Unseen-Type`). There is no authoritative list of document types to encode, so the shape is
  what is enforced.
- **Trigger:** Validating `doc.type` with an unfamiliar but well-shaped value.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:126` — `accepts doc.type as a character class rather than an enum`
- **Note:** The stated rationale is that an enum guessed at now would break `lint`'s `set-doc-type` remedy on
  exactly the notes it exists to fix. `lint` is out of scope for harvesting but is named here as the reason.

### REG-019 — `doc.type` refuses anything outside letters-and-dashes, and anything over 20 characters

- **Behavior:** `doc.type` must be 1–20 characters drawn from letters and dashes. `Note 2` (space) is refused,
  a value containing an embedded newline followed by what looks like another key
  (`"Note\nnav.contents: x"`) is refused, 21 `A`s is refused, and exactly 20 `A`s is accepted.
- **Trigger:** Validating `doc.type` with an out-of-class character, an injected newline, and boundary lengths.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:133` — `refuses a doc.type outside the character class or over 20 characters`
- **Note:** The newline row is a smuggling test — a value that would inject a second frontmatter key if a
  line-splicing writer emitted it. The 20/21 pair pins the boundary exactly.

### REG-020 — `scope` accepts any string, including a multi-line one

- **Behavior:** `scope` has no shape constraint beyond being a scalar string; `personal` and a two-line string
  are both accepted.
- **Trigger:** Validating `scope` with an arbitrary or multi-line string.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:140` — `accepts any string for scope`
- **Note:** Newline is *not* a control character for this purpose (REG-021's class excludes tab, LF, CR). Also:
  the implementation records that `scope` is allowlisted bare, unlike every other namespaced key, and that
  renaming it to `tasks.scope` is deferred follow-on work.

### REG-021 — A control character in a scalar value is refused, on every key

- **Behavior:** Any scalar frontmatter value carrying a control character is refused, whichever key it is being
  written to. Frontmatter values are agent-facing text rendered into terminals and logs, and ANSI escape
  sequences are control characters.
- **Trigger:** Validating `scope` with `"personal\e[31m"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:145` — `refuses a control character in any scalar value, on every key`
- **Note:** Read the implementation (`Private/FrontmatterHelpers.ps1:205`) for the class: `\x00-\x08`, `\x0B`,
  `\x0C`, `\x0E-\x1F`, `\x7F` — i.e. tab, LF and CR are deliberately *not* control characters here, which is what
  lets REG-020's multi-line `scope` pass. The rule **refuses rather than strips**: silently rewriting a caller's
  value would report success while changing what they asked for, worst on `agent.instructions` where it would
  quietly edit a directive future agents obey. Ordering matters — the control-character check runs *before* the
  key's own pattern, so a control character in a `doc.type` reports as a control character, not a shape failure.

### REG-022 — A list or a map where a scalar is expected is refused, and the refusal says which

- **Behavior:** A non-agentic key expecting a scalar refuses a list value with a reason naming `found a list`,
  and a mapping value with a reason naming `found a mapping`. The two are distinguished.
- **Trigger:** Validating `doc.type` with `@('a','b')`; validating `scope` with an ordered map.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:151` — `refuses a list or a map where a scalar key is expected`
- **Note:** The distinction is load-bearing beyond message quality — REG-031 filters the corpus check on the
  `found a mapping` text specifically.

### REG-023 — No value pattern is applied to an agentic key

- **Behavior:** Validating an agentic key with any value yields no violation, whatever the value's shape: a list
  for `agent.instructions` and a bare scalar for `nav.contents` both pass. An agentic key's gate is the
  `agentic:write` capability and its shape is the entry schema validator's business.
- **Trigger:** Validating an agentic key with a bound value.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:156` — `applies no value pattern to an agentic key`
- **Note:** Note the second row: `nav.contents: 'even a scalar'` passes the *registry* even though the entry
  schema would flag it `inline_scalar` (REG-052). The two layers are independent and both must run.

### REG-024 — A clean non-agentic tree validates without asserting any capability

- **Behavior:** Validating a whole frontmatter tree containing only non-agentic keys with valid values succeeds
  under a grant holding only `content:write` — no agentic capability is demanded.
- **Trigger:** Whole-tree validation of `{ doc.created: '2026-07-30', doc.type: 'Note' }` with `content:write`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:166` — `passes a clean non-agentic tree without asserting a capability`
- **Note:** The grant is injected via a global op-class list, which is `artifact`; the capability requirement is
  not. `AfterEach` clears that global.

### REG-025 — A tree carrying an agentic key demands `agentic:write`

- **Behavior:** Whole-tree validation of a tree containing an agentic key fails under a grant lacking
  `agentic:write`, refusing as insufficient capability.
- **Trigger:** Whole-tree validation of `{ agent.instructions: ['hint'] }` with only `content:write`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:172` — `asserts agentic:write when the tree carries an agentic key`
- **Note:** The error code `insufficient_capability` is asserted. Implementation attaches
  `requiredClass = 'agentic:write'` and a sorted `agenticKeysChanged` list to the error data; the test does not
  check that payload but a consumer might. Required write authority per key is exactly this: the four
  `doc.*`/`scope` keys need only content-write; the three agentic keys need content-write **plus** agentic-write.

### REG-026 — The same tree passes when the grant holds `agentic:write`

- **Behavior:** With `agentic:write` present in the grant, the agentic-bearing tree validates.
- **Trigger:** Whole-tree validation of `{ agent.instructions: ['hint'] }` with `content:write` + `agentic:write`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:178` — `passes the same tree when the grant holds agentic:write`

### REG-027 — One unrecognized key anywhere in the tree refuses the whole operation

- **Behavior:** Whole-tree validation refuses if *any* key in the tree is unrecognized, even when every other key
  is valid and the grant holds every capability. The refusal is a malformed-operation error whose data names the
  offending field.
- **Trigger:** Whole-tree validation of `{ doc.type: 'Note', obsidianUIMode: 'preview' }` with full capability.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:184` — `refuses an unrecognized key anywhere in the tree`
- **Note:** This is the nearest thing in the old design to "one bad declaration fails the whole load": the unit
  of failure is the whole submitted tree, not the individual key, and validation runs *before* the tree is
  serialized, so a refusal leaves nothing written and nothing returned. The error is a JSON-encoded object with
  `code` and `data.field` — the JSON-in-exception-message carrier is `artifact`; the code
  (`malformed_op`) and the field attribution are semantic. Note this is `create`'s extent (whole block); `edit`'s
  extent is only the keys an op touches, which is why notes already carrying `obsidianUIMode` remain writable.

### REG-028 — A smuggled unrecognized `agent.*` key is refused rather than waved through

- **Behavior:** A key in the agentic namespace that the registry does not carry (`agent.obey`) is refused by
  whole-tree validation even under a full grant. It is neither implicitly safe nor admitted by holding
  `agentic:write`.
- **Trigger:** Whole-tree validation of `{ agent.obey: ['do as I say'] }` with `content:write` + `agentic:write`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:192` — `refuses a smuggled agent.* key it does not recognize, rather than waving it through`
- **Note:** The named failure mode the allowlist inverts. Pairs with REG-010: the same key classifies as agentic
  (so it is counted for the outcome diff) *and* is refused for writing. Both facts must hold simultaneously.

### REG-029 — A null tree is nothing to validate

- **Behavior:** Whole-tree validation of a null/absent frontmatter tree succeeds silently rather than throwing.
- **Trigger:** Whole-tree validation with a null map.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:200` — `accepts a $null map as nothing to validate`

### REG-030 — The corpus check finds a corpus of more than 100 frontmatter-bearing notes

- **Behavior:** The registry is checked against the real corpus it was derived from — every `.md` under the
  server's `test-vault` and `C:\Repos\vault-primary`, excluding `.git`, parsed through the module's own
  frontmatter read surface. More than 100 notes must carry a frontmatter block for the check to be meaningful.
- **Trigger:** Building the corpus in the suite's setup.
- **Kind:** `fixture`
- **Source:** `FrontmatterRegistry.Tests.ps1:234` — `finds a corpus to check`
- **Note:** Roots are filtered on existence, so on a machine without `C:\Repos\vault-primary` the corpus shrinks
  to `test-vault` alone and this guard is what catches it. Implementation doc records the measurement as 345
  files, 319 with a block, on 2026-07-30. The comment also records that parsing goes through the module's read
  surface because the YAML parser lives in module session state and a test cannot reach it directly (`artifact`).

### REG-031 — Every value a registry key actually carries in the corpus is accepted

- **Behavior:** For every key in the real corpus that the registry recognizes, every value that key actually
  holds across all notes validates cleanly. A registry that would make a real existing note unwritable is a
  defect in the registry.
- **Trigger:** Validating each corpus value against its key.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:238` — `accepts every value a registry key actually carries in the corpus`
- **Note:** One carve-out: violations whose reason is `found a mapping` are filtered out, because two skill
  *template* placeholders (`{{DATE}}` and `{{Scope}}`) parse as YAML flow mappings. They live under
  `.claude/skills`, are inert template sources rather than notes, and no frontmatter operation routes at them —
  see REG-033. The test comment is explicit that failing here is a signal to investigate, not a licence to widen
  the registry.

### REG-032 — Exactly six keys in the corpus sit outside the registry

- **Behavior:** The set of frontmatter keys present in the real corpus but not carried by the registry is exactly
  `description`, `disable-model-invocation`, `name`, `obsidianUIMode`, `status`, `title` — the three deliberately
  unowned groups (Obsidian's UI preference; a superseded status/title convention; Claude Code's skill schema).
  A new unowned key appearing in the vault is a signal.
- **Trigger:** Diffing corpus keys against the registry.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:255` — `leaves exactly the three deliberately-unowned key groups outside the registry`
- **Note:** Same list as REG-015, asserted from the opposite direction — REG-015 says they are refused, this says
  they are the complete set of what exists-and-is-refused.

### REG-033 — The excluded-frontmatter path list holds `.claude/skills` and nothing else

- **Behavior:** Exactly one vault-relative path prefix is excluded from frontmatter tooling entirely:
  `.claude/skills`. Paths under it carry Claude Code's own frontmatter schema, not the vault's.
- **Trigger:** Reading the exclusion list.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:262` — `carries .claude/skills and nothing else`
- **Note:** The implementation records that the exclusion's real reason is *shape*, not schema: every file under
  it would refuse on the first key anyway, but three skills carry a `description: >` folded scalar, and
  reserializing re-emits the fold as a literal block of 200–435-character single lines — semantically identical,
  and exactly the shape those blocks exist for, destroyed. Measured 2026-07-30 across all 18 skill files. Also
  recorded: this is deliberately not a config surface and not an abstraction over the server's scoping facility.

### REG-034 — A path under an excluded prefix is excluded

- **Behavior:** A vault-relative path sitting beneath an excluded prefix is reported as excluded.
- **Trigger:** Testing `.claude/skills/vault-docs/SKILL.md`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:266` — `matches a path under the prefix`

### REG-035 — Exclusion matching is case-insensitive

- **Behavior:** `.CLAUDE/Skills/vault-docs/SKILL.md` is excluded. A caller must not reach an excluded file by
  spelling it with different casing.
- **Trigger:** Testing an excluded path in mixed casing.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:270` — `matches case-insensitively, because a caller must not reach it by spelling it differently`
- **Note:** Framed as anti-evasion, not convenience.

### REG-036 — Exclusion matching folds backslash separators to forward slashes

- **Behavior:** A path using backslashes, or mixing backslashes and forward slashes, matches the exclusion the
  same as a forward-slashed one.
- **Trigger:** Testing `.claude\skills\vault-docs\SKILL.md` and `.claude/skills\vault-mode\SKILL.md`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:274` — `matches through a backslash separator`
- **Note:** Justified by the server running on Windows where the separators are interchangeable. On a
  case-sensitive/forward-slash-only platform this is arguably an over-match, but as harvested it is a deliberate
  anti-evasion rule.

### REG-037 — Exclusion matching resolves `.` and `..` segments before matching

- **Behavior:** A path that reaches an excluded location through relative segments is still excluded:
  `Areas/../.claude/skills/vault-bug/SKILL.md`, `./.claude/./skills/vault-bug/SKILL.md`, and
  `.claude/skills/vault-bug/../vault-docs/SKILL.md` all match.
- **Trigger:** Testing excluded paths written with `.`/`..` segments.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:279` — `cannot be defeated by a .. segment that climbs back into the prefix`
- **Note:** Implementation detail worth carrying: a `..` that would climb *above* the root is kept as a literal
  segment rather than dropped, because such a path is out of contract and silently rebasing it at the root is how
  a normalizer invents a match — or an escape — the caller never wrote. Not directly tested.

### REG-038 — A path that leaves the excluded prefix via `..` is not excluded

- **Behavior:** Normalization runs both ways: `.claude/skills/../guides/Vault-Guide.md` resolves to
  `.claude/guides/Vault-Guide.md` and is *not* excluded.
- **Trigger:** Testing a path that textually starts with the prefix but escapes it.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:285` — `does not catch a path that only leaves the prefix through ..`
- **Note:** The mirror of REG-037. A naive prefix-string match would wrongly exclude this.

### REG-039 — `.claude/guides` and other `.claude` files are not excluded

- **Behavior:** `.claude/guides/Vault-Guide.md` and `.claude/settings.json` are not excluded. Files under
  `.claude/guides` are vault notes that happen to live under a dot directory and carry the vault's own
  frontmatter; the exclusion is not "anything under `.claude`".
- **Trigger:** Testing sibling paths under `.claude`.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:289` — `leaves .claude/guides alone - those are vault notes under a dot directory`

### REG-040 — Prefix matching is on whole path segments only

- **Behavior:** The prefix must match on whole segments and must be anchored at the vault root:
  `.claude/skillset/x.md`, `my.claude/skills/x.md`, and `Areas/Homelab/.claude/skills/x.md` are all *not*
  excluded.
- **Trigger:** Testing near-miss paths.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:294` — `matches whole segments only`
- **Note:** Three distinct near-misses: partial trailing segment, partial leading segment, and a nested
  occurrence deeper in the tree. The third is the interesting one — the exclusion is root-anchored, so a
  `.claude/skills` directory inside a project folder is a normal vault location.

### REG-041 — An empty or absent path answers false rather than throwing

- **Behavior:** Testing exclusion on an empty or absent path returns "not excluded" rather than failing.
- **Trigger:** Testing `''` and null.
- **Kind:** `semantic`
- **Source:** `FrontmatterRegistry.Tests.ps1:300` — `returns false rather than throwing on an empty or absent path`
- **Note:** Implementation uses a whitespace check, so an all-whitespace path also answers false; untested.

### REG-042 — A well-formed nav block yields no schema violations

- **Behavior:** A `nav.contents` block whose entries are objects — one with `link` + `description` + `load_when`,
  one with `folder` + `description` — is clean.
- **Trigger:** Schema-checking a valid nav block.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:19` — `returns an empty list for a well-formed nav block`
- **Note:** The only place `load_when` appears in a passing case, which is what establishes it as a permitted nav
  entry field. The full permitted set (from implementation) is `link`, `folder`, `description`, `load_when`.

### REG-043 — A well-formed `agent.instructions` block yields no schema violations

- **Behavior:** An `agent.instructions` block mixing all three permitted entry forms is clean: a bare string
  hint, a `pinned:` object, and a `use_skill:` + `when:` + `note:` object.
- **Trigger:** Schema-checking a valid instructions block.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:31` — `returns an empty list for a well-formed agent.instructions block`
- **Note:** Establishes the three entry forms and, via the third entry, that `note:` is a permitted companion of
  `use_skill`. Implementation records the semantics: a bare string is a revisable hint; `pinned` is human-authored
  and frozen; `use_skill` is skill-authored and frozen.

### REG-044 — A note with no frontmatter is vacuously clean

- **Behavior:** A note with no frontmatter block at all produces no schema violations.
- **Trigger:** Schema-checking body-only content.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:43` — `returns an empty list for a note with no frontmatter at all`
- **Note:** Implementation records that the parse is *tolerant* and single-sourced: a block that will not parse
  reads as no block here, because this check reports on notes rather than gating them and runs over every file in
  the vault. The write path refuses that same note outright, which is where an unparseable block is actionable.
  That divergence between the reporting path and the write path is a real semantic behavior not directly tested.

### REG-045 — Keys outside the `agent.*` / `nav.*` namespace are ignored by the entry schema

- **Behavior:** `doc.created`, `doc.type` and any other lifecycle field are out of the entry schema's scope and
  produce no violations, valid or not. The registry, not the schema, governs them.
- **Trigger:** Schema-checking a note carrying only `doc.*` keys.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:47` — `ignores keys outside the agent.*/nav.* namespace`

### REG-046 — The entry schema does not check that a declared `folder:` path resolves

- **Behavior:** A nav entry naming a folder that does not exist (`Nowhere/At/All`) is schema-clean. The schema is
  the per-note, in-memory half only; filesystem resolution is a separate pass.
- **Trigger:** Schema-checking a nav entry with an unresolvable folder path.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:51` — `does NOT check that a declared folder path resolves - that stays lint's filesystem pass`
- **Note:** Mentions `lint`, which is out of scope for harvesting; the case itself is about this validator's
  boundary and is in scope. Pulling the filesystem checks in would give the edit path vault enumeration it never
  calls. Two named checks stay out: `folder:` path resolution and folder-note completeness (which needs
  parent-directory enumeration plus a known-ignored-subfolders suppression list).

### REG-047 — Violations are structured objects, not prose

- **Behavior:** Each finding is a structured record carrying a machine-usable rule identifier, the top-level
  field it concerns, a 1-based entry index (absent when the finding is about the key rather than an entry), an
  optional detail (the offending sub-field name or inline value), and a human-readable reason. The rule
  identifier is the contract; the reason may be reworded.
- **Trigger:** Any violation — asserted concretely on `agent.foo` (`Rule` = `unknown_key`, `Field` = `agent.foo`,
  `EntryIndex` absent, `Reason` non-empty).
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:62` — `flags an unknown agent.* key`
- **Note:** The rationale recorded at the head of the test file: the skill-side original only printed, so
  consumers had nothing but regexes over console output to branch on. The complete rule vocabulary is
  `unknown_key`, `empty_block`, `empty_placeholder`, `inline_scalar`, `entry_not_object`, `link_folder_both`,
  `link_folder_neither`, `missing_description`, `unknown_entry_field`, `both_discriminators`, `no_discriminator`,
  `unexpected_pinned_field`, `use_skill_missing_when`, `unexpected_use_skill_field`, `tasks_missing_use_skill`.
  Also recorded: the return is always a list, never a boolean — empty means valid.

### REG-048 — An unrecognized `agent.*` key is flagged `unknown_key`

- **Behavior:** A key in the agent namespace that is not `agent.instructions` produces one `unknown_key`
  violation naming that key, with no entry index.
- **Trigger:** Schema-checking `agent.foo:` with a list under it.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:62` — `flags an unknown agent.* key`
- **Note:** Exactly one violation — the unknown key's *contents* are not then also validated.

### REG-049 — An unrecognized `nav.*` key is flagged `unknown_key`

- **Behavior:** A key in the nav namespace that is neither `nav.contents` nor `nav.see_also` produces
  `unknown_key` naming that key.
- **Trigger:** Schema-checking `nav.bogus: value`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:71` — `flags an unknown nav.* key`
- **Note:** The value here is an inline scalar, which for a *known* key would be `inline_scalar` (REG-052). The
  unknown-key check short-circuits it — only one violation results.

### REG-050 — A valueless known key is flagged `empty_block`

- **Behavior:** A recognized agentic key present with no value at all is a violation: presence means content, so
  the field should be omitted instead.
- **Trigger:** Schema-checking `agent.instructions:` with nothing under it.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:77` — `flags an empty block list`

### REG-051 — An inline empty-list placeholder is flagged `empty_placeholder`

- **Behavior:** A recognized agentic key whose value is an empty list is a violation, with the detail recording
  the offending literal `[]`. Presence means content.
- **Trigger:** Schema-checking `nav.see_also: []`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:83` — `flags an inline empty-list placeholder`
- **Note:** The implementation has a second arm of this rule — an empty-string value (`key: ''`) also emits
  `empty_placeholder`, with detail `''` — which no test exercises. Untested but real.

### REG-052 — A scalar where a block list belongs is flagged `inline_scalar`

- **Behavior:** A recognized agentic key holding a plain scalar rather than a block list is a violation, with the
  detail recording the offending value.
- **Trigger:** Schema-checking `nav.contents: Some-Note`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:89` — `flags a stray inline scalar where a block list belongs`
- **Note:** The registry layer permits this same value (REG-023) — the two layers are complementary, and only
  running both catches it.

### REG-053 — A bare-string nav entry is flagged `entry_not_object`

- **Behavior:** A nav block entry that is a bare string rather than a link/folder object is a violation, reported
  against entry index 1 for the first entry.
- **Trigger:** Schema-checking `nav.contents` with a single bare-string entry.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:97` — `flags a bare-string nav entry`
- **Note:** The asymmetry with `agent.instructions`, where a bare string *is* valid (REG-065), is deliberate.

### REG-054 — A nav entry with both `link` and `folder` is flagged `link_folder_both`

- **Behavior:** A nav entry must carry exactly one target discriminator. Carrying both is a violation.
- **Trigger:** Schema-checking a nav entry with `link`, `folder` and `description`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:103` — `flags an entry with both link and folder`
- **Note:** Asserted as exactly one occurrence of that rule (filtered, not indexed `[0]`), because other rules
  may also fire on the same entry.

### REG-055 — A nav entry with neither `link` nor `folder` is flagged `link_folder_neither`

- **Behavior:** A nav entry carrying no target at all is a violation, reported against its 1-based entry index.
- **Trigger:** Schema-checking a nav entry carrying only `description`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:109` — `flags an entry with neither link nor folder`

### REG-056 — A nav entry missing `description` is flagged `missing_description`

- **Behavior:** Every nav entry must carry a description; its absence is a violation with detail `description`.
- **Trigger:** Schema-checking a nav entry with `link` and `load_when` but no `description`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:115` — `flags a missing description`
- **Note:** Implementation tests for whitespace-emptiness, so `description: "   "` also violates. Untested.

### REG-057 — An unrecognized nav entry field is flagged `unknown_entry_field`

- **Behavior:** A nav entry field outside the permitted set (`link`, `folder`, `description`, `load_when`) is a
  violation, with detail naming the offending field.
- **Trigger:** Schema-checking a nav entry carrying `group: "Ref"`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:121` — `flags an unknown entry field`

### REG-058 — Entries are indexed 1-based so a later entry is identifiable

- **Behavior:** Entry indices in violations are 1-based and identify the specific offending entry: a two-entry
  nav block whose second entry lacks a description reports exactly one violation, at index 2.
- **Trigger:** Schema-checking a nav block whose first entry is valid and second is not.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:128` — `indexes entries 1-based so a later entry is identifiable`
- **Note:** Also confirms a valid entry produces no noise. The 1-based choice matters for remedy payloads.

### REG-059 — `nav.see_also` is checked with exactly the same rules as `nav.contents`

- **Behavior:** Both nav block-list keys share one entry rule set; a `nav.see_also` entry missing `description`
  reports `missing_description` against `nav.see_also`.
- **Trigger:** Schema-checking `nav.see_also` with a link-only entry.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:137` — `checks nav.see_also with the same rules as nav.contents`

### REG-060 — An `agent.instructions` entry with both `pinned` and `use_skill` is flagged `both_discriminators`

- **Behavior:** An object instruction entry may carry at most one discriminator. Carrying both is a violation at
  that entry's index.
- **Trigger:** Schema-checking an entry with `pinned` and `use_skill`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:145` — `flags an entry with both pinned and use_skill`

### REG-061 — An `agent.instructions` object entry with neither discriminator is flagged `no_discriminator`

- **Behavior:** An object instruction entry carrying neither `pinned` nor `use_skill` is a violation.
- **Trigger:** Schema-checking an entry carrying only `note:`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:152` — `flags an object entry with neither discriminator`
- **Note:** Applies only to *object* entries — a bare string entry is exempt (REG-065).

### REG-062 — A `pinned` entry carrying any other field is flagged `unexpected_pinned_field`

- **Behavior:** A `pinned` instruction entry permits `pinned` and nothing else; any companion field is a
  violation naming that field.
- **Trigger:** Schema-checking a `pinned` entry that also carries `when:`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:157` — `flags an unexpected field on a pinned entry`
- **Note:** Notably `note:` is permitted on a `use_skill` entry but *not* on a `pinned` one — the two forms have
  different field sets, not one shared set.

### REG-063 — A `use_skill` entry missing `when` is flagged `use_skill_missing_when`

- **Behavior:** A `use_skill` instruction entry must carry `when`; its absence is a violation with detail `when`.
- **Trigger:** Schema-checking a `use_skill` entry with no `when`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:164` — `flags a use_skill entry missing when`
- **Note:** Implementation tests whitespace-emptiness, so a blank `when` also violates. Untested.

### REG-064 — A `use_skill` entry carrying a field outside its set is flagged `unexpected_use_skill_field`

- **Behavior:** A `use_skill` instruction entry permits `use_skill`, `when` and `note`, and nothing else.
- **Trigger:** Schema-checking a `use_skill` entry that also carries `bogus:`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:170` — `flags an unexpected field on a use_skill entry`

### REG-065 — A bare-string `agent.instructions` entry is valid

- **Behavior:** An instruction entry that is a plain string is accepted with no violation — it is the revisable
  hint form.
- **Trigger:** Schema-checking `agent.instructions` with a single bare-string entry.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:177` — `accepts a bare-string hint entry`
- **Note:** The deliberate opposite of REG-053 for nav entries.

### REG-066 — The Tasks.md check is skipped entirely when no path is supplied

- **Behavior:** The one path-dependent check is skipped when the note's vault-relative path is not supplied;
  every other check runs unchanged, and no filesystem access happens either way.
- **Trigger:** Schema-checking content with no path.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:183` — `is skipped entirely when no RelativePath is supplied`
- **Note:** The check needs the note's *name* only — no filesystem and no suppression state — which is why it
  rides on an optional parameter of the in-memory validator rather than living in the filesystem pass.

### REG-067 — A `Tasks.md` without a `vault-tasks` directive is flagged `tasks_missing_use_skill`

- **Behavior:** A note whose filename is `Tasks.md`, outside an `Archive` folder, must carry an
  `agent.instructions` entry with `use_skill` naming `vault-tasks`. A `Tasks.md` carrying only an unrelated skill
  directive violates, reported against `agent.instructions`.
- **Trigger:** Schema-checking `Projects/Widget/Tasks.md` whose instructions name `some-unrelated-skill`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:189` — `flags a Tasks.md with no vault-tasks directive`
- **Note:** The one check where *absence* is the violation — everywhere else in this schema, an absent field is
  valid. Implementation matches the skill name by substring, so `vault-tasks-extra` would satisfy it; harvested
  as observed, possibly loose.

### REG-068 — A `Tasks.md` with no frontmatter at all is flagged `tasks_missing_use_skill`

- **Behavior:** The Tasks.md directive requirement fires even when the note has no frontmatter block — an empty
  note is not vacuously clean here, unlike REG-044.
- **Trigger:** Schema-checking `Projects/Widget/Tasks.md` with body-only content.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:196` — `flags a Tasks.md with no frontmatter at all`
- **Note:** The explicit exception to "no frontmatter means vacuously clean". Easy to miss in a reimplementation
  that early-returns on an absent block.

### REG-069 — A `Tasks.md` carrying its directive is clean

- **Behavior:** A `Tasks.md` with an `agent.instructions` entry of `use_skill: vault-tasks` plus `when:` produces
  no violations.
- **Trigger:** Schema-checking a compliant `Projects/Widget/Tasks.md`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:201` — `accepts a Tasks.md carrying its directive`

### REG-070 — An archived `Tasks.md` is exempt from the directive requirement

- **Behavior:** A `Tasks.md` under an `Archive` path segment is exempt: `use_skill` entries are stripped at
  archive time, so their absence there is correct, not a violation.
- **Trigger:** Schema-checking `Archive/Old-Project/Tasks.md` carrying only `doc.created` / `doc.archived`.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:207` — `exempts an archived Tasks.md, whose directive is stripped at archive time`
- **Note:** Implementation matches an `Archive` segment anywhere in the path (`(^|/)Archive(/|$)`),
  case-insensitively, not just at the root. Only the root-level form is tested. Couples to whatever archive
  operation does the stripping — a cross-area dependency.

### REG-071 — The Tasks.md check keys off the filename, not the folder

- **Behavior:** A note that merely sits in the same folder as a `Tasks.md` does not attract the directive
  requirement.
- **Trigger:** Schema-checking `Projects/Widget/Widget.md` with body-only content.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:213` — `does not fire on a note that merely lives beside a Tasks.md`
- **Note:** Filename comparison is case-insensitive in the implementation, so `tasks.md` also triggers it.
  Untested.

### REG-072 — Backslash-separated paths are accepted by the path-dependent check

- **Behavior:** A path written with backslashes (`Projects\Widget\Tasks.md`) is recognized the same as a
  forward-slashed one, so the Tasks.md check fires.
- **Trigger:** Schema-checking a backslash-separated `Tasks.md` path.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:217` — `accepts backslash-separated paths`
- **Note:** Same separator-folding rule as REG-036, applied in a different component.

### REG-073 — Every violation is reported, not just the first

- **Behavior:** The schema check reports the complete set of findings across the whole note — multiple rules on
  one entry, plus key-level rules on a different key, all appear in one result. A single malformed nav entry with
  an unknown key elsewhere yields more than two violations including `link_folder_both`, `missing_description`,
  `unknown_entry_field` and `unknown_key` together.
- **Trigger:** Schema-checking a note with a nav entry carrying `link` + `folder` + `group` and no
  `description`, alongside an `agent.foo` key.
- **Kind:** `semantic`
- **Source:** `FrontmatterSchema.Tests.ps1:224` — `reports every violation, not just the first - lint renders findings from the whole list`
- **Note:** Exhaustive reporting is the contract because a consumer renders findings and remedy payloads from the
  whole list. Note the interaction with the short-circuits: within *one* key, several checks do `continue` after
  firing (unknown key, empty block, inline scalar), so exhaustiveness is per-entry and per-key, not literally
  every possible finding. Mentions `lint`, which is out of scope; the behavior itself is in scope.
