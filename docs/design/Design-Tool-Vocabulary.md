# Tool Vocabulary

## What This Answers

- [[Spec-Tool-Capabilities.md#The Argument Surface]] - one record per tool as binder and schema source, the structured argument, the snake_case rule, and the refusal of an unrecognized enumerated value, met by [[#Argument Records]] and [[#Argument Rules]].
- [[Spec-Tool-Capabilities.md#The Operand Vocabulary]] and [[Spec-Tool-Capabilities.md#The Operand Perimeter]] - the path operand, the kind assertion and where it is required, and the never-inferred extension, met by [[#Argument Rules]].
- [[Spec-Tool-Capabilities.md#What Each Tool Must Accomplish]] - the seven, expressed as their argument and result contracts, met by [[#Argument Records]] and [[#Result Tiers]].
- [[Spec-Tool-Capabilities.md#Result Tiers]] and [[Spec-Tool-Capabilities.md#Result-Count Semantics]] - the two tiers, the four envelope obligations, and each tool's count rule, met by [[#Result Tiers]] and [[#The Mutation Envelope]].
- [[Spec-Tool-Capabilities.md#The Preview Contract]] - one gate with one meaning, the two tools that carry it, and a plan as a faithful prediction, met by [[#The Preview Gate]].
- [[Spec-Tool-Capabilities.md#Staleness And Chaining]] - the token's derivation, where it is required, and the chain, met by [[#Staleness Tokens]].
- [[Spec-Tool-Capabilities.md#The Error Vocabulary]] - the code set, the payload rules, the distinctions that must survive, and failure ordering, met by [[#The Error Catalogue]] and [[#Failure Ordering]].
- [[Spec-Vault-Access-Design.md#Guarantees To Callers]] - errors structured, coded and oracle-free, met by [[#The Neutral Result And Refusal]] and [[#The Error Catalogue]].
- [[Spec-Tool-Capabilities.md#Path Policy]] - the two enforcers the frontmatter write path does not carry, removal and relocation, met by [[#Path Policy At Removal And Relocation]].
- [[Spec-Tool-Capabilities.md#Authority]] - what a mutation reports having asserted, and the artifact contribution on a relocation, met by [[#The Mutation Envelope]] and [[#Path Policy At Removal And Relocation]].
- [[Spec-Tool-Capabilities.md#Wikilink Maintenance]] and [[Spec-Tool-Capabilities.md#Enumeration Domains]] - what each of the three passes reports and which domain it asks, met by [[#Reporting Out Of The Vault-Wide Pass]].
- [[Spec-Tool-Capabilities.md#Destruction]] and [[Spec-Tool-Capabilities.md#Multi-Target Mutation]] - the three guards as refusals, the plan-before-anything rule, and where partial-failure vocabulary lives, met by [[#The Error Catalogue]] and [[#The Mutation Envelope]].
- [[Design-Core-Access-Layer.md#The Vault-Wide Pass]] - the constraint on what an unscoped pass may report, met by [[#Reporting Out Of The Vault-Wide Pass]].
- [[Design-Frontmatter-Surface.md#Refusals And Violations]] - what each `RefusalReason` is called on the wire, met by [[#The Error Catalogue]].

Not answered here, deliberately:

- The shared execute path - the op-class floor check, the mutex hold and its crash recovery, and who constructs the per-call `VaultAccess`: [[Design-Front-End-Projection.md]]. This document fixes what a tool's contract is, not what invokes it.
- [[Spec-Vault-Access-Design.md#The Front-End Boundary]] - one declaration projected into two front-ends, and how a result or a refusal is rendered on each: [[Design-Front-End-Projection.md]].
- [[Spec-Frontmatter-Substrate.md#The Operation Vocabulary]] - the four capabilities and their semantics are [[Design-Frontmatter-Surface.md]]. `edit`'s arguments are the wire projection of them and are here.
- [[Spec-Access-Layer-Capabilities.md]] - every primitive these tools compose: [[Design-Core-Access-Layer.md]].

## The Neutral Result And Refusal

A tool answers with a value or raises a refusal. Neither knows a transport.

```csharp
public abstract record ToolResult
{
    public sealed record Content(string Text) : ToolResult;   // the content tier
    public sealed record Data(object Value)   : ToolResult;   // the structured tier
}

public sealed class ToolRefusal(string code, string message, object? data = null) : Exception
{
    public string Code { get; }
    public object? Data { get; }
}
```

`Data` carries one of the per-tool result records below, serialized under the same `JsonSerializerOptions` the argument records bind through, so one naming policy governs both directions and nothing translates between a sent name and a returned one.

A refusal is raised rather than returned because it must survive frames that did not expect it ([[Spec-Tool-Capabilities.md#The Error Vocabulary]]). A tool catches nothing it did not raise, and no layer re-shapes a refusal on the way past.

The wire form of the envelope is carried over from the retained `ToolError`: no whitespace, `code` then `message` then `data`, and `data` absent from the object rather than emitted as null.

`FrontmatterRefusal` ([[Design-Frontmatter-Surface.md#Refusals And Violations]]) is the one refusal raised in another vocabulary. One table maps its `RefusalReason` to a code from [[#The Error Catalogue]], applied where the envelope is rendered rather than in any tool's catch, so the mapping has a single site and no tool is a translation point.

## Argument Records

One record per tool, both the binding target and the source of the advertised schema, through the retained `NativeToolArguments`. Every wire name below is the PascalCase member under `JsonNamingPolicy.SnakeCaseLower`; none is authored twice.

```csharp
public interface IToolArguments
{
    ArgumentFault? Validate();      // pure: shape, exclusivity, inert parameters. No vault, no filesystem.
}

public enum OperandKind { Note, Folder }

public sealed record ReadArguments(string? Vault, string Path, ReadPart Part, bool WithToken) : IToolArguments;
public enum ReadPart { All, Frontmatter, Body }

public sealed record GlobArguments(string? Vault, string Pattern) : IToolArguments;

public sealed record GrepArguments(string? Vault, string Pattern, GrepGranularity Granularity, string? PathGlob) : IToolArguments;
public enum GrepGranularity { Lines, Files, Counts }

public sealed record EditArguments(
    string? Vault, string Path,
    IReadOnlyList<OperationArgument>? Operations,
    BodyChange? Body,
    string? ExpectedToken) : IToolArguments;

public sealed record BodyChange(string? Content, string? Find, string? Replace);   // exactly one of Content, or Find with Replace

public sealed record OperationArgument(
    string Op,                      // set | remove | add_entry | remove_entry
    string Key,
    string? Value,                  // set
    JsonObject? Entry,              // add_entry: a variant's fields, or a bare scalar
    string? Target,                 // remove_entry: an identity value
    string? Variant,                // remove_entry: a variant cleared wholesale
    OperationArgument? Anchor);

public sealed record CreateArguments(
    string? Vault, string Path, OperandKind Kind,
    string? Content,                // note only
    bool GitKeep) : IToolArguments; // folder only

public sealed record DeleteArguments(string? Vault, string Path, OperandKind Kind, bool Recursive, bool Commit) : IToolArguments;

public sealed record MoveArguments(string? Vault, string Path, string Destination, OperandKind? Kind, bool Commit) : IToolArguments;
```

`Vault` is the only argument that is not an operand or a modifier, and it selects rather than restricts: absent, the configured default vault applies. Scope and held authority appear nowhere, which is what makes the guarantee checkable by reading one record.

`Path` names the thing that exists; `Destination` is the one constructed name in the set, and its difference in spelling is the difference the specs draw between resolving and constructing.

## Argument Rules

**Validation is a member on the arguments, not on the tool.** `Validate` is pure - mutual exclusivity, an inert parameter, an unrecognized enumerated value - so the whole argument surface can be refused before a vault is resolved and before anything is locked. That is what makes [[#Failure Ordering]]'s first phase real rather than a claim about where the checks happen to sit.

**An unrecognized enumerated value is refused in this vocabulary**, trimmed and case-folded before comparison, never through the binder's own rejection path.

**Kind is required exactly where the two kinds take different arguments.** `create` and `delete` require it - `Content` and `GitKeep` split on it, and `Recursive` means nothing for a note. `move` makes it optional, because both kinds take the same arguments there and the assertion buys only fail-fast for a caller that thinks it is renaming one note. `read`, `glob` and `grep` have no operand kind to assert.

**An argument that cannot change an outcome for the supplied operand is refused.** `GitKeep` on a note, `Recursive` on a note, `Content` on a folder, `ExpectedToken` with no body change: each is a fault from `Validate`, not a value ignored.

**The extension is never inferred.** A note operand ends in `.md`; a folder operand does not. An extensionless path handed to a note-kind operation is refused naming the spelling that would have worked.

**`edit`'s operations stay structured.** `OperationArgument` is the wire projection of the substrate's four capabilities, decoded onto `FrontmatterOperation` before the write path sees it, and the `Entry` branch's advertised schema is generated from the loaded registry ([[Design-Frontmatter-Surface.md#Advertising The Vocabulary]]). An entry kind is not an `Op`: the payload states its own variant.

**`Body` carries the two forms the staleness rule distinguishes.** `Content` replaces the body wholesale and requires a token; `Find` with `Replace` substitutes once and self-verifies through its own anchor. Exactly one form, and a `Find` matching zero times or more than once is a refusal rather than a partial application.

## Result Tiers

The content tier is text and nothing else, in the shape an agent already consumes.

- `read` returns the note's whole text, the block's inner text without its fences, or the body. A note with no block answers `frontmatter` with empty text. With `WithToken` it is the one content-tier result that carries structure, and returns `ReadResult(Content, Token)` instead.
- `glob` returns newline-joined vault-relative paths, and empty text for no match.
- `grep` returns `path:line:text` per match, one path per line for the file granularity, and `path:count` for counts. Empty text is the empty result, never a failure.

Both search tools treat any count as a result. Every other tool operates on an existing target and fails on a miss, except `create`, for which existence is the failure.

The structured tier is every mutation, returned as `Data`.

## The Mutation Envelope

There is no single envelope record. The four obligations produce a shape per tool that shares members only where every tool sharing one genuinely does that thing.

```csharp
public sealed record EditResult(
    string Token, bool FrontmatterChanged, bool BodyChanged,
    IReadOnlyList<string> ChangedKeys, IReadOnlyList<int> AppliedOperations,
    IReadOnlyList<string> AssertedAuthority, IReadOnlyList<Violation>? Warnings);

public sealed record CreateResult(
    string Path, string? Token, IReadOnlyList<LinkRewrite>? PinnedLinks,
    IReadOnlyList<string> AssertedAuthority);

public sealed record MoveResult(
    bool Committed, string Path, string? Token, bool CaseOnly,
    IReadOnlyList<string> Moved, IReadOnlyList<LinkRewrite> RepairedLinks,
    IReadOnlyList<LinkRewrite>? PinnedLinks, string? FolderNote,
    IReadOnlyList<string> AssertedAuthority);

public sealed record DeleteResult(
    bool Committed, IReadOnlyList<string> Removed,
    IReadOnlyList<LinkOccurrence> Backlinks, IReadOnlyList<string> AssertedAuthority);

public sealed record LinkRewrite(string Path, int Line, string From, string To);
public sealed record LinkOccurrence(string Path, int Line, string Target);
```

`AssertedAuthority` is on all four and stays when empty: absence would not distinguish an operation that asserted nothing from a tool that never asserts.

`Token` is absent where there is no single note left to describe - a folder creation, a folder move - and `Committed` is absent from `CreateResult` because creation has no gate to report the state of.

`PinnedLinks` is nullable because absence and emptiness part company: no member means the basename was unique and no pass ran, an empty list means the pass ran and pinned nothing. `Backlinks` is not nullable for the same reason read the other way - a census that ran and found nothing is a finding.

`Warnings` carries the validator violations the write path returned as data, and is absent when there are none.

**Partial-failure vocabulary is in the refusal, not the envelope.** A mutation that fails partway raises `partial_mutation` carrying what landed, what did not, and the ordered recovery steps. Keeping it out of the success shape is what stops a single-write mutation from carrying vocabulary for a state it cannot reach.

## The Preview Gate

`Commit` is the one gate, false by default, and only `delete` and `move` carry it. `edit` and `create` have none: `edit`'s blast radius is its own request, and a creation destroys nothing.

A plan runs the identical path - the same lock, the same validation, the same refusals - and answers the same record with `Committed: false`. The two differ in that member alone, so a caller can compare a plan against the result of the call it authorized. Every path-valued member names post-mutation paths in both.

## Staleness Tokens

The token is the SHA-256 of the file's bytes as stored, lowercase hex, opaque to callers. It is taken from the same read that produced the content and describes the whole file whichever part was read, so a token from a read and a token from a mutation are the same value and chain in either direction.

`ExpectedToken` is required for a wholesale body replacement and permitted anywhere a mutation can destroy content the caller did not see. A mismatch refuses without returning the current token: recovery is a re-read.

## Path Policy At Removal And Relocation

Neither `delete` nor `move` routes through the frontmatter write path, so each consults the vault's `PathPolicy` in its own validate phase, before the lock is used for anything.

A `mutation_requires` class contributes to the required authority for every target in the operation's set, and contributions union. A relocation contributes what a path entry declares whether or not the destination stays under it, and contributes what a structural rule declares only where artifact status changes - a note renamed to match its folder acquires it, a folder note carried along by its folder's rename does not.

Wikilink repair contributes nothing for the notes it rewrites. Those rewrites are maintenance, and the pass reads no key.

**Named is loud, swept is silent.** An excluded path given as an operand refuses with `path_excluded`, distinguishably from a miss and from a scope denial. An excluded path met inside a sweeping pass is skipped without comment.

## Reporting Out Of The Vault-Wide Pass

The three passes reach notes the caller's grant cannot read, so what they report is bounded even though the pass is not.

**A pass reports identities and positions, never content.** `LinkRewrite` and `LinkOccurrence` carry a path, a line, and the link target - values the caller already named or the operation itself produced. No surrounding text, no note excerpt, and no key value leaves a note the grant does not admit. A caller therefore learns that some note links to the thing it named, which is the minimum a repair report can be, and nothing else about that note.

Each pass asks the domain its question needs: note enumeration for the repair, the pinning census and the backlink scan; the file census, with dot entries admitted, for the destruction plan alone.

## Failure Ordering

Every tool observes one order, and a refusal from an earlier phase wins over one from a later.

1. **Arguments** - `Validate` on the record. Pure, and complete before anything else runs.
2. **Vault resolution** - `unknown_vault` or `vault_unavailable`.
3. **Path resolution, scope, and policy** - malformed, excluded, or out of scope.
4. **Plan** - existence, occupancy, the destruction census and its guards, staleness, and authority classification and assertion.
5. **Apply.**

Routing precedes authorization because phase 2 precedes phase 3: a call naming a vault nobody configured is told that, rather than being refused for a scope that could never have admitted it.

A mutating tool holds the lock from before phase 3, since the lock's identity derives from the vault resolved in phase 2 ([[Design-Front-End-Projection.md]]). Nothing is written before phase 5, so every refusal still precedes every change.

A tool reports the first failure it meets and stops. These are not batch reports.

## The Error Catalogue

One catalogue, drawn from by every tool.

```
unknown_vault          A vault nobody configured. Caller error.
vault_unavailable      Configured but not usable. Operational; retryable.
invalid_argument       Shape, exclusivity, an inert parameter, an unrecognized enumerated value.  data: {argument}
invalid_path           Malformed, escaping, rooted, empty, or missing the markdown extension.     data: {suggested?}
invalid_name           A new segment outside the name grammar.                                    data: {suggested?}
path_excluded          The tooling does not own this path. No grant changes it.
out_of_scope           The grant does not reach it. No payload.
authority_required     A key, namespace, or artifact class the caller lacks.                      data: {keys | paths}
path_not_found         The sole target is not there.
read_failed            There, and the read failed.                                                data: {path}
already_exists         The target is occupied. Creation and move destinations.
parent_missing         The destination's parent does not exist.                                   data: {parent}
wrong_kind             The operand is not the kind asserted.
not_empty              A plain folder removal met contents.                                       data: {plan}
git_directory_present  A .git directory is inside the removal set.                                data: {paths}
stale_token            The file changed since the token was taken. No payload.
body_match_not_found   A substitution anchor matched nothing.
body_match_ambiguous   A substitution anchor matched more than once.                              data: {count}
invalid_pattern        A malformed glob or regular expression. Caller error.
search_failed          The engine failed.                                                         data: {detail}
frontmatter_unparsable The block will not parse, or is not a mapping.
frontmatter_unavailable The registry is absent or would not load. Frontmatter writes only.
unknown_key            No declaration recognizes the key.
invalid_value          A value constraint refused, or caller text carried a control character.    data: {expected}
shape_mismatch         Scalar written to a block key, or the reverse.
entry_not_found        An identity or anchor resolved to nothing.
entry_ambiguous        An identity or anchor resolved to more than one.
lock_timeout           The mutex lock was not acquired. Raised at the lock; no tool catches it.   data: {timeout}
partial_mutation       A multi-target mutation failed partway.                                    data: {applied, not_applied, recovery}
```

Three rules govern the payloads. **A payload is liftable into a corrected call** - `suggested`, `parent`, and `plan` are; `out_of_scope` and `stale_token` therefore carry nothing. **A payload names the caller's own coordinates only** - `authority_required` names the keys or paths the caller supplied, never what the grant holds and never the subset that failed. **A payload never answers a question the refusal withheld** - no code discloses whether a target the caller may not reach exists.

## What This Closes

- **A tool answers with a value and raises a refusal.** Raising is what lets a refusal cross frames unchanged; returning it would make surfacing it correctly a convention every caller keeps.
- **One record per tool is the binder and the schema source**, through the retained `NativeToolArguments`, so a tool's declared argument names are the names it actually takes and a remedy can be built from them.
- **Argument validation is a member on the argument record.** Pure, and run before vault resolution, which is what makes the ordering guarantee structural rather than a habit. Rejected: validation inside the tool body, which puts every argument refusal after the lock is taken.
- **`Vault` is an argument; scope and authority are not.** Selection is the caller's, restriction is the grant's.
- **The source operand is `path` on every tool and the constructed one is `destination`.** The spelling carries the resolve-versus-construct distinction the operand vocabulary draws.
- **Kind is required on `create` and `delete`, optional on `move`.** The rule is whether the kinds take different arguments; on `move` they do not, and the assertion survives only as a fail-fast.
- **`edit` takes a body change in two forms, not one.** A wholesale replacement requires a token; a substitution matches exactly once and needs none. Rejected: one form with an optional anchor, which makes the token requirement conditional on a value rather than on a shape.
- **The content tier is text, and `read` with a token is its one structured member.** Rejected: wrapping every content result in an object for uniformity, which costs tokens on exactly the results a model reads directly.
- **There is no shared mutation record.** Four per-tool shapes, sharing a member only where every sharer populates it. `AssertedAuthority` is on all four and stays when empty; `Committed` and `Token` are absent where the tool cannot populate them.
- **Nullable means the question was never asked.** `PinnedLinks` absent is no pass; empty is a pass that pinned nothing. That distinction is the whole reason the member is nullable rather than defaulted to empty.
- **Partial-failure vocabulary lives in `partial_mutation`, not in the envelope.** A creation that writes once cannot report a partial state, and giving it the members to do so would be a claim it never makes.
- **`commit` is the gate, false by default, on `delete` and `move` alone.** A plan differs from its result in that member and nothing else.
- **The token is a SHA-256 of the stored bytes, opaque on the wire.** Whole-file whichever part was read, so nothing authorizes a write from a partial read.
- **A staleness refusal returns no token.** Returning the current one would let a caller retry into content it never read.
- **A vault-wide pass reports identities and positions, never content.** The bound is on the report rather than on the primitive, because the primitive must reach notes the grant cannot.
- **Routing beats authorization by phase order**, not by a rule each tool remembers: vault resolution is phase 2 and scope is phase 3.
- **One table maps `RefusalReason` to a code**, applied where the envelope is rendered. Rejected: each tool catching and re-coding, which reintroduces per-tool reasoning about a refusal it did not raise.
- **`path_not_found` rather than `not_found`.** The retained dispatcher already spends `not_found` on an unknown tool name, and a caller branching on one code for two unrelated conditions is the collapse the vocabulary exists to prevent.
- **Snake_case governs results as well as arguments**, through the same serializer options the binder uses, so nothing translates between a sent name and a returned one.
