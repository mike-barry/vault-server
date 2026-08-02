# Vault Access Build Brief

Read this before anything else in the repo. It says what is being built, which of the repo's code is yours to write and which is already correct, and which constraints are deliberate rather than accidental. It does not specify the system - the spec bundle does that, and this brief points into it.

## What Is Being Built

One access layer owning all vault I/O, with containment and scope enforced by construction; a tool layer above it with no hosting dependencies and a transport-neutral result and error representation; and two front-ends over that tool layer - an MCP server and a CLI - which render the representation differently and are otherwise indistinguishable to a tool.

`Spec-Vault-Access-Design.md` is the primary spec. Read it whole before writing anything. This brief never restates it, and where the two appear to disagree, the spec wins and the disagreement is a defect in this brief worth reporting and getting instruction on how to reconcile the disagreement.

## The Bundle

Seven documents. They are files in this repo, and they are the entire specification.

- **`Spec-Vault-Access-Design.md`** - the primary spec: scope, guarantees to callers, load-bearing semantics, and fixed constraints. Read first, read whole.
- **`Spec-Access-Layer-Capabilities.md`** - what the access layer must offer: vault resolution, enumeration, content search, reading, writing, moving, the mutex lock, and wikilink grammar and resolution.
- **`Spec-Tool-Capabilities.md`** - what each tool must accomplish and which of its behaviors are load-bearing. The input to the tool-vocabulary design phase, and the heaviest document in the set.
- **`Spec-Frontmatter-Substrate.md`** - the frontmatter block grammar, the facet split, the preservation invariants, the operation vocabulary, and the single write path. Knows no vault vocabulary at all.
- **`Spec-Frontmatter-Registry.md`** - the declaration that supplies that vocabulary: recognized keys with shape, value constraint, and required write authority; protected namespaces; entry schemas; and path policy.
- **`Spec-Auth-Requirements.md`** - the obligations across the seam with the authorization model. Mostly pointer, because its subject is retained code.
- **`Spec-Obsidian-Link-Behavior.md`** - how the editor actually resolves links. Empirical, and a requirement: the vault's existing content depends on which file a bare wikilink opens, so wikilink handling matches this rather than deriving a rule of its own.

**Which document wins.** Each spec names what it owns in its own boundary section, and on a subject it owns, that spec wins. The companions are more specific than the design spec, not subordinate to it. `Spec-Vault-Access-Design.md` governs what no companion owns: the scope of the build, the guarantees to callers, and the fixed constraints. This brief never wins over any of them.

Two specs contradicting each other on the same subject is a defect in the bundle, not a choice to make locally. Raise it.

## What Is Retained And Readable

Most of this repo carries forward from the previous implementation and is **meant to be read directly**. It works, it is the authority on itself, and no spec describes it:

- Authentication and authorization - grants, tokens, bindings, minting, elevation, the op-class registry
- The scope primitives - segment normalization, containment, per-vault root resolution
- Health state, logging, configuration loading, and MCP host wiring
- MCP registration, argument validation, and the vault-membership check. The compiled-tool surface is what the new tools plug into: one record per tool is both the binding target and the source of its advertised schema, and the dispatcher already lists, floor-checks, and dispatches compiled tools alongside script ones
- The script-loading framework - discovery, the runspace runner, the script dispatcher, and the folder watcher
- The `test-vault/` fixture, and the tests belonging to all of the above

**Do not reimplement any of it, and do not refactor it to taste.** Call it.

Retained code is not frozen, though. Where fitting the new code around a retained component would mean writing something convoluted, and a change to that component would avoid it, propose the change and get the operator's approval before making it. Contorting new code to preserve an existing shape is not the goal.

Where a retained component appears to be wrong, raise it rather than fixing it - a misread requirement is the likelier explanation.

## What Was Deleted

The PowerShell access module and the PowerShell tool scripts, along with their test suites. Those are the subject of the spec bundle: everything they knew that still matters is in the specs, and everything they knew that does not is deliberately gone.

**Git history was restarted, so there is nothing to recover.** That is the point rather than an oversight. A specification that can be checked against the code it replaces is advisory, and the whole design of this build assumes it is not.

## Constraints Not To Infer

Each of these is a decision already made. None is an oversight to correct, and none should be re-derived.

- **This repo has no access to the knowledge vault, by design.** The as-built documentation of the system being replaced is deliberately out of reach, because grounding a redesign in documentation of the old shape is how the old shape survives a rewrite. Do not attempt to reach it.
- **Gaps escalate rather than resolve locally.** On a question the bundle cannot answer, raise it with the operator and stop that thread. Do not fill it from judgment and carry on. An agreed escalation goes to `spec/Questions.md` per the section below; a question the bundle cannot answer is itself the finding, and the answer returns as an amended spec.
- **The empty tool folder is intentional.** The script-loading framework is retained as a capability with no scripts to load. The folder and its `.gitkeep` stay: the watcher only attaches to a directory that exists, and it decides that once at construction.
- **The external dependencies are chosen.** Ripgrep for search and enumeration, YamlDotNet for frontmatter, `System.CommandLine` for the CLI's command tree, and CliWrap for invoking ripgrep. Do not evaluate alternatives to any of them. Ripgrep and YamlDotNet are fixed constraints in the design spec and were settled by measurement; the other two are settled build decisions
- **Junctions, symbolic links, and reparse points are not accommodated.** The layer assumes a plain directory tree and neither detects nor handles them.
- **Three tools are out of the first pass**: `lint`, `lint_suppressions`, and `sync`. Do not build them, and do not design the surface around them.

## Where Code Goes

Four projects. `VaultShared`, `VaultMcpServer`, and `VaultWatcher` exist; `VaultCli` is new.

- **`VaultShared`** - the access layer, the tool layer, and everything a tool call needs. A plain `Microsoft.NET.Sdk` library.
- **`VaultMcpServer`** - the MCP front-end. Host wiring, HTTP auth, MCP registration and schema projection, and the script-loading framework.
- **`VaultCli`** - the CLI front-end. Compiles to `vault.exe`: set `AssemblyName` to `vault`, and leave `UseAppHost` at its default, since the server sets it false and the CLI needs an executable.
- **`VaultWatcher`** - the third consumer of shared, and the reason shared is not just "what the CLI also needs". The design spec requires it to derive the mutex lock's identity from the same code, never a re-implemented convention. **It stays a stub and nothing in it is yours to write.** What it constrains is where the derivation lands: in shared, reachable by a consumer that is not a front-end, so the watcher takes it as code when it is picked up.

**The line: everything a tool call needs is shared; a front-end project holds only what makes its transport work.** The test is whether the CLI would have to duplicate it.

**Retained code migrating into `VaultShared` is expected, not a refactor to propose.** The scope primitives, vault resolution, the op-class registry, the grant model, and the compiled-tool surface all move. What stays behind is what binds to a transport: HTTP auth middleware, host wiring, MCP registration, and the script-loading framework. These are moves, not redesigns - the code arrives unchanged, and altering its behavior on the way is a separate proposal under [What Is Retained And Readable](#what-is-retained-and-readable).

Two boundaries that hold hard:

- **`VaultShared` references no web framework.** Its SDK choice already makes hosting types unavailable, which is what turns the design spec's "no hosting dependencies in the layer or the tools" from a rule someone must remember into a compile error.
- **`VaultShared` references no `Microsoft.PowerShell.SDK`.** Measured: adding that reference to a console app takes its framework-dependent publish output from 175 KB across 5 files to 67 MB across 433, whether or not a single PowerShell type is ever touched. Startup is unaffected, since assemblies load lazily - the cost is entirely distribution size. The CLI runs no scripts and must not carry the runspace host.

Two new test projects join the existing `VaultMcpServer.Tests`:

- **`VaultShared.Tests`** - the conformance suite, which asserts the substrate against a synthetic declaration and must not need a web host to run. It also owns the **front-end parity tests**: that one tool declaration projects into both front-ends with the same semantics is a claim about both, so it belongs to neither front-end's own suite.
- **`VaultCli.Tests`** - the command tree built from the registry, option strings parsed into arguments, the raw-JSON escape hatch, the output flag, and exit codes. Most importantly the fail-closed path: configuration that will not load fails the CLI rather than admitting it unrestricted ([Spec-Auth-Requirements](Spec-Auth-Requirements.md)). An untested fail-closed path is one that quietly becomes fail-open.

## Build Order

The order matters, and it is not arbitrary.

1. **The core layer, resolved-path type first.** Every later choice depends on whether an out-of-scope path is representable at all, so this settles before anything is built on top of it.
2. **The tool vocabulary, designed as one coherent surface.** Its own phase rather than a by-product of implementing the tools. Argument shapes, result tiers, error vocabulary, and the preview and write-class conventions are settled together or they will not be coherent.
3. **The tool layer** over the access layer, with the transport-neutral result and error representation.
4. **The two front-ends**, which should be thin enough to be uninteresting by the time they are written. A tool is declared once and projected into both; neither front-end carries per-tool code.

The specs state what must be true, not how to build it, so the early architectural work makes those calls. Each one is recorded as it lands, in `spec/Decisions.md`.

## Working Files

Two files in `spec/` are written during the build rather than read from it. They have opposite lifetimes: one accumulates permanently, the other is worked down to empty.

**`spec/Decisions.md`** is permanent, and holds the architectural decisions this build makes, one entry each, newest last:

```
## <The decision, as a claim>

Date, and the phase it landed in.
What was rejected, and why it lost.
Which spec section it answers, where one applies.
```

Record a decision when a fork closes - something was rejected. A choice with no alternative is a design fact and belongs in the code and its comments, not here. Nothing outside this repo is watching the build, so an unrecorded decision is one nobody can review.

**`spec/Questions.md`** is the escalation channel, and it is two-way. Its normal state is empty.

**A question reaches this file only after the operator has agreed it should.** Raise the gap in the session first and say what is blocked; the operator decides whether it is a genuine hole in the bundle or something answerable from what is already in the repo. Only an agreed escalation gets written. This file is not a place to park anything uncertain and move on - a question written here stops a thread of work and costs someone a round trip, so the bar is that the build genuinely cannot proceed correctly without an answer.

```
## <The question>

Asked: date, and what was being built at the time.
Blocked: what stopped, and what can continue meanwhile.
What the bundle says, and why it does not settle this.
What the build would do under each plausible reading, and what it costs to be wrong.

Answer: filled in by the responder, naming the spec file amended.
```

**Clear the entry once it is consumed.** When the answer has been read, the specs have been updated, and the code reflects it, delete the entry. It has no further job: the amended spec is the requirement, the code is the implementation, and a decision worth keeping goes to `spec/Decisions.md`. Leaving answered questions to accumulate turns the file into a second source of truth that quietly competes with the specs, and makes the open ones harder to find among them. The exchange stays in git history either way.

## Testing

**Tests are built alongside the functionality they cover**, split into files by logical area rather than accumulating in one. Coverage derives from the specs - the guarantees, the load-bearing semantics, the capability documents - and there is nothing else to derive it from.

A separate exercise runs later, outside this repo, harvesting behavioral cases from the deleted implementation and reconciling them against the committed suite. **That is not this session's work and must not be anticipated.** Writing toward what the old tests might have checked is the same failure as reading the old code. Build the suite the specs justify; the reconciliation will find what the specs failed to carry, which is the information it exists to produce.
