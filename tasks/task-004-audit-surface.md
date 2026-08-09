# task-004: The audit surface - outcome recording, a content-redaction flag, and a CLI audit log

**Goal:** Every dispatched tool call leaves an audit record carrying its outcome, the trail can be kept without note content, and a CLI mutation leaves a record.

## Context

Carried over from `docs/Sidequests.md`, which was retired. The original entry is reproduced in full under Notes; it states what is wrong now, what it costs while it stays, what the fix touches, and what is less obvious than it looks. `docs/Plan.md` tracked the same middleware separately until it too was retired, and its wording is reproduced there beside the original.

[task-013](archive/task-013-refusal-outcomes-in-the-audit-trail.md) was folded into this task on 2026-08-09 and archived. It asked whether a refused call should be recorded server-side. The rest of this section is what it established.

**The trail is for reconstruction** (operator determination, 2026-08-09). It answers what an agent did to the vault, not what a caller was denied. That framing was chosen over security review and over server debugging, and it decides the shape of the record: a refused call changed nothing and its caller already holds the coded reason, so a refusal is not itself the thing worth recording.

**What follows from it is a terminal line per dispatched call, carrying the outcome code.** The gap reconstruction actually has is not the refusal - it is that an arrival line for an `edit` says a mutation was asked for and never says whether it landed. A terminal line closes that, and records refusals as a consequence rather than as its purpose. Recording only refusals is rejected outright: it produces a trail where the absence of a line means both "succeeded" and "never finished", and a call that died mid-dispatch becomes indistinguishable from one that was denied.

**What exists now.** `BearerGrantMiddleware` writes one arrival line per request at `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:127`, carrying the grant id, the JSON-RPC method, the tool, the vault named, and the full arguments, before the call is dispatched. A second, narrower line at `:116` records a non-loopback authentication failure with `result=rejected-non-loopback`. Those are the only two audit sites in the solution. The CLI writes no trail at all.

**The middleware cannot write the terminal line.** A tool refusal is not an HTTP-level event: it becomes a `CallToolResult` with `isError` set, via `src/VaultMcpServer/Tools/ToolError.cs:43`, carried inside a 200 response. The middleware awaits `next(context)` and sees only the status code, so reaching the outcome would mean buffering and re-parsing the response body - wrong for a streaming transport, and a second parse of something the layer below already knows.

**Where it can be written.** Each front-end has one dispatch chokepoint that already holds the outcome code:

- MCP - `src/VaultMcpServer/Tools/ScriptToolDispatcher.cs`, which dispatches native and script tools alike. Six return sites: `:137` unknown tool, `:145` floor denial, `:160`, `:189`, `:216` no grant, and `:271` for a refusal translated out of `Render`.
- CLI - `src/VaultCli/CliRendering.cs:63-70`, the same shape.

`ToolRefusal` is an exception, and `ToolRefusalEnvelope.Refusal` at `src/VaultShared/Tools/ToolRefusalEnvelope.cs:70` is already the single site deciding what counts as a refusal for both front-ends. That is the seam the composer belongs beside, and it is why this work and the redaction flag are one piece rather than two: both need one composer in `VaultShared` consumed by both front-ends, which is what the original entry below already proposed.

**Where the determination gets written down.** In a comment on the composer, and nowhere else. The composer will be the one place audit lines are made, so it is where a reader asking what the trail is for actually lands. No document states it - see `docs/conventions/Documentation-Strategy.md`. How the determination was reached is the commit message.

**Do not change the shipped log format without checking `e2e-smoke.ps1`.** Its log-hygiene probe at `e2e-smoke.ps1:428` is narrower than it looks: it asserts that `Audit: grant=` appears somewhere in the newest log, and that no token value ever appears. Appending a terminal line breaks neither. Changing the arrival line's prefix, or dropping `args=`, breaks the first.

## Acceptance criteria

Incomplete. The redaction and CLI-log criteria depend on the open questions below and are not yet written. What the determination already fixes:

- [ ] Every dispatched tool call on the MCP front-end leaves one terminal audit record carrying its outcome code.
- [ ] A successful call and a refused call are each represented by a line. Neither is represented by the absence of one.
- [ ] The composer states the trail's purpose and the terminal-line rule in a comment that stands on its own, naming what changing it breaks.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Open questions

- The entry says the config flag defaults to today's behaviour, because the e2e log-hygiene probes pin the current format under the shipped config. Is unredacted-by-default the intended end state, or the interim while the probes are updated?
- Where does `AuditLogPath` point when opted in, and what happens when it is unset - no CLI trail at all, or a default location?
- The entry calls the lazy `FileLoggerProvider` file creation "worth folding in while there". Is it in scope for this task or its own item?
- A completion invocation should produce no audit or log line, which the entry says should be settled once for both this and task-002. Which task owns that decision?
- The log is unrotated, which the redaction switch does not address. Does retention belong to this task, or is it its own item?
- **Does the arrival line stay?** The arrival line is written by the middleware before dispatch and the terminal line at the chokepoint after it, so they cannot be merged - the shipped trail becomes two lines per tool call. The arrival line also covers JSON-RPC methods that are not `tools/call`, which the chokepoint never sees, so dropping it loses more than it saves.
- **Does the CLI get a terminal line for every dispatched call, or only for a mutation?** Reconstruction wants every call on the server, where a caller may be an agent the operator is not watching. The CLI's caller is the operator, and its trail does not exist yet, so the answer is not forced to match.

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

The original entry named the composer as consumed by `BearerGrantMiddleware`. That holds for the arrival line and the redaction switch, which are middleware-side. It does not hold for the outcome, which the middleware cannot see - see Context.
