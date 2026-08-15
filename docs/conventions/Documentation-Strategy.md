# Documentation Strategy

Whether a document should exist at all, and what may go in one. The root
`CLAUDE.md` carries the table of where a fact goes.

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

A convention and a guide differ in what they have authority over. A convention
rules on the artifact: it says what the result must look like, it binds every
edit of its kind, and departing from it is a defect. A guide rules on the
procedure: it says how to carry out an undertaking that happens rarely, and
departing from it is a different plan rather than a defect. A guide holds
nothing about any particular run of the thing it describes - what triggered one,
what it covered, what it found - because the next reader is running a different
one. That belongs in `tasks/`.

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

## Promoting a fact to a reference document

Some comments record something established outside the code - a measurement,
another application's observed behavior, a dependency's contract, an agreement.
`Code-Comments.md` carries what such a comment must say, and why nobody who
cannot repeat the measurement may shorten one. What follows is the separate
question of whether the fact belongs in a `docs/reference/` document instead of
in the comment.

`docs/reference/` holds only subjects this repository does not control. Such a
document cannot be falsified by a change here, which is what qualifies it. Files
there are named `Ref-<Subject>.md`.

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
