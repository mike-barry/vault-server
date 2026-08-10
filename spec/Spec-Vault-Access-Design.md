# Vault Access Design

Forward specification for the vault access layer and the tool layer above it: what they own, what they guarantee, which semantics are load-bearing and why, and which decisions the implementation must make explicitly. Written to be built from directly - it describes what must be true, not what any prior implementation did.

Target: C# throughout, with two front-ends over one core - the MCP server and a CLI application that invokes each tool directly.

## Scope Of The Build

Under design here: the vault access layer, the tool layer, and the front-end boundary between them.

Not under design, and taken as existing requirements the new layer must satisfy: the grant and op-class model, health and degraded-state reporting, logging, configuration loading, and MCP host wiring. The layer consumes the grant model's scope restrictions; it does not define them.

### Companion Inputs

This doc is not self-sufficient on facts about the world. It travels with:

- **[[Obsidian-Link-Behavior]]** - the editor's bare-link resolution, an external behavior to match
- **[[Spec-Frontmatter-Substrate]]** and **[[Spec-Frontmatter-Registry]]** - the frontmatter write surface and its authorization layer, not restated here
- **[[Spec-Auth-Requirements]]** - the obligations across the seam with the retained authorization code, which is itself read from source rather than specified
- **[[Spec-Access-Layer-Capabilities]]** - the operations the layer must offer, and the requirements attached to each
- **[[Spec-Tool-Capabilities]]** - what each tool must accomplish and which of its behaviors are load-bearing. Capabilities and semantics only: the tool vocabulary itself is designed in this build, not inherited

Where a companion doc describes a mechanism rather than a requirement, take the requirement and discard the mechanism.

## The Thesis

**All vault I/O flows through one layer, and nothing outside it touches a vault path.**

A scope restriction is meaningful only if there is no path around it, a lock serializes only if every writer takes it, and a path is contained only if no caller can construct one the layer never saw.

The cost: any vault-touching operation a tool needs must live in the layer or be built from primitives it hands out. There is no third option where a tool does a quick read of its own - the moment one exists, everything below becomes a claim about most callers rather than a property of the system.

## What The Layer Owns

Inside: vault resolution; path resolution and containment; scope enforcement; enumeration and content search; note read, write, and move; mutation serialization; frontmatter mutation and validation ([[Spec-Frontmatter-Substrate]]); wikilink grammar and bare-link resolution; read-failure classification; and the result and error vocabulary tools return through.

Outside, deliberately:

- **Op-class floor enforcement** - a tool's minimum authority is checked by its front-end before the tool runs. The layer holds only the mid-operation key assertion, which depends on what an operation turned out to do rather than what was requested
- **Directory creation** - a write does not bring a directory into existence, making "who may materialize a path" one decision in the tool that owns creation rather than a side effect available to every writer
- **Scaffolding and convention** - the layer knows what a vault path is, not what a vault means
- **Repository operations** - git is whole-repo by construction, cannot be scoped, and so composes with nothing else here

## The Front-End Boundary

Two front-ends invoke the same tool layer: the MCP server, and a CLI that runs a single tool per invocation. The tools must not be able to tell which one called them.

- **No hosting dependencies in the layer or the tools.** No web framework types, no DI container, no request context. Both front-ends construct what they need and pass it in
- **Results and errors are transport-neutral.** One representation, rendered by each front-end: structured tool results and the coded error envelope over MCP, exit codes and stderr at the CLI. A tool never formats for a transport
- **Scope arrives as an explicit parameter, never as ambient state.** Not readable from a global, an environment variable, or anything else a tool could reach unhanded, and never settable by a tool's caller - a client-supplied scope argument is a forged grant
- **The CLI is an unscoped, ungranted path.** No grant, so no scope check, no op-class check, and no elevation. That makes it a dev-machine and recovery surface rather than something an unattended agent is given - deliberate, not an accident of having no grant to check, and said as much in the CLI's own help text
- **Both front-ends produce identical semantics** for the same operation on the same vault, differing only in how the result is rendered and in the absence of grant enforcement at the CLI

The reason the CLI exists: the vault must stay fully operable with the server stopped, paused, or abandoned, so that moving vault work onto the server is never a one-way commitment.

## Guarantees To Callers

The conformance surface. How they are achieved is free.

- **Containment.** Every path the layer returns or permits an operation on is inside the resolved root. Escaping text is refused, never clamped or normalized into something else
- **Vault-relative terms.** Emitted paths are vault-relative with one separator convention on every platform, so results can be quoted back into later calls unchanged. Absolute paths never leave the layer
- **A write creates nothing but the file.** Writing under a missing parent fails, naming what was missing in vault-relative terms
- **Scope is enforced at the operation.** Every read, write, move, enumeration, and search consults the restriction before touching anything; no entry point opts out. This is the layer's own obligation - the containment semantics themselves live in the retained scope primitives, and the rest of the seam is [[Spec-Auth-Requirements]]
- **Filtering is silent; gating is loud.** A list returns the in-scope subset without saying what was withheld; a named out-of-scope path errors. The first leaks nothing, the second is actionable
- **Mutations serialize across processes.** The shared execute path wraps every invocation of a tool that declares it mutates in a lock other processes contend for on the same vault. No tool acquires it, and a tool declaring no mutation never takes one
- **A crashed writer does not wedge the vault.** Recovery is signalled, not inferred: the next waiter learns it inherited the lock from a dead owner. Any acquire timeout is a diagnostic liveness guard only - a *hung* writer is alive and never trips the recovery signal
- **Failure to read is classified, not guessed.** "Not there to read" versus "there and something went wrong" comes from the failure itself, never from a separate existence probe
- **Errors are structured and carry no oracle.** Coded, stable, human-messaged, and disclosing nothing the caller was not authorized to see - an out-of-scope refusal must not reveal whether the target exists

## Load-Bearing Semantics

Each prevents a specific failure that stays invisible until it happens. None is a style preference.

- **The lock's identity derives from the vault's location, not its name**, so two configurations naming one directory contend. The path is case-folded first, or a case-only difference leaves two writers unserialized on the same files. The watcher shares this derivation as code, never as a convention re-implemented on each side
- **Containment is decided textually and never touches the filesystem** - the property to preserve when calling the retained primitives, since it keeps the check total, cheap, and identical for a path that does not exist yet
- **A search that cannot read a file fails the search.** Skipping it turns a permissions problem into a wrong answer the caller cannot distinguish from a correct one
- **Frontmatter mutation preserves the body byte-for-byte and key order exactly**, and a no-op write produces no diff. Those are the invariants; the mechanism delivering them is unspecified
- **Bare-link resolution is observed behavior, not a design choice.** Which file a bare link opens is decided by the editor, and the vault's contents already depend on it. The rule - nearest by relative path from the linking file, ties broken deterministically ([[Obsidian-Link-Behavior]]) - is empirical, and must be isolated enough to change in a line or two. Deriving a better rule from first principles is wrong by construction
- **Link resolution ranks, it does not search.** It takes a candidate set and returns an ordering, touching no filesystem and deciding no candidacy. Purity is what lets the move protection pass and the delete link census share one comparator instead of two that drift
- **Reading tolerates what writing refuses.** A reporting pass runs over every file in the vault and must survive content it cannot parse; a write must refuse that same content. This asymmetry is forced by having both kinds of consumer, and is the one place "be consistent" is the wrong instinct

## Fixed Constraints

Imposed from outside the code, and not negotiable by the implementation.

- **The vault must stay operable with no server running** - see [[#The Front-End Boundary]]
- **A second process contends for the write lock.** The watcher is a peer, not a subordinate
- **The editor's link semantics are external** - matched, not defined
- **The vault is a plain directory tree.** No symbolic links, no junctions, no reparse points. The layer assumes this and does nothing to detect or accommodate them
- **The vault is a git working tree.** Recoverability of a deleted file is load-bearing elsewhere, constraining what may be destroyed without ceremony
- **The frontmatter format is a real standard.** What counts as a block is decided by the format rather than by convenience, and the degenerate shapes are where the divergences live
- **The grant, scope, and op-class model is given** ([[Spec-Auth-Requirements]]). The layer enforces it and does not redesign it
- **Ripgrep is the search and enumeration engine**, rooted at the vault directory - which is what makes emitted paths vault-relative and structurally confined. Its *visibility* behavior is not inherited: the layer hides nothing, and all narrowing happens above it in declared policy and scope
- **Frontmatter is parsed and emitted through a mutable node tree, never an object serializer.** Only the node layer preserves key order and per-node quoting style, which the invariants above require
- **Folded scalars cannot appear in frontmatter the tooling writes.** A folded scalar's line breaks are consumed at parse time, so re-emission reflows hand-wrapped prose and no emitter setting prevents it. Paths whose frontmatter uses them stay excluded from frontmatter writes entirely

## Decisions The Implementation Must Make Explicitly

Each of these has a default that looks like no decision at all. Make them deliberately and record the reasoning.

- **Make out-of-scope paths unrepresentable rather than checked.** Correctness must not depend on every operation remembering to call a check. A resolved-path type producible only by passing resolution and scope, with operations accepting nothing else, turns that discipline into a construction-time property. The highest-value structural decision available, and the reason no function is named anywhere here - a hook-shaped description would pre-commit the build to the weaker form
- **Give the lock a stated granularity.** Per-vault whole-repo serialization is almost certainly right for a personal vault, but it must be a decision with its cost written down
- **Version the error vocabulary.** That errors are coded, stable, and oracle-free is fixed; the specific code set is an artifact that will change and needs somewhere to be recorded
