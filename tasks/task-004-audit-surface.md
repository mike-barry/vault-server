# task-004: The audit surface - a content-redaction flag, and a CLI audit log

**Goal:** The audit trail can be kept without note content, and a CLI mutation leaves an audit record.

## Context

Carried over from `docs/Sidequests.md`, which was retired. The original entry is reproduced in full under Notes; it states what is wrong now, what it costs while it stays, what the fix touches, and what is less obvious than it looks. `docs/Plan.md` tracked the same middleware separately until it too was retired, and its wording is reproduced there beside the original.

## Acceptance criteria

Not yet written - see Open questions.

## Open questions

- The entry says the config flag defaults to today's behaviour, because the e2e log-hygiene probes pin the current format under the shipped config. Is unredacted-by-default the intended end state, or the interim while the probes are updated?
- Where does `AuditLogPath` point when opted in, and what happens when it is unset - no CLI trail at all, or a default location?
- The entry calls the lazy `FileLoggerProvider` file creation "worth folding in while there". Is it in scope for this task or its own item?
- A completion invocation should produce no audit or log line, which the entry says should be settled once for both this and task-002. Which task owns that decision?
- The log is unrotated, which the redaction switch does not address. Does retention belong to this task, or is it its own item?

## Notes

`docs/Plan.md` carried the same problem as an open item until that file was retired. Its wording, which adds that the log is unrotated and states that the decision is the operator's and unmade:

> **`BearerGrantMiddleware` logs every call's full arguments.** `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:131` writes `args={Args}` to an unrotated log on every request. With `edit` and `create` shipped, that means note content and frontmatter operations land in `logs/`. This is an operator decision that has not been made, and it is not blocked by anything above.

The entry as it stood in `docs/Sidequests.md`, verbatim:

> ## The audit surface: a content-redaction flag, and a CLI audit log
>
> Approved: 2026-08-03.
>
> Every server call's full arguments land verbatim in `logs/` - since `edit` and
> `create` shipped, that includes note bodies and frontmatter operations - and the
> CLI writes no audit line at all, so a `vault.exe` mutation leaves no record
> anywhere except the working tree. Chattiness and off already exist through the
> standard `Logging` section; what does not exist is a way to keep the audit trail
> without the content, or any trail at all on the second front-end. The cost of
> leaving it: logs unsuitable for sharing or long retention while writes flow
> through them, and an audit answer of "nothing happened" for CLI mutations.
>
> The fix is an audit-line composer in `VaultShared` - tool name, grant id, bound
> arguments, outcome code, with a redaction switch that drops content-carrying
> values and keeps identity-shaped ones, driven by the declaration's `Mutates`
> flag - consumed by `BearerGrantMiddleware` behind a config flag defaulting to
> today's behaviour, and by an opt-in `AuditLogPath` in `vault.settings.json`.
> Less obvious than it looks: the e2e log-hygiene probes pin parts of the current
> audit format and assert lines are present under the shipped config, so the
> shipped defaults must not change; a CLI log write failure must not fail the
> tool call it records; and `FileLoggerProvider` creates its timestamped file
> unconditionally at startup, so "logging off" still litters `logs/` with empty
> files unless creation goes lazy - worth folding in while there.
