# Core Access Layer

## What This Answers

- [[Spec-Vault-Access-Design.md#Load-Bearing Semantics]] - out-of-scope paths
  unrepresentable rather than checked, met by [[#The Ladder Of Proofs]] and the single
  producer in [[#Producing An Admitted Path]]; containment decided textually, met by
  [[#Paths And Vaults]]; a search that cannot read a file failing the search, met by
  [[#Enumeration And Search]]; ranking that does not search, met by [[#Wikilinks]].
- [[Spec-Vault-Access-Design.md#Guarantees To Callers]] - containment and
  vault-relative terms, met by [[#Paths And Vaults]]; filtering silent and gating loud,
  met by the two entry points in [[#Producing An Admitted Path]]; no parent created on
  the way past and read-failure classification, met by [[#The File Primitives]].
- [[Spec-Vault-Access-Design.md#The Mutex Lock Name]] - the derivation, met by
  [[#The Lock's Identity]]. Acquiring is not here.
- [[Spec-Auth-Requirements.md#What The Layer Must Do]] - every path-touching
  operation through the scope check and read-class operations through the read
  check, met by [[#The Ladder Of Proofs]]; authority handed in and never ambient, met
  by [[#Producing An Admitted Path]]; refusal without an oracle, met by [[#Refusals]]; the
  mid-operation key assertion, met by [[#Producing An Admitted Path]].
- [[Spec-Access-Layer-Capabilities.md#Vault Resolution]] - the canonical name and
  the two structurally distinguishable failures, met by [[#Paths And Vaults]].
- [[Spec-Access-Layer-Capabilities.md#Enumeration]] and
  [[Spec-Access-Layer-Capabilities.md#Content Search]] - both surfaces, the glob,
  the dot switch, and the four outcomes, met by [[#Enumeration And Search]].
- [[Spec-Access-Layer-Capabilities.md#Reading]],
  [[Spec-Access-Layer-Capabilities.md#Writing]],
  [[Spec-Access-Layer-Capabilities.md#Moving]],
  [[Spec-Access-Layer-Capabilities.md#Removal]] and
  [[Spec-Access-Layer-Capabilities.md#Name Validation]] - met by
  [[#The File Primitives]].
- [[Spec-Access-Layer-Capabilities.md#Wikilink Grammar And Resolution]] and
  [[Spec-Obsidian-Link-Behavior.md]] - recognition, the two ranges, and the one
  comparator, met by [[#Wikilinks]].
- [[Spec-Tool-Capabilities.md#Wikilink Maintenance]] - the pass scope does not
  bound, met by [[#The Vault-Wide Pass]].

Not answered here, deliberately:

- [[Spec-Access-Layer-Capabilities.md#The Mutex Lock]] - acquiring, holding, and
  crash recovery are the shared execute path's: [[Design-Tool-Vocabulary.md]].
- [[Spec-Frontmatter-Substrate.md]] - the substrate is
  [[Design-Frontmatter-Surface.md]]; this document supplies the paths it reads and
  writes through.
- [[Spec-Tool-Capabilities.md#The Error Vocabulary]] - a refusal's codes and
  rendering: [[Design-Tool-Vocabulary.md]]. This document fixes what a refusal
  distinguishes, not what it is called on the wire.
- Who constructs the per-call access object, and when:
  [[Design-Tool-Vocabulary.md]].

## Paths And Vaults

A vault and a path within it are different types. Resolution produces the first;
only the first can produce the second.

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

Resolution is total and answers rather than throwing, and its three cases come
from configuration state - `Vaults` versus `SkippedVaults` - so an unknown vault
and a broken one are told apart structurally rather than by message text
([[Spec-Access-Layer-Capabilities.md#Vault Resolution]]). `Name` on a resolved
vault is the configuration's spelling, never the caller's.

Resolution lives here rather than on `VaultConfig` because its product is the
layer's type: configuration holds which vaults exist, and the layer decides what a
usable vault is.

`Vault` is what a whole-vault operation takes as its operand - enumeration, the
census, search, and the lock's identity. The vault root is therefore never a path,
and the retained grammar's refusal to normalize one (`ScopePath.Normalize`) needs
no carve-out. Nothing named `""` is readable, and a glob under a scope that admits
nothing else still runs, because its operand is the vault rather than a path the
scope must admit.

`VaultPath` is proof of containment and nothing else: inside this vault,
well-formed, vault-relative with one separator convention. It is what the layer's
own enumeration hands out, and it carries its vault so an operation taking two
paths can require them to agree.

Case is two questions at once. Equality is case-insensitive, matching scope
semantics, because two spellings name one file. Move needs the other question - a
case-only rename names a destination that already exists - and asks for an ordinal
comparison explicitly, so the carve-out is visible at its one call site rather than
being a default nobody sees.

## The Ladder Of Proofs

The specs describe three strengths of admitted, not one. A grant scoped to
`Projects/Widget` in a vault declaring `Resources/Vault/Guides` always-readable may
read that guide and must not write it; the wikilink pass touches notes it may do
neither to. One type cannot carry that, so admission is in the type rather than in
a check.

```csharp
public sealed record ReadPath  { public VaultPath Path { get; } }
public sealed record WritePath { public VaultPath Path { get; } public ReadPath AsRead(); }
```

A `WritePath` widens to a `ReadPath`, since anything writable is readable. Nothing
narrows the other way. Every primitive states its authority in its signature -
`read` takes a `ReadPath`; writing, creation, directory creation and both ends of a
move take a `WritePath` - so the floor's asymmetry is not a rule to remember. No
expression hands a floor-readable path to a write.

## Producing An Admitted Path

One type produces the admitted pair, and holding the caller's authority is the
whole of what it is:

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

Constructed per call from a `Vault` and the caller's `Grant`, which is what makes
authority handed in rather than ambient: the parameter is an object the caller must
already hold, and no public surface takes a scope. It takes the grant rather than a
bare scope because the layer owes two things to authority, not one - path admission
from `Grant.ScopeIn`, and the mid-operation key assertion
([[Spec-Auth-Requirements.md#What The Layer Must Do]]) from the same grant's op
classes. A key is asserted where the required authority depends on what the work
turned out to touch; the floor for what was *requested* is checked above the layer.

The effective read scope - the grant's roots unioned with the vault's
always-readable roots - is computed once here rather than per candidate, so a
whole-vault enumeration filters against one scope instead of rebuilding the union
for every path it considers.

`ForRead` and `ForWrite` are the loud path: a caller named this path, and the
answer is admitted or refused. `Admits` is the quiet path, filtering paths the
layer itself produced. They are separate members rather than one with a flag,
because the quiet one cannot construct a refusal and so cannot leak one.

## Refusals

Resolution answers; it does not throw. Three refusals are distinguishable, because
callers above need to tell them apart: malformed (escaping, rooted, or empty),
outside the operand perimeter (a dot-directory segment, which no caller may name
whatever its scope), and out of scope.

A refusal carries the caller's own coordinates and nothing else - never whether the
target exists, never the grant's roots, never the subset that failed a check.

The perimeter is a property of the caller-named path, not of containment. The
census reports what a removal destroys, including files under a nested `.git`, so
those paths are representable as `VaultPath`. They are simply not nameable.

## Enumeration And Search

Both surfaces and all three search granularities are operations on `VaultAccess`,
so their results are filtered against the effective read scope by construction
rather than by each caller remembering to. Note enumeration answers markdown only;
the census answers every file. Both take an optional glob and a switch admitting
dot entries, which is off by default.

**Glob matching happens in the layer, over vault-relative paths, not in the
engine.** The requirement is `**`, case-insensitive, matched against the
vault-relative path, and the engine's own glob does not have those semantics on
every platform. Matching after enumeration keeps both surfaces, both front-ends,
and every platform agreeing on what a pattern means, which is the point of the
requirement that matching belong to the layer at all.

The engine is invoked once per call and its output is read to completion before the
operation answers. Streaming would let a caller consume results from a search that
is about to fail, and a search that cannot read a file must fail whole
([[Spec-Vault-Access-Design.md#Load-Bearing Semantics]]) - which is only decidable
once the engine has finished and its error stream has been read.

Four outcomes, distinguishable at the call site: a result that may be empty, a
malformed pattern as a caller error, an unreadable file as a whole-search failure,
and anything else the engine reported as an operational failure carrying its text.
Empty is never a failure and a malformed pattern is never operational, because
collapsing either is unrecoverable above the layer.

The layer reports these to its caller and writes nothing to health state. A failing
call is a call's failure; a layer that could degrade the server from inside one
would give a single caller's bad pattern server-wide visibility.

## The File Primitives

**Reading classifies from the failure.** "Nothing there to read" and "there and
something went wrong" come from the failure the filesystem raised, never from a
separate existence probe, and the classification is part of the read rather than
something a caller may redo. Every enumerate-then-read pass in the system depends
on one definition of nothing-to-read.

**Two write forms, and the creating one refuses an existing target as part of the
write.** The refusal is the filesystem's, from an atomic create, not a check the
layer performs first - a check-then-write leaves a window in which the editor or a
person creates the file and the write destroys it. That refusal is distinguishable
from every other write failure.

**Directory creation is its own primitive**, so materializing a path is a
deliberate call rather than a side effect of writing. It creates one folder under
an existing parent.

**Nothing creates a parent on the way past.** Writing, creating a folder, and a
move destination all fail under a missing parent, naming what was missing in
vault-relative terms - which the path type can render, since it holds segments and
its vault.

**Encoding and terminators are stated once, at the composing seam**: UTF-8 with no
byte-order mark, LF. Nothing detects what was there and nothing preserves another
convention.

**Move is the one primitive taking two paths**, both `WritePath`, both required to
carry the same `Vault`. An occupied destination is an error, compared against the
resolved source ordinally so a case-only rename is not mistaken for a collision;
the outcome says the move was case-only, so a later git pickup can stage it.

**Removal is two operations, not one** - remove a file, remove an empty folder -
because a path does not say which of those it names
([[#What A Path Does Not Assert]]) and neither may guess. Both take a `WritePath`, both refuse from the
attempt rather than from a prior probe, and neither walks a tree: the caller holds
the validated plan and drives it entry by entry
([[Spec-Access-Layer-Capabilities.md#Removal]]).

**The name grammar is one type, applied to the single segment a call brings into
existence** - a creation target, a folder, or a move destination. It is not applied
at path construction, so everything already on disk stays reachable.

## Wikilinks

Two pure pieces, neither of which touches a filesystem.

**Recognition** runs over a note's whole text, frontmatter included, and reports
each occurrence as two byte ranges - the whole link, and the target inside it. A
consumer rewrites a target by writing over its range, so it never parses, re-emits,
or splits the note. One definition serves every consumer, because a link one
consumer sees and another misses is a rewrite that silently misses a link.

**Ranking** is a comparator over a candidate set and the linking note's path,
ordering candidates by nearest relative path with deterministic tie-breaking. It
takes the candidates it is given, finds nothing itself, and is the whole of what
[[Spec-Obsidian-Link-Behavior.md]] fixes - so the observed rule sits in one
comparator that can be changed in a line or two when the editor's behaviour is
re-measured.

## The Vault-Wide Pass

Wikilink maintenance is the one pass scope does not bound: it must reach notes the
grant can neither read nor write. Those operations - enumerate every note, read
one, write one - take merely-contained paths and sit on their own object, obtained
from `VaultAccess` by a call that names what it is.

The separate object is the whole of the mechanism. The operations themselves are
ordinary, so a consumer composes what it needs and no new primitive is required
each time one wants something slightly different. What the seam buys is that
bypassing scope is a deliberate call rather than a method that happens to be within
reach on an object a caller already holds, and that every site doing it is one grep
away when the question is asked.

What may be *reported* out of such a pass - how much of an out-of-scope note
reaches a result, a preview, or a log - is a constraint on the report rather than
on the primitive, and belongs to [[Design-Tool-Vocabulary.md]] with the rest of the
result tiers. That is where it binds the consumer the specs actually worry about, a
minted grant driving an automated caller.

## What A Path Does Not Assert

**Existence.** Containment is textual and touches no filesystem, so a path to
something that does not exist resolves normally - `create` needs exactly that. No
member offers an existence answer, because one would invite the check-then-act
pattern the specs forbid for both creation and reading.

**A conforming name.** Names already on disk are not checked, so a note predating
the grammar stays readable, searchable, and movable from.

**Whether it is a file or a folder.** Deciding that textually is wrong and deciding
it from disk is forbidden, so each operation states what it expects and fails on
the other.

## The Lock's Identity

The derivation of the lock name from the vault root
([[Spec-Vault-Access-Design.md#The Mutex Lock Name]]) is a property of `Vault`,
which already holds the canonicalized root the hash is taken over. It sits in the
layer, not the tool layer, because `VaultWatcher` is not a front-end and must take
the derivation as code rather than re-implement the convention. Nothing in the
layer acquires the lock.

## What This Closes

- **What denotes the vault root.** The root is not a path: resolution produces a
  `Vault`, whole-vault operations take one, and no path denotes it.
  Rejected: a path with no segments, which would be one value admitted under every
  scope, and so an exception inside the property the build most depends on.
- **How many proofs of admission there are.** Two types over a contained path, with
  widening from write to read only, so a primitive's authority is its signature.
  Rejected: one admitted type carrying its class as data, which turns the floor
  asymmetry back into a check every write primitive must remember.
- **What the per-call object holds.** The caller's grant, not a bare scope: the
  layer owes authority both path admission and the mid-operation key assertion, and
  taking the grant means neither is reconstructed from parts.
- **Where vault resolution lives, and what it answers.** In the layer, as the only
  producer of `Vault`, answering a three-case result whose cases come from
  configuration state.
  Rejected: keeping it on `VaultConfig` behind two same-typed exceptions, which is
  the fragile form the capability spec names.
- **Loud and quiet resolution are different members.** Naming a path answers with a
  refusal; filtering a layer-produced path answers with a boolean and cannot
  construct one. Disclosure is then a property of which member exists rather than
  of how carefully each caller handles a result.
- **Where the operand perimeter sits.** Dot-directory paths are representable and
  not nameable: the census must report them, and no caller may name one.
- **Where glob matching happens.** In the layer over vault-relative paths, not
  delegated to the engine, so the pattern means one thing on every surface and
  platform.
- **Engine output is read to completion before answering.** A search that cannot
  read a file must fail whole, which is not decidable until the error stream has
  been read; streaming would hand a caller results from a search about to fail.
- **The layer writes nothing to health state.** An operational failure is returned
  to its caller. A layer that could degrade the server from inside a call would
  give one caller's failure server-wide visibility.
- **How the vault-wide pass sits outside the ladder.** Unscoped enumerate, read
  and write take merely-contained paths and live on their own object, obtained by
  an explicitly-named call, so bypassing scope is deliberate and greppable.
  Rejected: a single operation exchanging link occurrences for substitutions, which
  confines the pass to link text but makes every consumer wanting anything else a
  new primitive and a new decision.
- **Removal is two operations rather than one.** A path does not say whether it
  names a file or a folder, and a removal that inspected the target to find out
  would be the probe every other primitive here avoids. The caller states which it
  means.
- **What the path type does not assert.** Neither existence, nor a conforming name,
  nor whether it names a file or a folder. Each is checked by the operation that
  needs it, where it needs it.
- **Where the lock name derivation sits.** On `Vault`, in the layer, because the
  watcher must take it as code and is not a front-end.
