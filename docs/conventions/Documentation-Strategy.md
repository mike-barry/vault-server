# Documentation Strategy

Where a fact about this system is written down. Load this before creating a
document, before moving a fact out of the code, and before concluding that
something needs writing down at all.

Code comments are the primary description of this system. A comment is edited
in the same diff as the code it describes; a document is edited in a different
diff, or not at all. Everything below follows from that one difference.

## The admission test

> A document earns its place only if no code change can make it wrong.

Apply it to the sentence, not to the subject. "The audit trail exists to
reconstruct what happened to the vault" survives any refactor and is a
candidate. "The audit line is composed in `AuditLine`" is falsified by a
rename, and is a comment.

A document that fails the test does not merely go stale. It becomes a second
answer to a question the code already answers, and a reader who finds the two
in disagreement has no way to tell which is current. That costs more than the
document ever paid for, which is why the test admits so little.

## Where a fact goes

| Home | Holds |
| --- | --- |
| `CLAUDE.md` | What the system is, the project layout, the chosen constraints, and where to look for the rest. |
| `docs/conventions/` | How to do a kind of work. Loaded before doing it. |
| `docs/guides/` | How to carry out one undertaking. Loaded before starting it. |
| `docs/reference/` | Facts about things outside this repository. |
| `tasks/` | What to do next. |
| Code comments | What the code does, and why it is the way it is. |
| The commit message | How a change was reached. |

A convention and a guide differ in what they have authority over. A convention
rules on the artifact: it says what the result must look like, it binds every
edit of its kind, and departing from it is a defect. A guide rules on the
procedure: it says how to carry out an undertaking that happens rarely, and
departing from it is a different plan rather than a defect. A guide holds
nothing about any particular run of the thing it describes - what triggered one,
what it covered, what it found - because the next reader is running a different
one. That belongs in `tasks/`.

`docs/reference/` holds only subjects this repository does not control - another
application's behavior, a dependency's contract, a measurement taken against
something external. Such a document cannot be falsified by a change here, which
is what qualifies it. Files there are named `Ref-<Subject>.md`.

## What has no home

**A decision record.** How a decision was reached goes in the commit message
and nowhere else. A standing decision log is a document whose subject is the
code, so every code change can falsify it, and it accumulates entries faster
than anyone retires them. What survives from a decision is its outcome, and an
outcome with a code site is a comment there, written as a constraint rather
than a story.

**A description of the code's shape.** Types, seams, ordering, and failure
taxonomies are read off the code, which is authoritative and cannot drift from
itself.

## A concern that spans files

The one thing comments handle badly is behavior with no single site. Either
every file describes it and the copies diverge, or none does.

The rule is that the chokepoint owns the account. Where a concern passes
through one place - the class every caller routes through, the seam both
sides share - that place carries the whole description, says that it is the
only site, and says what breaks if a second one appears. The participants
carry nothing and restate nothing.

> `ToolRefusalEnvelope` lists the refusal types both front-ends recognize, and
> says that listing them in each front-end's rendering path instead would let
> one front-end crash where the other returned an envelope, for the same call.

Where a concern genuinely has no chokepoint, that is a finding about the
design rather than a documentation problem.

## A fact the code cannot re-derive

A comment describing what the code does is recoverable. Read the code and write
it again. A comment recording something established outside the code is not: a
measurement, another application's observed behavior, a dependency's contract,
an agreement. Whoever shortens one of those is guessing at what the writer
verified, and the loss is silent, because the shortened version still reads as
though someone had checked.

**Such a comment is not shortened by anyone who cannot repeat the measurement.**
It is long because the fact is, and length is not a defect in it.

No list of protected sites is kept, and one must not be started. A roster
protects what existed when it was written, implies that everything absent from
it is fair game, and becomes a third copy of a fact that drifts from the two
real ones. The comment identifies itself instead: one recording a measured or
agreed fact says what was measured, what against, and what changing it breaks,
which is what makes it usable by someone who cannot repeat the measurement and
recognizable to someone deciding whether to cut it.

### Whether it belongs in a reference document instead

Three conditions, all of them, or it stays a comment:

1. **External.** The subject is outside this repository's control - the
   condition that admits any document to `docs/reference/`.
2. **Shared.** More than one code site depends on it. A single-site fact stays
   at that site, however external its subject.
3. **Larger than any one site needs.** The whole fact exceeds what the sites
   depending on it have to decide. Where it fits in the comment that uses it,
   being external buys it nothing.

Passing all three is uncommon and meant to be. Failing any one of them is not a
problem to solve - it means the comment is already the right home.

### Only the operator adds or changes a reference document

An agent creates no file under `docs/reference/` and edits none that is there.
It describes the fact, says which of the three conditions it believes hold, and
waits. This holds however clearly the conditions are met, and it holds for a
one-line correction as much as for a new document.

Adding one is not an ordinary edit. A reference document is the only document a
comment may cite, so creating one mints an authority every future comment may
lean on, and the repository cannot check anything it says.

Changing one is worse, because an agent working inside this repository has no
legitimate trigger for it. What qualifies a document for `docs/reference/` is
that no change here can falsify it, so nothing done to the code can make one
wrong. The only thing that can is the external system changing, which is found
by re-measuring it - and an agent that cannot run the measurement is not
correcting the document but overwriting a measured result with a guess, in the
one place written to be trusted by someone who cannot check it.

## Comments cite only a reference document

A comment references no document except one in `docs/reference/`, and names it
by bare filename in backticks - `Ref-Obsidian-Link-Behavior.md`, never a path,
because a path is a fact about layout that moving the file falsifies.

The exception is the same property running in both directions. A document
qualifies for `docs/reference/` because no change here can falsify it, and that
is exactly what makes citing it safe: the citation cannot be broken by an edit
to the code beside it. A citation to anything else can, which is why there is
no second exception - not to a specification, a design, a guide, or a
convention, and not to a document that has been archived or frozen. A frozen
document is the worst of them, because it resolves, and answers.

**The citation supplements the fact and never replaces it.** The comment still
states, in full, what its own site depends on, and stands on its own for a
reader who never opens the reference. What the citation adds is depth and
provenance - the whole rule where the site needs one clause of it, and the
conditions a measurement holds under, which a comment may not carry.
