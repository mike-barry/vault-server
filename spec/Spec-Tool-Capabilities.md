# Tool Capabilities And Semantics

Forward specification for the tool layer: what each tool must accomplish, and which of its behaviors are load-bearing rather than incidental. [[Spec-Vault-Access-Design]] fixes the guarantees the layer beneath owes and [[Spec-Access-Layer-Capabilities]] states the operations it must offer; this doc states what the tools do with them.

**Capabilities and semantics only.** Argument names, result fields, error codes, and the shape of any envelope are designed in this build. A sentence here that could only be satisfied by one spelling has overreached.

Organized by concern rather than by tool: every hard question in this layer is cross-cutting, and a per-tool organization would state each answer seven times and let the copies drift.

## Where The Boundary Sits

Owned elsewhere:

- Guarantees, fixed constraints, and the front-end boundary: [[Spec-Vault-Access-Design]]
- The operations available to build a tool from: [[Spec-Access-Layer-Capabilities]]
- Frontmatter parsing, mutation, validation, and the single write path: [[Spec-Frontmatter-Substrate]]
- The declaration of recognized keys, path policy, and required authorities: [[Spec-Frontmatter-Registry]]
- The grant, scope, and op-class model: [[Spec-Auth-Requirements]]
- The editor's bare-link resolution, which the link-graph work matches rather than defines: [[Obsidian-Link-Behavior]]

**The dividing line against the access layer: the layer offers surfaces, tools shape results.** What enumeration can see is the layer's; what a discovery tool returns is the tool's. A question answerable in either doc belongs to the layer.

## What Each Tool Must Accomplish

Seven tools in the first pass. Their names and purposes are fixed; how those purposes are expressed is open.

- **`read`** - return the content of one note, whole or by part, plus a whole-file staleness token derived from it
- **`glob`** - the only name-and-path search surface, and the discovery step that turns a name into a path
- **`grep`** - the only content-search surface, at the three granularities the layer offers
- **`edit`** - the only way to mutate an existing note: ordered frontmatter operations plus at most one body change, applied as a single outcome
- **`create`** - the only way to bring a note or a folder into existence, and the tool that owns directory creation
- **`delete`** - the only way to remove a note or a folder, reporting the full blast radius
- **`move`** - the only way to relocate or rename a note or folder, keeping the link graph intact

Two properties of the set:

**It is closed over vault mutation of markdown content.** These tools are the whole sanctioned write surface for notes and folders, so anything they cannot express has no path at all once direct file access is denied.

**Existence, content, and location are three separate tools.** No tool performs a second one as a side effect. A tool that created on a miss would turn a typo into a new note; a tool that moved on a path change would break every reference silently.

## The Operand Perimeter

**Every operand is a markdown note or a folder.** One uniform rule across all seven tools, with no per-tool variation and no file-general operand anywhere.

**The perimeter is narrower than an operation's footprint, deliberately.** What a caller can *name* is a note or a folder. What an operation touches inside its own footprint is whatever the operation requires: folder creation seeds the marker that makes a folder exist in the repository, and a recursive folder removal removes everything beneath it whatever its type.

**Internal reach is derived from the operand, never caller-directed.** Folder creation seeds a marker it chooses rather than taking a file to seed; folder removal removes what lies under the folder that was named rather than taking a file list. That derivation is what keeps the perimeter meaningful.

**The marker's name is declared, not compiled in.** Making a created folder committable is a property of the vault being a git working tree; what the marker is called is convention, and convention stays out of code.

**Non-markdown vault content is the editor's.** Attachments and images are placed and maintained through the editor; no tool creates, relocates, or discovers one. A boundary, not a gap.

**This restriction is the tool set's own policy, not a property of the layer.** The layer keeps its file-capable primitives and both enumeration surfaces, and a future tool selects a different policy over the same primitives. Containment and scope are not selectable: a file-kind filter composes beneath them, never beside them.

## The Operand Vocabulary

**Every operand naming an existing thing is a vault-relative path** - no bare names, no link syntax, no per-tool name resolution. Duplicating a resolution ladder onto every operand would give the vault as many notions of "the same name" as it has tools.

Three consequences are load-bearing:

- **A path is unambiguous by definition**, so no tool needs a candidate list, a disambiguation gate, or an ambiguity failure. A vocabulary carrying an error for it has admitted a resolution step somewhere.
- **Discovery-then-act is the intended two-call idiom**, matching what an agent already does with its native tools.
- **Link syntax survives only as internal machinery** where the link graph is the subject. It is never an operand, and the grammar belongs to the layer ([[Spec-Access-Layer-Capabilities#Link Grammar And Resolution]]).

**The markdown extension is never inferred.** Folders share the operand space, so a name without one is genuinely ambiguous between a note and a folder, and resolving that would reintroduce the ladder. A note-kind operation handed an extensionless path refuses and **names the spelling that would have worked**, so the miss costs one retry rather than a diagnosis.

**Destinations are constructed, not resolved.** A path naming something that does not exist yet is not subject to the resolve-to-exactly-one rule; the checks that apply to it are occupancy, well-formedness, and containment.

**A kind assertion distinguishes a note from a folder, and is an intent check rather than a disambiguator.** Where a tool can infer an operand's shape, an optional assertion lets a caller who believes it is acting on one note fail fast rather than acting on a folder of fifty. It is **absent wherever it would be inert**. The one place it does real work is where it discriminates between disjoint parameter surfaces, and there it is required, with a parameter inert for the selected kind refused rather than ignored.

**Well-formedness of a new name has two tiers.** Characters that would break the vault's link syntax, characters the filesystem refuses, and trailing punctuation the filesystem silently eats are refused outright: a name that cannot be linked to is not a name. Everything else about naming is convention.

## Result-Count Semantics

**Whether a result count is an error follows from what a tool is for, and is fixed per tool rather than per call.**

- **A search tool treats any count as a result.** Zero matches is an empty result and never a failure; an existence check must not require a caller to catch. A caller needing exactly one enforces that itself, because only the caller knows it needed one.
- **A tool operating on an existing target requires exactly one and fails on a miss.** Returning an absent value pushes a check into every caller, where one will forget it.
- **A tool bringing something into existence inverts that: existence is its failure.** An occupied target is refused rather than overwritten or merged, distinguishably from every other refusal, because the caller's next move is entirely different. Occupancy itself belongs to the layer ([[Spec-Access-Layer-Capabilities#Moving]]); what the tool owes is reading that answer rather than deciding a second one.

## Path Policy

Which paths this tool set may reach, and what authority reaching them requires, is a section of the declaration. Entry shape, matching, dispositions, and the dot-directory default are stated in [[Spec-Frontmatter-Registry#Path Policy]]. What binds the tools:

**Data adjacency is not enforcement coupling.** The access layer never reads the declaration; the tool layer loads it and hands each consumer the sections that bind it. A markdown-only policy sitting beside frontmatter rules puts no markdown policy in the layer.

**Filtering is silent, gating is loud.** Enumeration omits excluded paths without saying so; a named excluded path errors, distinguishably from a miss.

**Policy and scope compose as a conjunction, and scope only narrows.** A grant naming an excluded path still does not reach it. Because policy is a property of the tool set rather than of a grant, it binds both front-ends: the unscoped recovery front-end is not a way around it.

**A future non-markdown surface is bound by the same declaration and takes different sections of it**, so its opt-outs are stated in one place rather than being an absence someone has to notice.

## The Argument Surface

**One declaration per tool, projected into both front-ends** ([[Spec-Vault-Access-Design#The Front-End Boundary]]). The obligation it creates here: **no tool carries front-end-specific argument handling**, because a tool that did would have to know which front-end called it.

**Advertised is not enforced.** Conditional requiredness, mutual exclusivity between optional arguments, and required forms only a description can carry have no clean expression in a client-facing schema. The requirement is not that the schema express everything but that **the tool remain the sole enforcement point**, so nothing downstream treats schema conformance as permission.

**An unrecognized value of an enumerated argument is refused in the tool's own error vocabulary.** Whatever binds arguments has its own rejection path, and a refusal escaping through it arrives outside the coded envelope callers branch on. Such values are trimmed and case-folded before comparison.

**A structured argument stays structured.** An argument whose contract is an ordered list of operations is that on the wire - not a caller-encoded string, not flattened into siblings. Encoding a front-end needs is that front-end's business.

**Scope and held authority are not arguments**, so the declared argument surface is the whole of what a caller controls - which makes that guarantee checkable by reading one declaration.

## Result Tiers

**Two tiers, split by what the output is for.** Results a model reads as content - note text, search hits, path lists - return in the plain native shape an agent already consumes, with each shape's own empty-case convention; structure there costs tokens and buys nothing. Results a caller branches on are structured, which covers every mutation and any assessment whose purpose is a decision.

**Mutation results share one envelope**, under three obligations:

- **Uniform members are present even on tools that cannot populate them**, emitted empty rather than omitted. Teaching callers which tools carry which members is the cost the envelope exists to avoid.
- **A list is a list of identities, not a count.** Counts derive from lists; lists do not derive from counts.
- **A warning whose presence is the signal is omitted when it does not fire.** An always-present member reads as checked-and-clear on tools that never check.

**The echo of asserted authority is uniform for the same reason**: a tool that asserts nothing still reports that, so the audit trail is a property of every mutation rather than a signal that this tool *can* assert.

## The Preview Contract

**Any tool whose mutation has a discoverable blast radius offers a preview, through one gate with one meaning across the layer.** Its default follows what the routine call is - assessment-first where assessment is routine, mutation-first where a preview would be ceremony. Both occur in this set, each a stated per-tool decision. The gate is **absent where there is nothing to discover**; a preview that echoes the request back trains callers to skip previews generally.

**A plan is a faithful prediction of its own result:**

- **A plan emits the tool's complete result shape**, so a plan and the result of the same call differ in exactly one discriminating member. A reduced shape cannot be compared against what it predicted.
- **Path-valued members name post-mutation paths**, so no caller translates between two path vocabularies.
- **A plan takes the same lock, runs the same validation, and refuses everything a real call would.** A preview is the same refusal earlier, never a way around one; validation reads state that changes, so an unlocked plan can return a prediction already stale.

**The one licensed divergence is a tool whose output is a sequence of steps rather than a single outcome.** The licensing condition matters more than the exception: a tool that cannot state one in a sentence does not have one. None in the first pass qualifies.

## The Error Vocabulary

Errors are coded, stable, human-messaged, and oracle-free ([[Spec-Vault-Access-Design#Guarantees To Callers]]); the code set is an artifact the build owns and versions. What is fixed here is **which distinctions must remain distinguishable at the call site**, each collapse being unrecoverable above the tool:

- **A miss versus a failure to read**, taken from the failure itself rather than a separate probe
- **A grant that does not reach versus tooling that does not own.** One is remedied by elevation, the other by nothing a grant can change
- **Authorization versus routing.** Where both would fire, the routing answer is more actionable and wins
- **A caller error versus an engine failure**, in search especially - the pair most easily collapsed into one "search failed"
- **A transient throttle versus a permanent refusal**, or a retryable condition routes into a branch that never retries
- **A vault nobody configured versus one configured but unavailable** ([[Spec-Access-Layer-Capabilities#Vault Resolution]])
- **A partial mutation versus a clean refusal**

**Structured payloads earn their place by being retry-actionable.** The test is whether the caller lifts the value verbatim into a corrected call. A refusal whose remedy is elevation has no corrected call to feed and carries no payload for symmetry's sake, so payloads are deliberately not uniform across a code and callers branch on the code.

**A payload diagnoses and does not bypass.** A staleness refusal returning the current token would let a caller retry with it and clobber content it never read; recovery is a re-read.

**A refusal is not an enumeration surface.** No payload carries a set computed by filtering the request against authority the caller lacks - not what is held, and not the violating subset, which is the same disclosure one item at a time.

**A refusal raised beneath a tool surfaces unchanged.** Catching and re-shaping reintroduces reasoning about message text. Where a refusal needs the call's coordinates, they are attached at the raise.

**Failure ordering is part of the contract.** A refusal must fire before anything is locked or mutated, forcing a stated order between argument validation, vault resolution, path resolution, and lock acquisition - an order that differs between mutating and non-mutating tools. Where a tool reports the first failure only, that is retry material for one thing at a time and not a batch report.

## Staleness And Chaining

**The read-to-write window is the one an agent occupies** - model time during which the editor, another session, or a person can change the file. Serialization between tools does nothing about it; this guard is separate and caller-carried.

- **The token covers the whole file regardless of which part was read**, so nothing authorizes a write from a partial read.
- **The token is acquired before the content, and that order fails closed.** Token-then-content leaves a caller holding an old token against content it can see, so a concurrent change refuses; the reverse pins a token to content the caller never saw.
- **The guard is required on the one operation that can destroy content the caller never saw** and optional elsewhere: a surgical change self-verifies through its own anchor, a wholesale replacement has nothing to self-verify against.
- **A mutation returns the token carrying forward**, derived from what landed rather than what was composed, so a chain needs no interleaved read.

## Authority

The grant model is given ([[Spec-Auth-Requirements]]). Three dimensions contribute to what a tool call requires, and they union.

**A declared floor**, the least authority any use of the tool requires, checked before it runs. Anything more is asserted inside the tool on the path that needs it, so conjunction is structural and no combined token exists. A floor need not be a write class on a tool that can write: an assessment tool with an opt-in fix mode has a read floor and asserts a write key, which is what lets a read-only caller run the assessment.

**Key-level authority, classified by outcome** over what the operation turned out to change. Classification by declared intent classifies by what the caller said they were doing, and every injection path is an operation aimed at something innocuous that mutates the protected layer anyway. The predicate is shared ([[Spec-Frontmatter-Substrate#Write Authority Classification]]) so it cannot drift per tool, and the degenerate cases - composing a new note against no original, removing a note's frontmatter entirely - fall out of it rather than becoming second rules.

**Artifact-level authority, declared by path** ([[#Path Policy]]). Some files exist to instruct agents, and their bodies are that instruction rather than prose - the root manifest, folder notes, and the shared guides. For these the key-level diff is not enough, because a body-only write to one changes the agentic layer while touching no key. The declaration names them and states the authority; the requirement is declared membership of a path set, exactly as key-level authority is declared membership of a key set.

**Artifact authority binds an operation's operand and its enumerated target set.** It contributes unconditionally where a file is created, mutated, or removed, and on a relocation only where artifact status *changes* at either endpoint - minting one, or shedding it. A relocation of an artifact that stays one contributes nothing. Status change rather than presence, because shedding is otherwise the way out: move a protected file aside, rewrite it freely, move it back.

It never contributes for a file merely read, nor for one incidentally rewritten to preserve reference integrity - the rewrites a relocation causes in fifty other notes are maintenance, not authoring.

**Assertion happens in the validate phase with zero mutation on denial**, and what was asserted is echoed on success. Where a tool classifies a set, the refusal names the offending members - a caller told that something in a folder needed authority it lacks, without being told what, cannot act.

**Not every authority dimension is scopeable.** Where an operation is whole-repository or whole-vault by construction, path scope has nothing to bind, and a tool must not perform a scope check that falsely implies it does. Such authority is withheld rather than half-granted.

## Link-Graph Maintenance

Two tools touch the link graph, in opposite modes, over the layer's single grammar and ranking rule. **Relocation maintains the graph; removal reports on it** - a relocation leaves a new target to point at, a removal leaves nothing.

For relocation:

- **Both link forms are covered**, the plain reference and the embedding one. Only the target portion is rewritten; decorations a link carries survive verbatim.
- **A path-qualified reference matches by component-aligned suffix**, because the editor writes partial suffixes. A bare reference matches only where it *resolves to* the relocated item under the editor's own rule - bare references under duplicate names are deterministic, not ambiguous.
- **Emission is canonical rather than minimal**: the short form where the name is unique, the qualified form where it is not. Computing a shortest sufficient suffix produces references the editor rewrites on its next touch.
- **A relocation never fails on ambiguity and never re-aims a reference.** Where it would change what another reference resolves to, that reference is pinned to the target it currently has and the operation proceeds. Pinning freezes; it never redirects. A gate that could trip will trip in the middle of a hundred-file pass.

For removal:

- **The scan is vault-wide and unscoped.** Link-graph integrity is not a scoped property, and a report stopping at a scope boundary under-reports where the caller most needs the truth. Relocation has the same latitude: its endpoints are scoped, its graph maintenance is not.
- **A bare reference is reported only where it resolved to something removed.** A same-named bystander elsewhere is the reference's real target.
- **References inside the removed set are not reported**, and each surviving occurrence is reported individually, because a caller fixing them visits each one.

**A folder relocation carries coupled effects under one operation**: the relocation, the folder-note invariant, and reference repair for everything contained. Contained names do not change, so the repair is a single prefix substitution and the folder note is the only name event.

**Neither tool authors routing.** Registry entries and navigation frontmatter aimed at an item's role are composed by the caller. These tools track names; they do not decide meaning.

## Enumeration Domains

**Which domain a tool enumerates is a semantic choice, not a shared-code convenience.** Discovery is the only tool whose *output* is an enumeration; search selects the files it reads, relocation walks a folder's contents and runs the basename census behind link emission and the reference scan, and removal builds a destruction plan and a backlink census. The layer offers two domains ([[Spec-Access-Layer-Capabilities#Enumeration]]), and a tool asking the wrong one gets a confidently wrong answer.

- **A tool asking what an agent can see** wants the agent-visible domain - discovery, search, the reference scan, and the uniqueness census, whose correctness depends on matching what the agent would find.
- **A tool asking what is about to be destroyed** wants the whole domain. Anything invisible to the first answer is still on disk, still destroyed, and still possibly an instruction artifact that must be classified before it goes. Getting this one wrong fails three ways at once, all silent: a folder holding only invisible files enumerates as empty and skips its own guard, the blast radius under-reports, and authority classification runs over a set missing exactly the files most likely to be excluded from tooling.

**Widening the layer's answer to serve one consumer is not the fix.** It changes what discovery returns and what the uniqueness census counts, and that census decides whether a rewritten link is emitted short or qualified. A tool needing a different domain asks for a different domain.

**Three separate questions compose here, and only one is the tool's.** What the layer's surfaces can see is the layer's, and the answer is everything ([[Spec-Access-Layer-Capabilities#Enumeration]]). Which surface a tool asks is the tool's. Which paths this tool set may reach is [[#Path Policy]]'s. Narrowing therefore happens only in the last two, both of which a caller can reason about.

## Destruction And Recoverability

**The repository is the undo, and that licenses removal without a dedicated destructive authority class.** With a repository behind it, removing a note is no more destructive than replacing its contents, and blast radius is a scope concern rather than a per-tool one.

The backstop is not universal, and the tool says so rather than implying cover it lacks:

- **The subset the repository cannot restore is identified and reported.** Empty is the normal case; non-empty is where recovery advice does not cover everything and must be qualified.
- **A vault that is not a repository reports its whole plan as unrecoverable.** An empty list would read as all-clear.
- **The refusal preceding a bulk removal carries the same information**, so a caller sees what an opt-in retry destroys beyond reach before opting in.
- **Classification comes from what the repository tracks and only that.** Reasoning about ignore configuration instead would misclassify a tracked-but-ignored file, and the caller would decline a safe operation.

**There is no bypass and no force.** The guards layered on recoverability are an explicit opt-in for a non-empty container, a refusal to descend into a nested repository, and validate-before-remove. None has an override: a gate waivable in the same call is not a gate.

**Removal reports orphaning it caused rather than repairing it.** Completeness is a sweeping concern, not a primitive's.

## Multi-Target Mutation

**Any mutation touching more than one file plans, validates the whole set, then applies**, so a refusal is impossible to reach after the first write.

**Serialization is not atomicity, and the difference is stated rather than implied.** One lock means no other tool interleaves; it does not mean rollback. A failure during application names exactly what landed and what did not, and carries recovery guidance keyed to that state. Two properties of that guidance are load-bearing: it is **non-destructive and correctly ordered** - where content exists at two locations, the new one is cleaned before the old is restored, or the item ends up at both - and where it **cannot fully cover a case the gap is recorded rather than emitted**, since doubling the length of what an operator reads at their worst moment is the wrong trade.

**A single-target mutation composed in memory and written once has no partial state to report**, and must not carry the vocabulary for one.

**A missing sole target is a failure, never a skip.** Skip-the-unreadable belongs to enumerate-then-read passes, and applying it to a single target converts a failed operation into a silent success.

**The skip set is narrower than unreadable.** What may be skipped is a file that enumerated but has nothing left to read. A file present and genuinely unreadable fails the operation, and the line between them comes from classifying the failure rather than probing afterwards.

## Naming And Composition

**Convention violations warn and never fail; only unlinkable or filesystem-invalid names refuse.** The naming convention is editorial and has legitimate exceptions, and a primitive enforcing it would be unusable on the files that already violate it.

**No tool computes a name or injects provenance.** The caller decides what something is called and what it records about itself; tools take resolved values.

**Scaffolding is composed, not built in.** Multi-file structures the vault creates together - a folder, its folder note, its task file, its registry entry - are composed from these primitives by whatever knows the convention. Building them in would put the vault's churniest layer inside the thing that must stay stable.

## What Must Not Come Back

Each was settled against real pressure, and each looks like an obvious addition to someone reading the tool set fresh.

- **Inferring the markdown extension.** Folders share the operand space, so it reintroduces the resolution ladder and gives one note two spellings.
- **A resolution ladder, candidate list, or ambiguity gate anywhere.** Operands are paths.
- **A bulk in-place replacement across a body.** Exactly-once matching *is* the self-verification of a surgical edit.
- **A force or purge that bypasses the guards on removal.**
- **Server-side templating, or field-by-field note composition on creation.** A template is read, filled in by the caller, and passed as finished text.
- **A file-general operand on any tool.** The perimeter is uniform; a non-markdown surface is a different tool with different authority.
- **An inert parameter kept for symmetry.** A parameter that cannot change an outcome for the supplied operand is refused, not ignored.

## Beyond The First Pass

`lint`, its suppression companion, and `sync` are out of scope, as is the non-markdown settings surface. Four properties they hold shaped the conventions above and must not be quietly narrowed in their absence:

- **A read-floor tool asserting a write key** is a real shape, not a hypothetical.
- **A structured, actionable remedy attached to a finding** constrains this layer: a remedy names a target tool and a payload of that tool's own arguments, which holds only if a tool's declared argument names are its actual ones.
- **A whole-repository operation is unscopeable**, and its authority is withheld rather than half-granted.
- **The settings surface edits existing non-markdown files, is not bound by path scope, and is gated by its own authority** - which is the authority to change how agents behave, and belongs in no content class. It is bound by the same declaration and takes different sections of it. What keeps that safe is that the declaration lives outside the vault, so an unscoped in-vault tool still cannot rewrite what protects.

When they are picked up, the suppression companion is the one tool whose name and purpose are also open.

## Incidental, Not Carried

Recorded so a reader checking this spec against its sources can tell absence from oversight. Each is real in the current implementation and is an artifact of how it was built rather than a capability:

- Schema derivation from a script's own declarations, hand-authored schema side-cars, and the parsing rules around both
- The argument-name casing split between what a caller sends and what a result carries
- Wire-level encoding of structured arguments at a call boundary
- Ambient injection of scope and authority into a script's execution context
- Serialization depth limits and truncation at the edge of them
- Per-tool standalone-invocability tests: the property is structural, since a front-end that runs a tool without a server is part of the design
- Extension preservation on a constructed destination, which a uniform markdown perimeter makes vacuous
- The names of every argument, result member, and error code in the current surface
