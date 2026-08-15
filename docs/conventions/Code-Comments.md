# Code Comments

The standard for every comment in this repository, whatever file holds it: C# in
`src/` and `tests/`, PowerShell, JSON with comments, project files, and scripts.

A rule naming a C# construct applies wherever the language has one and is inert
where it does not. Only "Document every parameter" is C# alone, because nothing
else here is enforced by a compiler.

`.claude/prompting/Prose-Style.md` governs how a sentence is worded, in a comment
as much as anywhere else, and every session has it loaded already. This document
decides what a comment says and whether it is there at all.

Comments are written in the register of MSDN and BCL documentation: simple,
clear, concise, complete. The reader to write for is a developer who has opened
nothing but the file in front of them.

## The rules

### A summary opens with a verb phrase

One or two plain sentences, active voice, saying what the thing does.

> **No:** `What a removal turned out to be, or would be.`
> **Yes:** `Represents the result of a delete operation or its preview.`

### A comment stands on its own

It never leans on a document, on another file, or on an idiom the reader has to
have met before. Whatever it depends on is said in it.

> **No:** `The settings are fixed by measurement.`
> **Yes:** `Do not change the four emitter settings. They were chosen by measuring round-trip fidelity against the real vault's existing notes, and changing any of them reformats existing frontmatter on its next write.`

The test: someone who has read nothing else understands it on first read. An
idiom fails that test silently - it reads as confident prose and only breaks
when someone who was not here tries to use it.

One citation is permitted: a document in `docs/reference/`, named by bare
filename in backticks, as in `Ref-Obsidian-Link-Behavior.md`. Never a path,
because a path is a fact about layout that moving the file falsifies. It
supplements the comment and never replaces it - the comment still states in full
what its own site depends on, and the reference carries the whole rule and the
conditions a measurement holds under.

The exception is the same property running in both directions. A document
qualifies for `docs/reference/` because no change here can falsify it, and that
is what makes citing it safe, since the citation cannot be broken by an edit to
the code beside it. A citation to anything else can, which is why there is no
second exception. Not to a specification, a design, a guide, or a convention,
and not to a document that has been archived or frozen. A frozen document is the
worst of them, because it resolves, and answers.

### Justify only what the code cannot show

Keep a "why" where a maintainer would otherwise break something: a cross-process
contract, a deliberate asymmetry, a non-obvious refusal, a settled decision
someone would be tempted to "fix". A longer caution goes in `<remarks>`, written
calmly.

Everything else needs no defence. Most members only need describing.

### The chokepoint owns a concern that spans files

The one thing comments handle badly is behavior with no single site. Either
every file describes it and the copies diverge, or none does.

Where a concern passes through one place - the class every caller routes
through, the seam both sides share - that place carries the whole description,
says that it is the only site, and says what breaks if a second one appears. The
participants carry nothing and restate nothing.

> `ToolRefusalEnvelope` lists the refusal types both front-ends recognize, and
> says that listing them in each front-end's rendering path instead would let
> one front-end crash where the other returned an envelope, for the same call.

Where a concern genuinely has no chokepoint, that is a finding about the design
rather than a documentation problem.

### Document every parameter

A documented member carries a `<param>` for each of its parameters, plus
`<returns>` and `<exception>` where they apply. Records document their
positional parameters the same way. One sentence each; near-tautological is
fine (`The vault the access object is scoped to.`). Holes are not fine:
completeness is what lets a reader trust that an absence means nothing.

`CS1573` enforces this at build time on the three `src/` projects, which
generate their documentation file with `CS1591` suppressed. A member with no
documentation at all is allowed; a member documented in part is not.

The test projects deliberately do not generate documentation, so nothing there
is enforced. Write it to the same standard anyway.

### Present tense, no chronology

A comment says what is true now. No "previously", "no longer", "moved from",
"this build", "as of this step". Git history holds the chronology. Where a
change makes a comment describe a world that no longer exists, rewrite it in the
same commit.

### Inline comments earn their place

Only where the next line is genuinely non-obvious. An inline comment that
restates the code is noise; one carrying a real reason stays. Banner comments
are fine and carry no numbering, which would index something outside the file.

### Colons that are not prose

`Prose-Style.md` keeps a colon from standing in for a reason. Two colons in code
are not that colon. A `Windows:` breadcrumb is unaffected, and so is a colon
inside a code literal such as `<c>isError: true</c>`.

## What is not yours to edit

**String literals are wire contract.** Tool `Description` strings, refusal
messages, error codes, and schema text are caller-visible and pinned by tests.
They look like documentation and are not. Where a `Description` string and a
class doc say the same thing, the class doc is the copy that shrinks.

**Test method names are documentation.** They are not rewritten, however long or
oddly phrased. The same goes for test data and assertion strings.

## Facts the code cannot re-derive

Some comments record something established outside the code - a measurement,
another application's observed behavior, a dependency's contract, an agreement.
The code cannot be read to recover one of these, so anyone shortening it is
guessing at what the writer verified.

**A comment recording a measured or agreed fact is not shortened by anyone who
cannot repeat the measurement.** It is long because the fact is.

Such a comment says what was measured, what against, and what changing it
breaks. That is what makes it usable by someone who cannot repeat the
measurement, and it is also how it is recognized: there is no list of protected
comments, and starting one is not the answer. A roster protects what existed
when it was written, implies that everything absent from it is fair game, and
becomes a third copy of a fact that drifts from the two real ones.

`docs/conventions/Documentation-Strategy.md` carries the test for when a fact of
this kind belongs in a `docs/reference/` document rather than in the comment.

It does not carry the date it was measured on. A date is chronology, which the
present-tense rule keeps out, and it earns no exception here: nothing in this
repository can check it, so it goes stale silently while reading as though it
had been verified. What a reader needs instead is the condition the result holds
under - the settings, the fixture, the version - and how to establish it again.

## Related

- `docs/guides/Comment-Sweep.md` - how to run a sweep over the whole repository
  against this standard.
