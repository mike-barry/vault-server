# task-013: Whether a refused call is recorded, and where

**Folded into `task-004-logging-standard.md` on 2026-08-09 and archived.** The determination this task existed to reach was made and is recorded below. Carrying it out belongs to `task-004`, which absorbed it along with this task's findings; nothing here is outstanding, and nothing else should be worked from this file.

> **Superseded, 2026-08-09.** Later the same day the operator withdrew the reconstruction determination this file records and reframed `task-004` around the three reasons to log - diagnostics, security, and troubleshooting - which have opposed requirements on whether a record may be switched off, how long it is kept, and whether it may carry vault content. Reconstruction survives as the security purpose of three rather than as the whole question. What still holds below: the merge reasoning, that the middleware cannot see a tool outcome, and the notes. What does not: that a refusal is recorded only as a consequence of a terminal line, and the redaction switch `task-004` was then carrying. `task-004` was renamed to `task-004-logging-standard.md` in the same change; the links to it below have been repointed, and nothing else in this file was edited.

**Goal:** A determination on whether the server should record the outcome of a refused call, reached by examining what the audit trail is for, and then carried out - which may be a change, or a decision that nothing is needed, written down where the next reader will find it.

## The determination

**The audit trail is for reconstruction** (operator, 2026-08-09): it answers what an agent did to the vault. Security review and server debugging were the two alternatives considered and were not chosen.

**Refusals are not recorded as such. Every dispatched call gets a terminal line carrying its outcome code, and a refusal gets one as a consequence.** The reasoning: a refused call changed nothing and its caller already holds the coded reason, so under a reconstruction trail it is not the interesting record. What reconstruction actually lacks is the other half - an arrival line for an `edit` says a mutation was asked for and never says whether it landed. A terminal line closes that gap and picks up refusals on the way.

Recording only refusals was rejected. It yields a trail where the absence of a line means both "succeeded" and "never finished", and it leaves a call that died mid-dispatch indistinguishable from one that was denied.

**The determination is written down in a comment on the composer, and in no document.** That follows the documentation strategy settled in the same session (`docs/conventions/Documentation-Strategy.md`): the composer is the single site audit lines are made, so it is where a reader asking what the trail is for lands. How the determination was reached is the commit message.

## Why it merged with `task-004`

The terminal line cannot be written by `BearerGrantMiddleware`. A tool refusal is not an HTTP-level event - it becomes a `CallToolResult` with `isError` set, inside a 200 response - so the middleware, which sees only the status code, would have to buffer and re-parse the response body to reach the outcome.

It has to be written at each front-end's dispatch chokepoint instead, which means one composer in `VaultShared` consumed by both front-ends. That is `task-004`'s stated deliverable, already specified in the right project and already naming an outcome code among the fields it would carry. Building it here would have meant writing that composer and having `task-004` immediately rewrite it to add the redaction switch, and `task-004`'s CLI audit log is the same call site as this task's CLI terminal line.

The call sites and the e2e finding - the log-hygiene probe pins less than it appears to, so appending a line breaks nothing - are recorded in `task-004`'s Context.

## Acceptance criteria

- [x] The determination is made and written down, with the reasoning that produced it.
- [ ] Whatever the determination requires is carried out. **Not done here** - carrying it out is `task-004`'s, by the merge below.
- [x] A recommendation on whether this and `task-004-logging-standard.md` are one piece of work, put to the operator rather than acted on. Recommended as one piece of work; the operator agreed and directed the fold.
- [x] `dotnet build --no-incremental` clean and `dotnet test` green, if anything changed. No code changed under this task.

## Notes

Raised during `task-010-conformance-audit.md`, when removing the transport-level vault gate removed the only site that logged a refusal outcome.

The one refusal outcome still logged anywhere is `result=rejected-non-loopback` at `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:116`, for a non-loopback request that fails authentication. That is a transport-level decision the middleware does make and can see, and the determination above does not disturb it.
