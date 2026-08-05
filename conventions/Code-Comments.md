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

## What is not yours to edit

**String literals are wire contract.** Tool `Description` strings, refusal
messages, error codes, and schema text are caller-visible and pinned by tests.
They look like documentation and are not. Where a `Description` string and a
class doc say the same thing, the class doc is the copy that shrinks.

**Test method names are documentation.** They are not rewritten, however long or
oddly phrased. The same goes for test data and assertion strings.

## Facts that live only in comments

The specification and design documents this build was written from are archived
at cutover and are unreachable afterwards. Four comments are therefore the only
surviving record of something that cannot be re-derived from the code. Do not
compress them:

- `FrontmatterComposer` - the four YamlDotNet emitter settings, the round-trip
  measurement behind them, and what changing one costs.
- `FrontmatterTree` - why the representation model's node layer is used and its
  object serializer is not.
- `WikilinkRanking` - the observed Obsidian resolution rule, the date it was
  measured, and the editor settings it was measured under.
- `Vault.LockName` - the mutex name derivation, and that a separate process
  reimplements it.

When a comment records something measured or agreed, it says what was measured,
what against, and what changing it breaks. That is what makes it usable by
someone who cannot repeat the measurement.

## Related

- `Comment-Sweep.md` - how to run a sweep over the whole repository against this
  standard.
