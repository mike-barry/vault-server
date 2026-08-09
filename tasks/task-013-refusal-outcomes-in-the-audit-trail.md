# task-013: Whether a refused call is recorded, and where

**Goal:** A determination on whether the server should record the outcome of a refused call, reached by examining what the audit trail is for, and then carried out - which may be a change, or a decision that nothing is needed, written down where the next reader will find it.

## Context

This is an exploration first. Nothing here asserts that the current behaviour is wrong; the work is to find out, decide, and act on the decision.

**What is observable now.** `BearerGrantMiddleware` writes one audit line per request, carrying the grant id, the JSON-RPC method, the tool, the vault the caller named, and the full arguments. It is written before the call is dispatched, so it records that a call arrived and what it asked for. The string `"Audit:"` appears nowhere else in the solution.

No tool refusal is recorded server-side, on either front-end. A refusal reaches the caller in the response and is not written anywhere else. The one exception was a transport-level vault-scope denial in the middleware, which logged `result=rejected-vault-scope`; [task-010](archive/task-010-conformance-audit.md) removed that check, because the same condition answered in two different shapes depending on whether the caller spelled the vault argument, and the coded refusal from the execute path is the answer that survives.

**Questions the work answers, rather than assumes:**

- What is the audit trail for here - reconstructing what an agent did to the vault, security review, debugging, or something narrower? The answer decides whether an attempt that changed nothing is in scope at all.
- Does an arrival line without an outcome already answer the question a reader brings? A reader can see what was asked; whether the absence of a completion line is a gap depends on what they are asking.
- If outcomes should be recorded, where. The sites that decide a refusal are `ToolExecution` and `ScriptToolDispatcher`; the CLI decides its own and writes no trail at all. A single composer and three call sites is one shape; a decision that only the server needs it is another.
- What a successful call's record should be, if a refusal gets one. Recording only failures produces a log where absence means two different things.

**Relationship to [task-004](task-004-audit-surface.md).** That task covers the audit surface from the content side: a redaction flag so the trail can be kept without note bodies, and a CLI audit log where none exists. Its proposed composer already names an outcome code among the things an audit line would carry. The two overlap and may be one piece of work; deciding whether to merge them is part of this task, and the merge is the operator's call rather than this task's.

**Do not change the shipped log format without checking `e2e-smoke.ps1`.** Its log-hygiene probes pin parts of the current audit format and assert lines are present under the shipped configuration.

## Acceptance criteria

- [ ] The determination is made and written down, with the reasoning that produced it.
- [ ] Whatever the determination requires is carried out. If it is that nothing changes, that is recorded where someone asking the same question next will find it rather than only in this file.
- [ ] A recommendation on whether this and [task-004](task-004-audit-surface.md) are one piece of work, put to the operator rather than acted on.
- [ ] `dotnet build --no-incremental` clean and `dotnet test` green, if anything changed.

## Notes

Raised during [task-010](archive/task-010-conformance-audit.md), when removing the transport-level vault gate removed the only site that logged a refusal outcome.
