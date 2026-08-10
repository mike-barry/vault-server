# Front-End Projection

> **Archived 2026-08-09. Not to be modified.** This document is a historical record of the vault access layer redesign. It describes what was specified and designed, not what the code does now. Where it and the code disagree, the code is right.

## What This Answers

- [[Spec-Vault-Access-Design.md#The Front-End Boundary]] - one declaration projected into two front-ends, met by [[#The Declaration]] and [[#The Command Tree]]; results and errors transport-neutral, met by [[#Rendering]]; scope arriving as an explicit parameter, met by [[#What Each Front-End Supplies]]; the CLI's named grant, met by [[#The CLI's Grant]].
- [[Spec-Vault-Access-Design.md#Guarantees To Callers]] - the mutex lock wrapping every mutating invocation and no tool acquiring it, met by [[#The Lock]]; a crashed writer not wedging the vault, met by [[#The Lock]].
- [[Spec-Access-Layer-Capabilities.md#The Mutex Lock]] - the synchronous single-threaded hold and nothing below acquiring, met by [[#The Lock]] and [[#The Tool Surface]].
- [[Spec-Auth-Requirements.md#What The Layer Must Do]] - the floor checked before the operation runs, met by [[#The Shared Execute Path]].
- [[Spec-Auth-Requirements.md#Both Front-Ends Are Granted]] - every invocation granted with no authority-free context, and configuration that will not load failing the CLI, met by [[#The CLI's Grant]]. Elevation at the CLI is the operator's choice of grant, met by [[#The CLI's Grant]].
- [[Spec-Tool-Capabilities.md#The Argument Surface]] - no front-end-specific argument handling, and the CLI's two spellings generated from one declared name, met by [[#The Command Tree]].
- [[Spec-Tool-Capabilities.md#Authority]] - the declared floor as a property of the declaration, met by [[#The Declaration]] and [[#The Shared Execute Path]].
- [[Design-Tool-Vocabulary.md#The Neutral Result And Refusal]] - where the neutral result and refusal convert to a transport, met by [[#Rendering]].
- [[Design-Tool-Vocabulary.md#Failure Ordering]] - how the execute path's own steps sit against a tool's five phases, met by [[#The Shared Execute Path]].
- [[Design-Core-Access-Layer.md#Producing An Admitted Path]] - who constructs the per-call access object and when, met by [[#The Shared Execute Path]].
- [[Design-Core-Access-Layer.md#The Lock's Identity]] - the derivation's one consumer, met by [[#The Lock]].

Not answered here, deliberately:

- What a tool does once invoked, and every argument, result and error code: [[Design-Tool-Vocabulary.md]].
- The layer's primitives and the admitted-path types the access object hands out: [[Design-Core-Access-Layer.md]].
- [[Design-Frontmatter-Surface.md#Loading, And Failing Closed]] - how a failed registry load reaches health state. This document routes calls; it does not report on configuration.
- The script-loading framework, MCP host wiring, and HTTP authentication, all retained and unchanged.

## The Declaration

A tool is declared once, as data, on the type that implements it.

```csharp
public sealed record ToolDeclaration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Type Arguments { get; init; }          // the record from Design-Tool-Vocabulary
    public required Floor OpClassFloor { get; init; }
    public bool Mutates { get; init; }                     // takes the mutex lock
    public bool RequiresClientSession { get; init; }       // needs a caller whose authority can be rebound
}

public abstract record Floor
{
    public sealed record Class(string OpClass) : Floor;    // checked before the tool runs
    public sealed record SelfGated             : Floor;    // authority is the tool's own to check
}

public interface ITool
{
    ToolResult Invoke(ToolCall call);
}
```

Each implementing type exposes its declaration as a static member, so the flags sit beside the code that is the reason for them and travel with it. The registry takes declarations rather than instances, so a tool with no declaration has nothing to register.

`Arguments` names the record that is both the binding target and the source of the advertised schema ([[Design-Tool-Vocabulary.md#Argument Records]]). It is the only statement of a tool's argument surface, and both front-ends read it rather than restating it.

**`Floor.SelfGated` is the exemption, and it replaces the retained dispatcher's exemption by tool name.** `health_check` and `request_elevation` are gated by something other than an op class - a right of connection, and a per-grant opt-in - and today the dispatcher knows which two by matching their names. Declared, the exemption is a property of the tool rather than a list a third such tool must be added to. `required` keeps every declaration stating one, so none reaches the exemption by omission.

`RequiresClientSession` is true on `mint_token` and `request_elevation` alone. Both mutate per-process authority state keyed to a live caller's token, so neither has anything to act on in a process that exits when its one command finishes. The CLI's equivalent is choosing a grant ([[#The CLI's Grant]]).

## The Tool Surface

`ITool` replaces the retained `INativeTool`, which cannot cross into `VaultShared` in its present shape: it returns `CallToolResult` and takes a raw `JsonElement` map, and both are the MCP transport reaching into the tool layer.

**The surface is synchronous.** The mutex hold must not cross a suspension point ([[Spec-Access-Layer-Capabilities.md#The Mutex Lock]]), and a `Mutex` is thread-affine, so a mutating invocation runs to completion on the thread that acquired it. Giving the non-mutating tools an asynchronous surface instead would put back the two tool kinds that folding the compiled tools into one declaration removes. The cost falls on `request_elevation` alone, which awaits an approver, takes no lock, and blocks its own invocation for the wait.

`ToolCall` is what the execute path hands in: the bound argument record, the caller's `VaultAccess` where the tool takes a vault, the `Grant`, and the client session where the tool declared it needs one. Nothing on it is a transport, and nothing on it is ambient - a tool reads authority only from what it was handed.

## The Shared Execute Path

One type in `VaultShared` wraps every invocation from either front-end. It runs six steps before the tool's own work begins, and a refusal from an earlier step wins over one from a later.

1. **Session.** A declaration requiring a client session, invoked without one, refuses `tool_unavailable`.
2. **The op-class floor.** `grant.Allows` the declared class, skipped on `Floor.SelfGated`.
3. **Arguments.** Bound onto the declared record, then `Validate()`.
4. **Vault resolution and membership.** For arguments carrying a vault operand: resolve, then check the grant reaches the resolved vault.
5. **The access object.** `VaultAccess` constructed from the resolved vault and the grant.
6. **The lock**, where the declaration says the tool mutates. A tool that does not mutate is invoked without reaching it.

**These six precede a tool's own five phases** ([[Design-Tool-Vocabulary.md#Failure Ordering]]) rather than interleaving with them. That document's first two phases - argument validation and vault resolution - happen here, above every tool, which is what makes its ordering guarantee structural. A tool's first own work is phase 3.

**Authorization precedes argument feedback.** The floor is checked before arguments are bound, so a caller holding no authority over a tool cannot use a malformed call to probe the argument surface of one it may not run. This is the retained dispatcher's ordering, kept.

**Routing precedes authorization within step 4**: an unknown vault answers `unknown_vault` rather than an out-of-scope refusal for a vault nobody configured.

**The vault-membership check reaches compiled tools here.** In the retained dispatcher its predicate is typed on `ScriptTool` and the compiled branch returns before reaching it, so a compiled tool taking a vault argument is unchecked. Whether a call targets a vault is a type question: the record implements `IVaultArguments` ([[Design-Tool-Vocabulary.md#Argument Records]]) or it does not. The compiler carries it, so the check covers every tool taking a vault and no new tool can opt out by accident. Script tools keep matching the parameter by name, since a `param()` block offers nothing to mark, and the dispatcher's script branch is unchanged. An approved change to retained code.

**A tool's declared floor is not its whole authority.** Anything more is asserted inside the tool on the path that needs it, through the access object ([[Design-Core-Access-Layer.md#Producing An Admitted Path]]), because the required class depends on what the work turned out to touch.

## The Lock

The execute path is the only thing in the build that acquires the mutex. Its name comes from the resolved `Vault` ([[Design-Core-Access-Layer.md#The Lock's Identity]]), which is why the lock is step 6 and vault resolution is step 4.

The hold spans the whole invocation: acquired before the tool is entered, released after it answers or raises. A plan takes it exactly as an apply does, so a preview cannot return a prediction that was already stale when it was computed ([[Spec-Tool-Capabilities.md#The Preview Contract]]).

**An acquire that inherits the lock from a dead owner proceeds.** The signal is that the previous holder died without releasing, which is a fact about that process and not about this call - there is no journal to replay and no state to repair, so refusing would wedge the vault exactly as the spec forbids. It is logged and the invocation continues.

**A timeout is a refusal, not an inheritance.** `lock_timeout` is raised at the lock and no tool catches it. The timeout is a constant in the execute path rather than a configured knob: it is a liveness diagnostic, and a tunable one gets tuned upward until it stops reporting the problem it exists to report.

## What Each Front-End Supplies

Both construct what the execute path needs and hand it in; neither carries per-tool code.

| | MCP | CLI |
| --- | --- | --- |
| Grant | `BearerGrantMiddleware`, from the bearer token | a named grant, by id ([[#The CLI's Grant]]) |
| Client session | the caller's token binding | none |
| Tools projected | every declaration, plus script tools | declarations not requiring a session |
| Configuration failure | recorded, server starts degraded | the process fails |

**Script tools are MCP-only, by construction rather than by choice.** The script-loading framework needs `Microsoft.PowerShell.SDK`, which `VaultShared` and the CLI must not reference, so they never enter the CLI's command tree.

## The CLI's Grant

The CLI presents a token-less named grant by id, the same rows `grants.json` already holds and `GrantStore` already resolves. It authenticates nothing: it selects.

**Which id is a setting, not a convention.** The CLI's own configuration names it, and a global `--grant <id>` overrides it for one invocation. This mirrors the server, whose localhost default is `LocalhostAuth:DefaultGrantId` naming a row that is otherwise ordinary - no grant id is special-cased in code on either side, so re-pointing a default is one edit and never a rename. The setting lives in the CLI's own configuration rather than in `vaults.json`, so there is no file in which it could be read as governing the server too.

**The shipped configuration supplies the unrestricted default**, as the server's shipped configuration supplies `admin`. Absence is not unrestricted: a setting naming no id, or naming one `grants.json` does not define, fails the invocation. No path through the CLI turns a missing value into authority.

**The CLI does not use the localhost resolution path.** That path exists never to error, because the server is localhost-only and a floor is safer there than a rejection. The CLI's equivalent condition must error, and reusing the retained member is the shortest route from this fail-closed requirement to a fail-open one.

**Elevation at the CLI is `--grant`.** The operator reruns naming a wider grant, which is the operator approving in the only sense a one-shot process supports, and matches the spec's own reading of this as a guard against an unintended edit rather than a boundary. No approver is constructed at the CLI, because the tool that consults one does not project there.

## Rendering

A tool answers `ToolResult` or raises `ToolRefusal` ([[Design-Tool-Vocabulary.md#The Neutral Result And Refusal]]). Each front-end renders both, and neither shape knows the other exists.

**MCP.** A content result becomes the text content block. A structured result is serialized under the shared options and carried the same way, as the retained compiled tools already do - the wire shape is unchanged and only the naming policy governing it moves. A refusal becomes the `{code, message, data?}` envelope with `isError`, in the wire form the retained `ToolError` fixes: no whitespace, `code` then `message` then `data`, `data` absent rather than null.

**CLI.** A content result prints to stdout as it is. A structured result prints as JSON, because there is no text rendering of a mutation result that is not a second statement of it. A refusal prints the same envelope to stderr.

**Three exit codes.** Zero for success, one for a tool refusal, two for a usage error the parser itself refused. The machine-readable distinction is the `code` in the envelope, which is where the vocabulary already lives.

**`--output json` wraps a content result**, so that standard output is a JSON document whichever tier answered. A structured result already is one and is written as it stands.

## The Command Tree

One subcommand per projected declaration, named for the declared tool name, with one option per member of its argument record. Both are generated at the projection site from the declaration; neither is authored per tool, and the CLI holds no per-tool file.

Each option is presented in kebab-case with the snake_case spelling registered as an alias, both derived from the one declared name ([[Spec-Tool-Capabilities.md#The Argument Surface]]).

**`--args <json>` binds the argument record directly, bypassing the options.** It is not a convenience: `edit` takes an ordered list of operations with nested anchors and per-variant entry payloads, which no flat option surface expresses, and inventing an encoding would be a second statement of the argument contract. It is general rather than `edit`-specific, because a per-tool exception is the per-tool front-end code the boundary forbids. Supplying it alongside option-form arguments is refused.

## What Moves And What Stays

Proposed changes to retained code, for approval with this document.

**Moves to `VaultShared`:** the tool abstraction, as `ITool` and `ToolDeclaration` replacing `INativeTool`; the registry, as a registry of declarations; and the refusal envelope's writer, which both front-ends emit.

**Stays in `VaultMcpServer`:** `ToolError`'s wrapping of that envelope in a `CallToolResult`, which is MCP's alone; the dispatcher, less the two checks below; HTTP authentication; host wiring; and the script-loading framework.

**Rewritten:** the dispatcher's op-class floor check and its vault-membership check both move into the execute path, the second widened to compiled tools. The dispatcher keeps `tools/list`, `tools/call` routing and the script-tool branch.

**Deleted:** the dispatcher's set of per-request `McpServer` handles and the `notifications/tools/list_changed` broadcast the script-tools watcher drove from it. The watcher still refreshes the script-tool registry and records health; a client sees a changed tool set by re-issuing `tools/list`. An approved change to retained code.

**Follows:** the three retained compiled tools return through the neutral result, so their camelCase members convert to snake_case with it - a breaking change for any existing caller of `health_check`, `mint_token` or `request_elevation`, and one `NativeToolSchemaTests` partly pins.

## What This Closes

- **A tool is declared once, as named properties on a static member of the implementing type.** Rejected: attributes read by reflection. The declaration is consumed as data by both projections and by the execute path, and reflection would buy no drift protection for values that are authored either way while costing the compiler's help on a misspelling. Rejected also: a positional record, where a transposed pair of flags compiles.
- **There is one kind of tool.** The three retained compiled tools become declarations rather than staying behind the MCP transport, so no second dispatch path survives and a fourth tool has no side to be added to.
- **The floor is one member with two cases, not a class beside a flag.** A self-gating tool declares `Floor.SelfGated`, so no declaration names an op class that is never checked and no pair of members can disagree. Rejected: a nullable class, which forces the same decision but reads as "no authority needed" at the site where it is written.
- **A tool declares what it needs, not where it may run.** `RequiresClientSession` states why `mint_token` and `request_elevation` do not reach the CLI; a list of front-end names would state only that they do not, and would need editing by every future front-end. Rejected: making the session a typed dependency so a CLI call cannot be constructed - the stronger form, and it requires two invoke signatures, which is the second tool kind this design just removed.
- **The session requirement is enforced at the execute path, not by omitting a subcommand.** The check runs on every route into a tool, and it is the whole of the enforcement in MCP, where both session-requiring tools are implemented and a call arriving without a session refuses `tool_unavailable`. The CLI cannot reference those `VaultMcpServer` types, so it registers neither of them and the command tree filters `RequiresClientSession` declarations besides; `--args` does not reach past that, since it is a recursive option read inside a subcommand's action and a name with no subcommand is a usage error.
- **The tool surface is synchronous.** The mutex hold cannot cross a suspension point and a `Mutex` is thread-affine.
- **The execute path's steps precede a tool's five phases rather than interleaving.**
- **The floor is checked before arguments are bound.** The retained dispatcher's ordering, kept, and it costs nothing since the lock is later than both.
- **Whether a call targets a vault is a type on the argument record.** Rejected: matching a member named `vault`, as the dispatcher does for scripts. It is the only option for a `param()` block, but a compiled tool spelling the operand differently would escape the check silently - the same gap in a new place.
- **The dispatcher tracks no sessions and sends no tools-list-changed notification.** The HTTP transport is stateless, where the SDK refuses an unsolicited server-to-client message, so the push could never arrive while the set of server handles grew by one entry per request. Rejected: pruning the set, which tidies a broadcast that cannot broadcast.
- **An inherited lock proceeds and is logged.** There is nothing to replay and nothing to repair.
- **The lock timeout is a constant, not a setting.**
- **The CLI's grant is a setting naming an ordinary row.** No id is special-cased in code, so a default is re-pointed by one edit rather than by renaming grants.
- **Absence of a CLI grant setting is a failure, not an unrestricted default.** The unrestricted default is supplied by the shipped configuration. Rejected: treating absence as unrestricted, which is a path from a missing value to full authority.
- **The CLI does not reuse the localhost grant resolution.** That member is built never to error; the CLI's equivalent condition must.
- **Elevation at the CLI is the choice of grant.** The retained mechanism rebinds a token in per-process state, which a process that exits after one command cannot carry, and the operator is present to make the choice directly.
- **Three exit codes, with the vocabulary in the envelope.** Rejected: an exit code per error class, a second and coarser copy of the catalogue that cannot represent it and drifts from it.
- **`--args` is general rather than `edit`-specific.** A structured argument has no flat option encoding, and a per-tool exception is per-tool front-end code.
