# task-004: Standardize logging - two records, JSON lines, content-free, rotated

**Goal:** Logging is one designed surface rather than an accident: an MCP request record and a shared tool-call record, both written as JSON lines to rotated, retained files that can never contain note content or a secret.

## Context

### What logging is for

Three purposes, and the reason this task exists is that they have opposite requirements on every axis that matters:

| | Can it be switched off? | How long is it kept? | May it contain vault content or secrets? |
| --- | --- | --- | --- |
| **Security** - who did what | Only deliberately, and the fact is recorded | 90 days | Never |
| **Diagnostics** - is the server healthy | Yes | Short | Doesn't need to |
| **Troubleshooting** - why did *this call* misbehave | Yes | Until it's fixed | Would tolerate it |

Everything today lands in one unrotated file, in one format, under one `Logging` section. The record that can never be reconstructed after the fact is mixed into the noise that can be discarded, and none of it is bounded. That is the defect, and it is larger than the one this task originally named.

The operator has not used the logs during development. That is the prioritization argument, not a reason to skip the work: diagnostics and troubleshooting logs can be added the day they are wanted, because the need arrives with someone at the keyboard who can reproduce. The security record is the only one whose absence at the moment of the event is permanent.

### What exists now

- `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:127` - one line per request, written *before* dispatch, carrying grant id, JSON-RPC method, tool, vault, and the full arguments verbatim. Since `edit` and `create` shipped, that means note bodies and frontmatter operations.
- `src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:116` - a narrower line for a non-loopback authentication failure, `result=rejected-non-loopback`.
- Those are the only two audit sites in the solution. The CLI writes no trail at all.
- `src/VaultMcpServer/Logging/FileLoggerProvider.cs:32` - one file per process, named for process start, opened `FileMode.Create` with `FileShare.Read`. No rotation, no retention.

### The two records

**A request record.** MCP only. Every JSON-RPC request and its result. Inherently transport-shaped, and it covers `initialize` and `tools/list` as well as `tools/call`. The CLI has no equivalent because it has no requests.

**A tool-call record.** Shared, and identical on both front-ends: tool, grant, front-end, argument *shape*, outcome. One composer in `VaultShared`, consumed by both.

This split replaces the framing the previous version of this task was built on. It also dissolves that version's central question - whether the middleware's pre-dispatch line survives alongside a post-dispatch one. They were never two halves of one call. They are records in two different logs answering two different questions, and the fact that the CLI can only ever have one of them is the correct shape rather than an inconsistency.

The middleware still cannot write a tool-call outcome. A tool refusal is not an HTTP-level event: it becomes a `CallToolResult` with `isError` set via `src/VaultMcpServer/Tools/ToolError.cs:43`, inside a 200 response. The middleware awaits `next(context)` and sees only the status code.

The tool-call record is written at each front-end's dispatch chokepoint, which already holds the outcome:

- MCP - `src/VaultMcpServer/Tools/ScriptToolDispatcher.cs`. Six return sites: `:137` unknown tool, `:145` floor denial, `:160`, `:189`, `:216` no grant, and `:271` for a refusal translated out of `Render`.
- CLI - `src/VaultCli/CliRendering.cs:63-70`, the same shape.

Three of the MCP sites have no declaration or no bound arguments. `ToolExecution.Invoke` checks the session, then the op-class floor, then binds arguments, then resolves the vault - the floor is deliberately before binding so a caller with no authority cannot use a malformed call to probe the argument surface of a tool it may not run. **Do not reorder it.** The consequence: at `:137` there is no declaration at all, and at `:145` and `:216` nothing has been bound. Those sites still produce a record; it carries the tool name as the caller spelled it, the outcome code, and no argument section.

Not every refusal is thrown. `ToolExecution.FloorRefusal` is a static that *returns* `ToolRefusal?` (null means allowed) and `ScriptToolDispatcher` uses it directly at `:145`; other refusals are thrown and caught in `Render` at `:271`. The composer sees both shapes and cannot assume an exception.

### Never content, and never by flag

The composer has no code path that can emit note content or a secret. Not a default - an absence.

The previous version of this task proposed a redaction *switch* defaulting to today's behaviour. That is rejected. A switch means the log's safety properties hold only by configuration: someone enables content to debug something, forgets, and an unrotated file accumulates note bodies. This system enforces containment by construction everywhere else, and a redaction flag is the thing that premise rejects.

Troubleshooting is served better by shape than by content, and shape is safe to keep:

- `edit` - how many matches the find pattern hit, the length of find and replace, the line range touched. "Matched 0 times" or "matched 47 times" is what diagnoses a bad edit; the replacement prose is not.
- `create` / write - byte count, frontmatter keys set (not values), whether it overwrote.
- reads and searches - result count, not results.

**One carve-out that is not configurable at any level.** `mint_token` returns the raw bearer token as `MintReport.Token` (`src/VaultMcpServer/Tools/MintTokenTool.cs:126`). It is the only raw secret in any tool result in the solution, it is returned exactly once and is never retrievable again, and `request_elevation` has no equivalent - `ElevationReport` (`src/VaultMcpServer/Tools/ElevationRequestTool.cs:138`) carries grant ids and lifetimes only, because elevation rebinds the caller's existing token rather than issuing one. The composer must be structurally incapable of emitting that value. `e2e-smoke.ps1` already asserts no token appears in a log; that assertion should be backed by construction, not only caught by a test.

Refusal payloads are clean today but only by call-site discipline: `ToolRefusalEnvelope.Json` (`src/VaultShared/Tools/ToolRefusalEnvelope.cs:38-54`) serializes `Data` reflectively with no allowlist, so a future refusal site that put a token-bearing object in `Data` would be written verbatim. The composer should not inherit that exposure.

### Identity

The CLI is not anonymous. Every invocation runs under a **named grant** - `--grant`, or `GrantId` in `vault.settings.json` - resolved at `src/VaultCli/CliHost.cs:129`. It fails closed: a missing id is an error and explicitly not an unrestricted default, and choosing a wider grant is how an operator elevates at that front-end.

So the record carries `grant` on both front-ends, meaning the same thing on each: the authority the call ran under. A separate front-end field (`via`) says which front-end. Both are kept even though the two front-ends write to separate files, because a JSON record must be self-describing once it has been pulled out of its file.

The OS username is deliberately not recorded. It is PII-shaped, it is meaningless for single-operator use, and the grant id is the more honest answer to who-did-what.

### Files, rotation, and retention

**MCP and CLI write to separate audit files.** This removes cross-process contention between the server and the CLI entirely, lets the server keep its per-process diagnostic file alongside a date-based audit file, and means a CLI invocation with nothing to log opens no file at all.

- `vault-audit-mcp-YYYYMMDD.log`
- `vault-audit-cli-YYYYMMDD.log`
- the diagnostic log keeps its current per-process `vault-mcp-server-<timestamp>.log` shape

Rotation is **daily, with a size cap as a fuse**: roll on date, and roll early within the day if the file exceeds a cap (`...-20260809-001.log`). Time alone bounds nothing - a runaway agent loop produces one enormous file for today - and size alone makes retention unanswerable, because you never know how far back you can see. Content-free JSON records are a few hundred bytes, so the cap should almost never fire; it is a fuse, not a routine.

Retention is **90 days, auto-delete enabled by default, configurable.** Rotation without deletion only renames the problem.

Two costs to accept knowingly: reconstruction across both front-ends means merging two files by timestamp, and separate files reduce rather than remove concurrency - two `vault.exe` invocations can still overlap, so CLI-to-CLI append remains.

### Switchable, but loud

The tool-call record can be turned off, but a gap in it must be explicable from the record itself rather than only from the configuration. Absence of a line must never mean both "nothing happened" and "recording was off" with no way to tell which.

The output path is configurable, and the test suite uses that: tests point it at a temp location and clean up after themselves, so an always-present record does not require an off switch to keep 1570 tests from writing into `logs/`.

Verbosity within a record stays configurable - shape detail, whether argument names appear, request-level logging on the server. Independent switches per record, not one global level: a single dial would force accepting diagnostic noise to get a complete security record.

### Placement

The composer goes in `VaultShared`, which must never reference a web framework or `Microsoft.PowerShell.SDK`. Front-end parity tests belong in `VaultShared.Tests`, not either front-end's suite - one composer serving both front-ends is a claim about both.

`ToolRefusalEnvelope.Refusal` at `src/VaultShared/Tools/ToolRefusalEnvelope.cs:70` is already the single site deciding what counts as a refusal for both front-ends. That is the seam the composer belongs beside.

### Things that go stale the moment this lands

- `BearerGrantMiddleware`'s `<remarks>` states "One audit line is written per request" (`src/VaultMcpServer/Auth/BearerGrantMiddleware.cs:31`). It is in `src/`, where `CS1573` and the comment standard apply.
- `FileLoggerProvider`'s `<remarks>` states "no rotation beyond one file per process" (`src/VaultMcpServer/Logging/FileLoggerProvider.cs:13`), which stops describing the audit files.
- `e2e-smoke.ps1:428` asserts that `Audit: grant=` appears in the newest log and that no token value ever appears. Moving to JSON lines breaks the first; the probe also needs to know which of the two logs it is reading.

### Where the determination gets written down

In a comment on the composer, and nowhere else. It is the one place audit lines are made, so it is where a reader asking what the trail is for actually lands. The comment cites no document: it states the purpose, the content-free rule, and what changing either breaks. How the determination was reached is the commit message.

## Acceptance criteria

- [ ] Every dispatched tool call on both front-ends leaves one tool-call record carrying its outcome code, including at the three MCP sites with no declaration or no bound arguments.
- [ ] The tool-call record has the same shape and field names from both front-ends, asserted by a parity test in `VaultShared.Tests`.
- [ ] The record carries `grant` and `via` on both front-ends.
- [ ] Every audit line is a single JSON object on one line.
- [ ] No code path in the composer can emit note content, and there is no setting that enables it.
- [ ] `MintReport.Token` cannot be emitted by the composer at any verbosity, asserted by a test.
- [ ] Content-carrying operations are recorded by shape - match counts, lengths, byte counts, frontmatter key names, result counts - not by value.
- [ ] The MCP front-end records every JSON-RPC request and its result, including methods other than `tools/call`.
- [ ] MCP and CLI write to separate audit files; the CLI opens no file when it has nothing to record.
- [ ] Audit files roll daily and roll early on a size cap.
- [ ] Retention deletes audit files older than 90 days, on by default, configurable.
- [ ] The tool-call record can be disabled, and when it is, a gap is explicable from the record itself.
- [ ] The audit output path is configurable; the test suite directs it away from `logs/` and cleans up.
- [ ] A CLI audit write failure does not fail the tool call it records.
- [ ] `BearerGrantMiddleware` and `FileLoggerProvider` remarks no longer state what this task falsifies.
- [ ] `e2e-smoke.ps1` passes against the new format.
- [ ] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

### History

Carried over from `docs/Sidequests.md` (approved 2026-08-03) and tracked in parallel by `docs/Plan.md`; both files were retired. [task-013](archive/task-013-refusal-outcomes-in-the-audit-trail.md) was folded in on 2026-08-09 and archived.

**A superseded determination.** On 2026-08-09 the operator determined that *the trail is for reconstruction* - it answers what an agent did to the vault, not what a caller was denied - chosen over security review and over server debugging. That framing produced this task's previous shape: a terminal line per dispatched call, a redaction switch defaulting to today's behaviour, and seven open questions.

It was withdrawn on 2026-08-09, in the same session, and replaced by the three-purpose frame above. The reason is worth keeping: the open questions it generated were not genuinely open. They were consequences of a framing that had already narrowed the problem to the audit trail's contents, when the actual problem was that logging had never been designed as a whole. The reconstruction framing is not wrong - it survives as the security row of the table - but it is one purpose of three, and treating it as the whole question is what produced questions that could only be answered by guessing.

The original `docs/Sidequests.md` entry, verbatim, for what it still gets right about the mechanics:

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

Of that entry, what survives: the composer in `VaultShared`, the CLI write-failure rule, and the `FileLoggerProvider` observation. What does not: the redaction switch, the `Mutates`-driven redaction, the middleware as the composer's consumer, and "the shipped defaults must not change."

### Proposal awaiting confirmation - what "loud" means mechanically

The requirement is agreed; the mechanism is not. Proposed:

- **Server** - on startup, the audit file always receives a configuration record stating what is being recorded. When the tool-call record is off, that record says so, and the gap that follows is explicable from the file. This works because the server is one long-lived process.
- **CLI** - a startup record per invocation would be noise, since every invocation is a process. Instead, with recording off the CLI writes a one-line warning to standard error on each invocation. It is loud to the operator at the moment rather than in the file, which is the only coherent option when "off" means no file is opened.

### Open questions

- Does the request record share the audit file's rotation and retention, or ride the diagnostic log? It is arguably security rather than diagnostics - who connected and when - which would put it in the audit file, but it is also far chattier.
- Is the lazy `FileLoggerProvider` file creation in scope here or its own item? The CLI side of the problem is solved by separate files; the server's diagnostic file is still created unconditionally at startup.
- Concurrent append from overlapping `vault.exe` invocations: retry on a sharing violation, or the cross-process mutex convention `VaultWatcher` already derives from shared code? Retry is far simpler and the contention window is milliseconds, but the append semantics are a cross-platform claim and `docs/conventions/Cross-Platform.md` must be loaded before deciding.
- What the verbosity settings are, concretely, per record.

### Deferred

A completion invocation should produce no audit line. Nothing to decide until shell completion exists; [task-002](task-002-cli-shell-completion.md) is in Sidequests and may never happen, so this cannot wait on it.
