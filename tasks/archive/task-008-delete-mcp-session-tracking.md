# task-008: Delete the MCP dispatcher's session tracking and its tools-list-changed broadcast

**Goal:** `ScriptToolDispatcher` no longer retains an `McpServer` per request, and the notification mechanism that could never deliver is gone rather than pruned.

## Context

A finding from the full code review (plan step 1), surviving an independent refutation pass, followed by an investigation into why the collection exists at all. This touches retained code — the script-loading framework — so it is a proposal rather than an ordinary change; the operator directed it be filed with deletion as the recommendation.

**What is there.** `src/VaultMcpServer/Tools/ScriptToolDispatcher.cs:60` holds `private readonly ConcurrentDictionary<McpServer, byte> _sessions = new()`. `TrackSession` (line 404) writes to it from `ListToolsAsync` (line 122) and `CallToolAsync` (line 147), both registered MCP handlers, so it runs on every `tools/list` and every `tools/call`. The only reads are in `NotifyToolsListChangedAsync` (lines 418, 429), whose one production caller is `ScriptToolsWatcher.NotifyAsync` (`ScriptToolsWatcher.cs:92`), reached only from `OnDebounceElapsed` behind a `FileSystemWatcher` filtered to `*.ps1`. The tools directory ships holding only `.gitkeep`, so no event can fire and the only removal path is unreachable. Retention is unbounded and proportional to request count, and nothing in health state or the logs reports it.

**Why deletion rather than pruning.** The broadcast cannot work under this configuration. `HttpServerTransportOptions.Stateless` is `true` (`src/VaultMcpServer/Program.cs:173`), and the SDK's own documentation states that in stateless mode unsolicited server-to-client messages are unsupported, `StreamableHttpServerTransport.SendMessageAsync` throws `InvalidOperationException` when `Stateless` is `true`, and the GET that would open the standalone SSE stream is refused in the same mode. So the send at line 422 could never deliver a notification to anything. Pruning correctly would tidy a broadcast that cannot broadcast.

**What the investigation ruled out.** The collection has never held anything but a set of server handles: the declaration line is byte-identical across all 140 commits, with `byte` as a dummy value and no room for a hash, a path, or read state. A hypothesis that it once supported server-side content-hash tracking across a read-then-write pair is not borne out — `git log -S` over `ContentHash`, `ReadState`, `LastRead` and `SessionState` finds nothing in the front-end, and the content-hash work landed entirely in `VaultShared` as caller-carried state, per `docs/spec/Spec-Tool-Capabilities.md:Staleness And Chaining`. There was also never a correct era: `Stateless = true` is in the root commit and has never been modified. History was restarted, so this rules the hypothesis out for this repository only; a pre-restart ancestor is out of reach by design and was not pursued.

**Precisely what is dead.** `TrackSession` and the writes are live code with no beneficiary — they execute on every request. The reader is live code with a dead consumer whose body is additionally inert. The collection is not unreachable, which is why this is a leak rather than dead weight.

One consequence worth knowing before deleting: if the watcher ever did fire, every send would throw and the surrounding catch would `TryRemove` the entry, so the set would drain itself on the first real event. That is not a defence — the event cannot occur — but it means accumulation is the whole of the harm.

## Acceptance criteria

- [x] `_sessions`, `TrackSession`, its two call sites, `NotifyCount`, and `NotifyToolsListChangedAsync` are removed from `ScriptToolDispatcher`, along with the `NotifyAsync` wrapper and the notify paragraph in `ScriptToolsWatcher`. Roughly 40 lines across the two files, plus the usings that fall out.
- [x] `ScriptToolsWatcher` keeps doing its actual job — refreshing `ScriptToolRegistry` and reporting health. Only the notification limb goes.
- [x] `tests/VaultMcpServer.Tests/ScriptToolsWatcherTests.cs` is updated: it would otherwise fail to compile, since two assertion lines read `NotifyCount` (`:97`, `:175`) and the dispatcher is threaded through `MakeRegistryAndDispatcher`. The other six tests cover registry refresh, debounce, a missing directory, health transitions and dispose, and must pass unchanged.
- [x] No test is written to replace the two removed assertions. They asserted a counter no caller can observe, against a push the file's own remarks concede there is nothing to receive — `conventions/Unit-Tests.md` does not admit that as a behaviour.
- [x] Any text in `docs/design/Design-Front-End-Projection.md` describing the dispatcher's session handling or the tools-list-changed notification is rewritten in the same commit, or confirmed not to mention it. No spec covers this subject.
- [x] The commit message carries why the mechanism was deleted rather than fixed — that it cannot deliver under a stateless transport — since that is the fact a future reader will otherwise re-derive from the SDK docs.
- [x] `dotnet build --no-incremental` clean, `dotnet test` green.

## Notes

Reinstating a working push later, if script tools are ever shipped, is unaffected by this deletion. It would require a transport decision — either `Stateless = false`, which now runs against the SDK's own post-SEP-2567 default and needs session affinity behind any load balancer, or accepting that clients rediscover a changed tool set by re-issuing `tools/list`. Re-typing a concurrent set and a broadcast loop is not the expensive part of that.

The three deferred tools (`lint`, `lint_suppressions`, `sync`) are compiled declared tools per `docs/Brief-Vault-Access-Build.md`, not `.ps1` files dropped into `tools/`, so picking them up would not revive the watcher on its own.

### Implementation notes

**The watcher's `dispatcher` constructor parameter went too.** With `NotifyAsync` gone the field had no reader, and the parameter could not be documented truthfully — `CS1573` requires a `<param>` for it, and the only honest one would say it is unused. Removing it is the tail of the notification limb rather than a separate change. It reaches `src/VaultMcpServer/Program.cs:197`, which now constructs `new ScriptToolsWatcher(toolsDir, scriptToolRegistry, logger)`. The dispatcher itself is unaffected: it is still constructed at `Program.cs:167` and still supplies both MCP handlers at `Program.cs:186-187`.

**The test file lost its `RunnerFixture` and its `IClassFixture`.** `_runner`, `ToolRegistry`, `ToolExecution`, `HttpContextAccessor` and `VaultConfig` existed in `MakeRegistryAndDispatcher` only to construct the dispatcher the watcher no longer takes. The helper is now `MakeRegistry`, returning the registry and the health state. The `using Microsoft.AspNetCore.Http;` and `using VaultShared.Tools;` lines fell out with them. Nothing else in the class used the shared `ScriptToolRunner`, so no test now starts a runspace pool — the class is cheaper than it was.

**`AddingScript_RefreshesRegistryAndNotifies` was renamed to `AddingScript_RefreshesRegistry`.** `conventions/Code-Comments.md` says a test method name is documentation and is not rewritten; it also says a comment describing a world that no longer exists is rewritten in the same commit. The second governs here, because the `AndNotifies` half named the assertion this task deletes. This is the only rename.

**`ScriptToolDispatcher._logger` and `AttachLogger` went too, in a follow-up.** The notify catch was the logger's only reader, so deleting the notification limb left a field written from `Program.cs` and read nowhere. No build warning fires on a write-only private field, so nothing would have forced the issue later. Removed with them: the `AttachLogger` call in `Program.cs`, and the two `Microsoft.Extensions.Logging` usings, which nothing else in the file needed. The dispatcher now takes no logger at all — a future need for one re-adds the field, and the comment about attaching it after `builder.Build()` goes back with it, since that constraint is still true of the type.

**Verified nothing else referenced the mechanism.** A repository-wide grep for `list_changed`, `ListChanged`, `session tracking`, `_sessions` and `TrackSession` finds only this task file and the design paragraph after the edit. No document under `docs/spec/` mentions either subject.

**`docs/design/Design-Front-End-Projection.md` carried it in two places.** `What Moves And What Stays` listed session tracking among what the dispatcher keeps; that line now names only `tools/list`, `tools/call` routing and the script-tool branch, with a `Deleted:` paragraph beside it. A `What This Closes` bullet carries the rule and the rejected alternative, per `docs/design/README.md`.

**Suite unchanged at 1560.** No test method was removed, only the two `NotifyCount` assertions: 1203 VaultShared, 298 VaultMcpServer, 59 VaultCli.
