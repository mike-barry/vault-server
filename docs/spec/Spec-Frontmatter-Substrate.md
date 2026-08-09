# Spec Frontmatter Substrate

Forward specification for the frontmatter write surface: what a block is, how a note splits into frontmatter and body, what a mutation preserves, what operations must be expressible, and what the single write path guarantees. Its companion, `Spec-Frontmatter-Registry.md`, owns which keys and paths are allowed through.

Several tools write frontmatter and all go through this surface. It is specified separately because a multi-consumer contract buried inside its first consumer is missed by the second.

## Where The Boundary Sits

The surface covers **the per-note, in-memory half of frontmatter correctness**: everything decidable from one note's frontmatter, its vault-relative path, and the declaration this layer is given. Checks needing more than that - a directory listing, to confirm a folder reference points at a real folder; a consumer's own configuration, to judge whether a folder note's listing is complete - belong to `lint`, the tool that sweeps the whole vault. `lint` is held back from the first pass, so these checks are deferred rather than dropped.

Three things break if the line moves. A tool acting on one named note cannot answer a question about its whole folder, and would have to carry enumeration it never uses. The write path would stop being in-memory, and so stop being cheap enough to run before any disk change. And the surface would end up depending on configuration that belongs to a single consumer.

**No rule here reasons about a note's content from its location.** The path is used for one thing: matching the path policy in `Spec-Tool-Capabilities.md:Path Policy`, which decides which files the tooling may reach and what authority mutating them requires - never what a file should say. Conventions of the form "a file in this position must carry this key" belong to `lint`; adopting one here would enforce an editorial convention underneath every tool that writes.

## What A Block Is

**A block is present or absent; there is no partial block.** The opening fence is the note's first line and is exactly `---` once trailing whitespace is discounted. The closing fence is a later line that is exactly `---` or `...` under the same rule.

- **An unclosed opening fence is not a block**, and the whole file is body. A parser treating a dangling opener as an unterminated block silently swallows the note's content.
- **A fence pair with nothing between them is a block with empty frontmatter.** Nothing here emits one, but one found on disk reads and edits normally.

**Frontmatter has no official standard, so the rules in this section are the whole of the grammar.** They match Markdig's convention, which is where they were taken from, but no code in the build reads that library and no test compares against it. A case these rules do not answer is settled by amending them.

Detection needs no markdown library. Bounded fence scanning answers it.

## The Facet Split

**One definition of the split, and it lives here.** Every consumer that needs either facet takes it from this one place: a reader returning parts of a note, a patcher anchoring against body text, a tool echoing back what it wrote, and the write path's own guard. Because they share one definition, the frontmatter a tool echoes back is byte-identical to what a reader returns, without anyone keeping the two in step.

The split produces four things: whether a closed block was found; the block's **inner text, raw and never parsed**, without the fence lines or the final inner line's terminator; the body, from the character after the closing fence line; and the offset at which the body begins.

- **A note with no block has empty frontmatter and is entirely its own body.** No special case elsewhere.
- **The body offset comes from the closing fence line's terminator, not the end of its fence text.** Trailing whitespace there belongs to the fence; taking the offset earlier leaks a fragment into the body facet, which surfaces as a phantom diff on a body-only write.

The offset lets a consumer put a new body into the original without recomposing a block it never touched. A body-only write therefore disturbs no frontmatter bytes, and a note's content hash stays put when nothing meaningful changed.

## Preservation Invariants

**A mutation changes the frontmatter tree and nothing else.** Every byte of the body survives verbatim except line terminators.

**Line terminators normalize to LF, with no detection of what was there.** Notes are stored that way (`Spec-Vault-Access-Design.md:Fixed Constraints`), and the repository normalizes every text file on checkout, so no other convention survives a round trip regardless.

**Normalization happens on entry, not on the way out.** The write path compares the block before and after composing final content to detect a body write that disturbed it; normalizing between those reads would trip the guard on terminators alone.

**The block's bytes are not preserved, only its meaning**, and reserialization is **confined to writes that actually ran a frontmatter operation**. A body-only write leaves the block exactly as found. Without that confinement a body edit reformats frontmatter it never touched and moves the content hash.

**Key order is preserved exactly.** The parse produces an order-preserving structure; without it the first operation against a note reshuffles every key in it.

**Emission indents sequence items beneath their key.** Both forms are valid and the parse cannot tell them apart, so an unstated choice means the emitter's default reformats every list on its first write. Indented matches the vault's existing notes, which confines the reserialization diff to quoting.

**A parsed-and-re-emitted block drops blank lines and comments, and reflows a folded scalar's line wrapping.** All three are artifacts of text with no place in the tree. Confining reserialization to writes that ran an operation keeps them out of a body-only write; the first frontmatter operation on such a note loses them.

**A no-op returns the caller's content unchanged**, byte-identical, not a reserialization of it.

**Exactly one place writes a fence.** Composing content from a tree and a body is one operation, and an empty tree emits no fence. A removal that empties the block therefore drops the fence with it, and no note is ever left holding an empty fence pair.

**Parse, mutate, emit - never splice.** No mutation reasons about lines or does text surgery on the block. Splicing is what makes injection through a caller-supplied value possible; a value that enters a tree and leaves through an emitter cannot become structure. Every operation reaching this surface carries caller text, so this binds all of them.

## Reading Tolerates What Writing Refuses

The read accessors - block to map, entries under a key, a field within an entry - treat **a block that will not parse as no block**. They report on notes rather than gating them and run over every file in the vault; one malformed note must not fail a whole-vault pass. The write path refuses that same note where the call interprets frontmatter - a write carrying operations or a caller-supplied block - because writing into a block nobody could parse means guessing. A body-only write neither reads a key nor composes a block, so a block that will not parse does not refuse it and is left exactly as found.

**Accessors returning collections return empty collections, never null.** Every caller enumerates the result, and an absent-value return pushes a null check into all of them.

## Shape Detection

**Reading a field's value cannot answer its shape or its presence.** A block-valued field, an absent field, and a field declared with no value all read back the same. Shape is a separate query with three answers: **absent**, **scalar**, **block**. A sequence or nested mapping is block; everything else, including a valueless key, is scalar.

A flat scalar operation aimed at a field holding a list would replace the list with a string, so detecting shape first is the only thing between a caller's typo and a destroyed list. A valueless key counts as scalar because there is no list under it to lose; the validator reports it separately as an empty block.

## The Operation Vocabulary

What the layer must make expressible, and the semantics each capability must have. Operation names and payload field names are the tool layer's to choose.

An operation names three separate things, kept distinct throughout:

- **The declared key** - the frontmatter key being mutated, such as `agent.instructions`. The declaration says whether it is recognized, what shape it holds, and what authority writing it requires.
- **The operation** - which of the four capabilities below is being applied.
- **The discriminator** - for a block-list entry, the field whose presence says which variant the entry is, such as `use_skill` on an `agent.instructions` entry. The declaration names one per variant, and two variants of a schema may not share one.

### What Must Be Expressible

- **Set a field to a scalar value.**
- **Remove a field.** The scalar or block path is chosen by the field's detected shape, not by the caller declaring which. A block-list field can be removed only where every entry under it belongs to a variant the schema allows clearing wholesale; otherwise the removal is refused rather than applied in part.
- **Add an entry to a block list.** The entry must satisfy one of the variants its schema declares, and those variants differ: one may be a bare scalar, another a mapping with its own required fields. An add is therefore not one fixed payload shape, and the entry's discriminating field decides which variant applies. An add carries the entry's fields and an optional positional anchor.
- **Remove an entry from a block list**, addressed by its identity, or - where the schema permits it for that variant - clearing every entry of the variant with no target named.

Block-list operations exist because nothing else reaches a block list: a flat scalar set against a block key is refused by construction, and body patches are body-confined. Whether the tool surface exposes these as two operations or several narrower ones is the tool layer's decision.

### Semantics Every Operation Must Obey

- **Operations are ordered, each seeing the previous result.** Repetition is meaningful: two additions differing only in a non-identity field are two entries.
- **Operation names and discriminators are trimmed and case-folded before dispatch.** An unrecognized operation, or an entry matching no declared variant, is refused rather than ignored.
- **Entries are addressed by declared identity, never by index.** An index names a different entry the moment an earlier operation shifts the list; an identity self-verifies. An identity matching **more than one** entry is a refusal, under the comparison the schema declares for that variant.
- **Block-list operations take fields, never pre-rendered block text.** Each field becomes a tree value that the emitter escapes, so a caller's value can never come out the other side as a key.
- **Positional anchors that do not resolve are refused** - an anchor naming no entry, or several, fails rather than falling back to appending.
- **Placement is explicit or it is an append.** Ordering conventions belong to the tool composing the write, which knows the note's purpose, reads the list, and computes the anchor.
- **Both directions are idempotent, keyed on declared identity.** A duplicate add and a removal of an absent identity report no change and mutate nothing. A duplicate add is a **pure no-op that does not update the entry's other fields**; rewriting an entry is a removal plus an addition in one ordered batch.
- **An add creates its key when absent**, and creates the block on a note that has none. Removing the last entry **drops the key** rather than leaving it empty.
- **Key spelling is canonicalized before any read or write.** Where membership is tested case-insensitively, a differently-cased spelling would pass the test and then be written as a second key alongside the first.

### Removal Semantics Are Declared, Not Fixed

Two properties differ between variants of one list, which is why `Spec-Frontmatter-Registry.md` carries them on the entry schema:

- **Whether a removal may clear a whole variant.** Entries a human authored are dropped one at a time, named precisely. Entries the tooling owns and rewrites are usefully cleared wholesale.
- **How an identity compares.** An identity that names a file or a wikilink compares case-insensitively, matching how the filesystem and the editor resolve it; otherwise a scan and an operation disagree about whether an entry is already there. An identity that is a line of human prose compares exactly.

A single global rule for either gets one case wrong. When a removal leaves nothing under a key, the key is dropped.

### Emptiness Is Tested One Way

**A value that is null, empty, or whitespace-only is empty**, and the additive and subtractive forms of an operation apply that one test to the same argument. Under two tests the same value is refused when added and silently does nothing when removed.

### How The Vocabulary Reaches Callers

The vocabulary is advertised as a **machine-readable schema**, one branch per entry variant, each variant's discriminating field marked required. A variant is selected by that field's presence rather than by its value, so no branch fixes what the field must say.

**The schema is generated from the declaration, not hand-authored beside it.** The entry schemas already carry variants, discriminators, required and optional fields, and value constraints. Generating from them means there is no second copy to drift, and no test needed to catch that drift.

**It advertises; it does not enforce.** Constraints with no clean client-facing expression - mutual exclusivity between two optional fields - live in descriptions, and **this surface remains the sole enforcement point**. A payload satisfying the advertised schema can still be refused here.

## The Single Write Path

**One entry point into every write that interprets frontmatter, and every tool uses it.** It takes the note's content, its vault-relative path, the loaded registry, the vault's path policy, what asserts write authority, and some combination of operations, a caller-supplied block, and a new body. It returns the composed content, both facets, the violation list, which operations applied, whether each facet changed, which keys changed, and what write authority the outcome required.

A consumer reaching the primitives directly would bypass the authority diff, the fence-introduction guard, the excluded-path refusal, and scalar hygiene. Those are properties of the path, not the primitives.

**One write does not route here: wikilink repair.** It replaces bytes inside a wikilink that already exists, reads no key, and builds no tree (`Spec-Tool-Capabilities.md:Wikilink Maintenance`), so none of the guarantees above have anything to act on. Two other rules keep it safe: the name grammar, and the rule that a substitution stays inside a link's target. Every write that reads or composes frontmatter routes here.

**The registry is received, not reached for.** It arrives as an argument rather than from a global or a container the layer reaches into, so the layer can be exercised against a synthetic vocabulary: a conformance suite declares its own keys, namespaces, and exclusions and asserts the mechanism against them, without asserting anything about the vault's actual vocabulary.

**The path argument is mandatory, because a refusal depends on it.** Path policy matching is its only use, and an absent path would read as permission on exactly the files the policy protects. Generally: anything whose absence weakens a refusal is required.

**A caller-supplied block is an input to a parse, never text spliced through.** It is reduced to inner text, parsed, validated whole, and serialized from that tree, so what lands on disk is what passed validation with no second reading of the caller's bytes to disagree with the first.

Three call shapes must be supported and covered by test: mutation by operations, possibly alongside a body change; composition of a new note from a supplied block and body, with no original to diff against; and a narrow pass setting one or two scalar fields requiring no elevation. **All three run the same code, with no branch for either of the last two.** Composition has no original, so the diff counts every key present as changed. The narrow pass changes only keys that declare no extra authority, so the required union comes out empty and no assertion fires.

### What Throws And What Comes Back As Data

**Integrity, hygiene, path exclusion, and authority denial are hard refusals. Schema violations come back as data.**

That split is set by the widest consumer. A path that threw on any schema violation would refuse a sweeping fix pass on exactly the notes carrying pre-existing problems. So it returns the violation list and the caller decides: a single-note tool treats a non-empty list as failure, a sweeping tool renders findings.

**On an edit, refusals and violations cover only the keys the operations touched.** A key the declaration does not recognize, sitting elsewhere in the block, is passed through untouched and not reported. Editing one key is never blocked by an unrelated one (`Spec-Frontmatter-Registry.md:Two Extents, One Closed Perimeter`).

- **Fence-introduction guard.** After composing final content, the path re-extracts its frontmatter and requires byte-equality with what the operations produced. This covers both directions: a body write *introducing* a fence on a fence-less note, minting a block no operation authored and bypassing validation and the authority assertion; and one destroying or truncating an existing fence.
- **Parse and fence integrity.** A located block that will not parse, and one parsing to something other than a mapping, are refusals where the call interprets frontmatter - a write carrying operations or a caller-supplied block. So is prepending a fresh block to a note whose first line already looks like a fence - an ambiguous shape, worse resolved silently than refused. A body-only write neither reads a key nor composes a block, so a block that will not parse does not refuse it: the new body is spliced at the recorded offset and the block survives untouched. The fence guard above still applies to it, and is what keeps such a write off the frontmatter.
- **Caller-declared frontmatter that does not hold up.** On a supplied block, an unclosed fence and content after the closing fence are refusals rather than readings of a file: the caller declared the text is frontmatter, so text that cannot hold as frontmatter is a caller error.
- **Path exclusion.** Distinct from an authorization denial, per `Spec-Tool-Capabilities.md:Path Policy`. Enforced here because every write that composes frontmatter routes here.
- **Scalar hygiene.** Every operation carrying caller text refuses **control characters**, binding entry field values as well as scalar values - terminal escapes are control characters, and the most agent-facing text in the vault lives in block-list entries. It **refuses rather than strips**: silently rewriting a value reports success while changing what was asked for. Newlines and quotes are **not** refused, since the emitter handles both, which keeps hygiene a policy about content rather than an artifact of a format.
- **Registry refusal.** A key or value failing the registry is refused before anything is emitted.
- **Shape mismatch.** A flat scalar operation against a field holding a block. Raised here because this path applies the operations and is the only place the corrupting path exists.
- **Authority denial.** Below.

Refusal payloads carry enough to locate the problem - which operation, which key - without disclosing anything the caller was not authorized to learn. A refusal over a key no declaration recognizes carries one thing more: the declared key names, sorted ordinally and identical from every raise site, so the caller lifts one of them into a corrected call. Every other refusal carries none. The list is the vault's declared vocabulary rather than a set filtered against what this caller may write, so it says nothing about the caller's authority.

### Write Authority Classification

**Required authority is the union of what the registry declares for every key whose *resulting* value differs from its original.** Classification is **outcome-based over the composed diff**, never over what operations nominally targeted and never over a notion of protection this layer holds independently of the declaration.

This is the layer's single most important property. Classifying by intent classifies by what the caller said they were doing, and every injection path is a case where an operation aimed at an innocuous field mutates the protected layer anyway. Measuring the outcome closes the class rather than the instances someone thought of.

- **Classification runs in the validate phase, before any disk change**, so a denial costs nothing and leaves nothing half-done.
- **A caller-supplied block asserts a second time, over every key it carries**, whether or not that key's value differs from the note's original, before the block is serialized (`Spec-Frontmatter-Registry.md:Write Authority`). It is the registry's statement that a key requiring elevation was supplied at all; the outcome diff still runs afterwards and remains what decides whether the key changed.
- **The diff compares re-emitted values, not raw text**, or a reserialization that changed only quoting reads as a change and denies a write that changed nothing.
- **No single operation owns the refusal.** The denial payload names the *changed key set*, not an operation index. That set is the call's own coordinates rather than information about the caller's authority, so it does not breach the no-oracle rule on errors.
- **A body-only write contributes no key-level authority**, by construction: patches match the body facet only, and the fence guard stops a body replace from creating frontmatter. It may still require authority the path policy declares, for a file whose body *is* an agent instruction.
- **An empty op-class set denies, and no authority context at all is not a valid state.** Every invocation carries a grant, so there is nothing to degrade to. Same principle as scope, where breadth is an explicit case rather than an absence.

**This assertion is a backstop, not the guarantee.** It denies a caller who lacks the authority, and lets through one who holds it - so it cannot be what makes a tool safe to run unelevated. A tool that claims to need no elevation earns that from what it can write: a fix set that could never change an elevated key, for any caller. This assertion catches the case where that claim is wrong.

## The Validator

Takes a note's content and the loaded registry, and **returns a list of violations, empty when valid**. It needs nothing else - no path, no filesystem, no consumer configuration.

**It never returns a verdict.** A boolean satisfies the single-note tools and strands the sweeping one, which builds findings and remedy payloads from what was wrong; **the return shape is set by the widest consumer**. Each violation carries what a remedy needs without re-deriving it: which rule fired, which key, which entry within a block list, the offending sub-field or inline value, and human-readable reasoning. Entry positions reported to humans are **1-based**, so findings from different sources correlate.

**Most of what it checks is the declaration, applied.** For a block-list key it walks each entry against the declared schema: whether the entry matches exactly one variant - neither and both are each a violation - required fields, unknown fields, mutually exclusive groups in both directions, and each field's value constraint. A bare scalar entry where no scalar variant is declared is the same check, not a special case.

Compiled in is what is true of any block rather than of a particular vocabulary: a block that will not parse or parses to something other than a mapping, a declared key carrying no value, an empty block list, and an inline value where the declaration says a block list belongs.

**A block that will not parse is reported alone.** It is the one violation naming no key, and no per-key check runs beside it, since a block that did not parse has no keys to walk. Reporting it is what keeps the validator from calling valid a note the write path refuses.

**Every key in the block is checked**, against the declaration it was given. The validator has no namespace of its own and skips nothing: a key the declaration does not recognize is itself a violation. This is a report on a note as it stands, not a gate - what an edit may write is the write path's question, and it is scoped to the keys that edit touches.
