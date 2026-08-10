# Spec Frontmatter Substrate

Forward specification for the frontmatter write surface: what a block is, how a note splits into frontmatter and body, what a mutation preserves, what operations must be expressible, and what the single write path guarantees. Its companion, [[Spec-Frontmatter-Registry]], owns which keys and paths are allowed through.

Several tools write frontmatter and all go through this surface. It is specified separately because a multi-consumer contract buried inside its first consumer is missed by the second.

## Where The Boundary Sits

The surface covers **the per-note, in-memory half of frontmatter correctness**: everything decidable from one note's frontmatter, its vault-relative path, and the declaration this layer is given. Checks that enumerate directories or read a consumer's own configuration - resolving a folder reference to a real folder, judging whether a folder note's listing is complete - belong to whatever pass sweeps the vault.

Three things break if the line moves. A tool operating on one named note has no standing to answer a completeness question about its folder, and would carry enumeration it never invokes. The write path would stop being in-memory, and so stop being cheap enough to run before any disk change. And the surface would depend on suppression configuration belonging to one consumer alone.

**No rule here reasons about a note's content from its location.** The path is used for one thing: matching the path policy in [[Spec-Frontmatter-Registry]], which decides which files the tooling may reach and what authority reaching them requires - never what a file should say. Conventions of the form "a file in this position must carry this key" belong to the sweeping pass; adopting one here would enforce an editorial convention underneath every tool that writes.

## What A Block Is

**A block is present or absent; there is no partial block.** The opening fence is the note's first line and is exactly `---` once trailing whitespace is discounted. The closing fence is a later line that is exactly `---` or `...` under the same rule.

- **An unclosed opening fence is not a block.** The markdown standard reads that shape as a horizontal rule plus a paragraph, and the whole file is body. A parser treating a dangling opener as an unterminated block silently swallows the note's content.
- **A fence pair with nothing between them is not a block.** Degenerate shapes are where implementations diverge from each other and from the standard, so they are pinned by explicit cases.

**The format standard decides what counts as a block, not local convenience.** Detection is held against an independent, standards-tracking implementation over a table of degenerate shapes and the retained fixture vault. A deliberate divergence - the empty fence pair, where reference implementations disagree with themselves across line terminators - is a recorded decision with its own case.

The production path carries no dependency on a markdown library to answer this. Bounded fence scanning suffices; the library belongs in the differential test.

## The Facet Split

**One definition of the split, and it lives here.** Every consumer needing either facet - a reader returning parts of a note, a patcher anchoring against body text, a tool echoing back what it wrote, the write path's own guard - takes it from this one place, which makes byte-identity between an echoed frontmatter and a reader's frontmatter facet **structural rather than maintained by hand**.

The split produces four things: whether a closed block was found; the block's **inner text, raw and never parsed**, without the fence lines or the final inner line's terminator; the body, from the character after the closing fence line; and the offset at which the body begins.

- **A note with no block has empty frontmatter and is entirely its own body.** No special case elsewhere.
- **The body offset comes from the closing fence line's terminator, not the end of its fence text.** Trailing whitespace there belongs to the fence; taking the offset earlier leaks a fragment into the body facet, which surfaces as a phantom diff on a body-only write.

The offset lets a consumer splice a new body into the original without recomposing a block it never touched, which keeps a body-only write from disturbing frontmatter bytes and a note's content hash stable when nothing meaningful changed.

## Preservation Invariants

**A mutation changes the frontmatter tree and nothing else.** Every byte of the body survives verbatim except line terminators.

**Line terminators normalize unconditionally, with no detection of what was there.** Forced rather than lossy: the editor that owns these files writes one convention and the repository normalizes every text file on checkout, so the other convention cannot survive a round trip regardless.

**Normalization happens on entry, not on the way out.** The write path compares the block before and after composing final content to detect a body write that disturbed it; normalizing between those reads would trip the guard on terminators alone.

**The block's bytes are not preserved, only its meaning**, and reserialization is **confined to writes that actually ran a frontmatter operation**. A body-only write leaves the block exactly as found. Without that confinement a body edit reformats frontmatter it never touched and moves the content hash.

**Key order is preserved exactly.** The parse produces an order-preserving structure; without it the first operation against a note reshuffles every key in it.

**Emission indents sequence items beneath their key.** Both forms are valid and the parse cannot tell them apart, so an unstated choice means the emitter's default reformats every list on its first write. Indented matches the vault's existing notes, which confines the reserialization diff to quoting.

**A parsed-and-re-emitted block drops blank lines and comments.** Both are artifacts of text with no place in the tree. Confining reserialization to writes that ran an operation keeps this latent, since an untouched note is never re-emitted.

**A no-op returns the caller's content unchanged**, byte-identical, not a reserialization of it.

**Exactly one place writes a fence.** Composing content from a tree and a body is one operation, and an empty tree emits no fence - which makes "a removal that empties the block drops the fence" structural, and prevents a note being left with an empty fence pair.

**Parse, mutate, emit - never splice.** No mutation reasons about lines or does text surgery on the block. Splicing is what makes injection through a caller-supplied value possible; a value that enters a tree and leaves through an emitter cannot become structure.

## Reading Tolerates What Writing Refuses

The read accessors - block to map, entries under a key, a field within an entry - treat **a block that will not parse as no block**. They report on notes rather than gating them and run over every file in the vault; one malformed note must not fail a whole-vault pass. The write path refuses that same note, because writing into a block nobody could parse means guessing.

**Accessors returning collections return empty collections, never null.** Every caller enumerates the result, and an absent-value return pushes a null check into all of them.

## Shape Detection

**Reading a field's value cannot answer its shape or its presence.** A block-valued field, an absent field, and a field declared with no value all read back the same. Shape is a separate query with three answers: **absent**, **scalar**, **block**. A sequence or nested mapping is block; everything else, including a valueless key, is scalar.

A flat scalar operation aimed at a field holding a list would replace the list with a string, so detecting shape first is the only thing between a caller's typo and a destroyed list. A valueless key is scalar because it has nothing to orphan, and the validator reports it separately as an empty block.

## The Operation Vocabulary

What the layer must make expressible, and the semantics each capability must have. Operation names and payload field names are the tool layer's to choose.

### What Must Be Expressible

- **Set a field to a scalar value.**
- **Remove a field**, routing to the scalar or block path **by detected shape**, not by the caller declaring which.
- **Add an entry to a block list.** The entry satisfies one of the variants its schema declares, and variants differ - one may be a bare scalar, another a mapping with its own required fields - so an add is not one fixed payload shape, and the applicable variant is settled by the entry's discriminating field. An add carries the entry's fields and an optional positional anchor.
- **Remove an entry from a block list**, addressed by its identity, or - where the schema permits it for that variant - clearing every entry of the variant with no target named.

Block-list operations exist because nothing else reaches a block list: a flat scalar set against a block key is refused by construction, and body patches are body-confined. Whether the tool surface exposes these as two operations or several narrower ones is the tool layer's decision.

### Semantics Every Operation Must Obey

- **Operations are ordered, each seeing the previous result.** Repetition is meaningful: two additions differing only in a non-identity field are two entries.
- **Discriminators are trimmed and case-folded before dispatch.** An unrecognized operation, or a recognized one carrying an unrecognized discriminator, is refused rather than ignored.
- **Entries are addressed by declared identity, never by index.** An index names a different entry the moment an earlier operation shifts the list; an identity self-verifies. An identity matching **more than one** entry is a refusal, under the comparison the schema declares for that variant.
- **Block-list operations take fields, never pre-rendered block text.** Each field becomes a tree value the emitter escapes, making "a caller's value cannot become a top-level key" a property of the emitter.
- **Positional anchors that do not resolve are refused** - an anchor naming no entry, or several, fails rather than falling back to appending.
- **Placement is explicit or it is an append.** Ordering conventions belong to the tool composing the write, which knows the note's purpose, reads the list, and computes the anchor. A layer that guessed would enforce an editorial convention underneath every tool that writes, and two tools wanting different conventions could not both be right.
- **Both directions are idempotent, keyed on declared identity.** A duplicate add and a removal of an absent identity report no change and mutate nothing. A duplicate add is a **pure no-op that does not update the entry's other fields**; rewriting an entry is a removal plus an addition in one ordered batch.
- **An add creates its key when absent**, and creates the block on a note that has none. Removing the last entry **drops the key** rather than leaving it empty.
- **Key spelling is canonicalized before any read or write.** Where membership is tested case-insensitively, a differently-cased spelling would pass the test and then be written as a second key alongside the first.

### Removal Semantics Are Declared, Not Fixed

Two properties differ between variants of one list, which is why [[Spec-Frontmatter-Registry]] carries them on the entry schema:

- **Whether a removal may clear a whole variant.** Entries a human authored are dropped one at a time, named precisely. Entries the tooling owns and rewrites are usefully cleared wholesale.
- **How an identity compares.** An identity denoting something the wikilink namespace and the filesystem both resolve case-insensitively must compare that way, or a scan and an operation disagree about whether an entry is present. An identity that is a human's prose compares exactly.

A single global rule for either gets one case wrong. When a removal leaves nothing under a key, the key is dropped.

### Emptiness Is Tested One Way

The additive and subtractive forms of an operation apply the same emptiness test to the same argument. Two tests produce a surface where one value is a refusal going in and a silent no-op coming out. Which test is chosen matters less than that both use it, and the choice is recorded.

### How The Vocabulary Reaches Callers

The vocabulary is advertised as a **machine-readable schema**, one branch per entry variant, each variant's discriminating field pinned as a constant.

**The schema is generated from the declaration, not hand-authored beside it.** The entry schemas already carry variants, discriminators, required and optional fields, and value constraints. Deriving it removes hand-maintained drift, and the test whose only job is catching that drift.

**It advertises; it does not enforce.** Constraints with no clean client-facing expression - mutual exclusivity between two optional fields - live in descriptions, and **this surface remains the sole enforcement point**. A payload satisfying the advertised schema can still be refused here.

## The Single Write Path

**One entry point into a frontmatter write, and every tool uses it.** It takes the note's content, its vault-relative path, the loaded registry, and some combination of operations, a caller-supplied block, and a new body. It returns the composed content, both facets, the violation list, which operations applied, whether each facet changed, which keys changed, and what write authority the outcome required.

A consumer reaching the primitives directly would bypass the authority diff, the fence-introduction guard, the excluded-path refusal, and scalar hygiene. Those are properties of the path, not the primitives, and there is no second route.

**The registry is received, not reached for.** It arrives as an argument rather than from an ambient singleton, so the layer can be exercised against a synthetic vocabulary: a conformance suite declares its own keys, namespaces, and exclusions and asserts the mechanism against them, without asserting anything about the vault's actual vocabulary.

**The path argument is mandatory, because a refusal depends on it.** Path policy matching is its only use, and an absent path would read as permission on exactly the files the policy protects. Generally: anything whose absence weakens a refusal is required.

**A caller-supplied block is an input to a parse, never text spliced through.** It is reduced to inner text, parsed, validated whole, and serialized from that tree, so what lands on disk is what passed validation with no second reading of the caller's bytes to disagree with the first.

Three call shapes must be supported and covered by test: mutation by operations, possibly alongside a body change; composition of a new note from a supplied block and body, with no original to diff against; and a narrow pass setting one or two scalar fields requiring no elevation. The degenerate cases **fall out of the general rules rather than branch** - with no original, every key present counts as changed; with only unelevated fields touched, the required union is empty and no assertion fires.

### What Throws And What Comes Back As Data

**Integrity, hygiene, path exclusion, and authority denial are hard refusals. Schema violations come back as data.**

That split is set by the widest consumer. A path that threw on any schema violation would refuse a sweeping fix pass on exactly the notes carrying pre-existing problems. So it returns the violation list and the caller decides: a single-note tool treats a non-empty list as failure, a sweeping tool renders findings.

- **Fence-introduction guard.** After composing final content, the path re-extracts its frontmatter and requires byte-equality with what the operations produced. This covers both directions: a body write *introducing* a fence on a fence-less note, minting a block no operation authored and bypassing validation and the authority assertion; and one destroying or truncating an existing fence.
- **Parse and fence integrity.** A located block that will not parse, and one parsing to something other than a mapping, are refusals. So is prepending a fresh block to a note whose first line already looks like a fence - an ambiguous shape, worse resolved silently than refused.
- **Caller-declared frontmatter that does not hold up.** On a supplied block, an unclosed fence and content after the closing fence are refusals rather than readings of a file: the caller declared the text is frontmatter, so text that cannot hold as frontmatter is a caller error.
- **Path exclusion.** Distinct from an authorization denial, per [[Spec-Frontmatter-Registry]]. Enforced here because every write routes here.
- **Scalar hygiene.** Every operation carrying caller text refuses **control characters**, binding entry field values as well as scalar values - terminal escapes are control characters, and the most agent-facing text in the vault lives in block-list entries. It **refuses rather than strips**: silently rewriting a value reports success while changing what was asked for. Newlines and quotes are **not** refused, since the emitter handles both, which keeps hygiene a policy about content rather than an artifact of a format.
- **Registry refusal.** A key or value failing the registry is refused before anything is emitted.
- **Shape mismatch.** A flat scalar operation against a field holding a block. Raised here because this path applies the operations and is the only place the corrupting path exists.
- **Authority denial.** Below.

Refusal payloads carry enough to locate the problem - which operation, which key - without disclosing anything the caller was not authorized to learn.

### Write Authority Classification

**Required authority is the union of what the registry declares for every key whose *resulting* value differs from its original.** Classification is **outcome-based over the composed diff**, never over what operations nominally targeted and never over a notion of protection this layer holds independently of the declaration.

This is the layer's single most important property. Classifying by intent classifies by what the caller said they were doing, and every injection path is a case where an operation aimed at an innocuous field mutates the protected layer anyway. Measuring the outcome closes the class rather than the instances someone thought of.

- **Classification runs in the validate phase, before any disk change**, so a denial costs nothing and leaves nothing half-done.
- **The diff compares re-emitted values, not raw text**, or a reserialization that changed only quoting reads as a change and denies a write that changed nothing.
- **No single operation owns the refusal.** The denial payload names the *changed key set*, not an operation index. That set is the call's own coordinates rather than information about the caller's authority, so it does not breach the no-oracle rule on errors.
- **A body-only write contributes no key-level authority**, by construction: patches match the body facet only, and the fence guard stops a body replace from creating frontmatter. It may still require authority the path policy declares, for a file whose body *is* an agent instruction.
- **Absent authority machinery degrades open; present-but-empty denies.** No op-class set injected at all - a standalone or recovery context - permits; an injected but empty set denies. Same principle as scope.

**This assertion is defense in depth, not the guarantee.** Its denial reaches a caller lacking the authority, not one holding it. A tool claiming to need no elevation earns that from its own fix set - one that cannot produce a diff over any elevated key for *any* caller - with this as the backstop.

## The Validator

Takes a note's content and the loaded registry, and **returns a list of violations, empty when valid**. It needs nothing else - no path, no filesystem, no consumer configuration.

**It never returns a verdict.** A boolean satisfies the single-note tools and strands the sweeping one, which builds findings and remedy payloads from what was wrong; **the return shape is set by the widest consumer**. Each violation carries what a remedy needs without re-deriving it: which rule fired, which key, which entry within a block list, the offending sub-field or inline value, and human-readable reasoning. Entry positions reported to humans are **1-based**, so findings from different sources correlate.

**Most of what it checks is the declaration, applied.** For a block-list key it walks each entry against the declared schema: whether the entry matches exactly one variant - neither and both are each a violation - required fields, unknown fields, mutually exclusive groups in both directions, and each field's value constraint. A bare scalar entry where no scalar variant is declared is the same check, not a special case.

Compiled in is what is true of any block rather than of a particular vocabulary: a declared key carrying no value, an empty block list, an inline value where the declaration says a block list belongs, and unreplaced placeholder content.

**Keys outside the namespaces the validator covers are skipped entirely.** That is its scope; the registry covers everything else. The two layers partition the problem, and neither is a fallback for the other.
