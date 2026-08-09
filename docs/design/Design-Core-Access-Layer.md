# Core Access Layer

## What This Answers

- [[Spec-Vault-Access-Design.md#Load-Bearing Semantics]] - out-of-scope paths unrepresentable rather than checked, met by [[#The Ladder Of Proofs]] and the single producer in [[#Producing An Admitted Path]]; containment decided textually, met by [[#Paths And Vaults]]; a search that cannot read a file failing the search, met by [[#Enumeration And Search]]; ranking that does not search, met by [[#Wikilinks]].
- [[Spec-Vault-Access-Design.md#Guarantees To Callers]] - containment and vault-relative terms, met by [[#Paths And Vaults]]; filtering silent and gating loud, met by the two entry points in [[#Producing An Admitted Path]]; no parent created on the way past and read-failure classification, met by [[#The File Primitives]].
- [[Spec-Vault-Access-Design.md#The Mutex Lock Name]] - the derivation, met by [[#The Lock's Identity]]. Acquiring is not here.
- [[Spec-Auth-Requirements.md#What The Layer Must Do]] - every path-touching operation through the scope check and read-class operations through the read check, met by [[#The Ladder Of Proofs]]; authority handed in and never ambient, met by [[#Producing An Admitted Path]]; refusal without an oracle, met by [[#Refusals]]; the mid-operation key assertion, met by [[#Producing An Admitted Path]].
- [[Spec-Access-Layer-Capabilities.md#Vault Resolution]] - the canonical name and the two structurally distinguishable failures, met by [[#Paths And Vaults]].
- [[Spec-Access-Layer-Capabilities.md#Enumeration]] and [[Spec-Access-Layer-Capabilities.md#Content Search]] - both surfaces, the glob, the dot switch, and the four outcomes, met by [[#Enumeration And Search]].
- [[Spec-Access-Layer-Capabilities.md#Reading]], [[Spec-Access-Layer-Capabilities.md#Writing]], [[Spec-Access-Layer-Capabilities.md#Moving]], [[Spec-Access-Layer-Capabilities.md#Removal]] and [[Spec-Access-Layer-Capabilities.md#Name Validation]] - met by [[#The File Primitives]].
- [[Spec-Access-Layer-Capabilities.md#Wikilink Grammar And Resolution]] and [[Spec-Obsidian-Link-Behavior.md]] - recognition, the two ranges, target matching, and the one comparator, met by [[#Wikilinks]].
- [[Spec-Tool-Capabilities.md#Wikilink Maintenance]] - the pass scope does not bound, met by [[#The Vault-Wide Pass]].

Not answered here, deliberately:

- [[Spec-Access-Layer-Capabilities.md#The Mutex Lock]] - acquiring, holding, and crash recovery are the shared execute path's: [[Design-Front-End-Projection.md]].
- [[Spec-Frontmatter-Substrate.md]] - the substrate is [[Design-Frontmatter-Surface.md]]; this document supplies the paths it reads and writes through.
- [[Spec-Tool-Capabilities.md#The Error Vocabulary]] - a refusal's codes and rendering: [[Design-Tool-Vocabulary.md]]. This document fixes what a refusal distinguishes, not what it is called on the wire.
- Who constructs the per-call access object, and when: [[Design-Front-End-Projection.md]].

## Paths And Vaults

A vault and a path within it are different types. Resolution produces the first; only the first can produce the second.

```csharp
public sealed record Vault(string Name, string Root, PathPolicy Policy)
{
    public static VaultResolution Resolve(VaultConfig config, string? name);
}

public abstract record VaultResolution
{
    public sealed record Resolved(Vault Vault)                  : VaultResolution;
    public sealed record Unknown(string Name)                   : VaultResolution;
    public sealed record Unavailable(string Name, string Reason) : VaultResolution;
}

public sealed record VaultPath { public Vault Vault { get; } /* relative segments */ }
```

Resolution is total and answers rather than throwing, and its three cases come from configuration state - `Vaults` versus `SkippedVaults` - so an unknown vault and a broken one are told apart structurally rather than by message text ([[Spec-Access-Layer-Capabilities.md#Vault Resolution]]). `Name` on a resolved vault is the configuration's spelling, never the caller's.

Resolution lives here rather than on `VaultConfig` because its product is the layer's type: configuration holds which vaults exist, and the layer decides what a usable vault is.

`Vault` is what a whole-vault operation takes as its operand - enumeration, the census, search, and the lock's identity. The vault root is therefore never a path, and the retained grammar's refusal to normalize one (`ScopePath.Normalize`) needs no carve-out. Nothing named `""` is readable, and a glob under a scope that admits nothing else still runs, because its operand is the vault rather than a path the scope must admit.

`VaultPath` is proof of containment and nothing else: inside this vault, well-formed, vault-relative with one separator convention. It is what the layer's own enumeration hands out, and it carries its vault so an operation taking two paths can require them to agree.

Case is two questions at once. Equality is case-insensitive, matching scope semantics, because two spellings name one file. Move needs the other question - a case-only rename names a destination that already exists - and asks for an ordinal comparison explicitly, so the carve-out is visible at its one call site rather than being a default nobody sees.

## The Ladder Of Proofs

The specs describe three strengths of admitted, not one. A grant scoped to `Projects/Widget` in a vault declaring `Resources/Vault/Guides` always-readable may read that guide and must not write it; the wikilink pass touches notes it may do neither to. One type cannot carry that, so admission is in the type rather than in a check.

```csharp
public sealed record ReadPath  { public VaultPath Path { get; } }
public sealed record WritePath { public VaultPath Path { get; } public ReadPath AsRead(); }
```

A `WritePath` widens to a `ReadPath`, since anything writable is readable. Nothing narrows the other way. Every primitive states its authority in its signature - `read` takes a `ReadPath`; writing, creation, directory creation and both ends of a move take a `WritePath` - so the floor's asymmetry is not a rule to remember. No expression hands a floor-readable path to a write.

## Producing An Admitted Path

One type produces the admitted pair, and holding the caller's authority is the whole of what it is:

```csharp
public sealed class VaultAccess              // a resolved vault, plus the caller's grant
{
    public Vault Vault { get; }
    public PathResolution<ReadPath>  ForRead(string relativePath);
    public PathResolution<WritePath> ForWrite(string relativePath);
    public bool Admits(VaultPath candidate);
    public void AssertKey(string opClass);   // mid-operation, once the outcome is known
}
```

Constructed per call from a `Vault` and the caller's `Grant`, which is what makes authority handed in rather than ambient: the parameter is an object the caller must already hold, and no public surface takes a scope. It takes the grant rather than a bare scope because the layer owes two things to authority, not one - path admission from `Grant.ScopeIn`, and the mid-operation key assertion ([[Spec-Auth-Requirements.md#What The Layer Must Do]]) from the same grant's op classes. A key is asserted where the required authority depends on what the work turned out to touch; the floor for what was *requested* is checked above the layer.

The effective read scope - the grant's roots unioned with the vault's always-readable roots - is computed once here rather than per candidate, so a whole-vault enumeration filters against one scope instead of rebuilding the union for every path it considers.

`ForWrite` checks the grant's own roots and nothing wider ([[Spec-Auth-Requirements.md#What The Layer Must Do]]). An always-readable root therefore admits a read and refuses a write, which is the asymmetry [[#The Ladder Of Proofs]] carries in the type.

`ForRead` and `ForWrite` are the loud path: a caller named this path, and the answer is admitted or refused. `Admits` is the quiet path, filtering paths the layer itself produced. They are separate members rather than one with a flag, because the quiet one cannot construct a refusal and so cannot leak one.

## Refusals

Resolution answers; it does not throw. Three refusals are distinguishable, because callers above need to tell them apart: malformed (escaping, rooted, or empty), outside the operand perimeter (a dot-directory segment, which no caller may name whatever its scope), and out of scope.
Each has its own code in [[Design-Tool-Vocabulary.md#The Error Catalogue]]: malformed is `invalid_path`, the perimeter is `path_excluded`, out of scope is `out_of_scope`. So `path_excluded` names the dot rule, not anything a vault declares.

A refusal carries the caller's own coordinates and nothing else - never whether the target exists, never the grant's roots, never the subset that failed a check.

The perimeter is a property of the caller-named path, not of containment. A destruction census asks for dot entries so that the guard refusing on a nested `.git` has something to find, and so that the dot entries a removal really does destroy are reported to the caller. Those paths are therefore representable as `VaultPath`, and simply not nameable.

## Enumeration And Search

Both surfaces and all three search granularities are operations on `VaultAccess`, so their results are filtered against the effective read scope by construction rather than by each caller remembering to. Note enumeration answers markdown only; the census answers every file. Both take an optional glob and a switch admitting dot entries, which is off by default.

**The caller's pattern goes to the engine as `--iglob`**, which supplies both `**` and case-insensitivity. The engine is invoked `--hidden --no-ignore`: its hidden-file skipping is not the dot rule - hidden on Windows is a file attribute, which the dot-prefixed directories do not carry - and the census must see ignored files.

**A pattern anchors the way the engine anchors it**: one containing no separator matches a path's final segment at any depth, one containing a separator anchors at the vault root. Stated as a requirement in [[Spec-Access-Layer-Capabilities.md#Enumeration]], because the two answers differ and a caller cannot tell which it got.

**Scope and note enumeration's markdown restriction are predicates over what the engine returns.** Neither is expressible as a second `--iglob`: positive globs union rather than intersect, so a second one widens the answer. **The dot rule is the exception and prunes the walk** - a negative glob intersects, so with dot entries off the engine never enters a dot-directory rather than reading it and discarding the results. The pruning is what keeps a default search from reading the object store under `.git`, and from failing over a file a concurrent git operation holds locked that the search was never going to include. The predicate keeps the dot rule as well, so correctness does not rest on the engine's glob semantics alone.

The engine is invoked once per call and its output is read to completion before the operation answers. Streaming would let a caller consume results from a search that is about to fail, and a search that cannot read a file must fail whole ([[Spec-Vault-Access-Design.md#Load-Bearing Semantics]]) - which is only decidable once the engine has finished and its error stream has been read.

Five outcomes, distinguishable at the call site: a result that may be empty, a malformed pattern as a caller error, an unreadable file as a whole-search failure, anything else the engine reported as an operational failure carrying its text, and an engine that could not be started at all. Glob and regex both arrive malformed as the engine's refusal, so one classification covers them. The last is separate from the fourth because a run that never started cannot be made to start by repeating the call, and the code the tool layer gives each one differs on exactly that ([[Design-Tool-Vocabulary.md#The Error Catalogue]]).

**The engine's location is interpreted once, in the layer, from a value a front-end hands it.** A front-end reads the configured text out of its own configuration and passes it with the directory to resolve a relative one against; the layer supplies the fallback to `rg` on `PATH`, the resolution, and the check that an explicitly configured executable exists, recording an absent one on the health surface as degraded. The layer still discovers nothing for itself, which is the constraint that keeps it off ambient state. Rejected: each front-end constructing the engine from its own configuration, which is where the fallback, the resolution base, and the missing existence check drift apart between two hosts that mean the same thing.

The layer reports these to its caller and writes nothing to health state. A failing call is a call's failure; a layer that could degrade the server from inside one would give a single caller's bad pattern server-wide visibility.

## The File Primitives

**Reading classifies from the failure.** "Nothing there to read" and "there and something went wrong" come from the failure the filesystem raised, never from a separate existence probe, and the classification is part of the read rather than something a caller may redo.

**Two write forms, and the creating one refuses an existing target as part of the write.** The refusal is the filesystem's, from an atomic create, not a check the layer performs first. It is distinguishable from every other write failure.

**Directory creation is its own primitive**, so materializing a path is a deliberate call rather than a side effect of writing. It creates one folder under an existing parent, and refuses an occupied one.

**That refusal is the one place a primitive probes before it acts.** The platform's directory creation is idempotent and reports no difference between making a folder and finding one, so nothing but a prior check can answer whether the target was occupied. The probe is racy in the way every check-then-act is, and the exception holds because a folder create destroys nothing: losing the race misreports which writer made the folder, where checking before a note write would risk destroying content that appeared in between.

**Nothing creates a parent on the way past.** A missing parent is named in vault-relative terms, which the path type can render since it holds segments and its vault.

**Encoding and terminators are stated once, at the composing seam**: UTF-8 with no byte-order mark, LF.

**Move is the one primitive taking two paths**, both `WritePath`, both required to carry the same `Vault`. An occupied destination is an error, compared against the resolved source ordinally so a case-only rename is not mistaken for a collision; the outcome says the move was case-only, so a later git pickup can stage it.

**Removal is two operations, not one** - remove a file, remove an empty folder - because a path does not say which of those it names ([[#What A Path Does Not Assert]]) and neither may guess. Both take a `WritePath`, both refuse from the attempt rather than from a prior probe, and neither walks a tree: the caller holds the validated plan and drives it entry by entry ([[Spec-Access-Layer-Capabilities.md#Removal]]).

**The name grammar is one type, applied to the single segment a call brings into existence** - a creation target, a folder, or a move destination. It is not applied at path construction, so everything already on disk stays reachable.

## Wikilinks

Three pure pieces, none of which touches a filesystem.

**Recognition** runs over a note's whole text, frontmatter included, and reports each occurrence as two byte ranges - the whole link, and the target inside it. It takes text and answers occurrences: no path goes in and none comes back. The consumer's enumeration already holds the path, and is what carries it to ranking. A consumer rewrites a target by writing over its range, so it never parses, re-emits, or splits the note. One definition serves every consumer, because a link one consumer sees and another misses is a rewrite that silently misses a link.

**Matching** answers whether a link's target names a given path.
A target comes in two forms and each matches its own way. A path-qualified target matches on a suffix of whole path components. A bare target matches on basename alone, and ranking decides which of several same-named candidates it resolves to.
Repair and the census ask this same question, so one piece answers it for both. Both ways of getting it wrong are silent: a link that repair fails to match is left pointing at nothing, and a link that the census fails to match is an orphan nobody reports.

**Ranking** is a comparator over a candidate set and the linking note's path, splitting the candidates into those inside the linking note's folder subtree and those outside it, ordering within each group by the character length of the relative target that reaches the candidate, and breaking a tie on ordinal comparison of the vault paths. It takes the candidates it is given, finds nothing itself, and is the whole of what [[Spec-Obsidian-Link-Behavior.md]] fixes - so the observed rule sits in one comparator that can be changed in a line or two when the editor's behaviour is re-measured.

Ranking models what following a link does. The editor's autocomplete popup orders a duplicated basename by a comparator of its own, and the two consumers - pinning, which asks whether a change alters what an existing link opens, and repair, which asks which note a target names - are both about resolution and neither about the popup. Nothing in the layer models the popup's order.

## The Vault-Wide Pass

Wikilink maintenance is the one pass scope does not bound: it must reach notes the grant can neither read nor write. Those operations - enumerate every note, enumerate every file, read a note, write a note - take merely-contained paths and sit on their own object, obtained from `VaultAccess` by a call that names what it is.

Both enumeration domains are here because a relocation asks both, for different questions: which notes the pass reads, rewrites and counts basenames over, and which non-note files a folder rename physically carries, so that an embed naming a carried attachment is repaired with the rest. Neither admits dot entries, unlike the scoped file surface, which offers the switch a destruction census needs. Reading and writing stay note-only - the pass rewrites link text, and nothing else in the vault holds any.

The separate object is the whole of the mechanism. The operations themselves are ordinary, so a consumer composes what it needs rather than asking for a new primitive each time. What the seam buys is that bypassing scope is a deliberate call rather than a method within reach on an object a caller already holds, and that every site doing it is one grep away.

What may be *reported* out of such a pass - how much of an out-of-scope note reaches a result, a preview, or a log - is a constraint on the report rather than on the primitive, and belongs to [[Design-Tool-Vocabulary.md]] with the rest of the result tiers. That is where it binds the consumer the specs actually worry about, a minted grant driving an automated caller.

## What A Path Does Not Assert

**Existence.** Containment is textual and touches no filesystem, so a path to something that does not exist resolves normally - `create` needs exactly that. No member offers an existence answer, because a general one would invite the check-then-act pattern the specs forbid: a reader and a note writer both take their answer from the attempt. Folder creation is the single exception and probes inside its own primitive, where the platform reports no difference between making a folder and finding one ([[#The File Primitives]]).

**A conforming name.** Names already on disk are not checked, so a note predating the grammar stays readable, searchable, and movable from.

**Whether it is a file or a folder.** Deciding that textually is wrong and deciding it from disk is forbidden, so each operation states what it expects and fails on the other.

## The Lock's Identity

The derivation of the lock name from the vault root ([[Spec-Vault-Access-Design.md#The Mutex Lock Name]]) is a property of `Vault`, which already holds the canonicalized root the hash is taken over. It sits in the layer, not the tool layer, because `VaultWatcher` is not a front-end and must take the derivation as code rather than re-implement the convention. Nothing in the layer acquires the lock.

## What This Closes

- **What denotes the vault root.** The root is not a path: resolution produces a `Vault`, whole-vault operations take one, and no path denotes it. Rejected: a path with no segments, which would be one value admitted under every scope, and so an exception inside the property the build most depends on.
- **How many proofs of admission there are.** Two types over a contained path, with widening from write to read only, so a primitive's authority is its signature. Rejected: one admitted type carrying its class as data, which turns the floor asymmetry back into a check every write primitive must remember.
- **What the per-call object holds.** The caller's grant, not a bare scope, since the layer owes authority both path admission and the key assertion.
- **Where vault resolution lives, and what it answers.** In the layer, as the only producer of `Vault`, answering a three-case result whose cases come from configuration state. Rejected: keeping it on `VaultConfig` behind two same-typed exceptions, which is the fragile form the capability spec names.
- **Loud and quiet resolution are different members.** Disclosure is then a property of which member exists rather than of how carefully each caller handles a result.
- **Where the operand perimeter sits.** Dot-directory paths are representable and not nameable: the census must report them, and no caller may name one.
- **Where glob matching happens.** In the engine, as `--iglob`, with the engine's anchoring stated as the requirement. Scope and the markdown restriction stay in the layer as predicates over the result; the dot rule prunes at the engine through a negative glob when dot entries are off, negative globs intersecting where positive ones union, with the layer's predicate kept beside the pruning. Rejected: matching in the layer, which reads the spec's "matching belongs here" as excluding the engine rather than each caller, at the cost of a second definition of the pattern and the engine's pruning.
- **Engine output is read to completion before answering.** Whether a search must fail whole is not decidable until the error stream has been read.
- **No per-call operation writes to health state.** An operational failure is returned to its caller. The only thing the layer records is what it found while interpreting configuration handed to it at startup, which is a condition of the host rather than of one call.
- **Where the engine's configured location is interpreted.** In the layer, once, from the configured text and a resolution base a front-end supplies: unset or empty means `rg` on `PATH`, a relative value resolves against the directory holding the host's own configuration, and an explicitly configured executable that is not there is recorded as degraded and still used, so search refuses while every operation that does not search keeps working. Fatal is rejected: it would take a host down over the two tools that search and refuse the six that do not.
- **How the vault-wide pass sits outside the ladder.** Unscoped enumerate, read and write live on their own object, so bypassing scope is deliberate and greppable. Rejected: a single operation exchanging link occurrences for substitutions, which confines the pass to link text but makes every consumer wanting anything else a new primitive.
- **What a leading separator on a caller's path means.** One is dropped, so `/Projects/Widget.md` names what `Projects/Widget.md` names, in either separator spelling and on either platform. Every path a caller spells is measured from the vault root already, so the separator has one available meaning and reading it costs nothing; the wikilink namespace already reads it that way ([[Spec-Obsidian-Link-Behavior.md]]). Two are refused, since `//server/share` is a UNC path and dropping one separator would turn the host's name into a folder segment, and a drive-qualified path is refused as before. Rejected: refusing the spelling outright, which is right for a spelling with more than one possible meaning and buys nothing for one with a single meaning.
- **Which enumeration domains the vault-wide pass offers.** Both of them, neither admitting dot entries, since a relocation needs the notes it reads and rewrites and the non-note files a folder rename carries, and neither question wants a dot path. Rejected: note enumeration widened to answer both, which would put every attachment into the list the pass reads and rewrites.
- **Matching is a piece of its own, beside recognition and ranking.** Rejected: folding it into ranking, which is handed its candidates and finds none itself.
- **Removal is two operations rather than one.** A path does not say which it names, and inspecting the target would be a probe where the attempt already answers.
- **Folder creation refuses an occupied target, and probes to do it.** No non-recursive directory create fails when the target exists, so the outcome a creating form owes its caller and the rule that a check and a write are one operation cannot both hold. The outcome wins: what that rule protects is content destroyed by the write following the gap, and a folder create destroys nothing. Rejected: reporting the platform's idempotent success, which tells a caller it made something that was already there.
- **What the path type does not assert.** Neither existence, nor a conforming name, nor whether it names a file or a folder. Each is checked by the operation that needs it, where it needs it.
- **Where the lock name derivation sits.** On `Vault`, in the layer, because the watcher must take it as code and is not a front-end.
