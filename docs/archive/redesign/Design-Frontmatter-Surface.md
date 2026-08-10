# Frontmatter Surface

> **Archived 2026-08-09. Not to be modified.** This document is a historical record of the vault access layer redesign. It describes what was specified and designed, not what the code does now. Where it and the code disagree, the code is right.

## What This Answers

- [[Spec-Frontmatter-Substrate.md#Where The Boundary Sits]] - the in-memory, per-note extent and the rule that no check reasons about content from location, met by [[#Two Halves, One Namespace]].
- [[Spec-Frontmatter-Registry.md#Where The Declaration Lives]] - named by vault configuration, outside the vault, read and never written, met by [[#Loading, And Failing Closed]].
- [[Spec-Frontmatter-Substrate.md#What A Block Is]] and [[Spec-Frontmatter-Substrate.md#The Facet Split]] - detection, the four products of the split, and the one definition every consumer takes, met by [[#Splitting A Note]].
- [[Spec-Frontmatter-Substrate.md#Preservation Invariants]] - order, the single fence writer, and the no-op guarantee, met by [[#The Tree And The Composer]]; terminator normalization on entry, met by [[#Splitting A Note]].
- [[Spec-Frontmatter-Substrate.md#Reading Tolerates What Writing Refuses]] and [[Spec-Frontmatter-Substrate.md#Shape Detection]] - the accessors and the three-answer shape query, met by [[#The Tree And The Composer]].
- [[Spec-Frontmatter-Substrate.md#The Operation Vocabulary]] - the four capabilities, ordering, identity addressing, anchors, and idempotence, met by [[#Operations]].
- [[Spec-Frontmatter-Substrate.md#The Single Write Path]] and [[Spec-Frontmatter-Registry.md#Two Extents, One Closed Perimeter]] - the one entry point, its inputs and outcome, and the three call shapes on one code path, met by [[#The Write Path]].
- [[Spec-Frontmatter-Substrate.md#What Throws And What Comes Back As Data]] - the split and every refusal in it, met by [[#Refusals And Violations]].
- [[Spec-Frontmatter-Substrate.md#Write Authority Classification]] and [[Spec-Frontmatter-Registry.md#Write Authority]] - the outcome diff, the union, and the pre-serialization assertion, met by [[#Classifying Authority]].
- [[Spec-Frontmatter-Substrate.md#The Validator]] - the violation list and what it carries, met by [[#The Validator]].
- [[Spec-Frontmatter-Registry.md#The Declaration]], [[Spec-Frontmatter-Registry.md#What A Key Declaration Expresses]], [[Spec-Frontmatter-Registry.md#Entry Schemas]], [[Spec-Frontmatter-Registry.md#Protected Namespaces]] and [[Spec-Frontmatter-Registry.md#The Value Constraint Vocabulary]] - the loaded shape of all five, met by [[#The Declaration, Loaded]].
- [[Spec-Frontmatter-Registry.md#Loading And Failure]] - where it loads, what load validation catches, and what a failed load leaves working, met by [[#Loading, And Failing Closed]].
- [[Spec-Frontmatter-Substrate.md#How The Vocabulary Reaches Callers]] - generation from the declaration and the advertise-not-enforce rule, met by [[#Advertising The Vocabulary]].
- [[Spec-Tool-Capabilities.md#Path Policy]] - only the two obligations this path carries, the excluded-path refusal and the `mutation_requires` contribution on a write including a body-only one, met by [[#The Write Path]] and [[#Classifying Authority]]. The policy's other enforcers are elsewhere.
- [[Spec-Vault-Access-Design.md#Frontmatter Emitter Settings]] - the settings and the decorator, met by the single composer in [[#The Tree And The Composer]].

Not answered here, deliberately:

- [[Spec-Tool-Capabilities.md#The Argument Surface]] - `edit`'s arguments, and how the generated schema reaches a client, are [[Design-Tool-Vocabulary.md]]. This document generates the schema; it does not decide what carries it.
- [[Spec-Tool-Capabilities.md#The Error Vocabulary]] - what a refusal is called on the wire, and its payload, are [[Design-Tool-Vocabulary.md]]. This document fixes what a refusal distinguishes.
- Reading and writing the file itself, and the paths both take: [[Design-Core-Access-Layer.md]]. Nothing here touches a filesystem.
- [[Spec-Tool-Capabilities.md#Path Policy]]'s enforcement at removal and relocation, which route through no frontmatter write: [[Design-Tool-Vocabulary.md]].
- [[Spec-Tool-Capabilities.md#Wikilink Maintenance]] - repair replaces bytes inside a link and reads no key, so it does not route here at all.

## Two Halves, One Namespace

`VaultShared.Frontmatter` holds both specs' subjects: the substrate that parses, mutates, and emits, and the registry that says what may be written.

**The surface is pure.** Content arrives as a string and leaves as a string, and no member opens, reads, or writes a file. The access layer supplies the bytes and stores them back ([[Design-Core-Access-Layer.md]]), which is what lets the conformance suite exercise every rule here against literals and a synthetic declaration, with no vault and no host. The vault-relative path arrives as text beside the content: it is what policy matches against, not a handle anything resolves.

## Splitting A Note

One type answers the split, and every consumer takes it from there.

```csharp
public readonly record struct NoteSplit(bool HasBlock, string InnerText, string Body, int BodyOffset)
{
    public static string Normalize(string content);
    public static NoteSplit Of(string content);
}
```

Detection is a bounded fence scan, with no markdown library involved. A note with no closed block is `HasBlock: false`, empty inner text, and its whole self as body, so the absent case is a value rather than a branch every caller writes.

`InnerText` is raw and never parsed. Parsing is a separate step because the accessors tolerate what the write path refuses: the same bytes read as no block on the reporting side and as a refusal on the writing side.

**Terminators normalize to LF here, on entry.** `Of` applies `Normalize` before scanning, and the write path applies it to the note's content and to a caller-supplied block before splitting either, so composed content and every offset index the normalized string. It is idempotent, so normalizing and then splitting gives the offsets splitting alone gives. Normalizing on the way out instead would trip the write path's block-equality guard on terminators alone.

`BodyOffset` is taken from the closing fence line's terminator, so a body-only write splices a new body into the original at that offset and disturbs no frontmatter byte.

## The Tree And The Composer

`FrontmatterTree` wraps YamlDotNet's node layer - the mutable mapping, never the object serializer ([[Spec-Vault-Access-Design.md#Fixed Constraints]]). The wrapper exists so no caller holds a raw node: it owns canonical key lookup, the shape query, and the reads that tolerate a malformed block, and it is the only thing the operations mutate.

**Key spelling is canonicalized on the way in**, to the tree's existing spelling where the key is present and the declaration's where it is not. Without that one seam, a differently-cased spelling passes a case-insensitive membership test and then lands as a second key beside the first.

**Shape is a query with three answers** - absent, scalar, block - because a value read cannot distinguish them.

**One type composes content from a tree and a body, and it is the only thing in the build that writes a fence.** It carries the emitter settings, the writer's newline, and the implicit-document decorator from [[Spec-Vault-Access-Design.md#Frontmatter Emitter Settings]], so those four settings have one site rather than one per caller. An empty tree emits no fence, which is how a removal that empties a block drops the fence with it.

**Reserialization is a decision the write path makes, not a property of composing.** The composer is called with a tree only where an operation ran; a body-only write reaches for `BodyOffset` instead. That confinement is what keeps a body edit from reformatting a block it never touched.

## The Declaration, Loaded

The loaded declaration is a closed set of records with no JSON in them, so the shape the rules run against is not the shape the file has.

```csharp
public sealed record FrontmatterRegistry(
    IReadOnlyDictionary<string, KeyDeclaration> Keys,      // case-insensitive
    IReadOnlyList<NamespaceRule> Namespaces,
    IReadOnlyDictionary<string, EntrySchema> EntrySchemas);

public sealed record KeyDeclaration(KeyShape Shape, ValueConstraint? Value, EntrySchema? Entry, string? WriteRequires);
public sealed record NamespaceRule(string Prefix, string WriteRequires);
public sealed record EntrySchema(IReadOnlyList<EntryVariant> Variants);
public sealed record EntryVariant(
    string Name, string? DiscriminatedBy, ValueConstraint? Scalar,
    IReadOnlyDictionary<string, EntryField> Fields,
    IReadOnlyList<string> ExactlyOneOf, IReadOnlyList<string> Identity,
    StringComparison IdentityMatch, bool RemovableWholesale);

public abstract record ValueConstraint      // date, enumeration, charclass, wikilink, string
{
    public abstract ConstraintResult Check(string text);
}
```

A schema is referenced by name in the file and resolved to the instance at load, so two keys naming one schema hold one object and cannot drift.

**A variant's selector is a discriminating field or a scalar constraint, never both.** Variants are matched by walking them once: a scalar node against the scalar variant, a mapping against the variant whose discriminating field it carries. Matching none and matching two are each a violation rather than a fallback.

**`IdentityMatch` is `StringComparison` on the variant**, so every identity question here - duplicate detection, an idempotent add, a removal target, an anchor - resolves through one member chosen in the declaration.

**The constraint vocabulary is a closed hierarchy with a checking member, not a table of predicates.** A new kind is a new record; there is no regular-expression kind and no place to put one, which is the point. `ConstraintResult` names what the constraint wanted, so a violation says more than that something did not match.

## Operations

The four capabilities are a closed hierarchy - set a scalar, remove a field, add an entry, remove an entry - and nothing above may add a fifth.

```csharp
public abstract record FrontmatterOperation(string Key)
{
    public sealed record SetScalar(string Key, string Value)                                  : FrontmatterOperation(Key);
    public sealed record RemoveField(string Key)                                              : FrontmatterOperation(Key);
    public sealed record AddEntry(string Key, EntryPayload Entry, EntryAnchor? Anchor)        : FrontmatterOperation(Key);
    public sealed record RemoveEntry(string Key, EntryTarget Target)                          : FrontmatterOperation(Key);
}
```

An `EntryPayload` is fields and values, or a bare scalar - never rendered block text, so a caller's value can never come out as structure. An `EntryTarget` is an identity value or a named variant to clear wholesale, and the second is refused unless that variant declares itself removable.

**Operations apply in order against one working tree**, each seeing the previous result, and the outcome records which of them changed anything. `RemoveField` takes the scalar or block path from the field's detected shape, never from the caller. Where the shape is block, the removal is refused unless the key declares an entry schema, holds a block list rather than a nested mapping, and every entry resolved under it belongs to a variant declaring itself removable wholesale.

**One resolver answers every identity question** - does this entry already exist, which entry does a removal name, where does an anchor point - so a duplicate add and a removal cannot disagree about whether an entry is there. Matching more than one is a refusal from that resolver, as is an anchor resolving to none or several.

**Two shared text rules sit on one type rather than in the capabilities.** Trimming runs as an operation is constructed, so every case sees the same text, and the one emptiness test runs in the write path's validate phase, where a refusal can name the operation it came from.

Nothing case-folds. Case-insensitivity is a property of the comparisons - the tree's key lookup, the variant name lookup, and the variant's declared `IdentityMatch` - because rewriting a key to lower case would destroy the spelling a write preserves.

## The Write Path

One entry point, and no consumer reaches the primitives around it.

```csharp
public static class FrontmatterWrite
{
    public static WriteOutcome Apply(WriteRequest request);
}

public sealed record WriteRequest(
    string Content, string RelativePath,
    FrontmatterRegistry Registry, PathPolicy Policy, IKeyAuthority Authority,
    IReadOnlyList<FrontmatterOperation> Operations, string? SuppliedBlock, string? NewBody);

public sealed record WriteOutcome(
    string Content, NoteSplit Split, IReadOnlyList<Violation> Violations,
    IReadOnlyList<int> AppliedOperations, bool FrontmatterChanged, bool BodyChanged,
    IReadOnlySet<string> ChangedKeys, IReadOnlySet<string> AssertedAuthority);
```

The registry, the policy, and the authority all arrive as arguments, never from a global or a container - which is what lets the suite hand in a synthetic vocabulary and the same code serve both front-ends.

`RelativePath` is mandatory. Its only uses are the exclusion refusal and matching path policy, and an absent path would read as permission on exactly the files those protect.

The phases are fixed, and every refusal precedes every change:

1. **Validate.** The exclusion refusal - the dot-segment rule, which is code rather than declared - then the caller-supplied block if there is one, then per-operation registry checks, scalar hygiene, and shape.
2. **Apply** the operations in order to the working tree, collecting violations for the keys they touch.
3. **Classify and assert**, over the composed diff, before anything is serialized ([[#Classifying Authority]]).
4. **Compose**, through the single composer where an operation ran, and by body splice where none did.
5. **Guard.** Re-split the composed content and require the frontmatter facet to equal what the operations produced, byte for byte.

The guard covers both directions at once - a body write minting a block no operation authored, and one destroying a block nobody validated - because both come out as the same inequality.

**Three call shapes run this path with no branch between them.** Mutation by operations, composition of a new note, and a narrow scalar pass are outcomes of the same code rather than modes: each falls out of the diff and the required union without a case of its own.

**Extent follows the shape rather than a flag**: a supplied block is validated whole because the caller supplied every key, and operations validate the keys they touch because a note is not made unwritable by a key nobody is editing.

## Classifying Authority

Required authority is the union of three contributions, computed over the outcome rather than the request: what the registry declares for every key whose re-emitted value differs from its original, what a namespace prefix adds for those same keys, and what path policy's `mutation_requires` adds for the note itself.

`mutation_requires` is the only modifier this path reads off the vault's `PathPolicy`; the read floor beside it belongs to scope and never reaches here.

**The diff compares re-emitted values.** Each changed candidate is emitted through the same composer settings and compared ordinally, so a reserialization that changed only quoting is not a change and does not deny a write that changed nothing.

**Assertion is a handed-in object with one job.**

```csharp
public interface IKeyAuthority
{
    void Assert(IReadOnlySet<string> required, IReadOnlySet<string> changedKeys);
}
```

The per-call `VaultAccess` from [[Design-Core-Access-Layer.md]] satisfies it, which is how the mid-operation key assertion the auth spec requires reaches this path without a vault type entering this namespace. An empty required set asserts nothing, and no authority context at all is not a representable state.

**The denial names the changed key set, never an operation index.** That set is the call's own coordinates, so it discloses nothing about the caller's authority.

The registry's whole-block assertion on a supplied tree runs in the validate phase, ahead of and independent of this diff. It is the statement that a key requiring elevation was supplied at all; the diff remains the authority on what changed.

## Refusals And Violations

**A hard refusal throws.** One exception type carries a reason from a closed enumeration, the coordinates that locate the problem - which operation, which key, and where in a caller's supplied text a parse failed - and, for a key no declaration recognizes, the declared key names. Nothing else.

```csharp
public sealed class FrontmatterRefusal : Exception { public RefusalReason Reason { get; } }
```

The reasons are the distinctions the specs require to survive: parse failure, a block that is not a mapping, a fence-introduction or destruction, a supplied block that does not hold up as frontmatter, path exclusion, a control character in caller text, an undeclared key or a failed value constraint, a shape mismatch, an unresolved identity or anchor, an ambiguous identity, a wholesale removal of entries nothing declares clearable, a declaration that is absent or would not load, and authority denial. What each is called on the wire is [[Design-Tool-Vocabulary.md]]'s.

**A schema violation comes back as data**, since the widest consumer sweeps notes that already carry problems.

```csharp
public sealed record Violation(ViolationRule Rule, string Key, int? EntryPosition, string? Field, string Reasoning);
```

`EntryPosition` is 1-based, and `Rule` is an enumeration rather than a message, so a remedy branches on it without reading prose.

## The Validator

The validator takes content and a registry and returns the same `Violation` list, empty when valid and never a verdict.

**It is the whole-block extent over the checkers the write path applies per key** - one variant matcher, one constraint check - so a note the validator passes cannot be one an operation refuses over the same key. What is compiled in rather than declared runs beside those checks rather than as a second pass.

## Advertising The Vocabulary

The generated schema is a projection of the loaded registry: one branch per entry variant, each variant's discriminating field listed among the branch's required fields, fields and constraints carried across, and what has no clean client-facing expression carried in a description. It is generated from the same `FrontmatterRegistry` instance the write path holds, so there is no second copy and no drift test to write.

The branch fixes no value for the discriminating field, because matching selects a variant by that field's presence and a fixed value would advertise a rule the enforcement point does not apply.

Generation is a read of the registry and asserts nothing, which is what keeps this surface the sole enforcement point.

## Loading, And Failing Closed

The declaration is named by a vault's own configuration entry, loaded once at startup, and never written. Two vaults naming one file share one loaded instance, since the file is the identity.

**A load answers a three-case state, never a null registry.**

```csharp
public abstract record RegistryState
{
    public sealed record Loaded(FrontmatterRegistry Registry) : RegistryState;
    public sealed record Absent(string Path)                  : RegistryState;
    public sealed record Unloadable(string Path, string Why)  : RegistryState;
}
```

Absent and unloadable refuse identically and report differently, because an operator's next move differs.

**Every load-time check the registry spec lists runs in the loader**, over the file's shape before the records are built. The cross-file one - an op class the op-class configuration does not define - runs against the loaded `OpClassRegistry` through its membership member, which is why this load joins the same cascade as grants, after it.

**A failed load is recorded Degraded, per vault, through the retained `IConfigProblemSink`.** Fatal would take down a server whose reads, searches, moves and removals are all still correct.

**What a failed load refuses is exactly the frontmatter writes.** Reads, enumeration, search, move and delete are untouched - what prices those is path policy, declared elsewhere. `edit` stays advertised with its ordinary argument surface and its variant branches simply absent, since advertising was never permission.

## What This Closes

- **The surface touches no filesystem.** Content in, content out, with the vault-relative path arriving as text.
- **Where the split lives, and that the absent case is a value.** One `NoteSplit` for every consumer, a fence-less note producing a well-formed one. Rejected: an optional block, which puts the same branch in every caller and lets two of them disagree about what a fence-less note is.
- **The tree is wrapped, not exposed.** Canonical key lookup, shape detection, and tolerant reads sit on the wrapper, so no caller holds a raw YamlDotNet node and no second place decides how a key is matched.
- **Reserialization is the write path's decision, not the composer's.**
- **Where the emitter settings live.** On the one composer that writes a fence, so they have a single site.
- **The declaration loads into records, not into a document.** The rules run against a closed shape, and the file's own shape exists only inside the loader.
- **Value constraints are a closed hierarchy with a checking member.** Rejected: a predicate table keyed by kind name, which is the same closed set with the reporting - what the constraint wanted - thrown away.
- **Identity comparison is a `StringComparison` on the variant**, so no site chooses one for itself.
- **The write path takes the registry, the policy, and the authority as arguments.** Received, never reached for, which is what makes path policy enforceable here without the surface knowing what a vault is.
- **No rule is conditioned on where a note sits.** The validator takes content and a registry, and the declaration has no way to spell "a note at this path must carry this key". Such a rule is a convention about how the vault is arranged, and it belongs to whatever composes the write or to a whole-vault sweep. One consequence follows and is accepted: the validator judges what a note carries, so a note carrying no frontmatter has nothing to fail and can never be a violation here.
- **Entry placement is explicit or it is an append.** Nothing computes a default position. A caller wanting an entry grouped rather than appended reads the list and supplies an `EntryAnchor` naming the entry to sit before or after. Where an entry belongs among its siblings depends on what the note is for, which the composer knows and this surface does not.
- **The dot-segment exclusion is compiled in and matches whole segments at any depth.** No declaration can admit a path with one, so there is no prefix string a path can climb back out of. The cost is accepted: notes under `.claude/` carry ordinary vault frontmatter, and nothing in the build can read or write them.
- **A failed load has no fallback vocabulary.** Rejected: a built-in default set, which is how a failed load becomes a vault with nothing protected.
- **Authority reaches this path as a one-member interface** that `VaultAccess` satisfies. Rejected: returning the requirement for a caller to assert, which makes the assertion optional at every call site and the guarantee a convention.
- **The fence guard is an equality, not a pair of checks.** Comparing the re-split facet covers introduction and destruction at once, so neither depends on somebody having enumerated it.
- **A block that will not parse is not repairable through the tool surface, and that is accepted.** An operation over such a block refuses, because the operation is what triggers the parse; a body-only write does not reach the block; and a caller-supplied block reaches only a note being created, so it cannot replace an existing note's block. The remaining path is delete-then-create.
- **Hard refusals throw with a reason from a closed enumeration; violations return as data.** Rejected: one result carrying both, which lets a caller read the violation list and never notice the refusal beside it.
- **A violation carries a rule enumeration rather than a message to match.** A remedy branches on the rule; the reasoning is for the human beside it.
- **The undeclared-key refusal carries the declared key names**, so the caller lifts one into a corrected call. Names only, sorted ordinally so the list is the same every call, and from both raise sites, so the code never arrives with a payload one way and without it the other. The list is the declared-keys map alone, since the refusal fires before a namespace prefix could matter, and it offers no nearest match: the caller reads the right key out of the list. Rejected: advertising the keys in the tool's schema, which every session pays for, where an error payload is paid for only by the call that erred.
- **A required entry field carrying no value fails as `MissingRequiredField`.** Empty means null, empty, or whitespace-only, so a field written with nothing under it fails the check an absent one fails and only the reasoning differs. The write path refuses an added entry's empty field the same way. Rejected: a rule of its own, which widens a closed enumeration every remedy branches on to separate two cases with one remedy.
- **A load answers three cases, and null is not one of them.** Rejected: an empty registry standing in for a failed load, which reads as a vocabulary that recognizes nothing and turns fail-closed into a vault with nothing protected.
- **A failed load is Degraded, not Fatal.** Everything but frontmatter writes is still correct, and taking the host down would refuse operations the failure does not touch.
