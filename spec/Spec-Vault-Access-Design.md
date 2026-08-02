# Vault Access Design

Forward specification for the vault access layer and the tool layer above it: what they own, what they guarantee, and which semantics are load-bearing and why. Written to be built from directly - it describes what must be true, not what any prior implementation did.

Target: C# throughout, with two front-ends over one core - the MCP server and a CLI application that invokes each tool directly.

## Scope Of The Build

Under design here: the vault access layer, the tool layer, and the front-end boundary between them.

Not under design, and taken as existing requirements the new layer must satisfy: the grant and op-class model, health and degraded-state reporting, logging, configuration loading, and MCP host wiring. The layer consumes the grant model's scope restrictions; it does not define them.

### Companion Inputs

This doc is not self-sufficient on facts about the world. It travels with:

- **[Spec-Obsidian-Link-Behavior](Spec-Obsidian-Link-Behavior.md)** - the editor's bare-wikilink resolution, an external behavior to match
- **[Spec-Frontmatter-Substrate](Spec-Frontmatter-Substrate.md)** and **[Spec-Frontmatter-Registry](Spec-Frontmatter-Registry.md)** - the frontmatter write surface and its authorization layer, not restated here
- **[Spec-Auth-Requirements](Spec-Auth-Requirements.md)** - the obligations across the seam with the retained authorization code, which is itself read from source rather than specified
- **[Spec-Access-Layer-Capabilities](Spec-Access-Layer-Capabilities.md)** - the operations the layer must offer, and the requirements attached to each
- **[Spec-Tool-Capabilities](Spec-Tool-Capabilities.md)** - what each tool must accomplish and which of its behaviors are load-bearing. Capabilities and semantics only: the tool vocabulary itself is designed in this build, not inherited

Where a companion doc describes a mechanism rather than a requirement, take the requirement and discard the mechanism.

## The Thesis

**All vault I/O flows through one layer, and nothing outside it touches a vault path.**

A scope restriction is meaningful only if there is no path around it, a mutex lock protects only if every writer takes it, and a path is contained only if no caller can construct one the layer never saw.

The cost: any vault-touching operation a tool needs must live in the layer or be built from primitives it hands out. There is no third option where a tool does a quick read of its own - the moment one exists, everything below becomes a claim about most callers rather than a property of the system.

## What The Layer Owns

Inside: vault resolution; path resolution and containment; scope enforcement; the grammar for all new names; enumeration and content search; note read, write, move, and directory creation; the mutex lock; frontmatter mutation and validation ([Spec-Frontmatter-Substrate](Spec-Frontmatter-Substrate.md)); wikilink grammar and bare-wikilink resolution; read-failure classification; and the result and error vocabulary tools return through.

Outside, deliberately:

- **Op-class floor enforcement** - a tool's declared minimum authority is checked by the shared execute path before the tool runs, in the same wrapper that takes the mutex lock. The layer holds only the mid-operation key assertion, which depends on what an operation turned out to do rather than what was requested
- **Scaffolding and convention** - the layer knows what a vault path is, not what a vault means
- **Repository operations** - git is whole-repo by construction, cannot be scoped, and so composes with nothing else here

## The Front-End Boundary

Two front-ends invoke the same tool layer: the MCP server, and a CLI that runs a single tool per invocation. The tools must not be able to tell which one called them.

- **Each tool is declared once, and both front-ends project from that declaration.** A tool's name, description, and typed argument contract have exactly one statement; the MCP front-end renders it as a tool definition and its schema, the CLI as a subcommand and its options. Neither front-end carries a second statement of a tool's arguments - two statements drift, and the drift stays invisible until a caller hits the side that was not updated
- **No hosting dependencies in the layer or the tools.** No web framework types, no DI container, no request context. Both front-ends construct what they need and pass it in
- **Results and errors are transport-neutral.** One representation, rendered by each front-end: structured tool results and the coded error envelope over MCP, exit codes and stderr at the CLI. A tool never formats for a transport
- **Scope arrives as an explicit parameter, never as ambient state.** Not readable from a global, an environment variable, or anything else a tool could reach unhanded, and never settable by a tool's caller - a client-supplied scope argument is a forged grant
- **The CLI runs under a named grant from configuration**, scope-checked and op-class-checked exactly as the server's is, with elevation available. The grant is unrestricted by default; a narrower one keeps a routine session from making edits it did not intend. An operator who can run the CLI can also edit the grant it uses, so this constrains accident and makes the CLI's reach reviewable rather than bounding that operator
- **Both front-ends produce identical semantics** for the same operation on the same vault, differing only in how the result is rendered

The reason the CLI exists: the vault must stay fully operable with the server stopped, paused, or abandoned, so that moving vault work onto the server is never a one-way commitment.

## Guarantees To Callers

The conformance surface. How they are achieved is free.

- **Containment.** Every path the layer returns or permits an operation on is inside the resolved root. Escaping text is refused, never clamped or normalized into something else
- **Vault-relative terms.** Emitted paths are vault-relative with one separator convention on every platform, so results can be quoted back into later calls unchanged. Absolute paths never leave the layer
- **Nothing creates a parent directory on the way past.** Writing a note, creating a folder, and moving to a destination all fail under a missing parent, naming what was missing in vault-relative terms. Materializing a path is always a deliberate call
- **Scope is enforced at the operation.** Every read, write, move, enumeration, and search consults the restriction before touching anything. No entry point opts out. The one pass that is not scope-bound is the next bullet. This is the layer's own obligation - the containment semantics themselves live in the retained scope primitives, and the rest of the seam is [Spec-Auth-Requirements](Spec-Auth-Requirements.md)
- **Wikilink repair is the one write scope does not bound.** Moving a note changes what every link to it must say, and creating a note whose basename already exists changes what bare links to that basename resolve to. Either set of links can be anywhere, so the pass runs over every note in the vault. The operation's own paths are scope-checked normally ([Spec-Tool-Capabilities: Authority](Spec-Tool-Capabilities.md#authority))
- **The layer owns the grammar for all new names.** A name is checked when something is created or moved to it. Names already on disk are not checked, so every existing file stays readable, searchable, and movable
- **Filtering is silent; gating is loud.** A list returns the in-scope subset without saying what was withheld; a named out-of-scope path errors. The first leaks nothing, the second is actionable
- **Only one mutation runs at a time, across processes.** The shared execute path wraps every invocation of a tool that declares it mutates in a mutex lock other processes contend for on the same vault. No tool acquires it, and a tool declaring no mutation never takes one
- **A crashed writer does not wedge the vault.** Recovery is signalled, not inferred: the next waiter learns it inherited the lock from a dead owner. Any acquire timeout is a diagnostic liveness guard only - a *hung* writer is alive and never trips the recovery signal
- **Failure to read is classified, not guessed.** "Not there to read" versus "there and something went wrong" comes from the failure itself, never from a separate existence probe
- **Errors are structured and carry no oracle.** Coded, stable, human-messaged, and disclosing nothing the caller was not authorized to see - an out-of-scope refusal must not reveal whether the target exists

## Load-Bearing Semantics

Each prevents a specific failure that stays invisible until it happens. None is a style preference.

- **Out-of-scope paths are unrepresentable rather than checked.** Correctness must not depend on every operation remembering to call a check. A resolved-path type that can only be produced by passing resolution and scope, with operations accepting nothing else, makes that true at construction instead. This is the most important property in the build, and it is why no function is named anywhere in these specs: naming one would describe a check to call, which is the weaker design
- **The lock's identity derives from the vault's location, not its name**, so two configurations naming one directory contend. The exact derivation is fixed ([The Mutex Lock Name](#the-mutex-lock-name)). The watcher shares it as code, never as a convention re-implemented on each side
- **Containment is decided textually and never touches the filesystem.** Preserve this when calling the retained primitives: it is what makes the check cheap, applicable to every path, and identical for a path that does not exist yet
- **A search that cannot read a file fails the search.** Skipping it turns a permissions problem into a wrong answer the caller cannot distinguish from a correct one
- **Frontmatter mutation preserves the body and key order exactly**, and a no-op write produces no diff. Line terminators are the one thing that normalizes. Those are the invariants; the mechanism delivering them is unspecified
- **Bare-wikilink resolution is observed behavior, not a design choice.** Which file a bare wikilink opens is decided by the editor, and the vault's contents already depend on it. The rule - nearest by relative path from the linking file, ties broken deterministically ([Spec-Obsidian-Link-Behavior](Spec-Obsidian-Link-Behavior.md)) - was measured, not designed, and must sit somewhere it can be changed in a line or two. There is no better rule to work out from first principles; the editor's behavior is the requirement
- **Wikilink resolution ranks, it does not search.** It takes a candidate set and returns them in order. It touches no filesystem and decides nothing about which candidates belong in the set. Because it does only that, the move protection pass and the delete wikilink census can share one comparator rather than each keeping its own
- **Reading tolerates what writing refuses.** A reporting pass runs over every file in the vault and must survive content it cannot parse; a write must refuse that same content. Both consumers are real, so the two sides differ on purpose - do not make them consistent

## Fixed Constraints

Imposed from outside the code, and not negotiable by the implementation.

- **The vault must stay operable with no server running** - see [The Front-End Boundary](#the-front-end-boundary)
- **One mutex lock per vault, covering the whole repository.** Granularity is not the build's to choose; nothing narrower is offered
- **A second process contends for the mutex lock.** The watcher is a peer, not a subordinate
- **The editor's link semantics are external** - matched, not defined
- **The vault is a plain directory tree.** No symbolic links, no junctions, no reparse points. The layer assumes this and does nothing to detect or accommodate them
- **The vault is a git working tree**, and nothing built here reads or writes repository state. What follows from the working tree - a folder that must persist while empty needing a `.gitkeep` - is stated where it applies
- **Notes are stored with LF line terminators.** Every write that composes content emits them. Nothing detects what was there or preserves another convention
- **Frontmatter has no official standard, and Markdig's convention is the reference** ([Spec-Frontmatter-Substrate: What A Block Is](Spec-Frontmatter-Substrate.md#what-a-block-is)). Markdig is also the library if markdown itself is ever parsed
- **The grant, scope, and op-class model is given** ([Spec-Auth-Requirements](Spec-Auth-Requirements.md)). The layer enforces it and does not redesign it
- **Ripgrep is the search and enumeration engine**, rooted at the vault directory - which is what makes emitted paths vault-relative and structurally confined. Its ignore-file behavior is not inherited. Dot entries are the one thing the layer drops on its own by default, and a caller that needs them asks for them explicitly. Every other narrowing happens above the layer in declared policy and scope
- **YamlDotNet parses and emits frontmatter, at its representation-model layer - the mutable node tree, never its object serializer.** Only the node layer preserves key order and per-node quoting style, which the invariants above require. The choice was settled by measuring the candidate libraries against the real corpus, an experiment this build cannot repeat, so it is not open to reconsideration. It needs two non-default emitter settings, an explicit newline choice, and a small emitter shim, named in [Frontmatter Emitter Settings](#frontmatter-emitter-settings)
- **Re-emission guarantees meaning, not bytes.** Key order, body bytes, and every value's content survive a write; formatting may normalize

## The Mutex Lock Name

A cross-process contract with `VaultWatcher`, so it is carried over exactly rather than redesigned:

```
Global\VaultWrite-<hash>
```

`<hash>` is the first 32 characters of the uppercase hex SHA-256 of the vault root path: canonicalized, trailing separators trimmed, lowercased, UTF-8 encoded. `Global\` is Windows-only; on other platforms the name is `VaultWrite-<hash>` alone.

Why each part: hashed because a mutex name is capped at 260 characters and cannot contain a backslash outside the reserved prefix; lowercased on every platform, or a case-only difference in configuration gives two processes different locks on the same files; `Global\` so the mutex spans sessions. Any change here breaks both processes at once.

## Frontmatter Emitter Settings

Established by measurement over the real corpus. Under the configuration below, 315 of 320 frontmatter blocks round-trip byte-identically; the five that differ are folded scalars in skill definition files, which sit under a dot-directory and are out of the tooling's reach.

- **`WithIndentedSequences()`** - sequence items indent beneath their key. Both forms are valid YAML and the parse cannot tell them apart, so the emitter's default reformats every list in the vault on its first write.
- **`WithNewLine("\n")`** on the emitter settings, **and** `NewLine = "\n"` on the writer it wraps. Both are needed: the settings govern the breaks the emitter itself writes, the writer governs the rest.
- **An `IEmitter` decorator forcing the document start and end implicit.** Without it YamlDotNet emits its own `---` opener and `...` terminator inside the block, where the fence lines already provide them.
- **`assignAnchors: false`** on the save, so anchors and aliases are never introduced into a block that had none.

```csharp
var settings = new EmitterSettings().WithNewLine("\n").WithIndentedSequences();
using var w = new StringWriter(sb) { NewLine = "\n" };
stream.Save(new ImplicitDocEnd(new Emitter(w, settings)), assignAnchors: false);

// The decorator: rewrite both document events to their implicit forms.
public sealed class ImplicitDocEnd(IEmitter inner) : IEmitter
{
    public void Emit(ParsingEvent e) => inner.Emit(e switch
    {
        DocumentStart => new DocumentStart(null, null, isImplicit: true),
        DocumentEnd   => new DocumentEnd(isImplicit: true),
        _             => e,
    });
}
```

