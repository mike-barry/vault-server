# task-014: A standard for commit messages

**Goal:** A convention document stating what a commit message must carry, now that it is the only record of why a change was made.

## Context

`CLAUDE.md`'s table of where a fact goes makes the commit message a documentation surface rather than a courtesy. `docs/conventions/Documentation-Strategy.md` rules out a decision log, the specification and design are being retired, and `docs/conventions/Code-Comments.md` holds a comment to what the code does and why it is that way, written as a constraint rather than a story. What is left over - how a decision was reached, what was considered and rejected, what was wrong with the state before - has exactly one home now, and nothing in the repository states what belongs in it.

**The voice already exists and should be described rather than replaced.** The history writes an imperative subject in sentence case, no trailing period and no type prefix, often two clauses joined by "and", followed by a body of plain prose paragraphs. `e8da253`, `039db0b` and `cf6cf87` are the fullest examples: each says what changed, why the previous state was wrong, what was rejected, and what a later reader should not have to re-derive. The standard's job is to make that reproducible by someone who has not read the history, not to introduce a different form.

**The line to draw is between the comment and the message.** Both carry a "why", and without a stated split both will end up carrying both. A comment states the constraint a maintainer would otherwise break, in one sentence, at the site, and stands on its own. The message carries the reasoning, the alternatives rejected, and the account of what the previous state got wrong - none of which has a code site, and all of which would turn a comment into an essay. `docs/conventions/Code-Comments.md` already fixes the comment side, so the standard defines itself against it rather than restating it. It says nothing about wording either, because `.claude/prompting/Prose-Style.md` already names the commit message in its scope and is loaded in every session.

**Where it lands.** `docs/conventions/Commit-Messages.md`, loaded before committing, with a row in CLAUDE.md's reference table.

## Acceptance criteria

- [ ] A convention document under `docs/conventions/` states what a commit message must carry, and what belongs in a comment instead.
- [ ] It describes the subject-line form already in the history rather than introducing a different one.
- [ ] It says what a body must cover for a change that decides something, and what may be left out for a mechanical one.
- [ ] CLAUDE.md's reference table names it and says when to load it.

## Open questions

- Is a body required on every commit, or only where a change decides something? A mechanical rename under three paragraphs is its own kind of noise.
- Does the `Co-Authored-By` trailer stay, and is it required or incidental?
- How does a commit reference a task? The history names them in prose - `295109b` and `039db0b` both name `task-013` - rather than in a trailer or a subject prefix.
- Does the standard say anything about commit granularity, or only about the message? The two are related: a commit spanning two unrelated threads cannot have a body that explains one thing.

## Notes
