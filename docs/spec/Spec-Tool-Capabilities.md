# Tool Capabilities And Semantics

Forward specification for the tool layer: what each tool must accomplish, and which of its behaviors are load-bearing rather than incidental. `Spec-Vault-Access-Design.md` fixes the guarantees the layer beneath owes and `Spec-Access-Layer-Capabilities.md` states the operations it must offer; this doc states what the tools do with them.

**Capabilities and semantics only.** Argument names, result fields, error codes, and the shape of any envelope are designed in this build. A sentence here that could only be satisfied by one spelling has overreached.

Organized by concern rather than by tool: every hard question in this layer is cross-cutting, and a per-tool organization would state each answer seven times and let the copies drift.

## Where The Boundary Sits

Owned elsewhere:

- Guarantees, fixed constraints, and the front-end boundary: `Spec-Vault-Access-Design.md`
- The operations available to build a tool from: `Spec-Access-Layer-Capabilities.md`
- Frontmatter parsing, mutation, validation, and the single write path: `Spec-Frontmatter-Substrate.md`
- The declaration of recognized keys and their required write authorities: `Spec-Frontmatter-Registry.md`
- The grant, scope, and op-class model: `Spec-Auth-Requirements.md`
- The editor's bare-wikilink resolution, which the wikilink work matches rather than defines: `Spec-Obsidian-Link-Behavior.md`

**The dividing line against the access layer: the layer answers the question, the tool decides which question to ask and how the answer comes back.** The layer says which files match a pattern; `glob` chooses which enumeration surface to ask, drops what path policy excludes, and returns the result as a path list. A question that could be answered in either doc belongs to the layer.

## What Each Tool Must Accomplish

Seven tools in the first pass. Their names and purposes are fixed; how those purposes are expressed is open.

- **`read`** - return one note's whole content, its frontmatter, or its body, with a whole-file staleness token when the caller asks for one. Those three are the whole set; there is no line range and no section addressing
- **`glob`** - the only name-and-path search surface, and the discovery step that turns a name into a path
- **`grep`** - the only content-search surface, at the three granularities the layer offers
- **`edit`** - the only way to mutate an existing note: ordered frontmatter operations plus at most one body change, applied as a single outcome
- **`create`** - the only way to bring a note or a folder into existence, and the tool that owns directory creation, holding existing wikilink resolution in place
- **`delete`** - the only way to remove a note or a folder, reporting the full blast radius
- **`move`** - the only way to relocate or rename a note or folder, keeping the wikilink graph intact

Two properties of the set:

**These seven are the whole write surface for notes and folders.** Once direct file access is denied, anything they cannot express cannot be done at all.

**Existence, content, and location are three separate tools.** No tool performs a second one as a side effect. A tool that created on a miss would turn a typo into a new note; a tool that moved on a path change would break every wikilink silently.

## The Operand Perimeter

**Every operand is a markdown note or a folder.** One uniform rule across all seven tools, with no per-tool variation and no file-general operand anywhere.

**The perimeter is narrower than an operation's footprint, deliberately.** What a caller can *name* is a note or a folder. What an operation touches inside its own footprint is whatever the operation requires: folder creation can seed the `.gitkeep` that makes a folder exist in the repository, and a recursive folder removal removes everything beneath it whatever its type.

**Internal reach is derived from the operand, never caller-directed.** A caller can ask folder creation for a `.gitkeep` but cannot name one; folder removal removes what lies under the folder that was named rather than taking a file list.

**`.gitkeep` is optional and off by default.** Creation writes it only when asked for it. Most folders hold files immediately and need nothing; a folder expected to exist while empty asks for it, because the repository does not track an empty directory.

**Non-markdown vault content is the editor's.** Attachments and images are placed and maintained through the editor; no tool creates, relocates, or discovers one. A boundary, not a gap.

**This restriction is the tool set's own policy, not a property of the layer.** The layer keeps its file-capable primitives and both enumeration surfaces, so a future tool can work over the same primitives with a different file-kind policy. Containment and scope are not open to that choice: every tool gets them, and a file-kind filter narrows what is already contained and in scope.

## The Operand Vocabulary

**Every operand naming an existing thing is a vault-relative path** - no bare names, no link syntax, no per-tool name resolution. Duplicating a resolution ladder onto every operand would give the vault as many notions of "the same name" as it has tools.

Three consequences are load-bearing:

- **A path is unambiguous by definition**, so no tool needs a candidate list, a disambiguation gate, or an ambiguity failure. If the error vocabulary has a code for ambiguity, something is resolving names that should not be.
- **Discovery-then-act is the intended two-call idiom**, matching what an agent already does with its native tools.
- **Wikilink syntax survives only as internal machinery** where the wikilink graph is the subject. It is never an operand, and the grammar belongs to the layer (`Spec-Access-Layer-Capabilities.md:Wikilink Grammar And Resolution`).

**The markdown extension is never inferred.** Folders share the operand space, so a name without one is genuinely ambiguous between a note and a folder, and resolving that would reintroduce the ladder. A note-kind operation handed an extensionless path refuses and **names the spelling that would have worked**, so the miss costs one retry rather than a diagnosis.

**Destinations are constructed, not resolved.** A path naming something that does not exist yet is not subject to the resolve-to-exactly-one rule; the checks that apply to it are occupancy, well-formedness, and containment.

**A kind assertion says whether the caller means a note or a folder.** It checks intent; it never resolves an ambiguous path. Where a tool can already tell which it has, the assertion is optional and lets a caller who thinks it is acting on one note fail fast instead of acting on a folder of fifty.

**The assertion is absent where it could not change an outcome.** It is required in one case only: where the two kinds take different arguments, so the kind decides which arguments apply. There, an argument that means nothing for the chosen kind is refused rather than ignored.

**The layer validates every new name** (`Spec-Access-Layer-Capabilities.md:Name Validation`). It owns the grammar and refuses any name outside it. Where a conforming spelling can be derived from what the caller sent, the refusal names it, so the miss costs one retry. Choosing among conforming names is the caller's.

## Result-Count Semantics

**Whether a result count is an error follows from what a tool is for, and is fixed per tool rather than per call.**

- **A search tool treats any count as a result.** Zero matches is an empty result and never a failure; an existence check must not require a caller to catch. A caller needing exactly one enforces that itself, because only the caller knows it needed one.
- **A tool operating on an existing target requires exactly one and fails on a miss.** Returning an absent value pushes a check into every caller, where one will forget it.
- **A tool bringing something into existence inverts that: existence is its failure.** An occupied target is refused rather than overwritten or merged, distinguishably from every other refusal, because the caller's next move is entirely different. Occupancy itself belongs to the layer - both the creating write and the move refuse a target that already exists (`Spec-Access-Layer-Capabilities.md:Writing`, `Spec-Access-Layer-Capabilities.md:Moving`); what the tool owes is reading that answer rather than deciding a second one.

## Path Policy

Two rules decide which paths this tool set may touch. Nothing under a dot-directory is reachable at all - not an operand, not enumerated, and no declaration admits one - and that rule is fixed in code rather than declared (`Spec-Access-Layer-Capabilities.md:Enumeration`). Every other path is reachable, and a vault's path policy says what mutating it requires. Narrowing further is the grant's scope roots.

**Path policy is declared per vault, in the host's vault configuration**, because its targets are one vault's layout. An entry attaches modifiers to a target: `mutation_requires` names the op class mutating that target requires, and `always_readable` puts the target in every grant's read scope for that vault (`Spec-Auth-Requirements.md:What The Layer Must Do`).

**A target is one of two things.** A **path**, matched as a whole-segment prefix, so a file entry matches that file and a directory entry matches everything beneath it. Or a **named structural rule** from a closed set - currently only the folder note, a note whose basename matches its parent folder. The rule is code; the declaration decides whether it applies and at what price.

**Mutating a target contributes its `mutation_requires` class to the required authority**, and contributions union - no entry can clear one another contributed.

Matching rules, each closing an evasion:

- **Anchored at the vault root.** A batch run rooted below the vault would hand the matcher relative paths that cannot match, and the policy would silently do nothing on the run touching the most files.
- **Whole segments**, so a similarly named sibling directory is not caught by accident.
- **Case-insensitive**, over a path with separators folded and relative navigation resolved away, so a caller cannot reach an excluded file by spelling it differently.

**Exclusion is a distinct refusal from an authorization denial.** A denial means *your grant does not reach this*, remedied by elevation. Exclusion means *the tooling does not own this file*, which no grant changes. Collapsing them sends a caller to widen a grant that cannot help.

**One declaration does not mean one enforcer.** Path policy is the tool set's; the access layer never reads it and stays file-general. Writes consult it on the single write path beside the authority assertion, **including a body-only edit**; tools that remove or relocate consult it at their own validate phase, since neither routes through a frontmatter write.

**A sweeping tool applies the policy to its writing passes only**; its reporting passes keep full coverage. A tool acting on one named file fails loudly when the policy refuses it. A tool sweeping a set skips such a file instead, since the caller never named it individually and one refusal should not end the run.

**Filtering is silent, gating is loud.** Enumeration and search both omit excluded paths without saying so; a named excluded path errors, distinguishably from a miss. The layer's own default already keeps dot entries out of a result; the policy is what refuses one named as an operand, and what binds the one tool that asks the layer for them.

**Policy and scope compose as a conjunction, and scope only narrows.** A grant naming an excluded path still does not reach it. Policy belongs to the tool set rather than to a grant, so it binds both front-ends and neither is a way around it.

**A future non-markdown surface is bound by the same policy**, so its opt-outs are stated in one place rather than being an absence someone has to notice.

## The Argument Surface

**One declaration per tool, projected into both front-ends** (`Spec-Vault-Access-Design.md:The Front-End Boundary`). The obligation it creates here: **no tool carries front-end-specific argument handling**, because a tool that did would have to know which front-end called it. The advertised schema, the CLI's option surface, and the type arguments bind onto are three renderings of one contract, not three artifacts kept in step.

**Advertised is not enforced.** Conditional requiredness, mutual exclusivity between optional arguments, and required forms only a description can carry have no clean expression in a client-facing schema. The requirement is not that the schema express everything but that **the tool remain the sole enforcement point**, so nothing downstream treats schema conformance as permission.

**An unrecognized value of an enumerated argument is refused in the tool's own error vocabulary.** Whatever binds arguments has its own rejection path, and a refusal escaping through it arrives outside the coded envelope callers branch on. Such values are trimmed and case-folded before comparison.

**A structured argument stays structured.** An argument whose contract is an ordered list of operations is that on the wire - not a caller-encoded string, not flattened into siblings. Encoding a front-end needs is that front-end's business.

**The frontmatter operation vocabulary is primitive.** The operations are the four the substrate makes expressible - set a scalar field, remove a field, add a block-list entry, remove a block-list entry - and no more. An entry kind is not an operation: a caller adds an entry and the entry states which variant it is, and the variants come from the declaration rather than from a set of operation names. Higher-level notions such as "add a skill directive" belong to whatever composes the call.

**Names are snake_case, on arguments and result members alike.** One convention on the wire, so nothing translates between a sent name and a returned one.

**The CLI derives both spellings from that one name.** An option is presented in kebab-case, which is what an operator reading `--help` expects, and the snake_case spelling is registered as an alias so either parses. Both are generated from the declared name; neither is authored per tool.

**Scope and held authority are not arguments**, so the declared argument surface is the whole of what a caller controls - which makes that guarantee checkable by reading one declaration.

## Result Tiers

**Two tiers, split by what the output is for.** Results a model reads as content - note text, search hits, path lists - return in the plain native shape an agent already consumes, with each shape's own empty-case convention; structure there costs tokens and buys nothing. Results a caller branches on are structured, which covers every mutation and any assessment whose purpose is a decision.

**One content-tier result carries structure**: a read asked for a staleness token returns the content and the token together.

**Mutation results share one envelope**, under four obligations:

- **A tool emits no member it can never populate.** Uniformity across the set is an outcome, not a goal: a member reaches every mutation result where every tool genuinely does that thing, and forcing one onto a tool that does not is inventing a claim it never made. A caller has already chosen its tool by the time it reads a result, so a shape fitted to that tool costs it nothing.
- **A member carrying no meaning for a particular call is omitted.** The test is whether absence would tell a caller anything different from emptiness. Where it would not, the member goes: a warning member appears when there is something to warn about and is absent otherwise, since no warning reads the same either way.
- **Empty is not the same as meaningless.** Where absence and emptiness would part company, the member stays. A census that ran and found nothing is a finding, and dropping it leaves "looked and found nothing" indistinguishable from "never looked".
- **A list is a list of identities, not a count.** Counts derive from lists; lists do not derive from counts.

**A mutation result reports which authorities the operation actually asserted beyond the tool's floor**, for the audit trail. An empty report stays, by the test above: absence would not distinguish an operation that asserted nothing from a tool that never asserts.

## The Preview Contract

**Any tool whose mutation has a discoverable blast radius offers a preview, through one gate with one meaning across the layer.** **`delete` and `move` both preview by default**: the routine call reports what the mutation would do, and a second call commits it. The gate is **absent where there is nothing to discover**; a preview that echoes the request back trains callers to skip previews generally. **`create` has no gate**, though a creation can pin wikilinks in notes the caller did not name. It destroys nothing, pinning holds the resolution that already applied, and the result reports what was pinned.

**A plan is a faithful prediction of its own result:**

- **A plan emits the shape its own result will carry**, so a plan and the result of the same call differ in exactly one discriminating member. A reduced shape cannot be compared against what it predicted, and a plan carrying members its result will omit is not a prediction of it.
- **Path-valued members name post-mutation paths**, so no caller translates between two path vocabularies.
- **A plan takes the same lock, runs the same validation, and refuses everything a real call would.** A preview is the same refusal earlier, never a way around one; validation reads state that changes, so an unlocked plan can return a prediction already stale.

**One exception is allowed**: a tool whose output is a sequence of steps rather than a single outcome. If a tool cannot state that sequence in one sentence, it does not have one. No tool in the first pass does.

## The Error Vocabulary

Errors are coded, stable, human-messaged, and oracle-free (`Spec-Vault-Access-Design.md:Guarantees To Callers`); the code set is the build's to define, in one catalogue every tool draws from. What is fixed here is **which distinctions must remain distinguishable at the call site**, each collapse being unrecoverable above the tool:

- **A miss versus a failure to read**, taken from the failure itself rather than a separate probe
- **A grant that does not reach versus tooling that does not own.** One is remedied by elevation, the other by nothing a grant can change
- **Authorization versus routing.** Where both would fire, the routing answer is more actionable and wins
- **A caller error versus an engine failure**, in search especially - the pair most easily collapsed into one "search failed"
- **A transient throttle versus a permanent refusal**, or a retryable condition routes into a branch that never retries
- **A vault nobody configured versus one configured but unavailable** (`Spec-Access-Layer-Capabilities.md:Vault Resolution`)
- **A partial mutation versus a clean refusal**

**One code is carried over: `lock_timeout`.** The mutex lock could not be acquired before the timeout elapsed, and the payload carries that timeout. It is raised where the lock is taken, so no tool catches it and it surfaces unchanged - otherwise every write tool re-derives the same condition by matching message text.

**A payload is there to be used in a retry.** The test is whether the caller can lift the value straight into a corrected call. A refusal whose only remedy is elevation has no corrected call, so it carries no payload. Payloads therefore differ from code to code, and callers branch on the code rather than on what came back with it.

**A payload diagnoses and does not bypass.** A staleness refusal returning the current token would let a caller retry with it and clobber content it never read; recovery is a re-read.

**A refusal is not an enumeration surface.** No payload carries a set computed by filtering the request against authority the caller lacks - not what is held, and not the violating subset, which is the same disclosure one item at a time.

**A refusal raised beneath a tool surfaces unchanged.** Catching and re-shaping reintroduces reasoning about message text. Where a refusal needs the call's coordinates, they are attached at the raise.

**Failure ordering is part of the contract.** A refusal must fire before anything is locked or mutated, so each tool states the order of argument validation, vault resolution, path resolution, and lock acquisition. That order differs between mutating and non-mutating tools. A tool that reports only the first failure fixes one thing per retry, which is the intent - these are not batch reports.

## Staleness And Chaining

**The read-to-write window is the one an agent occupies** - model time during which the editor, another session, or a person can change the file. The mutex lock does nothing about it; this guard is separate and caller-carried.

- **The token is a digest of the file's bytes as stored**, never of decoded or composed text. That is what makes a token from a read and a token from a mutation the same value, usable in either direction.
- **The token comes back with the content, from the same read**, so it describes exactly the bytes the caller received. A caller asks for it when it intends to write; a read that does not ask returns content alone.
- **The token covers the whole file regardless of which part was read**, so nothing authorizes a write from a partial read.
- **The guard is required on the one operation that can destroy content the caller never saw** and optional elsewhere: a surgical change self-verifies through its own anchor, a wholesale replacement has nothing to self-verify against.
- **A mutation returns the token carrying forward**, taken from the file after the write, so a chain needs no interleaved read.

## Authority

The grant model is given (`Spec-Auth-Requirements.md`). Four dimensions contribute to what a tool call requires, and they union.

**A declared floor**, the least authority any use of the tool requires, checked by the shared execute path before the tool runs. Anything more is asserted inside the tool on the path that needs it, so the requirements add up on their own and no combined token exists. A floor need not be a write class on a tool that can write: an assessment tool with an opt-in fix mode has a read floor and asserts a write key, which is what lets a read-only caller run the assessment.

**Key-level authority, classified by outcome** over what the operation turned out to change. Classification by declared intent classifies by what the caller said they were doing, and every injection path is an operation aimed at something innocuous that mutates the protected layer anyway. The predicate is shared (`Spec-Frontmatter-Substrate.md:Write Authority Classification`) so it cannot drift per tool, and the degenerate cases - composing a new note against no original, removing a note's frontmatter entirely - fall out of it rather than becoming second rules.

**Artifact-level authority, declared by path** ("Path Policy"). Some files exist to instruct agents, and their bodies are that instruction rather than prose - the root manifest, folder notes, and the shared guides. For these the key-level diff is not enough, because a body-only write to one changes the agentic layer while touching no key. A `mutation_requires` entry names them and states the authority; the requirement is declared membership of a path set, exactly as key-level authority is declared membership of a key set. This is why `Spec-Frontmatter-Substrate.md`'s guarantee that a body-only write never requires elevated authority holds for every note *except* those an entry names.

**Artifact authority applies to what an operation names and to everything in its target set.** Creating, editing, or removing an artifact always requires the declared class. Relocating one depends on how the artifact was declared:

- **A path entry always requires it.** A declared file or folder cannot be renamed or moved without the class, whether it stays under the declared path or leaves it. Otherwise the protection is trivially avoided: move the file aside, edit it, move it back.
- **A structural rule requires it only when artifact status changes.** Renaming a note so its name matches its folder makes it a folder note, which requires the class. Renaming a folder renames its folder note too, and that note is still a folder note, so it requires nothing.

Artifact authority never contributes for a file merely read, nor for one incidentally rewritten to keep its wikilinks valid - the rewrites a relocation causes in fifty other notes are maintenance, not authoring.

**Wikilink maintenance, required by relocation and by a creation that duplicates a basename.** Moving a note changes what every link to it must say. Creating a note whose basename already exists changes what bare links to that basename resolve to. Either set of links can be anywhere, so the pass runs over every note in the vault. Only dot-directories are left out; a path entry that prices a write does not narrow the pass. A creation taking a unique name runs no pass and requires none of this.

The pass is not scope-checked. Scope has nothing to bind on a vault-wide pass, and running a check anyway would imply a limit that is not there. The relocation's own source and destination, and the creation's own target, are scope-checked normally.

This authority permits one thing: replacing the target inside a link that already exists. It permits no other edit to any note the pass touches. The substitution reads no frontmatter, so it produces no key diff and needs no key-level authority.

**Assertion happens in the validate phase with zero mutation on denial**, and what was asserted is echoed on success. Where a tool classifies a set, the refusal names the offending members - a caller told that something in a folder needed authority it lacks, without being told what, cannot act.

**Not every authority dimension is scopeable.** Where an operation is whole-repository or whole-vault by construction, path scope has nothing to bind, and a tool must not perform a scope check that falsely implies it does. Such authority is withheld rather than half-granted.

## Wikilink Maintenance

Three tools touch the wikilink graph, over the layer's single grammar and ranking rule. **Relocation maintains the graph, creation protects it, and removal reports on it** - a relocation leaves a new target to point at, a creation leaves a second candidate for a name, and a removal leaves nothing.

For relocation:

- **The rewrite replaces bytes and parses nothing.** It runs over the note's whole text, frontmatter included, using the extents the layer supplies (`Spec-Access-Layer-Capabilities.md:Wikilink Grammar And Resolution`). Every byte outside a replaced target survives, so repairing fifty notes changes the wikilink targets in them and nothing else. The name grammar is what makes this safe: a conforming name cannot break out of whatever encloses the wikilink.
- **The pass covers every note in the vault.** Repairing only the notes inside the caller's scope leaves the rest of the vault pointing at nothing. Path policy still applies, so excluded paths are skipped here as everywhere else.
- **A note whose frontmatter will not parse is still repaired**, since the substitution parses nothing. Notes the frontmatter write path refuses are not holes in the graph.
- **Both wikilink forms are covered**, the plain one and the embedding one. Only the target portion is rewritten; decorations a wikilink carries survive verbatim.
- **A path-qualified wikilink matches on a suffix of whole path components**, because the editor writes partial paths: `Redesign/Spec-Tool-Capabilities` matches `Projects/Vault-MCP-Server/Inbox/Redesign/Spec-Tool-Capabilities`, and `sign/Spec-Tool-Capabilities` matches nothing. A bare wikilink matches only where it *resolves to* the relocated item under the editor's own rule - bare wikilinks under duplicate names are deterministic, not ambiguous.
- **Emission is canonical rather than minimal**: the short form where the name is unique, the qualified form where it is not. Computing a shortest sufficient suffix produces wikilinks the editor rewrites on its next touch.
- **A relocation never fails on ambiguity and never re-aims a wikilink.** Where it would change what another wikilink resolves to, that wikilink is pinned to the target it currently has and the operation proceeds. Pinning freezes; it never redirects. A gate that could trip will trip in the middle of a hundred-file pass.

For creation:

- **A basename already in use triggers the pass, and nothing else does.** Creation asks the basename census before it writes. A unique name runs no pass and asserts no wikilink authority.
- **Bystander bare wikilinks are pinned.** A bare wikilink whose resolution the new note would change is rewritten to the qualified form naming the target it resolves to now. Every other wikilink is left as it is, and no link is ever aimed at the new note.
- **Pinning runs before the note is written.** Interrupted after pinning, the vault holds links naming a target that is still correct; interrupted after writing, it holds bare wikilinks that have silently changed meaning.
- **The result reports which wikilinks were pinned.** There is no preview gate.
- **Folder creation runs no pass.** It brings no note into existence.

For removal:

- **The scan covers every note in the vault**, the same reach as relocation's rewrite. A report that stopped at a scope boundary would under-report exactly where the caller most needs the truth.
- **A bare wikilink is reported only where it resolved to something removed.** A same-named bystander elsewhere is that wikilink's real target.
- **Wikilinks inside the removed set are not reported**, and each surviving occurrence is reported individually, because a caller fixing them visits each one.
- **Removal pins nothing, and the asymmetry with creation is deliberate.** Deleting one of two same-named notes leaves bare wikilinks resolving to the survivor. Each is reported and none is rewritten, because the caller's intent for them is not knowable here.

**A folder relocation carries coupled effects under one operation**: the relocation, the folder note's rename, and wikilink repair for everything contained. A folder note is renamed to match its new folder; a folder that has none gets none created. Contained names do not change, so the repair is a single prefix substitution and the folder note is the only name event.

**Neither tool authors routing.** Registry entries and navigation frontmatter aimed at an item's role are composed by the caller. These tools track names; they do not decide meaning.

## Enumeration Domains

**Which domain a tool enumerates is a semantic choice, not a shared-code convenience.** Discovery is the only tool that returns an enumeration to the caller. Search uses one to select the files it reads. Relocation walks a folder's contents and runs the basename census behind link emission and the wikilink scan. Removal builds a destruction plan and a backlink census. The layer offers two domains (`Spec-Access-Layer-Capabilities.md:Enumeration`), and a tool asking the wrong one gets a confidently wrong answer.

- **A tool whose subject is a note** wants note enumeration - discovery, search, the wikilink scan, and the uniqueness census, each of which answers a question about notes.
- **A tool asking what is about to be destroyed** wants the census, and asks it for dot entries too. A non-markdown or hidden file is still on disk, still destroyed, and still possibly something that must be classified before it goes. Getting this one wrong fails three ways at once, all silent: a folder holding only non-markdown files reads as empty and skips the not-empty guard, the blast radius under-reports, and authority classification runs over an incomplete set.

**Widening the layer's answer to serve one consumer is not the fix.** It changes what discovery returns and what the uniqueness census counts, and that census decides whether a rewritten link is emitted short or qualified. A tool needing a different domain asks for a different domain.

**Three separate questions compose here, and only one is the tool's.** What the layer's surfaces can see is the layer's, and the answer is everything but dot entries unless the caller asks for them (`Spec-Access-Layer-Capabilities.md:Enumeration`). Which surface a tool asks is the tool's. Which paths this tool set may reach is "Path Policy"'s. Narrowing therefore happens only in the last two, both of which a caller can reason about.

## Destruction

**Removal takes no destructive authority class of its own.** It is authorized as a mutation like any other. Scope decides the folder that was named, not the files beneath it: a folder in scope has its whole subtree in scope, so a removal never meets a child it cannot reach (`Spec-Auth-Requirements.md:What The Layer May Assume`).

**Nothing is removed before its plan is reported.** A caller sees the full set a removal destroys, and the refusal that precedes a bulk removal carries that same set. The caller therefore retries against the actual file list rather than against the folder name it first sent.

**The tool makes no claim about what survives removal** ("What Must Not Come Back"). It reports what it is about to destroy; what can be restored afterwards is not its assessment to offer.

**Three guards on removal.**

- **A `.git` directory anywhere in the removal set refuses the removal.** It is a separate repository with its own history, and removing it from here destroys work this vault never tracked. The check runs over the destruction plan the tool has already built, from a census that asked for dot entries and before policy filtering - either one would otherwise hide a `.git` directory from the check that exists to find it. The refusal names what it found.
- **A folder with anything in it is not removed by a plain call.** The call fails and says the folder is not empty. To remove it and its contents, the caller repeats the call asking for a recursive removal. The recursive form states which operation the caller wants; it does not suppress a check. A folder holding nothing but a `.gitkeep` counts as empty here, and a plain call removes both - the `.gitkeep` is the tooling's own, not content a caller put there.
- **The whole plan is validated before anything is removed.** Nothing is deleted while the plan is still being built, so a refusal cannot arrive with part of the work already done.

**No guard has an override.** There is no force argument, no skip flag, and no spelling of the call that proceeds past a refusal. A gate the same call can waive stops nobody, because a caller that hits the refusal simply resends with the waiver attached.

**Removal reports orphaning it caused rather than repairing it.** Completeness is a sweeping concern, not a primitive's.

## Multi-Target Mutation

**Any mutation touching more than one file plans, validates the whole set, then applies**, so a refusal is impossible to reach after the first write.

**The mutex lock is not atomicity.** It means no other tool interleaves. It does not mean rollback. A failure partway through names exactly what landed and what did not, and carries recovery steps for that state.

Two things about those steps matter. They must be **non-destructive and in the right order**: where content now exists in two places, the new copy is removed before the old one is restored, or the item ends up in both. And where the steps **cannot cover a case, that gap is recorded here rather than printed into the error** - the error stays short enough to act on.

**A mutation composed in memory and written once has no partial state to report**, and must not carry the vocabulary for one. Whether that holds is a property of the call rather than the tool: a creation taking a unique name writes once, and a creation that pins fifty wikilinks is a multi-target mutation under the rules above.

**A missing sole target is a failure, never a skip.** Skip-the-unreadable belongs to enumerate-then-read passes, and applying it to a single target converts a failed operation into a silent success.

**Not everything unreadable may be skipped.** What may be skipped is a file that enumerated but has nothing left to read. A file present and genuinely unreadable fails the operation, and the line between them comes from classifying the failure rather than probing afterwards.

## Naming And Composition

**A name outside the grammar is refused, not warned** (`Spec-Access-Layer-Capabilities.md:Name Validation`). Anything else about naming - doc-type prefixes, what a note is called - is editorial, has legitimate exceptions, and is enforced nowhere.

**No tool computes a name or injects provenance.** The caller decides what something is called and what it records about itself; tools take resolved values.

**Scaffolding is composed, not built in.** Multi-file structures the vault creates together - a folder, its folder note, its task file, its registry entry - are composed from these primitives by whatever knows the convention. Building them in would put the vault's churniest layer inside the thing that must stay stable.

## What Must Not Come Back

Each of these looks like an obvious thing to add. None of them belongs.

- **Inferring the markdown extension.** Folders share the operand space, so it reintroduces the resolution ladder and gives one note two spellings.
- **A resolution ladder, candidate list, or ambiguity gate anywhere.** Operands are paths.
- **A caller-directed bulk replacement across a body.** Exactly-once matching *is* the self-verification of a surgical edit. Wikilink repair is different: the caller never says what it matches, the relocation determines that.
- **A force or purge that bypasses the guards on removal.**
- **A per-file recoverability verdict on removal.** Whether a removed file can be brought back turns on the repository's state and the caller's own, and a verdict computed at removal time is a safety claim the tool cannot back: a tracked file with uncommitted edits reads as restorable when what survives is a version the caller never chose to keep. Reporting the plan is the tool's job; reasoning about the repository is not.
- **Server-side templating, or field-by-field note composition on creation.** A template is read, filled in by the caller, and passed as finished text.
- **A file-general operand on any tool.** The perimeter is uniform; a non-markdown surface is a different tool with different authority.
- **An inert parameter kept for symmetry.** A parameter that cannot change an outcome for the supplied operand is refused, not ignored.

## Beyond The First Pass

`lint`, its suppression companion, and `sync` are out of scope, as is the non-markdown settings surface. Four properties they hold shaped the conventions above and must not be quietly narrowed in their absence:

- **A read-floor tool asserting a write key** is a real shape, not a hypothetical.
- **A structured, actionable remedy attached to a finding** constrains this layer. A remedy names a target tool and a payload of that tool's own arguments, so a tool's declared argument names must be the names it actually takes.
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
