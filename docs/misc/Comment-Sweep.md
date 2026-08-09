# Running a Comment Sweep

How to bring every comment in the repository to the standard in
`docs/conventions/Code-Comments.md`, which is the authority on what the comments
should say. This document is about getting there over a codebase too large for
one pass, and records the strategy that worked.

A sweep is worth running when the comments have drifted as a body rather than
individually - a register absorbed from somewhere else, a convention that
changed, references to documents that are going away. A handful of bad comments
is a normal edit, not a sweep.

## The shape that worked

Three passes, in order, each committed separately. The order is load-bearing.

### Pass 1 - Inline anything the comments point at

Find every comment referencing a document that is going away, and replace the
pointer with the fact it was carrying. Most are ordinary constraints whose
substance is already in the comment, and the pointer simply disappears. A few
are empirical or negotiated facts the code cannot re-derive; those comments
become the only living record, so they must state what was measured or agreed,
what against, and what changing it breaks.

This runs first because it is the only pass whose workers may read the source
documents, and because Pass 2 would otherwise rewrite around pointers that are
about to be deleted.

Grep for the document names, but do not stop there. Prose references carry the
same problem without being greppable - "the three the spec fixes", "per the
design", "as the design requires", and one naming a document that did not exist
in the repository at all. Sweep non-C# files too: one citation was found in a
`.csproj`.

Verify every inlined fact against the source yourself. After the archive the
comment is the only copy, and a worker's paraphrase is not good enough.

### Pass 2 - The bulk rewrite, one worker per area

Partition by directory into disjoint areas of roughly 2,000-5,000 lines and give
each to its own worker. Disjoint areas can run in parallel with no conflict.
Roughly one commit per area.

Two rules make the difference:

**Workers must not read the documents whose register is being removed.** Agents
mimic what they read, and a worker that has just read the source prose will
reproduce it faithfully while believing it is rewriting. Ground workers in the
standard and in the code alone. Only the arbiter consults the source documents,
and only to settle a disputed constraint.

**Workers report suspected false claims rather than fixing them.** A worker that
"corrects" a claim it misread introduces a defect that looks like a cleanup. The
arbiter verifies each report against the code and decides. This pass found three
real false claims that way, and each needed the code read to confirm.

Give every worker the same brief and add only the area-specific part: what the
area holds, and which constraints in it are known to be real. Parameter
completeness is part of this pass - a worker rewriting a doc block completes its
parameters in the same touch.

### Pass 3 - Enforcement

Turn on `GenerateDocumentationFile` for the production projects with `CS1591`
suppressed, so `CS1573` and the malformed-XML warnings start failing the build.
Fix what it finds and get back to zero warnings. Enforcement last, because it
catches what Pass 2 missed and there is no point running it before.

## Verifying a worker's output

**The diff must contain no non-comment lines.** This is the one check that
matters and it is cheap:

```bash
git diff -U0 -- <area> | grep -E "^[+-]" | grep -vE "^(\+\+\+|---)" \
  | sed -E 's/^[+-][[:space:]]*//' | grep -vE "^(///|//)" | grep -vE "^$"
```

Anything it prints is a code change and needs justifying. Run it. Three workers
in this pass reported that they had briefly replaced a declaration or a constant
with placeholder text and reverted it within the same file; the check is what
turns that from a claim into a verified fact. It also caught a worker
reformatting an enum and another editing inside a string literal.

Then build, run the full suite, and spot-review the densest handful of rewrites
in the area - not for style, but to confirm the constraint survived and the
wording is genuinely plain.

## Auditing the test projects

The test projects do not generate documentation, so their doc comments answer to
nothing and dangling references accumulate invisibly. Audit them periodically
rather than enforcing continuously:

1. Add `GenerateDocumentationFile` and the `CS1591` suppression to the three
   test project files.
2. Build. Fix every warning.
3. Revert the project files.

The first run found eight unresolvable or ambiguous `cref`s, including one that
can never resolve because it names a type across a project boundary that is not
referenced. That one became plain text.

## A worked transformation

The pattern to aim for. Before:

```
/// Composes a note's content from a tree and a body, and it is the only thing
/// in the build that writes a fence. One site, so a removal that empties the
/// block drops the fence with it and no note is ever left holding an empty
/// fence pair.
///
/// The emitter configuration lives here for the same reason. It was established
/// by measurement over the real corpus (Spec-Vault-Access-Design.md:Frontmatter
/// Emitter Settings) and carries four parts, each of which changes what lands on
/// disk; spread across call sites they would drift.
///
/// Composing guarantees meaning, not bytes: what comes back carries every key in
/// its original order with every value's content intact, and may differ from the
/// block that was parsed in quoting, and in blank lines, comments, and a folded
/// scalar's line wrapping, which are artifacts of text with no place in a tree.
/// Keeping those out of a note that was not edited is not this type's job -
/// reserialization is confined to writes that ran a frontmatter operation, and
/// that confinement is the write path's decision.
```

After:

```
/// <summary>
/// Composes a note's content from a frontmatter tree and a body. This is the only type that writes
/// a fence, so an empty tree emits no fence and no note is left with an empty fence pair.
/// </summary>
/// <remarks>
/// Do not change the four emitter settings. They were chosen by measuring round-trip fidelity
/// against the real vault's existing notes: 315 of 320 blocks round-trip byte-identically under
/// these settings, and the five that do not are folded scalars under a dot directory the tooling
/// cannot reach. That measurement cannot be re-run from this repository, and changing any setting
/// reformats existing frontmatter on its next write.
///
/// Composing preserves meaning, not bytes. The result carries every key in its original order with
/// every value's content intact, but may differ from the parsed block in quoting, blank lines,
/// comments, and a folded scalar's line wrapping, none of which a tree can hold. Keeping those out
/// of a note that was not edited is the write path's job.
/// </remarks>
```

What survived: the single-writer constraint, the do-not-change warning, and the
reason it cannot be re-derived - which grew, because the citation was carrying
the measurement and now the comment has to. What died: the argumentation, the
citation, and the cadence.

## What a sweep must not destroy

`docs/conventions/Code-Comments.md` lists the four comments that are the last
surviving record of something unrepeatable. A sweep optimising for concision is
exactly the thing that would compress them into a sentence that reads better and
cannot be acted on. Leave them long.

The same caution applies to any comment stating a cross-process contract, an
ordering that must not change, a check-then-act prohibition, or a rule about
what a refusal may disclose. When unsure whether a clause is a constraint or an
argument, keep the fact and drop the argument. If it cannot be decided from the
code, leave the comment alone and report it.
