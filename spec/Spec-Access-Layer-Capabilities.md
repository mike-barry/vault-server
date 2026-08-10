# Access Layer Capabilities

What the vault access layer must be able to do, and which of those behaviors are load-bearing. The companion [[Spec-Vault-Access-Design]] states the guarantees the layer owes its callers and the constraints it is built under; this doc states the operations that must exist for those guarantees to have anything to apply to.

**One source per requirement.** Where a subject belongs to another spec or to source that can be read directly, this doc points at it rather than restating it; where the design spec already carries a requirement, it is cited rather than repeated.

Mechanism is absent by intent, and no function is named, on the design spec's own reasoning: a shape-flavored description would pre-commit the build to a shape this work exists to choose freshly.

## Where The Boundary Sits

Owned elsewhere:

- Guarantees, fixed constraints, and the decisions the build must make explicitly: [[Spec-Vault-Access-Design]]
- Scope, grants, and the obligations across the seam with the authorization code: [[Spec-Auth-Requirements]]
- Frontmatter parsing, mutation, and validation: [[Spec-Frontmatter-Substrate]] and [[Spec-Frontmatter-Registry]]
- What each tool accomplishes using these capabilities: [[Spec-Tool-Capabilities]]
- Configuration loading and the vault resolution rules: source, read directly

What sits outside the layer - op-class floor enforcement, directory creation, convention, and repository operations - is enumerated in [[Spec-Vault-Access-Design#What The Layer Owns]]. Note-naming rules fall under convention.

Everything else the layer must do is here.

## Vault Resolution

The layer resolves a vault name to a root. The rules and the configuration behind them are retained; two obligations sit on the layer's side of that seam.

**Resolution returns the canonical vault name, never the caller's spelling.** A name that differed only in case or form must not be echoed back as though it were canonical, for the same reason emitted paths are vault-relative: results are quoted into later calls, and a result that reflects the request rather than the resolution makes the round trip lossy in a way nothing detects.

**The two failure modes are distinguishable from configuration state, not from message text.** A vault nobody configured and a vault configured but currently unavailable are different answers - the first is a caller error and the second an operational one, and something downstream will retry exactly one of them. The distinction must be derivable from what the layer already holds. Recovering it by inspecting an error message is the fragile form, and it breaks on the first reworded message.

## Enumeration

Two surfaces, with different consumers and different answers:

- **Note enumeration** - markdown files only
- **The file census** - every file, whatever its extension

Both must exist. The census backs whole-vault passes that must not miss a non-markdown file, where a silent omission is a wrong answer rather than a narrower one; note enumeration backs everything whose subject is a note.

**Neither surface hides anything** - no hidden-file rule, no ignore-file rule, no filtering of any kind. Both report what is on disk, differing only in whether non-markdown files are in the answer.

Visibility bounds what every caller above can reach, so anything hidden here is unreachable by any policy stacked on it, and narrowing belongs where a caller can reason about it: declared policy and scope. A layer honoring ignore rules would also hide ignored files from the census, whose whole job is to answer what is about to be destroyed.

## Content Search

Regex search over note content, able to answer at three granularities: matching lines with their locations, the set of files containing a match, and a count per file.

The cheaper shapes are their own capability rather than the richest one with fields discarded. A caller wanting a file list should not pay to materialize every match in order to throw them away, and on a whole-vault search that difference is the whole cost of the operation.

**Four outcomes must be distinguishable at the call site:**

- **No match is a result, not a failure.** An empty answer is the correct answer to a well-formed question. Folding it into an error path forces every caller to interpret a failure to find out whether anything actually went wrong
- **A malformed pattern is a caller error**, reported as one and distinct from the engine failing
- **A file that cannot be read fails the whole search** ([[Spec-Vault-Access-Design#Load-Bearing Semantics]]), rather than quietly narrowing the result
- **Anything else the engine reports is an operational failure**, carrying what the engine said

The first two are the pair most easily collapsed into one "search failed," and the collapse is unrecoverable above the layer.

## Reading

Read a note's content, and classify a failure to read.

The classification requirement is the design spec's. The capability statement here is that classification belongs to the read itself: it is not a second operation a caller may skip, and not one any caller may reimplement, because every enumerate-then-read pass in the system depends on the same definition of "nothing to read."

## Writing

Replace a note's content wholesale.

**Encoding is stated rather than inherited: UTF-8 with no byte-order mark.** The editor and the repository own these files jointly, and a byte-order mark appearing on rewritten notes is a diff on every file the tooling touches - the failure is invisible per-file and unmistakable across a sync. Line terminators are settled in [[Spec-Frontmatter-Substrate]], and the answer is the same for a whole-note write.

A write brings no directory into existence, and a missing parent is named in vault-relative terms; both are design-spec guarantees. What this doc adds is that there is no second writing path to route around them - the guarantee is a property of the only way to write, not of the way most callers write.

## Moving

Move is a layer primitive: the one mutating operation touching two paths, both resolved and scope-checked by the same construction ([[Spec-Vault-Access-Design#What The Layer Owns]]). Only the relocation is the primitive - the reference rewrite, the folder-note invariant, protective pinning, and partial-failure reporting are tool-layer work composed above it.

**An occupied destination is an error.** Move never overwrites and never merges. The one carve-out is the source itself: on a case-insensitive filesystem a case-only rename names a destination that already exists, so occupancy is compared against the resolved source case-sensitively.

**Case-only renames are allowed.** The change is invisible to the repository under the platform's default case handling, and the layer cannot fix that - repository operations are outside it ([[#Where The Boundary Sits]]). Git is out of scope for this build, but a later pickup makes a forced staged rename available to propagate a tracked file's new spelling, so the implementation reports a case-only rename as such rather than leaving the condition undetectable.

## Mutation Serialization

Identity derivation, the watcher's shared use of it, signalled crash recovery, and placement in the shared execute path are design-spec material. Two consequences of that placement:

**The hold spans one synchronous invocation.** Mutex ownership is thread-scoped - the crash-recovery signal comes from the OS releasing the lock when its owning *thread* dies. A hold that migrates between threads, as an asynchronous execute path does at any suspension point, fails release and may raise no crash signal at all. Neither failure appears under test.

**The layer's write primitives do not acquire it.** Serialization is a property of the execute path, not the layer, so a write primitive reached outside a tool is unserialized.

## Link Grammar And Resolution

Bare-link ranking is design-spec material - empirical, pure, and isolated to a line or two. The capability alongside it:

**The layer owns the wikilink grammar.** Recognizing a link in note text, deriving the target it names, and matching that target against a candidate path are all the layer's, not each caller's.

Three consumers read link text - reference rewriting on move, the link census on delete, and link linting - and the grammar is the thing that must not fork between them. A link one consumer recognizes and another does not yields a rewrite that misses a reference or a census that under-reports, and both fail silently, which is why one definition is the requirement rather than an economy.
