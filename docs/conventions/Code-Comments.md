# Code Comments

The comment standard for this repository's C# code, in `src/` and `tests/` alike.
Load this before writing or modifying a comment.

Comments are written in the register of MSDN and BCL documentation: simple,
clear, concise, complete. The reader to write for is a .NET developer who has
opened nothing but the file in front of them.

## The rules

### 1. A summary opens with a verb phrase

One or two plain sentences, active voice, saying what the thing does.

> **No:** `What a removal turned out to be, or would be.`
> **Yes:** `Represents the result of a delete operation or its preview.`

### 2. A comment stands on its own

It never leans on a document, on another file, or on an idiom the reader has to
have met before. Whatever it depends on is said in it.

> **No:** `The settings are fixed by measurement.`
> **Yes:** `Do not change the four emitter settings. They were chosen by measuring round-trip fidelity against the real vault's existing notes, and changing any of them reformats existing frontmatter on its next write.`

The test: someone who has read nothing else understands it on first read. An
idiom fails that test silently - it reads as confident prose and only breaks
when someone who was not here tries to use it.

One citation is permitted: a document in `docs/reference/`, named by bare
filename in backticks, as in `Ref-Obsidian-Link-Behavior.md`. It supplements the
comment and never replaces it - the comment still states in full what its own
site depends on, and the reference carries the whole rule and the conditions a
measurement holds under. Nothing else may be cited.

### 3. Justify only what the code cannot show

Keep a "why" where a maintainer would otherwise break something: a cross-process
contract, a deliberate asymmetry, a non-obvious refusal, a settled decision
someone would be tempted to "fix". One sentence, stated as fact rather than
argued. A longer caution goes in `<remarks>`, written calmly.

> **No:** `Checked over the set rather than inside the comparison, so a candidate from another vault - a layer defect rather than a ranking answer, the posture VaultAccess.Admits takes - surfaces as itself instead of as whatever the sort wraps it in.`
> **Yes:** `Checked here rather than inside the comparison, so the exception surfaces as itself instead of wrapped in whatever the sort throws.`

Everything else needs no defence. Most members only need describing.

### 4. No thesis paragraphs

No bolded claims, no manifesto structure, no rhetorical parallelism. Emphasis is
almost never needed.

> **No:** `**Authorization precedes argument feedback.** The floor is checked before arguments are bound, so a caller holding no authority over a tool cannot use a malformed call to probe the argument surface of one it may not run. It costs nothing, since the lock is later than both.`
> **Yes:** `The floor is checked before arguments are bound, so a caller with no authority over a tool cannot use a malformed call to probe the argument surface of a tool it may not run.`

### 5. Document every parameter

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

### 6. Present tense, no chronology

A comment says what is true now. No "previously", "no longer", "moved from",
"this build", "as of this step". Git history holds the chronology. Where a
change makes a comment describe a world that no longer exists, rewrite it in the
same commit.

### 7. Inline comments earn their place

Only where the next line is genuinely non-obvious. An inline comment that
restates the code is noise; one carrying a real reason stays. Banner comments
are fine and carry no numbering, which would index something outside the file.

### 8. A colon introduces a list, not a reason

A colon may introduce an enumeration of what was just named. It may not stand in
for "because", "so", or "and". Where it welds a claim to its justification, that
is two sentences and the connective belongs in the text.

> **No:** `Routing is decided before authority: a call naming a vault nobody configured is told so, rather than being refused for a scope that could never have admitted it.`
> **Yes:** `Routing is decided before authority. A call naming a vault nobody configured is told so, rather than being refused for a scope that could never have admitted it.`

> **Fine:** `Represents the result of resolving a path the caller named: the admitted proof, an unknown vault, or a path outside the scope.`

A `Windows:` breadcrumb is not a prose colon and is unaffected, and neither is a
colon inside a code literal such as `<c>isError: true</c>`.

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
comments, and starting one is not the answer. `docs/conventions/Documentation-Strategy.md`
carries the rule, and the test for when a fact of this kind belongs in a
`docs/reference/` document rather than in the comment.

It does not carry the date it was measured on. A date is chronology, which rule 6
keeps out, and it earns no exception here: nothing in this repository can check it,
so it goes stale silently while reading as though it had been verified. What a
reader needs instead is the condition the result holds under - the settings, the
fixture, the version - and how to establish it again.

## Related

- `docs/guides/Comment-Sweep.md` - how to run a sweep over the whole repository
  against this standard.
