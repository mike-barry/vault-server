# Access Layer Capabilities

What the vault access layer must be able to do, and which of those behaviors are load-bearing. The companion `Spec-Vault-Access-Design.md` states the guarantees the layer owes its callers and the constraints it is built under; this doc states the operations that must exist for those guarantees to have anything to apply to.

**One source per requirement.** Where a subject belongs to another spec or to source that can be read directly, this doc points at it rather than restating it; where the design spec already carries a requirement, it is cited rather than repeated.

No mechanism is described and no function is named, for the reason the design spec gives: describing how something works would commit the build to a design this work exists to choose fresh.

## Where The Boundary Sits

Owned elsewhere:

- Guarantees, load-bearing semantics, and fixed constraints: `Spec-Vault-Access-Design.md`
- Scope, grants, and the obligations across the seam with the authorization code: `Spec-Auth-Requirements.md`
- Frontmatter parsing, mutation, and validation: `Spec-Frontmatter-Substrate.md` and `Spec-Frontmatter-Registry.md`
- What each tool accomplishes using these capabilities: `Spec-Tool-Capabilities.md`
- Configuration loading and the vault resolution rules: source, read directly

What sits outside the layer - op-class floor enforcement, convention, and repository operations - is enumerated in `Spec-Vault-Access-Design.md:What The Layer Owns`. Note-naming rules fall under convention.

Everything else the layer must do is here.

## Vault Resolution

The layer resolves a vault name to a root. The rules and the configuration behind them are retained; two obligations sit on the layer's side of that seam.

**Resolution returns the canonical vault name, never the caller's spelling.** A name that differed only in case or form must not be echoed back as though it were canonical, for the same reason emitted paths are vault-relative: results are quoted into later calls, and a result that reflects the request rather than the resolution makes the round trip lossy in a way nothing detects.

**The two failure modes are distinguishable from configuration state, not from message text.** A vault nobody configured and a vault configured but currently unavailable are different answers - the first is a caller error and the second an operational one, and something downstream will retry exactly one of them. The distinction must be derivable from what the layer already holds. Recovering it by inspecting an error message is the fragile form, and it breaks on the first reworded message.

## Enumeration

Ripgrep backs both surfaces here and content search below, rooted at the vault directory (`Spec-Vault-Access-Design.md:Fixed Constraints`).

Two surfaces, with different consumers and different answers:

- **Note enumeration** - markdown files only
- **The file census** - every file, whatever its extension

Both must exist. The census backs whole-vault passes that must not miss a non-markdown file, where a silent omission is a wrong answer rather than a narrower one; note enumeration backs everything whose subject is a note.

**Both take an optional pattern**, using standard glob syntax with `**` for recursive descent, compared case-insensitively. A pattern containing no separator matches a path's final segment at any depth; a pattern containing one is anchored at the vault root. The anchoring is required rather than incidental: `*.md` meaning every note and `*.md` meaning the root's notes are different answers, and nothing downstream can tell which it got. With a pattern the surface is a name-and-path search; with no pattern it returns everything in its domain. Matching belongs here rather than to each caller, so a tool searching by name and a tool taking a census agree on what exists instead of holding two notions of it.

**No path under a dot-directory is reachable by the tool set** - not an operand, not enumerated, and no declaration admits one. The tooling's content lives outside them. The rule is fixed in code rather than declared, which is why nothing a read depends on comes from a configuration file.

**Dot entries are out by default at this layer, and both surfaces take a switch that admits them.** A path with a dot-prefixed segment at any depth is dropped unless the caller asks for it. The default is what keeps a caller off the vault copies that live under `.claude`; the switch is what lets a destruction census see a nested `.git` and report every file a removal takes with it. Content search takes the same default.

**No ignore-file rule.** A gitignored file is still on disk and still destroyed, so honoring `.gitignore` would hide from the census exactly what it exists to report.

Beyond the dot rule and the caller's own pattern, nothing is dropped here. The rest of the narrowing is path policy and scope, above the layer where a caller can reason about it.

## Content Search

Regex search over note content, on the same engine, able to answer at three granularities: matching lines with their locations, the set of files containing a match, and a count per file.

The cheaper shapes are their own capability rather than the richest one with fields discarded. A caller wanting a file list should not pay to materialize every match in order to throw them away, and on a whole-vault search that difference is the whole cost of the operation.

**Four outcomes must be distinguishable at the call site:**

- **No match is a result, not a failure.** An empty answer is the correct answer to a well-formed question. Folding it into an error path forces every caller to interpret a failure to find out whether anything actually went wrong
- **A malformed pattern is a caller error**, reported as one and distinct from the engine failing
- **A file that cannot be read fails the whole search** (`Spec-Vault-Access-Design.md:Load-Bearing Semantics`), rather than quietly narrowing the result
- **Anything else the engine reports is an operational failure**, carrying what the engine said

The first two are the pair most easily collapsed into one "search failed," and the collapse is unrecoverable above the layer.

## Reading

Read a note's content, and classify a failure to read.

The classification requirement is the design spec's. The capability statement here is that classification belongs to the read itself: it is not a second operation a caller may skip, and not one any caller may reimplement, because every enumerate-then-read pass in the system depends on the same definition of "nothing to read."

## Writing

Two forms: replace an existing note's content wholesale, and create a note that must not already exist.

**The creating form refuses a target whose name is already taken, and what occupies it makes no difference.** A file and a folder at the path are one answer: the name is unavailable, and the caller's remedy is the same either way. The refusal is distinguishable from every other write failure, so it is never confused with a permission problem or a device error.

**The refusal is decided before the write, and the write refuses the same condition again.** Creation opens its target in a mode that fails when anything is already there, so a file the editor, another session or a person creates in between is refused rather than overwritten, and is refused as the same thing. Deciding it first is what lets a caller that must do other work before the write - a pass that rewrites other notes first - learn of the refusal before that work instead of after it. Reading is unaffected: a failure to read is still classified from the failure rather than from a separate probe (`Spec-Vault-Access-Design.md:Guarantees To Callers`).

**Encoding is stated rather than inherited: UTF-8 with no byte-order mark.** The editor and the repository own these files jointly, and a byte-order mark appearing on rewritten notes is a diff on every file the tooling touches - the failure is invisible per-file and unmistakable across a sync. Line terminators are settled in `Spec-Frontmatter-Substrate.md`, and the answer is the same for a whole-note write.

A write brings no directory into existence, and a missing parent is named in vault-relative terms; both are design-spec guarantees. Both forms obey them, and there is no third form.

**Directory creation is a separate primitive, and only the tool that owns creation calls it.** It creates one folder under a parent that already exists, and refuses a missing parent exactly as a write does. Keeping it out of the write is what makes materializing a path one tool's decision rather than something every writer does as a side effect.

**It refuses an occupied target too, on the same terms as a note creation.** A file and a folder at the path are one answer there as well. The platform offers no non-recursive directory creation that fails when the target is already there, so unlike a note creation the check is the only thing that can answer: a creating form that reported making a folder someone else had already made would contradict what creation means everywhere else. Losing the race means reporting an occupied target that appeared a moment earlier, or a creation another writer had just made, and neither answer costs a caller anything it cannot re-ask for.

## Moving

Move is a layer primitive: the one mutating operation touching two paths, both resolved and scope-checked by the same construction (`Spec-Vault-Access-Design.md:What The Layer Owns`). Only the relocation is the primitive - wikilink repair, the folder note's rename, protective pinning, and partial-failure reporting are tool-layer work composed above it.

**An occupied destination is an error.** Move never overwrites and never merges. The one carve-out is the source itself: a case-only rename names a destination that already exists (`Spec-Vault-Access-Design.md:Fixed Constraints`), so occupancy is compared against the resolved source case-sensitively.

**A missing destination parent is an error.** Move brings no folder into existence; the destination's parent must already be there.

**Case-only renames are allowed**, where the new spelling satisfies the name grammar ("Name Validation"). Git does not see the change under the platform's default case handling, and the layer cannot fix that: repository operations are outside it ("Where The Boundary Sits"). The move therefore reports that it was case-only, so a later git pickup can stage the rename and propagate the new spelling.

## Removal

Remove one entry: a file, or a folder with nothing in it.

**The layer removes what it is named and never walks a tree.** A removal's plan is built, reported, and validated in full before anything is destroyed, and a failure partway through names exactly what landed and what did not (`Spec-Tool-Capabilities.md:Destruction`, `Spec-Tool-Capabilities.md:Multi-Target Mutation`). Both belong to the caller that holds the plan. A layer that recursed would hold the only record of how far it got, leaving the caller to report a boundary it did not observe - and the census above already produces the file list a removal works from, so nothing is served by deriving it a second time further down.

**A folder with anything in it is not removed, and the refusal comes from the attempt.** Not from an emptiness check the layer runs first: the check and the removal must be one operation for the same reason the creating write refuses an existing target as part of writing, and reading classifies rather than probing.

**A target that is not there is classified, not guessed.** "Nothing there to remove" and "there and something went wrong" are distinguishable, and come from the failure itself. A removal runs against a plan built a moment earlier, so an entry that vanished in between is a different answer from one that could not be removed, and only the caller can decide which of those matters to it.

## Name Validation

**The layer decides whether a name is acceptable, and nothing above it decides differently.** One grammar, checked on the one segment a call brings into existence - the creation target, or the move destination. Nothing else in the path is checked, because every other segment already exists.

**The grammar.** A name is one or more groups joined by single dashes. Each group is one or more ASCII letters, digits, or `+` and `&`, and the first character of the name is an uppercase letter or a digit. A note ends in `.md` and its stem satisfies the grammar; a folder is the grammar alone. Nothing else is allowed - no spaces, no underscores, no other punctuation, no non-ASCII, no leading or trailing dash.

The grammar exists for containment, not style. Wikilink repair substitutes a name into note text and into quoted frontmatter values without parsing either (`Spec-Tool-Capabilities.md:Wikilink Maintenance`). A name holding a quote, a bracket, or a line break could end the construct around it and corrupt the note. A conforming name cannot, so the substitution needs no escaping.

**Names already on disk are not checked.** Reading, enumeration, search, and moving a file *from* its current name all work on whatever exists, so a file that predates the grammar stays reachable and can be renamed into conformance.

**`.gitkeep` is not a caller-supplied name** and does not satisfy the grammar. No caller can name it (`Spec-Tool-Capabilities.md:The Operand Perimeter`).

## The Mutex Lock

The design spec covers how the lock's name is derived, the watcher's use of that same derivation, crash recovery, and the fact that the shared execute path is what acquires it. Two consequences of it living there:

**The hold is synchronous and stays on one thread.** The execute path acquires the lock, runs the tool, and releases, with no suspension point in between.

**Nothing below the execute path acquires it.** Neither the tools nor the layer's write primitives take the lock - the execute path holds it around the whole invocation, so a write primitive called outside a tool runs with no lock held and can overlap another writer.

## Wikilink Grammar And Resolution

Bare-wikilink ranking is design-spec material - empirical, pure, and isolated to a line or two. The capability alongside it:

**The layer owns the wikilink grammar.** Recognizing a link in note text, deriving the target it names, and matching that target against a candidate path are all the layer's, not each caller's.

**Recognition runs over a note's whole text, frontmatter included.** The vault's navigation graph lives in frontmatter values, so a grammar that stopped at the body would under-report to every consumer.

**Each occurrence reports two byte ranges** - the offset and length of the whole link, and of the target inside it. In `![[Notes/Old-Name|Alias]]`, one range is the whole link and the other is `Notes/Old-Name`. A consumer replaces a target by writing over its range, so it never parses the note, re-emits it, or splits it into frontmatter and body.

**A substitution stays inside the target and never crosses a line boundary.** That, plus the name grammar, keeps a rewrite from creating or moving a frontmatter fence or ending a quoted value.

Four consumers read link text - wikilink repair on move, wikilink bystander updates on create, the link census on delete, and link linting - and the grammar is the thing that must not fork between them. A link one consumer recognizes and another does not yields a rewrite that misses a wikilink or a census that under-reports, and both fail silently, which is why one definition is a requirement and not a convenience.
