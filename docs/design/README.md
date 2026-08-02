# Design Documents

This directory holds the design for the vault access build. The specs in `../spec/` say what must be true; a design document says what shape this build takes to meet it. This file is the convention every document in the directory follows, so the set reads as one thing.

See [[Brief-Vault-Access-Build.md#Working Files]].

## The Set

One document per design area, named for the area:

- `Design-Core-Access-Layer.md` - vault resolution, the resolved-path type, scope enforcement, enumeration, search, the read and write primitives, wikilink grammar and ranking
- `Design-Frontmatter-Surface.md` - the block grammar and facet split, the operation vocabulary, authority classification, the validator, the registry's loading and failure behaviour
- `Design-Tool-Vocabulary.md` - argument shapes, result tiers, the error vocabulary, the preview convention, the write-class envelope
- `Design-Front-End-Projection.md` - one tool declaration, two front-ends, and what each may vary; the shared execute path that checks the op-class floor, takes the mutex lock, and constructs the per-call access object

A document is created when its design step is worked, never ahead of it. An empty or speculative file is worse than none.

## Document Shape

Two fixed sections bracket a free-form body.

```markdown
# <Area>

## What This Answers

<coverage index - see below>

## <mechanism sections, as many as the design needs>

The design. Organised by mechanism, not by spec order.

## What This Closes

<decision index - see below>
```

The body is the design. Everything else on this page constrains what belongs in it.

## What This Answers

A coverage map from spec section to the section of this document that meets it. One line each, and the line says **where**, never **what**. A design document that restates a requirement has added nothing, and a second copy of a requirement is a copy that will disagree with the first.

```markdown
- [[Spec-Vault-Access-Design.md#Load-Bearing Semantics]] - the unrepresentability property, met by the resolved-path type's single construction seam ([[#Resolved Paths]]).
- [[Spec-Access-Layer-Capabilities.md#Content Search]] - the four distinguishable outcomes, met by [[#Search Results]].
- [[Spec-Access-Layer-Capabilities.md#The Mutex Lock]] - only the two consequences that bind the layer: no primitive acquires, and the hold is single-threaded. The lock itself is [[Design-Tool-Vocabulary.md]].
```

Close the section with what the area might be expected to cover and does not, naming the document that does:

```markdown
Not answered here, deliberately:

- [[Spec-Tool-Capabilities.md#Wikilink Maintenance]] - the grammar is here, what repair and the census do with it is [[Design-Tool-Vocabulary.md]].
```

Write this section first. It fixes the document's scope before the body is written, and it is what the operator reads against the bundle to ask whether anything the area owns is missing.

## What This Closes

Every place the specs leave a choice, with the choice made. One entry each: the open point, then the rule.

```markdown
- **What denotes the vault root.** The specs require the root to be an operand for whole-vault enumeration and the always-readable union, and the retained `ScopePath.Normalize` throws on it. A resolved path with no segments is the root; resolution handles that case before calling the primitive.
- **Whether resolution throws or answers.** The specs say the two vault-resolution failures must be distinguishable structurally, and say nothing about the form. Resolution is total and answers a three-case result. Rejected: a throwing form, which puts the distinction in an exception type and leaves a caller free to not handle it.
```

The rejection clause is optional and most entries do not carry one. Add it only where the rejected option is attractive enough that someone would try it, and keep it to a sentence or two - the entry states an outcome, it does not argue a case. Where nothing was rejected, the entry is the rule alone.

This is the only record of a decision that has no code site. One that does belongs in a comment there as well, written as a constraint rather than a story.

Write this section last, harvested from the body. It is the sign-off surface: the body describes the whole design, most of which the specs force, and this section is the subset where the design had latitude.

## Level

A design document is the shape a reviewer can reject before the code exists. That fixes both edges. Specific enough that the operator can say "not that type" - so types, seams, ownership, ordering, and failure taxonomies are named. General enough that adding a parameter does not invalidate it - so members, signatures, and per-line reasoning stay out.

For any statement, ask what it answers:

- **"This must be true, however it is built"** - the spec's. Do not write it.
- **"This is the shape it takes"** - the design document's.
- **"Why is this line like that"** - a comment's.

Two tests that catch most of it. If a sentence would still be true with no code written, it belongs in a spec. If a sentence stops being true when something is renamed, it belongs in a comment.

The right level, on the type the first document turns on:

> Vault paths are represented by a single type. It can be produced only by a resolution that has been given a vault root and a scope map, so an instance is proof that both were applied; no constructor, factory, or conversion produces one without them. Operations on vault content accept that type and never a string.

Too high, because it is the spec restated:

> Out-of-scope paths are unrepresentable rather than checked. Correctness must not depend on every operation remembering to call a check.

Too low, because nobody can review it and the code says it better:

> `VaultPath` exposes `Vault`, `RelativeSegments`, and `ToString()`. `TryResolve` returns `bool` with an `out VaultPath` and an `out ResolveFailure`.

## Code Blocks

Allowed where the shape is the design and prose would be longer than the code. A block states a contract, never an implementation: a type declaration with factory methods and no bodies is a contract, and a method with a body in it is a sign the document dropped a level.

## Size

A document holds no more than 2,500 words of prose. Code blocks and `What This Answers` do not count, since they do not cost the same to read. Words rather than lines, because a paragraph is one line here and its length says nothing about how much there is to read.

A document that runs over has usually restated requirements or dropped to member-level detail, and both look like a size problem without being one. Check those first. Where the length is real, the area divides along a seam the design itself has, and each half keeps the full document shape. Naming is by suffix so the set still sorts together: `Design-Tool-Vocabulary-Arguments.md`, `Design-Tool-Vocabulary-Results.md`. The split point is a design decision and goes to the operator rather than being made in the document.

## Cross-References

Wikilink syntax throughout, with the file extension and the header where one applies: `[[Spec-Tool-Capabilities.md#The Argument Surface]]`, `[[Design-Core-Access-Layer.md]]`. A section of the same document drops the file and keeps the header: `[[#Resolved Paths]]`. One syntax for every reference, so a reader follows them all the same way and a search for a section name finds the places that point at it.

## Boundaries

A design document that absorbs either of these becomes a second source of truth.

- **The bundle cannot answer it.** `Questions.md`, after agreement. The design document does not proceed under a guess and does not record the gap.
- **It only makes sense beside the code.** A comment. Design documents name types and their contracts; they do not list members.

There is no separate decision log. A choice the specs left open is [[#What This Closes]], and how the change was reached is the commit message.

## Lifetime

A design document describes the design as it is. When a change lands, the affected part is rewritten wholesale to say what is now true. Nothing is appended, struck through, or narrated: no history, no record of what an earlier version said, no account of how the design arrived. Git history holds the chronology.

A revision goes to the operator at the same bar as the original. The document's claim is that it was reviewed, and that claim has to survive the edit.

Where the code and the document disagree, the disagreement is a defect. Fix it in the same commit, in whichever of the two is wrong.

## Prose

- One line per paragraph, and per list item. Hard wrapping is for code: a wrapped paragraph turns an edited sentence into a diff of every line after it, and hides which sentence actually changed. The rest of the repo's documents already read this way.
- Present tense, declarative. No history, no "we chose", no hedging.
- The reader gets the meaning on one pass. A sentence needing a second pass gets split.
- No throat-clearing and no ornament. If it is a list, it is a list.
- Named things in backticks, so a reader can grep for them.
