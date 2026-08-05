# Harvest: Frontmatter core — block grammar, parsing, the note split, preservation

Sources:
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\Frontmatter.Tests.ps1` (783 lines) — primary
- `C:\Repos\vault-server\powershell\VaultAccess\Tests\TestHelpers.ps1` (47 lines) — read, but **not used by
  this suite**: the frontmatter tests are pure in-memory and build no fixture.

Read the implementation for:
- `powershell/VaultAccess/Private/FrontmatterHelpers.ps1` — to state the block grammar in full
  (`Get-VaultFrontmatterSpan`), the composer's line-ending policy (`Join-VaultNoteContent`), the
  control-character class actually refused (`Get-VaultFrontmatterTextViolation`), and the
  parse-failure refusals the tests never reach directly.
- `powershell/VaultAccess/Public/Frontmatter.ps1` — to confirm the `FenceState` value set, the
  `Absent`/`Scalar`/`Block` shape rules, and the `$null`-vs-empty read semantics.
- `powershell/VaultAccess/Public/FrontmatterRegistry.ps1` — to state which keys and value patterns
  the refusal cases are asserting against, since the tests name keys without listing the registry.

Cases where I leaned on the implementation say so in `Note`.

## Summary

This area is the in-memory frontmatter substrate: split a note into two facets, read a field, mutate
a field or a block list, and compose the note back. Nothing here touches the filesystem — every
function takes a content string and returns a content string — so there are no fixture cases at all
in this file.

Six things shape almost every case and are worth holding in mind before reading them:

1. **Read never parses; write always reparses and re-emits the whole block.** The split hands back
   raw block text (CRLF included). Any mutation runs YAML parse → tree edit → emit, so the
   frontmatter block's original *bytes* are explicitly **not** preserved — only its meaning, its key
   order, and its list indentation are. Quoting is the emitter's to choose.
2. **The body is the byte-preserved facet**, modulo line terminators, which normalize to LF
   unconditionally on any write.
3. **There are exactly two fence states, `Closed` and `None`.** An unclosed `---` is not a third
   state — it is *no frontmatter*, and the whole note is body. An empty `---`/`---` pair is likewise
   *no frontmatter*.
4. **An emptying removal drops the fence**, so the `---`/`---` pair can never be produced.
5. **A no-op returns the caller's exact bytes**, never a reserialization. This is asserted
   repeatedly and is a distinct guarantee from the mutation cases.
6. **Writable keys come from a positive allowlist** (`doc.created`, `doc.archived`, `doc.type`,
   `scope`, `agent.instructions`, `nav.contents`, `nav.see_also`), with per-key value patterns on
   the non-agentic ones. Everything else is refused — including well-formed names.

The block grammar is pinned **differentially against Markdig's `YamlFrontMatterBlock`** over a table
of 23 degenerate shapes plus every note in the repo's `test-vault`. I have split that table into a
case per distinct rule, because each row states a separate grammar decision a reimplementation could
get wrong independently. One deliberate divergence from Markdig is called out (FMC-113).

Preservation invariants are the highest-value cluster and are stated individually. The full set:
FMC-002 (read normalizes nothing), FMC-020 (key order), FMC-031 (quote character round-trip),
FMC-032 (siblings and body untouched by a removal), FMC-033 (a no-op returns the caller's exact
bytes — asserted separately on ~20 paths), FMC-051 (a no-op does not reformat foreign quoting),
FMC-055 (multi-line quoted text round-trips), FMC-097 (add-then-remove is a byte identity),
FMC-114 (the body-byte checklist), FMC-115 (emitter fixed point), FMC-116 (no YAML type coercion).

## Cases

### FMC-001 — A note splits into frontmatter and body at the closing fence

- **Behavior:** A note with a frontmatter block splits into exactly two facets: the block's inner
  text (fence lines excluded) and the body, which is everything after the closing fence line,
  verbatim. The split reports the block as present.
- **Trigger:** `"---\na: 1\n---\n# Body\nmore\n"` → frontmatter `a: 1`, body `"# Body\nmore\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:24` — `splits the two facets, body verbatim after the closing fence`

### FMC-002 — The split hands back block text raw and never normalizes it

- **Behavior:** The read side performs no normalization whatsoever on the frontmatter block. A block
  written with CRLF is returned with its CRLF intact. Normalization is a write-side concern only,
  and only when a block is reserialized.
- **Trigger:** `"---\r\na: 1\r\nb: 2\r\n---\r\n# Body"` → frontmatter is exactly `"a: 1\r\nb: 2"`,
  body is exactly `"# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:31` — `hands back the block text raw, never parsed - CRLF inside it included`
- **Note:** Preservation invariant. Paired with FMC-021, which asserts the opposite for the write
  side. A reader reporting what is on disk must not silently converge terminators.

### FMC-003 — A note with no fence is entirely body

- **Behavior:** A note with no frontmatter block reports no block, an empty frontmatter facet, and a
  body equal to the whole content.
- **Trigger:** `'# Body only'`
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:39` — `treats a fence-less note as all body`

### FMC-004 — A note ending at the closing fence has an empty body, not a missing one

- **Behavior:** When nothing follows the closing fence, the body facet is the empty string.
- **Trigger:** `"---\na: 1\n---\n"` → body is `''`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:46` — `reports an empty body for a note that ends at the closing fence`

### FMC-005 — An unclosed opening fence is not frontmatter at all

- **Behavior:** A `---` opening line that nothing closes does not produce a frontmatter block. The
  note reports no block and the *entire* content — including the `---` line and the apparent
  metadata under it — is the body. There is no third "unclosed" fence state to report, refuse, or
  mutate against.
- **Trigger:** `"---\na: 1\n# Body"`
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:50` — `treats an unclosed fence as no frontmatter at all, the whole note being body`
- **Note:** The test comment states the rationale: the CommonMark reading is a thematic break
  followed by a paragraph. This retires an earlier `unclosed_fence` error condition. See FMC-025,
  which is the write-side counterpart — writes still refuse to prepend a block over such a note,
  under a *different* reason code.

### FMC-006 — An empty fence pair is not frontmatter at all

- **Behavior:** `---` immediately followed by `---` produces no frontmatter block; the whole content
  including both fence lines is the body.
- **Trigger:** `"---\n---\nBody"`
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:61` — `treats an empty fence pair as no frontmatter at all`
- **Note:** The test comment records that a previous implementation both accepted *and produced*
  this shape, and that the fix is at the producer — see FMC-036, FMC-042, FMC-086, FMC-098. Also see
  FMC-113: this is the single point where the grammar deliberately departs from Markdig.

### FMC-007 — A four-dash opener is not a fence

- **Behavior:** An opening line of `----` (four or more dashes) does not open a frontmatter block.
  The opening fence must be exactly three dashes.
- **Trigger:** `"----\na: 1\n----\nBody"` → no block.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:70` — `does not read a four-dash opener as a fence`

### FMC-008 — A fence below the first line does not open a block

- **Behavior:** The opening fence must be the note's *first* line. A `---` appearing after any
  preceding content, even a heading, does not open a frontmatter block.
- **Trigger:** `"# Heading\n---\na: 1\n---\n"` → no block.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:74` — `does not read a fence that starts below the first line`

### FMC-009 — The reported body offset splices exactly at the body

- **Behavior:** The split reports a character offset at which the body begins, such that taking the
  content up to that offset yields exactly the frontmatter block including both fence lines and the
  closing fence's terminator. This lets a caller replace a body without recomposing (and therefore
  reformatting) the frontmatter it did not touch.
- **Trigger:** `"---\na: 1\n---\n# Body"` → prefix is `"---\na: 1\n---\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:78` — `gives a BodyOffset that splices exactly at the body`
- **Note:** The offset is stated as a character index into the string. A reimplementation may express
  the same guarantee differently (a byte offset, a prefix string), but the *guarantee* — a
  body-only edit does not disturb the block's bytes — is semantic.

### FMC-010 — The body offset accounts for a CRLF closing fence

- **Behavior:** The body offset lands past the closing fence's full CRLF terminator, not between the
  CR and the LF.
- **Trigger:** `"---\r\na: 1\r\n---\r\n# Body"` → prefix is `"---\r\na: 1\r\n---\r\n"`, body is `'# Body'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:84` — `gives a BodyOffset past the closing fence in a CRLF note`

### FMC-011 — A scalar field reads back with quoting resolved

- **Behavior:** Reading a flat scalar field returns the *parsed* value, with YAML quoting removed —
  `doc.type: "Corpus"` reads as `Corpus`, not as `"Corpus"`.
- **Trigger:** `Get` of `doc.type` from `"---\ndoc.type: \"Corpus\"\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:93` — `reads a flat scalar, quoting resolved by the parser`

### FMC-012 — An absent field reads as nothing

- **Behavior:** Reading a field the block does not carry yields nothing (null/empty).
- **Trigger:** `Get` of `doc.created` from a block carrying only `doc.type`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:97` — `returns $null for an absent field`

### FMC-013 — A field read from a note with no block yields nothing

- **Behavior:** Reading any field from a note with no frontmatter block yields nothing rather than
  an error.
- **Trigger:** `Get` of `doc.created` from `'# B'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:101` — `returns $null when there is no frontmatter block`

### FMC-014 — A present-but-empty value reads as nothing, so the read is not a presence test

- **Behavior:** A key declared with no value reads back as nothing — indistinguishable from an
  absent key. The scalar read therefore **cannot** serve as a presence test; presence is the shape
  query's job (FMC-015 ff.).
- **Trigger:** `Get` of `doc.type` from `"---\ndoc.type:\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:105` — `returns $null for a present-but-empty value, so it cannot serve as a presence test`
- **Note:** This conflation is deliberate and is the stated reason the shape query exists as a
  separate operation. A reimplementation that made the read distinguish these would change the
  contract three other cases rest on (FMC-012, FMC-013, FMC-018).

### FMC-015 — A missing field has shape "Absent"

- **Behavior:** The field-shape query reports `Absent` for a key the block does not carry.
- **Trigger:** shape of `nav.contents` in `"---\na: 1\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:111` — `reports Absent for a missing field`

### FMC-016 — A field in a note with no block has shape "Absent"

- **Behavior:** The shape query reports `Absent` when the note has no frontmatter block at all,
  rather than erroring.
- **Trigger:** shape of `doc.created` in `'# B'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:115` — `reports Absent when there is no frontmatter block`

### FMC-017 — A flat value has shape "Scalar"

- **Behavior:** A key whose value is a plain scalar reports shape `Scalar`.
- **Trigger:** shape of `doc.type` in `"---\ndoc.type: Corpus\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:119` — `reports Scalar for a flat field`

### FMC-018 — A sequence value has shape "Block", which the scalar read cannot distinguish from a miss

- **Behavior:** A key whose value is a sequence reports shape `Block`. The *same* content read
  through the scalar field read yields nothing — identical to an absent field — so only the shape
  query can tell a block from a miss.
- **Trigger:** `"---\nnav.contents:\n  - link: \"[[X]]\"\n    description: \"d\"\n---\n# B"`; shape
  is `Block`, scalar read of `nav.contents` is empty.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:123` — `reports Block for a key whose value is a sequence`
- **Note:** The test comment names this as the detection a `field_shape_mismatch` refusal depends on:
  a caller must not be able to replace a whole nav list with a string by naming the flat-set op.

### FMC-019 — A bare key with no value has shape "Scalar", not "Block"

- **Behavior:** A key declared with no value at all reports shape `Scalar`. The stated reason is that
  such a key has nothing to orphan, so the shape guard's purpose (preventing a flat set from
  destroying a block) does not apply.
- **Trigger:** shape of `doc.type` in `"---\ndoc.type:\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:131` — `reports Scalar for a bare key with no value at all (nothing to orphan)`
- **Note:** Read the implementation to confirm the rule generalizes: a nested *mapping* value is also
  `Block`, and everything not a sequence or mapping is `Scalar`. Only the sequence case is asserted
  in the tests.

### FMC-020 — Setting an existing field updates it in place, preserving the note's key order

- **Behavior:** Writing a field that already exists replaces its value at its existing position. The
  note's own key order is preserved — a reserialize does not reshuffle keys into any canonical
  order.
- **Trigger:** set `doc.type` = `Corpus` on `"---\na: 1\ndoc.type: Idea\nb: 2\n---\n# B"` →
  `"---\na: 1\ndoc.type: Corpus\nb: 2\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:137` — `updates an existing field in place, preserving the note key order`
- **Note:** Preservation invariant. Stated in the implementation as the reason the tree is *ordered*:
  a reserialize that reshuffled `doc.*` and `nav.*` would rewrite every note in the vault the first
  time any field was touched.

### FMC-021 — A caller's key casing updates the existing key rather than minting a second one

- **Behavior:** Field names are matched case-insensitively against the keys already in the note. A
  caller naming `DOC.Type` updates the existing `doc.type` key, keeping the note's own spelling — it
  does not add a second, differently-cased key beside it.
- **Trigger:** set `DOC.Type` = `Corpus` on `"---\ndoc.type: Idea\n---\n# B"` →
  `"---\ndoc.type: Corpus\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:142` — `updates the existing key rather than minting a second one for a caller's casing`

### FMC-022 — A new field is appended after the last existing key

- **Behavior:** Writing a field the block does not carry appends it after the last existing key —
  immediately before the closing fence.
- **Trigger:** set `doc.type` = `Corpus` on `"---\na: 1\n---\n# B"` →
  `"---\na: 1\ndoc.type: Corpus\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:147` — `appends a new field after the last existing key`

### FMC-023 — Writing a field to a note with no block creates the block at the top

- **Behavior:** Setting a field on a note with no frontmatter creates a complete block at the top of
  the file, with the body following unchanged after the closing fence.
- **Trigger:** set `doc.type` = `Corpus` on `'# B'` → `"---\ndoc.type: Corpus\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:152` — `creates a frontmatter block when the note has none`

### FMC-024 — Any write emits LF throughout, body included, with nothing detected and nothing preserved

- **Behavior:** A mutation writes LF line terminators unconditionally across the whole note —
  frontmatter and body alike. A CRLF input note comes back entirely LF; no CR survives anywhere.
  There is no detection of the input's terminators and no preservation of them.
- **Trigger:** set `doc.type` = `Corpus` on `"---\r\na: 1\r\n---\r\n# Body\r\nsecond\r\n"` →
  `"---\na: 1\ndoc.type: Corpus\n---\n# Body\nsecond\n"`, and the result contains no `\r` at all.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:157` — `writes LF throughout a CRLF note, body included`
- **Note:** The test comment gives the justification: the editor writes LF and the vault repository
  normalizes every text file to LF on checkout, so a CRLF note cannot survive a round trip through
  the vault regardless. This is the *one* narrowing of the body-preservation guarantee (FMC-114).
  Contrast FMC-002: the read side normalizes nothing.

### FMC-025 — A write refuses to prepend a block above an apparent unclosed fence

- **Behavior:** Setting a field on a note whose first line is a `---` that nothing closes is refused,
  rather than writing a fresh complete block above the user's apparent metadata and stranding it as
  prose. The refusal is a frontmatter-integrity error carrying the reason `apparent_fence`.
- **Trigger:** set `doc.type` = `Corpus` on `"---\nfoo: 1\n# Body"` → throws
  `code = frontmatter_integrity`, `data.reason = apparent_fence`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:166` — `refuses to prepend a block to a note whose first line is an unclosed fence`
- **Note:** The reason code is deliberately *not* the retired `unclosed_fence`: the note has no fence
  state to speak of (FMC-005), so this is a write-side protection rather than a parse outcome. The
  code and reason string are `artifact`-flavored; the *refusal* is semantic. The literal spellings
  `frontmatter_integrity` / `apparent_fence` are asserted exactly.

### FMC-026 — A field name the registry does not carry is refused

- **Behavior:** Writing a field whose name is not in the tooling's key allowlist is refused as a
  malformed operation. The check is membership, not a character class.
- **Trigger:** set field named `doc created` (with a space) on `"---\na: 1\n---\n# B"` → throws
  `code = malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:176` — `refuses a field name the registry does not carry`

### FMC-027 — A well-formed key name not in the registry is still refused

- **Behavior:** A field name that would pass any reasonable character class but is not in the
  allowlist is refused. This is the inversion the allowlist exists for: passing a name shape is not
  the test, membership is.
- **Trigger:** set `obsidianUIMode` = `preview` → throws `code = malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:181` — `refuses a well-formed key name that is simply not in the registry`
- **Note:** The test comment states `obsidianUIMode` is the editor's own preference, carried by six
  real notes, and that it must stay *unwritable* while those notes stay *writable* — i.e. a write
  validates only the keys it touches and passes unrecognized keys already in the note through
  untouched. Read `FrontmatterRegistry.ps1` to confirm; that split is not asserted in this file but
  is load-bearing for how this refusal must be scoped.

### FMC-028 — A recognized key whose value fails its declared pattern is refused

- **Behavior:** A recognized key's value must match that key's declared value pattern. A caller
  blocked from inventing a key must not be able to put arbitrary text into a legitimate one instead.
- **Trigger:** set `doc.created` = `whenever` → throws `code = malformed_op`. (`doc.created` requires
  a `YYYY-MM-DD` date string.)
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:189` — `refuses a recognized key whose value fails its registry pattern`
- **Note:** The pattern set itself is not asserted here. Read from `FrontmatterRegistry.ps1`:
  `doc.created` / `doc.archived` = `^\d{4}-\d{2}-\d{2}$`; `doc.type` = `^[A-Za-z-]{1,20}$`; `scope`
  = any string; the three agentic keys (`agent.instructions`, `nav.contents`, `nav.see_also`) have
  no value pattern applied at this layer.

### FMC-029 — A value carrying a control character is refused outright, never stripped

- **Behavior:** A frontmatter value containing a control character is refused. The stated reason is
  that frontmatter values are agent-facing text rendered into terminals and logs, and ANSI escapes
  are control characters — no escaping makes them safe to render. It refuses rather than strips,
  because silently rewriting a caller's value would report success while changing what was asked.
- **Trigger:** set `doc.type` = `"Corpus\e[31m"` → throws `code = malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:196` — `refuses a value carrying a control character, which no escaping makes safe to render`
- **Note:** Read the implementation for the exact class refused:
  `[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]` — i.e. tab (`\x09`), LF (`\x0A`) and CR (`\x0D`) are
  **permitted**. That exclusion is what makes FMC-030 possible.

### FMC-030 — A value containing a line break is written as a block scalar and reads back intact

- **Behavior:** A value containing a newline is a legitimate value. It is emitted so that it reads
  back exactly as given, and the text after the newline **cannot** become a second top-level key.
- **Trigger:** set `scope` = `"personal\ndoc.smuggled: yes"` → reading `scope` back yields the
  original two-line string; reading `doc.smuggled` yields nothing.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:201` — `writes a line break as a block scalar that reads back as the value given`
- **Note:** The test comment explains the choice of key: this asserts against `scope` (pattern: any
  string) rather than `doc.type`, whose pattern would refuse the value a step earlier and so would
  test the allowlist rather than the emitter. The comment also records that this replaces an earlier
  outright *refusal* of line breaks, which existed only because a line-splicing writer could not
  escape. **This is a structural anti-smuggling guarantee, not a formatting nicety.**

### FMC-031 — A value containing a double quote round-trips unchanged

- **Behavior:** A value containing double-quote characters is written and read back byte-identical.
  The emitter handles the quoting.
- **Trigger:** set `scope` = `say "hi"` → reads back as `say "hi"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:212` — `writes a double quote and reads it back unchanged`
- **Note:** Preservation invariant, value-level. The *emitted quoting style* is deliberately not
  asserted — the emitter chooses it.

### FMC-032 — Removing a scalar leaves the other keys and the body alone

- **Behavior:** Removing one flat scalar key removes exactly that key. The remaining keys keep their
  order and the body is untouched.
- **Trigger:** remove `doc.type` from `"---\na: 1\ndoc.type: Corpus\nb: 2\n---\n# B"` →
  `"---\na: 1\nb: 2\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:219` — `removes a flat scalar and leaves the other keys and the body alone`
- **Note:** Preservation invariant.

### FMC-033 — Removing an absent field returns the caller's exact bytes, not a reserialization

- **Behavior:** Removing a field the block does not carry is a no-op that returns the caller's
  content **byte-for-byte**. It does not parse-and-re-emit, so a write that changed nothing
  reformats nothing.
- **Trigger:** remove `doc.type` from `"---\na: 1\n---\n# B"` → identical string returned.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:224` — `is a no-op when the field is absent, returning the caller's bytes rather than a reserialize`
- **Note:** **Preservation invariant, and the strongest one in the file.** "No-op returns the input
  unchanged" is a distinct and much stronger claim than "no-op returns an equivalent note". The same
  guarantee is asserted separately on roughly twenty other no-op paths across every operation in
  the substrate: FMC-034, FMC-037, FMC-039, FMC-040, FMC-047, FMC-049, FMC-050, FMC-051, FMC-071,
  FMC-072, FMC-087, FMC-088, FMC-089, FMC-090, FMC-096, FMC-102, FMC-103, FMC-104, FMC-108,
  FMC-109, FMC-111, FMC-112. A reimplementation must therefore make it a property of the whole
  substrate rather than of one function.

### FMC-034 — Removing a field from a note with no block is a no-op

- **Behavior:** A removal against a note with no frontmatter returns the content unchanged.
- **Trigger:** remove `doc.type` from `'# Body only'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:229` — `is a no-op when there is no frontmatter block`

### FMC-035 — Removing the last field when nothing follows the closing fence produces a well-formed note

- **Behavior:** Removing a key from a note whose content ends at the closing fence with no trailing
  terminator produces a correctly-composed note: the remaining keys, a closing fence, and a
  terminator.
- **Trigger:** remove `foo` from `"---\na: 1\nfoo: 1\n---"` (note: no trailing newline) →
  `"---\na: 1\n---\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:233` — `removes the last field inside the block when nothing follows the closing fence`
- **Note:** Explicitly a **regression test**. The test comment records the original defect: an
  index-arithmetic removal built a *reversed* index range whenever the slice ran to the end of the
  content, corrupting or silently dropping the edit. Worth carrying forward as a case even though a
  tree-based reimplementation has no such range. Note also that the output *gains* a trailing
  newline the input lacked — the composer always terminates the closing fence.

### FMC-036 — A removal that empties the block drops the fence entirely

- **Behavior:** When removing the last key would leave the block empty, the fence lines are removed
  too. The result is a note with no frontmatter — never an empty `---`/`---` pair.
- **Trigger:** remove `doc.type` from `"---\ndoc.type: Corpus\n---\n# B"` → `'# B'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:241` — `drops the fence entirely when the removal empties the block`
- **Note:** The closure of FMC-006 at the producer: the shape the grammar refuses to read is also a
  shape the emitter refuses to write. Asserted again for block removal (FMC-042), nav removal
  (FMC-086), and the use_skill stripper (FMC-098).

### FMC-037 — A removal against a note whose only fence never closes is a no-op

- **Behavior:** A removal against a note with an unclosed opening fence returns the content
  unchanged — it does not refuse, and it does not attempt to edit the apparent metadata.
- **Trigger:** remove `foo` from `"---\nfoo: 1"` → identical string.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:245` — `is a no-op against a note whose only fence never closes`
- **Note:** Asymmetric with FMC-025 on purpose: a *write* that would prepend a block refuses, a
  *removal* silently does nothing, because there is nothing to remove and nothing to strand.

### FMC-038 — Removing a block field removes the key and every entry under it

- **Behavior:** The block-removal operation removes a key whose value is a sequence together with all
  of its entries, leaving other keys intact.
- **Trigger:** remove block `agent.pinned_instructions` from
  `"---\nagent.pinned_instructions:\n  - \"a\"\n  - \"b\"\nnav.contents:\n  - link: \"[[X]]\"\n---\n# Body"`
  → `"---\nnav.contents:\n  - link: '[[X]]'\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:252` — `removes a block field and every entry under it`
- **Note:** The expected output shows the **quoting change**: the surviving `link: "[[X]]"` is
  re-emitted as `link: '[[X]]'`. This is the emitter's choice and the test asserts it exactly. It
  confirms that the frontmatter block's bytes are *not* preserved across a mutation — only meaning,
  key order, and list indentation are. A reconciler should not read this as a requirement to emit
  single quotes specifically, but it *is* a requirement that a mutation reserializes the block. The
  key `agent.pinned_instructions` used here is not a registry key; block removal does not consult
  the registry.

### FMC-039 — Removing an absent block field is a no-op

- **Behavior:** A block removal naming a key the note does not carry returns the content unchanged.
- **Trigger:** remove block `agent.pinned_instructions` from
  `"---\nnav.contents:\n  - link: '[[X]]'\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:257` — `is a no-op when the field is absent`
- **Note:** The input here is already in emitted form (single quotes), so "unchanged" and
  "reserialized" would look identical — but the sibling case FMC-033 makes the stronger byte-level
  claim explicit.

### FMC-040 — Removing a block field from a note with no frontmatter is a no-op

- **Behavior:** A block removal against a note with no frontmatter returns the content unchanged.
- **Trigger:** remove block `agent.pinned_instructions` from `'# Body only'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:262` — `is a no-op when there is no frontmatter`

### FMC-041 — Scalar removal and block removal are two named operations over one underlying removal

- **Behavior:** The flat-scalar removal and the block removal produce identical results for a given
  key — every assertion pair in the two describe blocks matches (absent field is a no-op, absent
  frontmatter is a no-op, an emptying removal drops the fence). The two remain distinct operations
  because the **routing** is the policy: having a separate flat-scalar removal is what lets a caller
  be refused for naming the flat op against a block-shaped field, before any removal happens.
- **Trigger:** compare `Remove-VaultFrontmatterField` cases (FMC-032 ff.) against
  `Remove-VaultFrontmatterBlock` cases (FMC-038 ff.) — the behaviors coincide.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:218` and `:251` — the two describe blocks read together;
  rationale from `Public/Frontmatter.ps1:256` and `:279`
- **Note:** Read the implementation to confirm: both public removals delegate to a single internal
  removal, so they cannot drift. A reimplementation could legitimately expose one operation plus a
  shape check; what must survive is the *refusal* (`field_shape_mismatch`, see FMC-018), not the
  two-function shape. Recorded because a reconciler comparing suites will otherwise see two
  near-duplicate describe blocks and wonder which is authoritative — the answer is both.

### FMC-042 — A block removal that empties the block drops the fence entirely

- **Behavior:** When removing a block-list key would leave the frontmatter empty, the fence goes with
  it.
- **Trigger:** remove block `nav.contents` from `"---\nnav.contents:\n  - link: '[[X]]'\n---\n# Body"`
  → `'# Body'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:266` — `drops the fence entirely when the removal empties the block`

### FMC-043 — Adding a bare agent instruction to a note with no frontmatter creates the block

- **Behavior:** Inserting a bare (unqualified) agent-instruction entry into a note with no
  frontmatter creates the whole block, the key, and the entry as a plain list item.
- **Trigger:** add Bare `Do X` to `'# Body'` →
  `"---\nagent.instructions:\n  - Do X\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:273` — `creates a frontmatter block when none exists (Bare)`
- **Note:** Establishes the emitted shape for the three entry types: a Bare entry is a bare string, a
  Pinned entry is `pinned: <text>`, a UseSkill entry is `use_skill:` / `when:` / optional `note:`.
  List items are indented two spaces and the entry's sub-fields four.

### FMC-044 — Adding a pinned instruction creates the key beneath existing keys

- **Behavior:** When frontmatter exists but the instructions key does not, the key is appended after
  the last existing key with the new entry under it. A pinned entry is emitted as `pinned: <text>`.
- **Trigger:** add Pinned `Never exfil this` to
  `"---\nnav.contents:\n  - link: '[[X]]'\n---\n# Body"` →
  `"---\nnav.contents:\n  - link: '[[X]]'\nagent.instructions:\n  - pinned: Never exfil this\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:278` — `creates the agent.instructions key when frontmatter exists but the key does not (Pinned)`

### FMC-045 — A use_skill entry with a note appends to an existing list

- **Behavior:** A use_skill entry is appended at the end of the existing instruction list, carrying
  `use_skill`, `when`, and (when supplied) `note` in that order. Existing entries keep their order.
- **Trigger:** add UseSkill `vault-tasks` / when `editing this file` / note `Do not hand-edit.` to
  `"---\nagent.instructions:\n  - Existing hint\n---\n# Body"` →
  `"---\nagent.instructions:\n  - Existing hint\n  - use_skill: vault-tasks\n    when: editing this file\n    note: Do not hand-edit.\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:283` — `appends a UseSkill entry with a note to an existing block`

### FMC-046 — A use_skill entry with no note omits the note field entirely

- **Behavior:** When no note is supplied the `note:` field is not emitted at all — it is not written
  as an empty value.
- **Trigger:** add UseSkill `vault-docs` / when `editing this file` to `'# Body'` →
  `"---\nagent.instructions:\n  - use_skill: vault-docs\n    when: editing this file\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:288` — `omits the note field for a UseSkill entry with no note`

### FMC-047 — A duplicate use_skill entry (same skill and same when) is a no-op

- **Behavior:** Adding a use_skill entry identical to one already present — matching on both the
  skill name and the condition — returns the caller's content unchanged, so a backfill caller can
  invoke it unconditionally.
- **Trigger:** add UseSkill `vault-docs` / when `editing this file` to a note already carrying
  exactly that entry.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:295` — `is a no-op for a duplicate UseSkill entry (same skill and when)`

### FMC-048 — The same skill under a different condition is a distinct entry

- **Behavior:** A use_skill entry naming an existing skill but a *different* condition is not a
  duplicate; it is appended as a new entry.
- **Trigger:** add UseSkill `vault-docs` / when `acting on its checklist` to a note carrying
  `vault-docs` / `editing this file` → content changes and contains the new condition.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:300` — `treats a different when as a distinct entry, not a duplicate`

### FMC-049 — Duplicate pinned text is a no-op

- **Behavior:** Adding a pinned entry whose text matches one already present returns the content
  unchanged.
- **Trigger:** add Pinned `Never exfil this` to a note already carrying it.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:307` — `is a no-op for duplicate Pinned text`

### FMC-050 — Duplicate bare text is a no-op

- **Behavior:** Adding a bare entry whose text matches one already present returns the content
  unchanged.
- **Trigger:** add Bare `Do X` to a note already carrying it.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:312` — `is a no-op for duplicate Bare text`

### FMC-051 — Duplicate detection is keyed on the parsed value, not the written bytes

- **Behavior:** An entry written in a quoting style the emitter would not itself choose still
  compares equal for duplicate detection, because the comparison is against the *parsed* value. A
  backfill caller cannot append a second copy of an entry that a human wrote differently.
- **Trigger:** add Bare `Do X` to `"---\nagent.instructions:\n  - \"Do X\"\n---\n# Body"` → the
  content is returned **unchanged**, quoting included.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:317` — `recognizes a duplicate written in a quoting style the emitter would not choose`
- **Note:** **Preservation invariant, and a subtle one.** Two guarantees compose here: duplicate
  detection matches semantically, *and* the resulting no-op returns the caller's original bytes —
  so a note whose quoting the emitter dislikes is not silently reformatted by a no-op add. A
  reimplementation that re-emitted on a no-op would pass a parse-and-compare test and fail this one.

### FMC-052 — A bare/pinned entry with no text is refused

- **Behavior:** An instruction insert of the bare or pinned kind requires its text; omitting it is a
  malformed operation.
- **Trigger:** add Bare with no instruction text → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:326` — `refuses a missing Instruction for Bare`
- **Note:** Read the implementation: the check is whitespace-sensitive (`IsNullOrWhiteSpace`), so an
  all-blank instruction is also refused. Only the omitted case is asserted.

### FMC-053 — A use_skill entry with no condition is refused

- **Behavior:** A use_skill insert requires both the skill name and the condition; supplying only the
  skill is a malformed operation.
- **Trigger:** add UseSkill `vault-docs` with no `when` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:330` — `refuses a missing When for UseSkill`

### FMC-054 — Instruction entry text carrying a control character is refused

- **Behavior:** The same control-character gate applies to agent-instruction entry text as to
  frontmatter scalar values.
- **Trigger:** add Pinned `"bad\e[31m"` → throws `code = malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:334` — `refuses entry text carrying a control character`
- **Note:** Read the implementation: the gate is applied to *every* caller-supplied text field on the
  insert — instruction, use_skill, when, and note. Only the instruction field is asserted here.

### FMC-055 — Instruction text carrying quotes and line breaks is accepted and round-trips

- **Behavior:** Entry text containing double quotes and newlines is accepted, and the entry can
  subsequently be located and removed by that same exact text — so the value survives the write and
  the match is against the parsed value, not the emitted bytes.
- **Trigger:** add Pinned `"say \"hi\"\nand more"` to `'# Body'`, then remove the pinned entry by the
  identical text → result is `'# Body'` (the fence dropped with the emptied block).
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:339` — `accepts entry text carrying quotes and line breaks, reading them back unchanged`
- **Note:** **Preservation invariant, round-trip form.** This is the strongest statement in the file
  that values are values and not text spliced into lines: a multi-line quoted string written into a
  list entry comes back out equal enough to address the entry.

### FMC-056 — An instruction insert refuses to prepend a block above an apparent unclosed fence

- **Behavior:** Like the scalar set, an instruction insert against a note whose first line is an
  unclosed `---` refuses with a frontmatter-integrity error rather than stranding the apparent
  metadata.
- **Trigger:** add Bare `Do X` to `"---\na: 1\n# Body"` → throws containing `frontmatter_integrity`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:346` — `refuses to prepend a block to a note whose first line is an unclosed fence`

### FMC-057 — A link nav entry is inserted at the end of the link run, before the first folder entry

- **Behavior:** With no explicit position given, a link-typed nav entry is placed immediately before
  the first folder-typed entry — i.e. at the end of the leading run of link entries. This reproduces
  the convention every folder note in the vault follows, as a pure function of the existing list
  rather than something the caller must specify.
- **Trigger:** add link `[[Design]]` to a list of `link: [[Tasks]]` then `folder: Inbox` → the new
  link lands between them.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:352` — `puts a link entry at the end of the link run, before the first folder entry`

### FMC-058 — A folder nav entry is inserted at the end of the list

- **Behavior:** With no explicit position given, a folder-typed nav entry is appended at the end of
  the whole list, after any existing folder entries.
- **Trigger:** add folder `Archive` to the same list → appended last.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:359` — `puts a folder entry at the end of the list`

### FMC-059 — A link entry appends at the end when the list has no folder entries

- **Behavior:** When there is no folder entry to insert before, a link entry goes at the end of the
  list.
- **Trigger:** add link `[[B]]` to `nav.see_also` carrying only `link: [[A]]` → appended after it.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:364` — `appends a link entry at the end when the list has no folder entries`

### FMC-060 — load_when is emitted when supplied and omitted entirely when not

- **Behavior:** A nav entry's optional load condition appears as a `load_when:` field indented with
  the entry's other fields when supplied, and the field does not appear at all when it is not.
- **Trigger:** add link `[[D]]` with load condition `designing` → output contains
  `"    load_when: designing"`; the same add without it → output contains no `load_when` anywhere.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:370` — `emits load_when when supplied and omits the field when not`

### FMC-061 — An explicit "before" anchor inserts immediately before the named entry

- **Behavior:** A nav insert may name an existing entry's target value to position the new entry
  immediately before it, overriding the default placement.
- **Trigger:** add link `[[First]]` before `[[Tasks]]` → the new entry precedes `[[Tasks]]` at the
  head of the list.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:378` — `inserts before the entry named by -Before`

### FMC-062 — An explicit "after" anchor may name a folder target

- **Behavior:** The anchor names an entry's *target value* regardless of whether that entry is a link
  or a folder. Anchoring after a folder target places the new entry after it, even when the new
  entry is a link and the default rule would have put it earlier.
- **Trigger:** add link `[[Last]]` after `Inbox` (a folder entry) → the link is appended after the
  folder entry, overriding the link-run default.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:383` — `inserts after the entry named by -After, which may be a folder target`

### FMC-063 — An anchor positions past a whole entry, whatever fields it carries

- **Behavior:** "After" means after the anchor entry's *entire* set of fields, not after its first
  line. An anchor entry carrying a description and a load condition is stepped over completely.
- **Trigger:** add link `[[B]]` after `[[A]]`, where `[[A]]` carries both `description` and
  `load_when` → the new entry follows all of `[[A]]`'s fields.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:388` — `anchors past a whole entry, whatever fields it carries`
- **Note:** This was a real hazard for the line-splicing predecessor. In an entry-list model it is
  free, but a reimplementation that positions by line still has to get it right.

### FMC-064 — Naming both "before" and "after" is refused

- **Behavior:** The two positioning anchors are mutually exclusive; supplying both is a malformed
  operation.
- **Trigger:** add with both `-Before '[[Tasks]]'` and `-After 'Inbox'` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:394` — `refuses -Before and -After together`

### FMC-065 — An anchor naming no existing entry is refused

- **Behavior:** A positioning anchor that matches no entry in the list is refused rather than falling
  back to the default position.
- **Trigger:** add link `[[X]]` before `[[Nope]]` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:399` — `refuses an anchor that names no existing entry`

### FMC-066 — An anchor matching more than one entry is refused as ambiguous

- **Behavior:** When two entries carry the same target, an anchor naming that target is refused
  rather than resolved to one of them.
- **Trigger:** add after `[[A]]` in a list carrying `[[A]]` twice → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:404` — `refuses an anchor that matches more than one entry`

### FMC-067 — A nav insert creates the key when the note has frontmatter but not the key

- **Behavior:** Adding a nav entry to a note whose frontmatter carries no such key creates the key
  after the last existing key, with the entry under it.
- **Trigger:** add link `[[Tasks]]` to `"---\ndoc.type: Corpus\n---\n# Body\n"` →
  `"---\ndoc.type: Corpus\nnav.contents:\n  - link: '[[Tasks]]'\n    description: Task list\n---\n# Body\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:411` — `creates the key when the note has frontmatter but not the key`

### FMC-068 — A nav insert creates the frontmatter block when the note has none

- **Behavior:** Adding a nav entry to a note with no frontmatter creates the whole block.
- **Trigger:** add folder `Corpus` under `nav.see_also` to `'# Body'` →
  `"---\nnav.see_also:\n  - folder: Corpus\n    description: Reference\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:417` — `creates the frontmatter block when the note has none`

### FMC-069 — An anchor is refused when the key does not exist yet

- **Behavior:** A positioning anchor cannot be honoured against a list that does not exist, so the
  insert is refused rather than silently creating the key and ignoring the anchor.
- **Trigger:** add link `[[X]]` after `[[Y]]` to a note whose frontmatter has no `nav.contents` →
  throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:422` — `refuses an anchor when the key does not exist yet`

### FMC-070 — Creating one nav key leaves an unrelated existing nav key alone

- **Behavior:** Adding under one nav key when a *different* nav key already exists creates the new
  key after the existing one and does not touch the existing one's entries.
- **Trigger:** add link `[[B]]` under `nav.contents` to a note carrying `nav.see_also` → both keys
  present, `nav.see_also` unchanged and first.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:427` — `leaves an unrelated existing nav key alone`

### FMC-071 — A duplicate link target is a no-op regardless of description

- **Behavior:** Adding a nav entry whose link target already appears under that key returns the
  content unchanged — even when the supplied description differs. "Already there" is the answer;
  updating an existing entry is a removal plus an add.
- **Trigger:** add link `[[Tasks]]` with a different description to a list already carrying
  `[[Tasks]]` → identical bytes returned.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:435` — `is a no-op for a duplicate link target, whatever the description says`

### FMC-072 — A duplicate folder target is a no-op

- **Behavior:** The same idempotency holds for folder-typed entries, keyed on the folder path.
- **Trigger:** add folder `Inbox` with a different description to a list already carrying `Inbox` →
  identical bytes returned.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:440` — `is a no-op for a duplicate folder target`

### FMC-073 — The same target under a different key is a distinct entry

- **Behavior:** Idempotency is scoped to the key. The same link target added under a different nav
  key is not a duplicate and is inserted.
- **Trigger:** add link `[[Tasks]]` under `nav.see_also` to a note carrying `[[Tasks]]` under
  `nav.contents` → content changes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:444` — `treats the same target under a different key as a distinct entry`

### FMC-074 — A key outside the nav allowlist is refused by the nav insert

- **Behavior:** The nav entry operations address only the nav block-list keys. Naming any other key —
  including a legitimate registry key like `agent.instructions` — is refused.
- **Trigger:** add nav entry under key `agent.instructions` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:448` — `refuses a key outside the nav block-list allow-list`
- **Note:** Read the implementation: the allowlist is `nav.contents` and `nav.see_also`, derived from
  the same key registry rather than restated.

### FMC-075 — The canonical key spelling is written whatever case the caller used

- **Behavior:** The nav key allowlist check is case-insensitive, and the key is then written in its
  *canonical* spelling — a caller naming `NAV.Contents` produces `nav.contents`, not a second
  differently-cased key.
- **Trigger:** add link `[[X]]` under key `NAV.Contents` to `"---\ndoc.type: Corpus\n---\n# B"` →
  `"---\ndoc.type: Corpus\nnav.contents:\n  - link: '[[X]]'\n    description: x\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:452` — `writes the canonical key spelling whatever case the caller used`
- **Note:** The test comment names the failure mode explicitly: without the normalization step, a
  case-insensitive allowlist check *passes* and the key is then written under the caller's spelling.
  A reimplementation matching case-insensitively must canonicalize too.

### FMC-076 — Supplying both link and folder is refused

- **Behavior:** A nav entry carries exactly one discriminator. Supplying both is refused.
- **Trigger:** add with both `-Link '[[X]]'` and `-Folder 'Inbox'` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:459` — `refuses both link and folder`

### FMC-077 — Supplying neither link nor folder is refused

- **Behavior:** A nav entry requires a discriminator; supplying neither is refused.
- **Trigger:** add with only a description → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:463` — `refuses neither link nor folder`

### FMC-078 — A missing description is refused

- **Behavior:** A nav entry's description is required. Omitting it is refused at the operation rather
  than written and then reported by a downstream schema check.
- **Trigger:** add link `[[X]]` with no description → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:467` — `refuses a missing description`

### FMC-079 — A blank (whitespace-only) description is refused

- **Behavior:** A description consisting only of whitespace is treated as missing and refused.
- **Trigger:** add link `[[X]]` with description `'   '` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:471` — `refuses a blank description`

### FMC-080 — The control-character gate applies to every caller-supplied nav text field

- **Behavior:** A control character is refused on *each* of the six caller-supplied text fields on a
  nav insert — the link target, the folder path, the description, the load condition, and both
  positioning anchors — not merely on the value being stored.
- **Trigger:** six separate adds, each with `"x\e[31m"` in one of `-Link`, `-Folder`,
  `-Description`, `-LoadWhen`, `-Before`, `-After` → each throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:475` — `refuses a control character on every caller-text field`
- **Note:** Worth carrying as a single case listing all six fields. The anchors are the interesting
  ones: they are never written to the note, yet they are still gated.

### FMC-081 — A description carrying a line break stays inside its own entry

- **Behavior:** A nav description containing a newline cannot become a top-level frontmatter key. The
  operation takes structured fields rather than pre-rendered block text, so the emitter decides how
  a line break is represented and the smuggled text stays a value.
- **Trigger:** add link `[[X]]` with description `"d\ndoc.type: smuggled"` → afterwards
  `doc.smuggled` has shape `Absent` and `doc.type` still reads `Corpus` (its pre-existing value).
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:485` — `keeps a description carrying a line break inside its own entry`
- **Note:** The nav-entry counterpart of FMC-030. Both assert that structure cannot be injected
  through a value; together they are the substrate's central anti-smuggling property. Note the
  assertion also checks the *existing* `doc.type` was not overwritten.

### FMC-082 — A nav insert refuses to prepend a block above an apparent unclosed fence

- **Behavior:** Like the other creating writes, a nav insert against a note whose first line is an
  unclosed `---` refuses with a frontmatter-integrity error.
- **Trigger:** add link `[[B]]` to `"---\nnav.contents:\n  - link: '[[A]]'"` (unclosed) → throws
  containing `frontmatter_integrity`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:493` — `refuses to prepend a block to a note whose first line is an unclosed fence`

### FMC-083 — Removing a nav entry by link keeps the rest of the list in order

- **Behavior:** A nav removal addressed by link target removes exactly the matching entry, with all
  of its fields, and preserves the order of the remaining entries.
- **Trigger:** remove link `[[Tasks]]` from the two-entry nav note → only the folder entry remains,
  with its description.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:500` — `removes the entry whose link matches, keeping the rest of the list in order`

### FMC-084 — Removing a nav entry by folder removes its load_when too

- **Behavior:** A removal takes the whole entry including optional fields such as the load condition,
  leaving no orphaned sub-fields behind.
- **Trigger:** remove folder `Inbox` from an entry carrying `description` and `load_when` → the entry
  and all three of its fields are gone.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:505` — `removes the entry whose folder matches, including its load_when`

### FMC-085 — A nav removal that empties the list drops the whole key

- **Behavior:** When removing the last entry would leave the key holding an empty list, the key
  itself is dropped rather than left as an empty block. Other keys survive.
- **Trigger:** remove link `[[A]]` from `"---\ndoc.type: Corpus\nnav.contents:\n  - link: '[[A]]'\n    description: a\n---\n# B"`
  → `"---\ndoc.type: Corpus\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:511` — `drops the whole key when the removal empties the list`

### FMC-086 — A nav removal that empties the block drops the fence too

- **Behavior:** When the emptied nav key was the note's only key, the fence goes with it.
- **Trigger:** remove link `[[A]]` from `"---\nnav.contents:\n  - link: '[[A]]'\n    description: a\n---\n# B"`
  → `'# B'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:516` — `drops the fence too when the key was the note's last`
- **Note:** Two cascading rules compose here — empty list drops the key (FMC-085), empty block drops
  the fence (FMC-036). Both must hold for this to be right.

### FMC-087 — Removing a nav target no entry carries is a no-op

- **Behavior:** A removal naming a target that matches no entry returns the caller's content
  unchanged.
- **Trigger:** remove link `[[Nope]]` from the nav note → identical bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:521` — `is a no-op when no entry carries the target`

### FMC-088 — Removing from an absent nav key is a no-op

- **Behavior:** A removal against a note whose frontmatter carries no such key returns the content
  unchanged.
- **Trigger:** remove link `[[A]]` from `"---\ndoc.type: Corpus\n---\n# B"` → identical bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:525` — `is a no-op when the key is absent`

### FMC-089 — Removing a nav entry from a note with no frontmatter is a no-op

- **Behavior:** A nav removal against a note with no frontmatter returns the content unchanged.
- **Trigger:** remove folder `Inbox` from `'# Body only'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:530` — `is a no-op when there is no frontmatter at all`

### FMC-090 — A nav removal does not cross keys

- **Behavior:** A removal is scoped to the named key. A target present under `nav.see_also` is not
  removed by a removal naming `nav.contents`; the note is returned unchanged.
- **Trigger:** remove link `[[B]]` under `nav.contents` from a note whose `[[B]]` sits under
  `nav.see_also` → identical bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:534` — `does not cross keys - a target under nav.see_also is not removed from nav.contents`

### FMC-091 — A nav target matching more than one entry is refused rather than guessed

- **Behavior:** When two entries under a key carry the same target, a removal naming it is refused as
  ambiguous rather than removing one of them. The stated reason: the note is already malformed and
  picking one would be a coin flip the caller cannot see.
- **Trigger:** remove link `[[A]]` from a list carrying `[[A]]` twice → throws `code = malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:539` — `refuses a target matching more than one entry rather than guessing`
- **Note:** Contrast FMC-106, where a *repeated pinned instruction* is explicitly removed one at a
  time rather than refused. The two are deliberately asymmetric.

### FMC-092 — A key outside the nav allowlist is refused by the nav removal

- **Behavior:** The removal is restricted to the same nav key allowlist as the insert.
- **Trigger:** remove under key `agent.instructions` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:547` — `refuses a key outside the nav block-list allow-list`

### FMC-093 — Supplying both link and folder to the nav removal is refused

- **Behavior:** The removal addresses exactly one discriminator; supplying both is refused.
- **Trigger:** remove with both `-Link` and `-Folder` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:551` — `refuses both link and folder`

### FMC-094 — Supplying neither link nor folder to the nav removal is refused

- **Behavior:** The removal requires a discriminator; supplying neither is refused.
- **Trigger:** remove with only a key → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:555` — `refuses neither link nor folder`

### FMC-095 — Nav removal target text carrying a control character is refused

- **Behavior:** The control-character gate applies to the removal's target text as well as to text
  being written.
- **Trigger:** remove link `"x\e[31m"` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:559` — `refuses target text carrying a control character`
- **Note:** The target is never written to the note, so this is a gate on the *input surface*
  generally, not on what reaches disk.

### FMC-096 — A nav removal against an unclosed fence is a no-op

- **Behavior:** A nav removal against a note whose only `---` never closes returns the content
  unchanged rather than refusing.
- **Trigger:** remove link `[[A]]` from `"---\nnav.contents:\n  - link: '[[A]]'"` → identical bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:563` — `is a no-op against a note whose only fence never closes`

### FMC-097 — Add then remove restores the original bytes exactly

- **Behavior:** Adding a nav entry and then removing it by its target returns the note to its
  original bytes, including the description and load condition of the untouched entries and the
  original key order.
- **Trigger:** add link `[[D]]` with description and load condition to the nav note, then remove link
  `[[D]]` → byte-identical to the starting note.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:568` — `round-trips with the adder: add then remove restores the original bytes`
- **Note:** **Preservation invariant, round-trip form, and the cleanest statement of emitter fixed
  point in the file.** It only holds because the starting fixture is *already in emitted form* — see
  the Summary and the fixture note at `Frontmatter.Tests.ps1:17`. A reimplementation whose emitter
  chose different quoting would need its own equivalent fixture; the *guarantee* (an add followed by
  the matching remove is a byte-level identity on an already-canonical note) is the portable part.

### FMC-098 — Stripping use_skill entries removes the only entry, the key, and the fence

- **Behavior:** Removing every use_skill-typed instruction from a note where that is the only entry
  drops the entry, then the key, then the fence — leaving the body alone.
- **Trigger:** strip use_skill from
  `"---\nagent.instructions:\n  - use_skill: vault-docs\n    when: editing this file\n---\n# Body"`
  → `'# Body'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:575` — `removes the only entry, drops the key, and drops the fence with it`
- **Note:** The test comment restates the empty-pair rule (FMC-006): the `---`/`---` pair a previous
  implementation left behind is never emitted again.

### FMC-099 — A use_skill entry carrying a note is removed whole

- **Behavior:** The strip removes a use_skill entry including its optional note field.
- **Trigger:** strip use_skill from an entry carrying `use_skill`, `when`, and `note` → `'# Body'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:582` — `removes a use_skill entry carrying a note`

### FMC-100 — Stripping use_skill leaves bare and pinned entries in place

- **Behavior:** The strip is typed: only use_skill entries are removed. Bare entries and pinned
  entries survive, in order.
- **Trigger:** strip use_skill from a list of `Load X when Y` (bare), `pinned: Never exfil this`, and
  a use_skill entry → the first two remain in order under the key.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:587` — `leaves bare and pinned entries in place, removing only use_skill`

### FMC-101 — Stripping use_skill removes every such entry at once

- **Behavior:** The strip removes *all* use_skill entries in one operation, not just the first. The
  all-at-once granularity is deliberate and asymmetric with the pinned strip (FMC-106).
- **Trigger:** strip use_skill from a list of two use_skill entries alongside `doc.type: Corpus` →
  `"---\ndoc.type: Corpus\n---\n# Body"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:592` — `removes multiple use_skill entries`
- **Note:** The stated rationale: a use_skill entry is skill-authored routing, whereas a pinned entry
  is the user's own binding rule and must be named one at a time.

### FMC-102 — Stripping use_skill from a block with none is a no-op

- **Behavior:** A strip against an instruction list carrying no use_skill entries returns the
  caller's content unchanged.
- **Trigger:** strip use_skill from a list of one bare and one pinned entry → identical bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:597` — `is a no-op when the block has no use_skill entries`

### FMC-103 — Stripping use_skill with no instructions key is a no-op

- **Behavior:** A strip against a note whose frontmatter carries no instructions key returns the
  content unchanged.
- **Trigger:** strip use_skill from `"---\nnav.contents:\n  - link: '[[X]]'\n---\n# Body"` →
  identical bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:602` — `is a no-op when there is no agent.instructions block`

### FMC-104 — Stripping use_skill from a note with no frontmatter is a no-op

- **Behavior:** A strip against a note with no frontmatter returns the content unchanged.
- **Trigger:** strip use_skill from `'# Body only'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:607` — `is a no-op when there is no frontmatter at all`

### FMC-105 — Removing a pinned instruction removes exactly the matching entry

- **Behavior:** A pinned removal addressed by text removes only the entry whose pinned text matches,
  leaving other pinned entries and bare entries in order.
- **Trigger:** remove pinned `Never exfil this` from a list of `hint` (bare),
  `pinned: Never exfil this`, `pinned: Other rule` → the bare entry and `Other rule` remain in order.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:613` — `removes exactly the entry whose text matches`

### FMC-106 — A repeated pinned text removes only one entry

- **Behavior:** When the same pinned text appears twice, the removal takes exactly one occurrence and
  leaves the other. Removing a user's own binding rule must name it, and must not take a second copy
  with it.
- **Trigger:** remove pinned `Same` from a list of two `pinned: Same` entries → one remains.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:618` — `removes only one entry when the same text appears twice`
- **Note:** Deliberately opposite to the nav removal's ambiguity refusal (FMC-091) and to the
  use_skill strip's remove-all (FMC-101). All three granularities are asserted separately and a
  reconciler should treat them as three independent decisions.

### FMC-107 — A pinned removal that empties the list drops the whole key

- **Behavior:** When removing the only pinned entry empties the instruction list, the key is dropped
  rather than left as an empty list. Other keys survive.
- **Trigger:** remove pinned `Only one` from `"---\ndoc.type: Corpus\nagent.instructions:\n  - pinned: Only one\n---\n# B"`
  → `"---\ndoc.type: Corpus\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:625` — `drops the whole agent.instructions key when the removal empties it`

### FMC-108 — A pinned removal matching nothing is a no-op

- **Behavior:** A pinned removal whose text matches no entry returns the caller's content unchanged.
- **Trigger:** remove pinned `Some other rule` from a note carrying `Never exfil this` → identical
  bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:630` — `is a no-op when no entry matches the text`

### FMC-109 — Pinned text matches case-sensitively

- **Behavior:** A pinned entry's text is the user's own prose, so matching is **case-sensitive** — a
  differently-cased text matches nothing and the removal is a no-op.
- **Trigger:** remove pinned `never exfil this` from a note carrying `Never exfil this` → identical
  bytes.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:635` — `matches case-sensitively - a pinned rule is the user's own text`
- **Note:** Deliberately opposite to nav target matching, which the implementation states is
  case-*in*sensitive because a nav target is a denotation (a wikilink or folder path) that the vault
  and the filesystem both resolve case-insensitively. That nav-side case-insensitivity is **not**
  directly asserted anywhere in this file — read from `Select-VaultNavEntry` in
  `FrontmatterHelpers.ps1`. Also opposite to key matching, which is case-insensitive (FMC-021,
  FMC-075). A reconciler should check all three separately.

### FMC-110 — A pinned removal leaves use_skill entries alone

- **Behavior:** The pinned removal is typed and does not touch use_skill entries.
- **Trigger:** remove pinned `Rule` from a list of `pinned: Rule` and a use_skill entry → only the
  use_skill entry remains.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:640` — `leaves use_skill entries alone`

### FMC-111 — A pinned removal with no instructions key is a no-op

- **Behavior:** A pinned removal against a note whose frontmatter carries no instructions key returns
  the content unchanged.
- **Trigger:** remove pinned `Rule` from `"---\nnav.contents:\n  - link: '[[X]]'\n---\n# B"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:646` — `is a no-op when there is no agent.instructions block`

### FMC-112 — A pinned removal against a note with no frontmatter is a no-op

- **Behavior:** A pinned removal against a note with no frontmatter returns the content unchanged.
- **Trigger:** remove pinned `Rule` from `'# Body only'`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:651` — `is a no-op when there is no frontmatter at all`

### FMC-113 — Pinned match text carrying a control character is refused

- **Behavior:** The control-character gate applies to the pinned removal's match text.
- **Trigger:** remove pinned `"bad\e[31m"` → throws `malformed_op`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:655` — `refuses match text carrying a control character`

### FMC-114 — Every byte of the body survives a frontmatter mutation, terminators excepted

- **Behavior:** A frontmatter mutation preserves the body verbatim except for line terminators, which
  normalize to LF. Specifically preserved: `---` sequences appearing in prose, a line that looks like
  a frontmatter key (`not frontmatter: really`), quote characters, leading indentation, **trailing
  whitespace on a line**, list markers and nested list indentation, fenced code blocks and their
  contents, non-ASCII characters (`naïve`, an em dash, emoji), literal tab characters, blank lines,
  and the absence of a final newline.
- **Trigger:** a CRLF note whose body contains all of the above; setting `doc.created` = `2026-07-28`
  → the resulting body equals the original body with only `\r\n` → `\n` applied.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:661` — `preserves every byte of the body except line terminators, across a frontmatter mutation`
- **Note:** **The headline preservation invariant.** The test's own comment calls terminator
  normalization "the one guarantee the port narrows rather than keeps whole". Each preserved element
  above is a separate way a naive line-based reimplementation could corrupt a body — trailing
  whitespace and the missing final newline are the two most easily lost. A reconciler should treat
  this as a checklist, not one assertion.

### FMC-115 — The emitter reaches a fixed point: a second identical mutation reproduces the first result byte for byte

- **Behavior:** Applying the same mutation twice produces byte-identical output the second time. An
  arbitrarily-quoted note converges on the first write and does not drift on later ones.
- **Trigger:** a note whose every value is double-quoted (dates, doc.type, bare/pinned/use_skill
  instruction entries, nav link/folder/description/load_when); set `doc.archived` = `2026-07-28`,
  then set it again to the same value → second result equals first exactly.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:673` — `reaches a fixed point: a second mutation reproduces the first result byte for byte`
- **Note:** **Preservation invariant, and the one that makes the whole reserialize-on-write design
  tolerable.** The stated consequences: a one-time corpus reformat converges, and a mutation on an
  already-emitted note produces a diff of the change alone. Note this is *not* the no-op case —
  the second write does change a value (to the same value), so it genuinely reserializes.

### FMC-116 — A leading-zero-shaped date and a yes/no value stay the strings they were

- **Behavior:** The classic YAML type coercions do not occur. A `YYYY-MM-DD` value stays the string
  `2026-01-05` rather than becoming a date, and `no` stays the string `no` rather than becoming a
  boolean. Both survive a reserialize of the block triggered by an unrelated field write, and are
  re-emitted defensively quoted.
- **Trigger:** `"---\ndoc.created: 2026-01-05\nscope: no\n---\n# B"`; set `doc.type` = `Corpus` →
  reading back yields exactly `2026-01-05` and exactly `no`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:683` — `keeps a leading-zero-shaped value and a yes/no value as the strings they were`
- **Note:** The test comment names the reason: the YAML **1.2 core schema**, under which `no`/`yes`
  are not booleans and an unquoted date is not a timestamp. A reimplementation using a YAML 1.1
  parser would silently fail this. The registry's own comment (`FrontmatterRegistry.ps1:62`)
  restates it: `doc.created`'s pattern is a *format check on text*, not a type test.

### FMC-117 — The opening fence is the first line and exactly three dashes, trailing whitespace discounted

- **Behavior:** The block grammar's opening rule: the note's first line, after discounting trailing
  spaces, tabs, and a trailing CR, must be exactly `---`. A four-dash opener disqualifies it; a
  leading space disqualifies it; a fence below the first line disqualifies it. A trailing space on
  the opener does **not** disqualify it.
- **Trigger:** grammar table rows `basic` (block), `four-dash opener` (no block),
  `opener trailing space` (block), `opener leading space` (no block),
  `fence not on the first line` — `"\n---\na: 1\n---\nBody\n"` (no block),
  `heading before the fence` (no block).
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (rows: basic, four-dash opener, opener trailing space, opener leading space, fence not on the first line, heading before the fence)
- **Note:** Each row is verified against Markdig's `YamlFrontMatterBlock` — the standard's own
  implementation — as the oracle, comparing whether a block was found and where its closing fence
  ends. The `fence not on the first line` row is subtle: a *leading blank line* before the `---` is
  enough to disqualify. Read `Get-VaultFrontmatterSpan` for the grammar statement in full.

### FMC-118 — The closing fence is a later line that is exactly `---` or `...`, trailing whitespace discounted

- **Behavior:** The closing fence may be spelled `---` **or** `...` (the YAML document-end marker).
  Trailing whitespace on it — a space or a tab — does not disqualify it. A four-dash closer or a
  four-dot closer does not close the block.
- **Trigger:** grammar table rows `closer trailing space` (block), `closer trailing tab` (block),
  `dots closer` — `"---\na: 1\n...\nBody\n"` (block), `four-dash closer` (no block),
  `four-dot closer` (no block).
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (rows: closer trailing space, closer trailing tab, dots closer, four-dash closer, four-dot closer)
- **Note:** The `...` closer is easy to miss in a reimplementation — it is a real, oracle-confirmed
  spelling of the closing fence, and it is only ever exercised here.

### FMC-119 — A closing fence carries no leading whitespace at any indent

- **Behavior:** A closing fence must begin at column zero. One, three, and four spaces of leading
  whitespace all fail to close the block equally — there is no 3-vs-4-space CommonMark indentation
  tolerance on the frontmatter closer. Trailing spaces and tabs on the closing line *are* tolerated
  and belong to the fence rather than the body.
- **Trigger:** grammar table rows `closer one-space indent`, `closer three-space indent`,
  `closer four-space indent`; contrast rows `closer trailing space` and `closer trailing tab`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (rows as above),
  resolved against `Private/FrontmatterHelpers.ps1:54`.
- **Note:** Corrected by the orchestrator against source; the original harvest read a graded
  indentation rule into this case that the test does not state. The table is purely *differential* —
  each row asserts only that the scan and the Markdig oracle reach the same answer, and no row
  hardcodes what that answer is. The answer comes from the implementation: the candidate closing
  line is matched after `TrimEnd(" \t\r")` only, so leading whitespace is never stripped and no
  indented closer is a fence at any width. Markdig agrees, which is why the rows pass.
- **Harvest hazard this illustrates:** every row of this table states a *relationship*, not a
  behavior. Any case harvested from it has to be resolved against the implementation before it
  means anything, and a case that reads a specific outcome off a row name alone is inventing it.

### FMC-120 — An unclosed fence yields no block

- **Behavior:** An opening `---` with no closing fence anywhere in the note yields no frontmatter
  block, agreeing with the standard.
- **Trigger:** grammar table row `unclosed fence` — `"---\na: 1\nBody\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (row: unclosed fence)
- **Note:** The grammar-level confirmation of FMC-005.

### FMC-121 — A bare fence with or without a terminator, and empty content, yield no block

- **Behavior:** Content consisting solely of `---`, or solely of `---\n`, yields no block. Empty
  content yields no block.
- **Trigger:** grammar table rows `fence alone` (`'---'`), `fence alone with terminator`
  (`"---\n"`), `empty content` (`''`).
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (rows: fence alone, fence alone with terminator, empty content)
- **Note:** Empty input is a distinct degenerate case worth its own assertion — a reimplementation
  that indexes before checking length fails here rather than returning "no block".

### FMC-122 — A block with no trailing newline after its closing fence is still a block

- **Behavior:** A note ending exactly at the closing fence with no terminator after it is a
  well-formed frontmatter block.
- **Trigger:** grammar table row `no trailing newline` — `"---\na: 1\n---"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (row: no trailing newline)
- **Note:** Paired with FMC-035, which asserts what a *mutation* of such a note produces.

### FMC-123 — Blank lines inside the block do not terminate it

- **Behavior:** Blank lines within the frontmatter block — leading, trailing, or interior — are part
  of the block and do not end it early.
- **Trigger:** grammar table row `blank lines inside` — `"---\n\na: 1\n\n---\nBody\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (row: blank lines inside)
- **Note:** This is the only assertion in the file about blank lines *inside* a block. Note it
  concerns **recognition** only — nothing asserts that a blank line inside a block is *preserved*
  across a mutation, and given that mutations reserialize the whole block from a parsed tree, it
  almost certainly is not. Same for comments: **nothing in this suite asserts anything about YAML
  comments (`#` lines) inside a frontmatter block at all** — not their recognition, not their
  preservation. That is a genuine gap in the old suite rather than a behavior I failed to find.

### FMC-124 — The whole grammar holds identically under CRLF

- **Behavior:** A note written entirely with CRLF terminators is recognized exactly as its LF
  counterpart is, with the closing fence located in the same place.
- **Trigger:** grammar table row `CRLF throughout` — `"---\r\na: 1\r\n---\r\nBody\r\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (row: CRLF throughout)
- **Note:** The one exception is the empty-pair case — see FMC-126, where CRLF and LF are
  *deliberately* made to agree against the oracle rather than with it.

### FMC-125 — Two blocks back to back: only the first is frontmatter

- **Behavior:** When a second `---`-delimited region immediately follows the first, only the first is
  the frontmatter block; the rest is body.
- **Trigger:** grammar table row `two blocks back to back` —
  `"---\na: 1\n---\n---\nb: 2\n---\nBody\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (row: two blocks back to back)

### FMC-126 — A `---` line inside a YAML block scalar still closes the frontmatter block

- **Behavior:** The block locator is a **line-level scan, not a YAML parse**. A `---` line appearing
  inside what YAML would read as a block scalar still terminates the frontmatter block — the scan
  does not understand YAML nesting, and this agrees with the standard's own implementation.
- **Trigger:** grammar table row `a --- line inside a block scalar` — `"---\na: |\n  ---\n---\nBody\n"`.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:750` — `agrees with Markdig on <Name>` (row: a --- line inside a block scalar)
- **Note:** **Likely to be missed by a spec-derived suite, and a genuine trap.** The intuitive
  behavior (respect the block scalar) is the *wrong* one here. Note the interaction with FMC-030: a
  value containing a newline is emitted as a block scalar — so a value whose text contains a line
  reading exactly `---` would, once written, produce a note whose block truncates early. Nothing in
  the suite tests that composition. Flagging as a possible latent defect rather than an asserted
  behavior.

### FMC-127 — The empty fence pair is the one deliberate departure from the oracle, because the oracle is inconsistent there

- **Behavior:** `---` immediately followed by `---` yields no block under **both** LF and CRLF. The
  oracle (Markdig) reads it as two thematic breaks under LF but as a real block under CRLF; the
  vault is LF, so the LF reading is applied under both terminators rather than inheriting the split.
- **Trigger:** `"---\n---\nBody\n"` → no block (oracle: no block, agrees).
  `"---\r\n---\r\nBody\r\n"` → no block (oracle: **finds a block**, disagrees).
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:761` — `departs from Markdig only on an empty fence pair, and only because Markdig is inconsistent there`
- **Note:** The word "only" in the test name is itself an assertion: this is claimed to be the sole
  divergence. The test also positively asserts the oracle's inconsistency, so it will fail loudly if
  a future Markdig version fixes it. The decision is recorded as locked by a plan document
  (`[[Plan-Frontmatter-YAML-Migration]]`), which is outside this repo.

### FMC-128 — The grammar agrees with the oracle on every note in the repo's fixture vault

- **Behavior:** For every `.md` file under the repository's `test-vault`, the block locator's answer
  — block or no block, and where the closing fence ends — matches the standard's implementation.
  The corpus is asserted to be non-empty.
- **Trigger:** recursive scan of `test-vault` for `*.md`; each file read whole and compared.
- **Kind:** `fixture`
- **Source:** `Frontmatter.Tests.ps1:771` — `agrees with Markdig on every note in the repo fixture vault`
- **Note:** Marked `fixture` because the assertion is about the fixture corpus rather than about any
  particular behavior. But the *intent* is semantic and worth carrying: the test comment says "a
  table of shapes proves the rules; a real corpus proves the rules are the ones the vault's notes
  actually exercise." A reimplementation would want the equivalent corpus-wide check even though the
  specific files differ. The failure message names the offending note, which matters for a suite
  that iterates rather than parameterizes.

### FMC-129 — The block grammar is pinned differentially against an external oracle rather than restated

- **Behavior:** What counts as a frontmatter block is not the implementation's own invention: it is
  verified at test time against an independent, standard implementation of the grammar, over both a
  table of degenerate shapes and a real corpus. The oracle is used only at test time, so the runtime
  carries no dependency on it.
- **Trigger:** the entire `the block grammar, differentially against Markdig` describe block.
- **Kind:** `artifact`
- **Source:** `Frontmatter.Tests.ps1:693` — describe block header comment
- **Note:** Marked `artifact` because the choice of Markdig, and the reduction of both sides to "is
  there a block and where does its closing fence end", are specific to the old implementation's
  language and hosting. But the **strategy** is portable and unusually valuable: it is what lets the
  test say "if a runtime upgrade moves the grammar, this is what says so." A reimplementation in
  another language would want an equivalent oracle rather than a hand-restated grammar. The
  comparison also walks the scan's reported body offset back over trailing whitespace before
  comparing, because the oracle's span stops at the fence's last character — a reconciler
  reproducing this needs that normalization step.

### FMC-130 — Assertions are on exact emitted strings, never on a parsed round trip

- **Behavior:** The emitted shape is part of the contract. Specifically: the vault's list
  indentation (two spaces for the `- ` marker, four for an entry's sub-fields) and the note's key
  order must both survive a mutation, and a parse-and-compare assertion would hide either of them
  changing.
- **Trigger:** the suite's assertion style throughout — exact-string comparison on every mutation
  result.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:1` — file header comment
- **Note:** Not an `It` block, but a stated contract that governs how every case above must be read,
  and it is a real constraint on a reimplementation: list indentation is not free choice. The
  header also states the boundary explicitly — the frontmatter block's *original bytes* are never
  asserted, because a mutation reserializes the whole block, so quoting is the emitter's to choose;
  the *body* is the facet held byte-for-byte, modulo terminators.

### FMC-131 — The frontmatter primitives are pure: no filesystem, no shared state

- **Behavior:** Every frontmatter primitive takes a content string and returns a content string.
  None reads or writes a file, so none needs sandboxing and none depends on vault configuration.
  The read/lock/write envelope belongs to the calling layer.
- **Trigger:** the suite's structure — no fixture setup, no temp root, no vault initialization.
- **Kind:** `semantic`
- **Source:** `Frontmatter.Tests.ps1:1` — file header comment; corroborated by
  `Public/Frontmatter.ps1:15`
- **Note:** Worth stating because it is a **layering** guarantee, not just a test convenience: it is
  what makes every case above testable without a vault, and a reimplementation that folded file
  access into these operations would lose it. Note this suite is the only one in the module that
  does *not* call `Initialize-VaultAccessTestFixture`.

## Gaps I want to flag explicitly

Not cases — observations for the reconciler about what the old suite did **not** assert in this area,
since a spec-derived suite might reasonably cover them and the reconciliation would otherwise read
the absence as agreement:

- **YAML comments inside a frontmatter block.** Nothing asserts they are recognized, and nothing
  asserts they survive a mutation. Given the reserialize-from-tree design they almost certainly do
  **not** survive. See FMC-123.
- **Blank-line preservation inside a block.** Recognition is asserted (FMC-123); preservation is not.
- **BOM handling.** Nothing in this file mentions a byte-order mark. `Get-VaultFrontmatterSpan`
  compares the first line against `---` with only trailing whitespace trimmed, so a leading UTF-8
  BOM would disqualify the opening fence and the note would read as having no frontmatter. Untested
  in either direction.
- **Nested mapping values.** The shape query's `Block` classification is asserted only for
  sequences; the implementation also classifies a nested mapping as `Block`, untested.
- **Nav target case-insensitivity.** The implementation states it explicitly and contrasts it with
  the pinned matcher's case-sensitivity (which *is* tested, FMC-109), but no test exercises it.
- **An unparseable frontmatter block.** The write path is documented to refuse with
  `frontmatter_integrity` / `frontmatter_unparsed`, and a block that parses to a non-mapping with
  `frontmatter_not_a_map`. Neither refusal is exercised anywhere in this file.
- **A composed value containing a literal `---` line.** See FMC-126 — the interaction of block-scalar
  emission with the line-level closing-fence scan is untested and looks like a latent defect.
