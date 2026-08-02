# Core Access Layer

## What This Answers

- [[Spec-Vault-Access-Design.md#Load-Bearing Semantics]] - out-of-scope paths
  unrepresentable rather than checked, met by §The Ladder Of Proofs and the single
  producer in §Producing An Admitted Path.
- [[Spec-Vault-Access-Design.md#Guarantees To Callers]] - containment and
  vault-relative terms, met by §Paths And Vaults; filtering silent and gating loud,
  met by the two entry points in §Producing An Admitted Path.
- [[Spec-Auth-Requirements.md#What The Layer Must Do]] - every path-touching
  operation through the scope check and read-class operations through the read
  check, met by §The Ladder Of Proofs; authority handed in and never ambient, met
  by §Producing An Admitted Path; refusal without an oracle, met by §Refusals.
- [[Spec-Auth-Requirements.md#What The Layer May Assume]] - scope never subtracts
  and a malformed element fails the whole check, both properties of the retained
  primitives §The Ladder Of Proofs builds on.
- [[Spec-Access-Layer-Capabilities.md#Enumeration]] - the operand of a whole-vault
  pass, met by the vault being a type rather than a path (§Paths And Vaults).
- [[Spec-Access-Layer-Capabilities.md#Name Validation]] - checked on the one new
  segment rather than at construction, met by §What A Path Does Not Assert.
- [[Spec-Tool-Capabilities.md#Wikilink Maintenance]] - the pass that scope does not
  bound, met by §The Unscoped Rewrite.

Not answered here, deliberately:

- [[Spec-Vault-Access-Design.md#The Mutex Lock Name]] - the derivation lands in the
  layer so `VaultWatcher` takes it as code, but acquiring is the shared execute
  path's: [[Design-Tool-Vocabulary.md]].
- [[Spec-Frontmatter-Substrate.md]] - the substrate is
  [[Design-Frontmatter-Surface.md]]; this document supplies only the paths it reads
  and writes through.
- Who constructs the layer's per-call access object, and the error vocabulary a
  refusal is rendered into: [[Design-Tool-Vocabulary.md]].

## Paths And Vaults

A vault and a path within it are different types. Vault resolution produces the
first; only the first can produce the second.

```csharp
public sealed record Vault(string Name, string Root, PathPolicy Policy);

public sealed record VaultPath { public Vault Vault { get; } /* relative segments */ }
```

`Vault` is what a whole-vault operation takes as its operand - enumeration, the
file census, content search, and the lock's identity. The vault root is therefore
never a path, and the retained grammar's refusal to normalize one
(`ScopePath.Normalize`) needs no carve-out. Nothing named `""` is readable, and a
glob under a scope that admits nothing else still runs, because its operand is the
vault rather than a path the scope must admit
([[Spec-Auth-Requirements.md#What The Layer Must Do]], filter silently and gate
loudly).

`VaultPath` is proof of containment and of nothing else: inside this vault,
well-formed, and vault-relative with one separator convention. It is what the
layer's own enumeration hands out, and it carries its vault so an operation taking
two paths can require them to agree.

Case is where a path is two questions at once. Equality is case-insensitive,
matching scope semantics, because two spellings name one file. Move needs the
other question - a case-only rename names a destination that already exists
([[Spec-Access-Layer-Capabilities.md#Moving]]) - and asks for an ordinal
comparison explicitly, so the carve-out is visible at its one call site instead of
being a default nobody sees.

## The Ladder Of Proofs

The specs describe three strengths of admitted, not one. A grant scoped to
`Projects/Widget` in a vault declaring `Resources/Vault/Guides` always-readable may
read that guide and must not write it; and the wikilink pass touches notes it may
do neither to. One type cannot carry all of that, so admission is in the type
rather than in a check.

```csharp
public sealed record ReadPath  { public VaultPath Path { get; } }
public sealed record WritePath { public VaultPath Path { get; } public ReadPath AsRead(); }
```

A `WritePath` widens to a `ReadPath`, since anything writable is readable. Nothing
narrows the other way. Every primitive states its own authority in its signature -
`read` takes a `ReadPath`, `write`, `create`, directory creation and both ends of a
move take a `WritePath` - so the read floor's asymmetry is not a rule to remember.
There is no expression that hands a floor-readable path to a write.

## Producing An Admitted Path

One type produces the admitted pair, and holding a scope is the whole of what it
is:

```csharp
public sealed class VaultAccess          // a vault, plus the caller's scope in it
{
    public Vault Vault { get; }
    public PathResolution<ReadPath>  ForRead(string relativePath);
    public PathResolution<WritePath> ForWrite(string relativePath);
    public bool Admits(VaultPath candidate);
}
```

Constructed per call from a vault and the grant's scope in it
(`Grant.ScopeIn`), which is what makes authority handed in rather than ambient:
the parameter is an object a caller must already hold, and no public surface takes
a scope as an argument.

The effective read scope - the grant's roots unioned with the vault's
always-readable roots - is computed once here rather than per candidate, so a
whole-vault enumeration filters against one scope instead of rebuilding the union
for every path it considers.

`ForRead` and `ForWrite` are the loud path: a caller named this path, and the
answer is admitted or refused. `Admits` is the quiet path, used to filter paths the
layer itself produced. They are separate members rather than one with a flag,
because the quiet one cannot construct a refusal and so cannot leak one.

## Refusals

Resolution answers; it does not throw. Three refusals are distinguishable, because
callers above need to tell them apart: malformed (escaping, rooted, or empty),
outside the operand perimeter (a dot-directory segment, which no caller may name
whatever its scope), and out of scope.

A refusal carries the caller's own coordinates and nothing else - never whether the
target exists, never the grant's roots, and never the subset that failed a check.

The perimeter is a property of the caller-named path, not of containment. The file
census reports what a removal destroys, including files under a nested `.git`
([[Spec-Access-Layer-Capabilities.md#Enumeration]]), so those paths are
representable as `VaultPath`. They are simply not nameable as operands.

## The Unscoped Rewrite

Wikilink maintenance is the one pass scope does not bound, and it must rewrite
notes the grant cannot otherwise touch. Rather than a hole in the ladder, the layer
offers exactly one mutation that takes a `VaultPath`: substitution over the byte
ranges the wikilink grammar reports. It cannot write content, create, or delete.

That is the shape the pass already needs - a consumer replaces a target by writing
over its range and never re-emits the note
([[Spec-Access-Layer-Capabilities.md#Wikilink Grammar And Resolution]]) - and it
means the scope exception is one auditable primitive with a name. A later consumer
needing more than a substitution needs a new primitive and its own argument for
one, rather than reaching for the general write.

## What A Path Does Not Assert

Two things a path is never proof of, both of which a caller would otherwise assume.

**Existence.** Containment is textual and touches no filesystem
([[Spec-Vault-Access-Design.md#Load-Bearing Semantics]]), so a path to something
that does not exist resolves normally - `create` needs exactly that. No member
offers an existence answer, because one would invite the check-then-act pattern the
specs forbid for both creation and reading.

**A conforming name.** Names already on disk are not checked
([[Spec-Access-Layer-Capabilities.md#Name Validation]]), so a note that predates
the grammar stays readable, searchable, and movable from. The grammar applies to
the single segment a call brings into existence, at the operation that brings it.

A path also does not say whether it names a file or a folder. Deciding that
textually is wrong and deciding it from disk is forbidden, so each operation states
what it expects and fails on the other.

## Still Open

**Temporary scaffolding, removed before sign-off.** Not an inventory: it is what
was known to be open when this was written, it makes no claim to be complete, and
working any one of these is expected to turn up others. Anything here either
becomes design above or stops mattering.

- Vault resolution's answer - the three-case result replacing the two same-typed
  exceptions in `VaultConfig.Resolve`, and where configuration state becomes it.
- Enumeration's two surfaces, where glob matching happens, and how the dot switch
  is expressed.
- Search - three granularities as separate capabilities, the four distinguishable
  outcomes, and where the engine's own failures surface.
- Read-failure classification.
- Write, create, and directory creation - the atomicity of the creating form, how a
  missing parent is named, and where encoding and line terminators are stated once.
- Move - occupancy against the resolved source, and reporting a case-only rename.
- Name validation's home.
- Wikilink grammar and the single ranking comparator, including where the empirical
  bare-link rule sits so it stays changeable in a line or two.
- The mid-operation capability key assertion, given authority is handed in.

## What This Closes

- **What denotes the vault root.** The specs require the root as the operand of
  whole-vault enumeration, and the retained grammar refuses to normalize a path
  that resolves to it. The root is not a path: vault resolution produces a `Vault`,
  whole-vault operations take one, and no path denotes it.
  Rejected: a path with no segments, which would be one value admitted under every
  scope and so an exception inside the property the build most depends on.
- **How many proofs of admission there are.** The read floor makes read-class
  admission strictly wider than write-class. Admission is carried by two types over
  a contained path, with widening from write to read only, so a primitive's
  authority is its signature.
  Rejected: one admitted type carrying its class as data, which turns the floor
  asymmetry back into a check every write primitive must remember.
- **Where the scope lives.** One per-call type holds a vault and the caller's scope
  in it, and is the only producer of an admitted path. No public surface accepts a
  scope as an argument.
- **How the unscoped wikilink pass stays inside the ladder.** The layer offers one
  mutation over a merely-contained path: substitution over the byte ranges the
  wikilink grammar reports. Everything else that mutates requires write admission.
- **Loud and quiet resolution are different members.** Naming a path answers with a
  refusal; filtering a layer-produced path answers with a boolean and cannot
  construct one. The disclosure rule is then a property of which member exists
  rather than of how carefully each caller handles a result.
- **Where the operand perimeter sits.** Dot-directory paths are representable and
  not nameable: the census must report them, and no caller may name one. The
  refusal belongs to caller-named resolution rather than to containment.
- **What the path type does not assert.** Neither existence nor a conforming name,
  and not whether it is a file or a folder. Each is checked by the operation that
  needs it, at the point it needs it.
