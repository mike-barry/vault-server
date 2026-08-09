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
| `docs/reference/` | Facts about things outside this repository. |
| `tasks/` | What to do next. |
| Code comments | What the code does, and why it is the way it is. |
| The commit message | How a change was reached. |

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

## Comments do not cite documents

A comment references no document, by name or by section. A fact worth keeping
is stated in the comment itself, including what it was measured or agreed
against and what changing it breaks. A citation is a dependency on a file the
reader may not have, may not find, and cannot be shown to have read.

The reference documents are for the writer, not the reader of the code. Load
one before doing the work it governs; the code that results carries the fact,
not the pointer.
