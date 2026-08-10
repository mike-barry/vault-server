# Running a Comment Sweep

How to bring every comment in the repository up to `docs/conventions/Code-Comments.md`
and `docs/conventions/Documentation-Strategy.md`. Those two are the authority on
what a comment says and on which facts belong in one at all; this document is
only about getting there over a codebase too large to hold in one pass.

A sweep is worth running when the comments have drifted as a body rather than
individually - a register absorbed from somewhere else, a convention that
changed, a class of fact that has to move into or out of the code. A handful of
bad comments is a normal edit, not a sweep.

**What triggered a given sweep, which areas it covers, and what it found belong
to that sweep's task file.** This document describes the procedure and nothing
else. A finding recorded here reads as a standing instruction to the next sweep,
which will not be looking at the same code.

## The three passes

In order, each committed separately. The order is load-bearing.

### Pass 1 - Settle what the comments have to carry

Reconcile the comments against the documentation strategy before rewriting any
of them, because Pass 2 would otherwise polish prose that is about to change.

Two kinds of work:

**Inline what a comment points at.** A comment cites no document, so every
citation is replaced by the fact it was carrying. Most are ordinary constraints
whose substance is already in the comment, and the pointer simply disappears. A
few are empirical or negotiated facts the code cannot re-derive; those comments
become the only living record, so they must state what was measured or agreed,
what against, and what changing it breaks.

**Rescue what only a document holds.** A decision the code settles without
saying why, or a constraint a document argued for and the code merely obeys, is
unreachable from the code once the document can no longer be cited - whether it
is deleted, archived, or frozen and still openable.

Grep for the document names, but do not stop there. A prose reference carries
the same problem without being greppable: "per the design", "the ones the spec
fixes", or a document named in passing that the repository never had. Sweep
non-code files too - project files and scripts carry comments as well.

Verify every inlined fact against its source yourself. Afterwards the comment is
the only copy, and a worker's paraphrase is not good enough.

This is the only pass whose workers may read the documents being reconciled
against. Rescuing anything later means opening them a second time, which no
later pass is allowed to do.

### Pass 2 - The bulk rewrite, one worker per area

Partition by directory into disjoint areas of roughly 2,000-5,000 lines and give
each to its own worker. Disjoint areas run in parallel with no conflict. Roughly
one commit per area.

Three rules make the difference:

**Workers must not read the documents whose register is being removed.** Agents
mimic what they read, and a worker that has just read the source prose will
reproduce it faithfully while believing it is rewriting. Ground workers in the
conventions and in the code alone. Only the arbiter consults a source document,
and only to settle a disputed constraint.

**Workers report suspected false claims rather than fixing them.** A worker that
"corrects" a claim it misread introduces a defect that looks like a cleanup. The
arbiter verifies each report against the code and decides. Confirming one takes
reading the code, which is why it is not the worker's call.

**Workers report comments describing behavior sited outside their area.** The
documentation strategy requires one site to own a cross-file concern, say that
it is the only site, and say what breaks if a second appears, with the
participants restating nothing. A worker partitioned by directory sees one
participant and cannot tell an owner from a restater, so left alone it polishes
a duplicate into a better duplicate. Ownership is decided by an arbiter pass
after Pass 2 completes, over the collected reports.

A fact about a system this repository does not control is reported the same way,
because its site is outside every area. Ownership can then resolve to a
`docs/reference/` document rather than to a code site, but only where all three
of the documentation strategy's conditions hold - external, depended on by more
than one site, and larger than any one of them needs. Most reports fail the
second or the third and stay where they are. Propose a promotion to the
operator rather than filing one; a reference document that nobody agreed to is
the dumping ground the three conditions exist to prevent.

Give every worker the same brief and add only the area-specific part: what the
area holds, and which constraints in it are known to be real. Parameter
completeness is part of this pass - a worker rewriting a doc block completes its
parameters in the same touch.

### Pass 3 - Enforcement

Turn on `GenerateDocumentationFile` for the production projects with `CS1591`
suppressed, so `CS1573` and the malformed-XML warnings start failing the build.
Fix what it finds and get back to zero warnings. Enforcement runs last because
it catches what Pass 2 missed, and there is nothing for it to catch before then.

## Verifying a worker's output

**The diff must contain no non-comment lines.** This is the one check that
matters and it is cheap:

```bash
git diff -U0 -- <area> | grep -E "^[+-]" | grep -vE "^(\+\+\+|---)" \
  | sed -E 's/^[+-][[:space:]]*//' | grep -vE "^(///|//)" | grep -vE "^$"
```

Anything it prints is a code change and needs justifying. Run it on every area,
including the ones whose worker reports having touched only comments. Workers do
reformat an enum, edit inside a string literal, and replace a declaration with
placeholder text and put it back - sometimes reporting it and sometimes not. The
check is what turns a worker's claim into a verified fact.

Then build, run the full suite, and spot-review the densest handful of rewrites
in the area - not for style, but to confirm the constraint survived and the
wording is genuinely plain.

## Auditing the test projects

The test projects do not generate documentation, so their doc comments answer to
nothing and dangling references accumulate invisibly. Audit them as part of a
sweep rather than enforcing continuously:

1. Add `GenerateDocumentationFile` and the `CS1591` suppression to each test
   project file.
2. Build. Fix every warning.
3. Revert the project files.

A `cref` naming a type across a project boundary that is not referenced can
never resolve, however it is spelled. Make it plain text rather than hunting for
a spelling that works.

## Deciding what to keep

The conventions carry the target wording and its examples. What a sweep adds is
the judgement about which clauses survive the rewrite, and one test does most of
the work: keep the fact, drop the argument. A constraint, a measurement, a
warning about what breaks - these survive, and one may have to grow when a
citation was carrying part of it. The argumentation, the cadence, and the
pointer die.

## What a sweep must not destroy

A comment recording something established outside the code - a measurement,
another application's observed behavior, a dependency's contract, an agreement -
is not shortened by a sweep. Nobody sweeping can repeat the measurement, so
cutting one is guesswork that reads afterwards as though it had been checked. A
sweep optimising for concision is exactly the thing that would compress one into
a sentence that reads better and cannot be acted on. Leave them long.

Such a comment is recognizable without a list: it says what was measured, what
against, and what changing it breaks. Take that shape as the signal, and do not
maintain a roster of protected sites - one goes stale, and its absence reads as
permission everywhere else.

The same caution applies to any comment stating a cross-process contract, an
ordering that must not change, a check-then-act prohibition, or a rule about
what a refusal may disclose. When unsure whether a clause is a constraint or an
argument, keep it. If it cannot be decided from the code, leave the comment
alone and report it.
